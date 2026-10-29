using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>Bounded positional history traversal owned by one authenticated profile listener.</summary>
public sealed class HistoryReader : IDisposable
{
    private const int MaximumCursors = 4;
    private const int MaximumPageInput = 4 * 1024 * 1024;
    private const int MaximumResultPageInput = 256 * 1024;
    private const int MaximumReplayCursors = 16;
    private const int MaximumRecordBytes = 16 * 1024;
    private const int RecordBudget = 38 * 1024;
    private static readonly TimeSpan IdleLease = TimeSpan.FromMinutes(5);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex DatePrefix = new(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private readonly CompanionProfileConfiguration _profile;
    private readonly string[] _secrets;
    private readonly object _gate = new();
    private readonly HistoryTimeIndex _index = new();
    private readonly Dictionary<string, CursorState> _cursors = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private long _nextCleanup;
    private bool _disposed;

    /// <summary>Creates a reader with optional synthetic clock; credentials are used only for redaction.</summary>
    public HistoryReader(CompanionProfileConfiguration profile, string[]? sensitiveValues = null, TimeProvider? clock = null)
    {
        _profile = profile;
        _secrets = sensitiveValues ?? Array.Empty<string>();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads or idempotently replays one bounded page; a cursor never grants authentication.</summary>
    public JObject Read(DiagnosticHistoryQuery query, CancellationToken cancellationToken)
    {
        if (!query.IsValid) throw new HistoryReadException("history-invalid-request");
        CursorState state;
        lock (_gate)
        {
            if (_disposed) throw new HistoryReadException("history-restart-required");
            ExpireLocked();
            if (query.Cursor is null)
            {
                state = _cursors.Values.SingleOrDefault(c => c.Query.QueryId == query.QueryId)!;
                if (state is null)
                {
                    if (_cursors.Values.Count(c => !c.Terminal && !c.Closed) >= MaximumCursors) throw new HistoryReadException("history-cursor-capacity");
                    foreach (CursorState old in _cursors.Values.Where(c => c.Terminal || c.Closed).OrderBy(c => c.LastUsed).ToArray())
                    {
                        if (_cursors.Count < MaximumReplayCursors) break;
                        TryRemove(old);
                    }
                    if (_cursors.Count >= MaximumReplayCursors) throw new HistoryReadException("history-cursor-capacity");
                    state = new CursorState(query, _clock.GetUtcNow());
                    state.Paths = Discover(state).GetEnumerator(); _cursors.Add(state.Id, state);
                }
            }
            else if (!_cursors.TryGetValue(query.Cursor, out state!)) throw new HistoryReadException("history-restart-required");
            if (!Monitor.TryEnter(state.Gate)) throw new HistoryReadException("history-busy");
        }
        try
        {
            if (state.Closed) throw new HistoryReadException("history-restart-required");
            if (state.Query != (query with { Cursor = null, Sequence = 0 })) throw new HistoryReadException("history-query-mismatch");
            state.LastUsed = _clock.GetUtcNow();
            if (query.Sequence == state.LastSequence && state.LastPage is not null) return (JObject)state.LastPage.DeepClone();
            if (query.Sequence != state.LastSequence + 1 || state.Terminal) throw new HistoryReadException("history-sequence-mismatch");
            cancellationToken.ThrowIfCancellationRequested();
            JObject page;
            try { page = ReadPage(state, query.Sequence, cancellationToken); }
            catch (Exception)
            {
                state.Dispose(); // Map cleanup is deferred; never reuse advanced state.
                throw new HistoryReadException("history-source-changed");
            }
            state.LastSequence = query.Sequence; state.LastPage = page;
            state.LastUsed = _clock.GetUtcNow();
            if (state.Terminal) state.CloseSources();
            return (JObject)page.DeepClone();
        }
        finally { Monitor.Exit(state.Gate); }
    }

    /// <summary>Releases a cursor only when its immutable query matches.</summary>
    public JObject Close(DiagnosticHistoryQuery query)
    {
        lock (_gate)
        {
            if (!query.IsValid || query.Cursor is null) throw new HistoryReadException("history-invalid-request");
            if (_cursors.TryGetValue(query.Cursor, out CursorState? state))
            {
                if (state.Query != (query with { Cursor = null, Sequence = 0 })) throw new HistoryReadException("history-query-mismatch");
                if (!TryRemove(state)) throw new HistoryReadException("history-busy");
            }
            return new JObject { ["closed"] = true };
        }
    }

    /// <summary>Reclaims idle state without holding up active cursors.</summary>
    public void ExpireIdle()
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _nextCleanup) || !Monitor.TryEnter(_gate)) return;
        try { _nextCleanup = Environment.TickCount64 + 5000; ExpireLocked(); }
        finally { Monitor.Exit(_gate); }
    }

    private void ExpireLocked()
    {
        foreach (CursorState state in _cursors.Values.ToArray())
        {
            if (!Monitor.TryEnter(state.Gate)) continue;
            try { if (state.Closed || _clock.GetUtcNow() - state.LastUsed >= IdleLease) TryRemove(state); }
            finally { Monitor.Exit(state.Gate); }
        }
    }

    private bool TryRemove(CursorState state)
    {
        if (!Monitor.TryEnter(state.Gate)) return false;
        try { _cursors.Remove(state.Id); state.Dispose(); return true; }
        finally { Monitor.Exit(state.Gate); }
    }

    private JObject ReadPage(CursorState state, long sequence, CancellationToken cancellationToken)
    {
        var records = new JArray();
        var watch = Stopwatch.StartNew();
        int input = 0, discovery = 0, output = 0;
        long scannedBefore = state.Scanned, indexedBefore = state.IndexRecords;
        state.Compressed?.BeginPage();
        DateTimeOffset? first = null, last = null;
        while (records.Count < state.Query.PageLines && input < (records.Count == 0 ? MaximumPageInput : MaximumResultPageInput) && discovery < 512 && watch.Elapsed < TimeSpan.FromMilliseconds(1200) && !cancellationToken.IsCancellationRequested)
        {
            JObject? record = state.Pending;
            if (record is not null) state.Pending = null;
            else
            {
                if (state.Stream is null)
                {
                    if (!state.Paths!.MoveNext()) { state.Terminal = true; break; }
                    discovery++;
                    string? path = state.Paths.Current;
                    if (path is null) continue;
                    if (!Open(state, path)) continue;
                }
                if (!CompanionFileAccess.TryReadMetadata(state.Stream!.SafeFileHandle, false, out CompanionFileMetadata current) || current.Length < state.Upper || current.Device != state.Metadata.Device || current.Inode != state.Metadata.Inode)
                {
                    state.Gap("file_changed"); state.CloseFile(); continue;
                }
                if (state.Index is not null && current != state.Metadata) throw new IOException("Indexed source changed.");
                if (current != state.Metadata) state.IndexBuilder = null;
                if (current.Permissions != state.Metadata.Permissions) state.Gap("file_permissions_changed");
                if (current.LinkCount == 0) state.Gap("file_unlinked");
                if (current.LinkCount > 1) state.Gap("hard_link_aliases");
                if (current.ModifiedUtc != state.Metadata.ModifiedUtc || current.ModifiedNanoseconds != state.Metadata.ModifiedNanoseconds)
                {
                    if (state.Compressed is null && current.Length > state.Upper) state.Warnings.Add("file_grew_after_open_boundary");
                    else state.Gap("file_modified_during_scan");
                }
                if (SkipIndexedBlock(state)) continue;
                string? line, omission; long offset;
                DateTimeOffset? inheritedBefore = state.InheritedTime;
                try { if (!ReadLine(state, ref input, out line, out offset, out omission)) continue; }
                catch (InvalidDataException)
                { state.Gap("gzip_invalid_or_resource_limit"); state.ArchiveFailures++; state.CloseFile(); continue; }
                if (line is null && omission is null) { FinishIndex(state); state.CloseFile(); continue; }
                state.Scanned++;
                DateTimeOffset? timestamp = line is null ? null : ParseTime(line, state);
                if (line is null) state.InheritedTime = null;
                if (omission is not null) { state.Omitted++; state.Gap(omission); }
                if (timestamp is null)
                {
                    state.IndexBuilder = null;
                    state.Unknown++; state.Gap("unknown_time");
                    if (state.Query.UnknownTime == "report-only") { state.UnknownExcluded++; continue; }
                }
                else
                {
                    state.IndexBuilder?.Add(offset, state.Position, timestamp.Value, inheritedBefore, state.InheritedTime);
                    state.ObservedMin = Min(state.ObservedMin, timestamp);
                    state.ObservedMax = Max(state.ObservedMax, timestamp);
                    if (timestamp < state.Query.Since || timestamp >= state.Query.Until) continue;
                }
                string text = line is null ? "[record omitted]" : DiagnosticReplyValidator.SanitizeEvidence(line, _secrets);
                record = new JObject
                {
                    ["id"] = state.FileId + ":" + offset.ToString(CultureInfo.InvariantCulture), ["source_file"] = state.FileId,
                    ["source_encoding"] = state.Compressed is null ? "utf8" : "gzip", ["offset_kind"] = state.Compressed is null ? "file_bytes" : "uncompressed_bytes",
                    ["byte_offset"] = offset, ["timestamp"] = TimeJson(timestamp),
                    ["time_status"] = timestamp.HasValue ? "known" : "unknown", ["text"] = text, ["content_omitted"] = omission is not null
                };
            }
            int size = Encoding.UTF8.GetByteCount(record.ToString(Formatting.None));
            if (size > RecordBudget)
            {
                record["text"] = "[record omitted]"; record["content_omitted"] = true;
                state.Omitted++; state.Gap("serialized_record_too_large");
                size = Encoding.UTF8.GetByteCount(record.ToString(Formatting.None));
            }
            if (output + size > RecordBudget) { state.Pending = record; break; }
            records.Add(record); output += size; state.Returned++;
            if (DateTimeOffset.TryParse(record["timestamp"]?.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset stamp))
            { first = Min(first, stamp); last = Max(last, stamp); }
        }
        bool complete = state.Terminal && state.Gaps.Count == 0;
        return new JObject
        {
            ["schema"] = "history_v1", ["source"] = state.Query.Source, ["query_id"] = state.Query.QueryId,
            ["cursor"] = state.Id, ["sequence"] = sequence, ["next_sequence"] = state.Terminal ? null : sequence + 1,
            ["since"] = state.Query.Since.ToString("O"), ["until"] = state.Query.Until.ToString("O"),
            ["records"] = records, ["has_more"] = !state.Terminal, ["complete"] = complete,
            ["terminal_reason"] = state.Terminal ? (complete ? "retained_sources_exhausted" : "retained_sources_exhausted_with_gaps") : "continue",
            ["coverage"] = new JObject
            {
                ["scope"] = "configured_retained_sources", ["consistency"] = "per_file_open_boundary_not_atomic", ["requested_window_complete"] = null,
                ["retention_attested"] = false,
                ["start_boundary"] = state.ObservedMin is null ? "no_timestamped_records" : state.ObservedMin <= state.Query.Since ? "observed_at_or_before_since" : "unproven_before_oldest_observed",
                ["warnings"] = new JArray(state.Warnings.OrderBy(x => x, StringComparer.Ordinal)),
                ["compressed_files_opened"] = state.CompressedFiles, ["gzip_files_verified"] = state.VerifiedArchives, ["gzip_integrity_failures"] = state.ArchiveFailures, ["unknown_time_policy"] = state.Query.UnknownTime, ["unknown_time_excluded"] = state.UnknownExcluded,
                ["page_observed_from"] = TimeJson(first), ["page_observed_to"] = TimeJson(last),
                ["scanned_observed_from"] = TimeJson(state.ObservedMin), ["scanned_observed_to"] = TimeJson(state.ObservedMax),
                ["page_records_scanned"] = state.Scanned - scannedBefore, ["total_records_scanned"] = state.Scanned,
                ["total_records_returned"] = state.Returned, ["total_bytes_scanned"] = state.Bytes,
                ["scan_accounting"] = "source_reads_plus_validated_index", ["index_bytes_reused"] = state.IndexBytes,
                ["index_records_reused"] = state.IndexRecords, ["page_index_records_reused"] = state.IndexRecords - indexedBefore,
                ["indexed_files_skipped"] = state.IndexedFiles, ["gzip_verifications_reused"] = state.IndexedArchives,
                ["files_opened"] = state.Files, ["unknown_time_records"] = state.Unknown, ["omitted_records"] = state.Omitted,
                ["gaps"] = new JArray(state.Gaps.OrderBy(x => x, StringComparer.Ordinal)), ["sources_exhausted"] = state.Terminal
            }
        };
    }

    private bool Open(CursorState state, string path)
    {
        try
        {
            CompanionConfiguration.ValidateNoSymlinkComponents(path);
            if (!CompanionFileAccess.TryOpenRegularFile(path, false, out FileStream? stream, out CompanionFileMetadata metadata) || stream is null)
            { state.Gap("file_unavailable"); return false; }
            state.Stream = stream; state.Metadata = metadata; state.Upper = metadata.Length; state.Position = 0; state.LineStart = 0;
            state.BufferCount = state.BufferIndex = 0; state.ArchiveEndChecked = false; state.Line.Clear(); state.Oversized = false; state.InheritedTime = null;
            state.FileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_profile.Name + "|" + state.Query.Source + "|" + metadata.Device + "|" + metadata.Inode))).ToLowerInvariant()[..24];
            bool gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            state.Files++;
            HistoryTimeIndex.Entry? indexed = _index.Get(state.FileId, metadata);
            if (indexed is not null && indexed.Gzip == gzip && indexed.Outside(state.Query) &&
                CompanionFileAccess.TryReadMetadata(stream.SafeFileHandle, false, out var afterLookup) && afterLookup == metadata)
            {
                ReuseIndex(state, indexed.Bytes, indexed.Records, indexed.Minimum, indexed.Maximum);
                state.IndexedFiles++;
                if (gzip) { state.CompressedFiles++; state.VerifiedArchives++; state.IndexedArchives++; }
                state.CloseFile(); return false;
            }
            state.Index = !gzip && indexed is { Blocks.Length: > 0 } ? indexed : null;
            state.IndexBlock = 0;
            state.IndexBuilder = indexed is null && HistoryTimeIndex.Eligible(metadata) ? new HistoryTimeIndex.Builder(metadata, gzip) : null;
            if (gzip)
            {
                try { state.Compressed = new HistoryCompressedInput(stream, metadata.Length); state.CompressedFiles++; }
                catch (InvalidDataException) { state.Gap("gzip_invalid"); state.ArchiveFailures++; state.CloseFile(); return false; }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { state.Gap("file_blocked"); return false; }
    }

    private void FinishIndex(CursorState state)
    {
        if (state.IndexBuilder is null || state.Stream is null ||
            state.Compressed is not null && !state.Compressed.IntegrityVerified ||
            !CompanionFileAccess.TryReadMetadata(state.Stream.SafeFileHandle, false, out var current) || current != state.Metadata) return;
        HistoryTimeIndex.Entry? entry = state.IndexBuilder.Finish();
        if (entry is not null) _index.Put(state.FileId, entry);
    }

    private static bool SkipIndexedBlock(CursorState state)
    {
        if (state.Index is null) return false;
        var blocks = state.Index.Blocks;
        while (state.IndexBlock < blocks.Length && state.Position >= blocks[state.IndexBlock].End) state.IndexBlock++;
        if (state.IndexBlock >= blocks.Length) return false;
        var block = blocks[state.IndexBlock];
        if (state.Position != block.Start || state.Line.Count != 0 || state.Oversized || !block.Outside(state.Query)) return false;
        state.Stream!.Seek(block.End, SeekOrigin.Begin);
        state.BufferCount = state.BufferIndex = 0; state.Position = state.LineStart = block.End;
        state.InheritedTime = block.InheritedAfter;
        ReuseIndex(state, block.End - block.Start, block.Records, block.Minimum, block.Maximum);
        state.IndexBlock++;
        return true;
    }

    private static void ReuseIndex(CursorState state, long bytes, long records, DateTimeOffset minimum, DateTimeOffset maximum)
    {
        state.Bytes += bytes; state.Scanned += records; state.IndexBytes += bytes; state.IndexRecords += records;
        state.ObservedMin = Min(state.ObservedMin, minimum); state.ObservedMax = Max(state.ObservedMax, maximum);
    }

    private static bool ReadLine(CursorState state, ref int input, out string? line, out long offset, out string? omission)
    {
        line = null; omission = null; offset = state.LineStart;
        while (input < MaximumPageInput)
        {
            if (state.BufferIndex >= state.BufferCount)
            {
                int count = state.Compressed is not null ? state.Buffer.Length : (int)Math.Min(state.Buffer.Length, state.Upper - state.Position);
                if (count == 0)
                {
                    if (state.Line.Count == 0 && !state.Oversized) return true;
                    break;
                }
                state.BufferCount = state.Compressed is not null ? state.Compressed.Read(state.Buffer) : state.Stream!.Read(state.Buffer, 0, count);
                state.BufferIndex = 0;
                if (state.BufferCount == 0)
                {
                    if (state.Compressed is null) throw new IOException("History file changed.");
                    if (!state.ArchiveEndChecked)
                    {
                        state.ArchiveEndChecked = true;
                        if (state.Compressed.IntegrityVerified) state.VerifiedArchives++;
                        else { state.ArchiveFailures++; state.Gap("gzip_integrity_failed_or_unsupported"); }
                    }
                    if (!state.Compressed.IntegrityVerified && (state.Line.Count != 0 || state.Oversized))
                    {
                        // A damaged stream can end midway through a UTF-8/log record. Never
                        // publish that fragment as ordinary evidence, even if it has a timestamp.
                        omission = "gzip_unverified_partial_record";
                        state.Line.Clear(); state.Oversized = false; state.LineStart = state.Position;
                        return true;
                    }
                    if (state.Line.Count == 0 && !state.Oversized) return true;
                    break;
                }
            }
            byte next = state.Buffer[state.BufferIndex++]; state.Position++; state.Bytes++; input++;
            if (next == (byte)'\n') break;
            if (state.Line.Count < MaximumRecordBytes) state.Line.Add(next);
            else state.Oversized = true;
            if (input == MaximumPageInput) return false;
        }
        if (state.Oversized) omission = "oversized_record";
        else
        {
            try { line = Utf8.GetString(state.Line.ToArray()).TrimEnd('\r'); }
            catch (DecoderFallbackException) { omission = "invalid_utf8"; }
        }
        state.Line.Clear(); state.Oversized = false; state.LineStart = state.Position;
        return true;
    }

    private DateTimeOffset? ParseTime(string text, CursorState state)
    {
        string? stamp = null;
        if (text.StartsWith('{') && text.Length <= MaximumRecordBytes)
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 };
                JObject obj = JObject.Load(reader);
                if (obj["ts"]?.Type == JTokenType.String) stamp = obj["ts"]!.Value<string>();
            }
            catch (JsonException) { }
        }
        Match match = DatePrefix.Match(text);
        if (stamp is null && match.Success && match.Length < text.Length && (char.IsDigit(text[match.Length]) || text[match.Length] is '+' or '-' or ':' or '.' or ',')) { state.InheritedTime = null; return null; }
        if (stamp is null && match.Success) stamp = match.Value.Replace(',', '.');
        DateTimeOffset? result = null;
        if (DiagnosticHistoryQuery.TryTime(stamp, out DateTimeOffset explicitTime)) result = explicitTime;
        else if (stamp is not null && _profile.HistoryTimeZone is not null && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (!_profile.HistoryTimeZone.IsInvalidTime(local) && !_profile.HistoryTimeZone.IsAmbiguousTime(local)) result = new DateTimeOffset(local, _profile.HistoryTimeZone.GetUtcOffset(local));
        }
        else if (stamp is null && (text.StartsWith("at ", StringComparison.Ordinal) || text.Length > 0 && char.IsWhiteSpace(text[0]))) result = state.InheritedTime;
        if (stamp is not null || !(text.StartsWith("at ", StringComparison.Ordinal) || text.Length > 0 && char.IsWhiteSpace(text[0]))) state.InheritedTime = result;
        return result;
    }

    private IEnumerable<string?> Discover(CursorState state)
    {
        bool logs = state.Query.Source == "logs";
        IReadOnlyList<string> configuredRoots = logs ? _profile.LogRoots : _profile.GuardianHistoryRoots;
        IReadOnlyList<string> globs = logs ? _profile.LogGlobs : _profile.GuardianHistoryGlobs;
        string? single = logs ? null : _profile.GuardianEventPath;
        if (single is not null) yield return single;
        if (single is null && configuredRoots.Count == 0) state.Gap("source_not_configured");
        string[] roots = configuredRoots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string root in roots.Where(r => !roots.Any(other => other != r && r.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
        {
            var stack = new Stack<DirectoryFrame>();
            DirectoryFrame? initial = TryDirectory(root, state);
            if (initial is null) { yield return null; continue; }
            stack.Push(initial);
            try
            {
                while (stack.Count > 0)
                {
                    DirectoryFrame frame = stack.Peek();
                    bool moved;
                    try { moved = frame.Entries.MoveNext(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { state.Gap("directory_unavailable"); moved = false; }
                    if (!moved)
                    {
                        if (Directory.GetLastWriteTimeUtc(frame.Path) != frame.Modified) state.Gap("directory_changed");
                        frame.Entries.Dispose(); stack.Pop(); yield return null; continue;
                    }
                    string path = frame.Entries.Current;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { state.Gap("entry_unavailable"); attributes = FileAttributes.ReparsePoint; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { state.Gap("symlink_skipped"); yield return null; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (stack.Count >= 64) state.Gap("directory_depth_resource_limit");
                        else
                        {
                            DirectoryFrame? child = TryDirectory(path, state);
                            if (child is not null) stack.Push(child);
                        }
                        yield return null;
                    }
                    else if (path != single && globs.Any(g => FileSystemName.MatchesSimpleExpression(g, Path.GetFileName(path)) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && FileSystemName.MatchesSimpleExpression(g, Path.GetFileName(path).AsSpan(0, Path.GetFileName(path).Length - 3)))) yield return path;
                    else yield return null;
                }
            }
            finally { foreach (DirectoryFrame frame in stack) frame.Entries.Dispose(); }
        }
    }

    private static DirectoryFrame? TryDirectory(string path, CursorState state)
    {
        try
        {
            CompanionConfiguration.ValidateNoSymlinkComponents(path);
            return new DirectoryFrame(path, Directory.GetLastWriteTimeUtc(path), Directory.EnumerateFileSystemEntries(path).GetEnumerator());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { state.Gap("directory_unavailable"); return null; }
    }

    private static JToken TimeJson(DateTimeOffset? time) => time.HasValue ? new JValue(time.Value.ToString("O", CultureInfo.InvariantCulture)) : JValue.CreateNull();

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;
    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (CursorState cursor in _cursors.Values) { lock (cursor.Gate) cursor.Dispose(); }
            _cursors.Clear(); _index.Clear(); _disposed = true;
        }
    }

    private sealed record DirectoryFrame(string Path, DateTime Modified, IEnumerator<string> Entries);
    private sealed class CursorState : IDisposable
    {
        internal CursorState(DiagnosticHistoryQuery query, DateTimeOffset now) { Query = query; LastUsed = now; }
        internal readonly object Gate = new();
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly DiagnosticHistoryQuery Query;
        internal DateTimeOffset LastUsed;
        internal long LastSequence = -1;
        internal JObject? LastPage, Pending;
        internal volatile bool Terminal, Closed;
        internal bool Oversized, ArchiveEndChecked;
        internal IEnumerator<string?>? Paths;
        internal FileStream? Stream;
        internal HistoryCompressedInput? Compressed;
        internal HistoryTimeIndex.Entry? Index;
        internal HistoryTimeIndex.Builder? IndexBuilder;
        internal int IndexBlock;
        internal CompanionFileMetadata Metadata;
        internal long Upper, Position, LineStart, Scanned, Returned, Bytes, Files, Unknown, Omitted, CompressedFiles, UnknownExcluded, VerifiedArchives, ArchiveFailures, IndexBytes, IndexRecords, IndexedFiles, IndexedArchives;
        internal readonly byte[] Buffer = new byte[32 * 1024];
        internal int BufferIndex, BufferCount;
        internal readonly List<byte> Line = new(MaximumRecordBytes);
        internal string FileId = string.Empty;
        internal DateTimeOffset? InheritedTime, ObservedMin, ObservedMax;
        internal readonly HashSet<string> Gaps = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Warnings = new(StringComparer.Ordinal);
        internal void Gap(string code) { Gaps.Add(code); IndexBuilder = null; }
        internal void CloseFile() { Index = null; IndexBuilder = null; Compressed?.Dispose(); Compressed = null; Stream?.Dispose(); Stream = null; Line.Clear(); Oversized = false; }
        internal void CloseSources() { CloseFile(); Paths?.Dispose(); Paths = null; }
        public void Dispose() { Closed = true; CloseSources(); }
    }
}

/// <summary>Safe, non-secret history refusal code.</summary>
public sealed class HistoryReadException : Exception
{
    /// <summary>Creates a refusal without source paths or request contents.</summary>
    public HistoryReadException(string code) : base(code) { }
}
