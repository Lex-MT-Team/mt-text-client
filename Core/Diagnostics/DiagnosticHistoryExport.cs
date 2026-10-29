using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTextClient.Core.Diagnostics.Companion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Streams sanitized history pages to a private crash-resumable local directory.</summary>
public static class DiagnosticHistoryExport
{
    /// <summary>Collects within one invocation deadline; repeating the same directory and query resumes without a total-depth cap.</summary>
    public static async Task<JObject> CollectAsync(IDiagnosticExecutor executor, ServerProfile profile, DiagnosticRoute route, DiagnosticIdentityEvidence identity, DiagnosticHistoryQuery original, TimeSpan timeout, TimeSpan deadline, string directory, CancellationToken cancellationToken)
    {
        if (!original.IsValid || original.Cursor is not null || original.Sequence != 0 || deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(60)) throw new ArgumentException("Invalid history export query.");
        string root = Path.GetFullPath(directory);
        ValidateDirectory(root);
        using var writerLock = AcquireWriter(root);
        string manifestPath = Path.Combine(root, "manifest.json");
        string routeBinding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Name + "|" + route.Address + "|" + route.Port))).ToLowerInvariant();
        JObject manifest;
        if (File.Exists(manifestPath))
        {
            manifest = ReadJson(manifestPath);
            if (manifest["schema"]?.Value<string>() != "history_export_v1" || manifest["next_sequence"]?.Type != JTokenType.Integer || manifest["next_sequence"]!.Value<long>() < 0 || manifest["next_sequence"]!.Value<long>() >= long.MaxValue || manifest["exhausted"]?.Type != JTokenType.Boolean ||
                (manifest["next_sequence"]!.Value<long>() == 0 ? manifest["cursor"]?.Type != JTokenType.Null || manifest["exhausted"]!.Value<bool>() : !DiagnosticHistoryQuery.IsId(manifest["cursor"]?.Value<string>())) ||
                manifest["profile"]?.Value<string>() != profile.Name || manifest["route_binding"]?.Value<string>() != routeBinding || manifest["query"] is not JObject stored || !DiagnosticHistoryQuery.TryParse(stored, out DiagnosticHistoryQuery? previous) || previous != original)
                throw new InvalidOperationException("History output belongs to a different query or route.");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".writer.lock")) throw new InvalidOperationException("History output directory must be empty for a new query.");
            manifest = new JObject { ["schema"] = "history_export_v1", ["profile"] = profile.Name, ["route_binding"] = routeBinding, ["query"] = original.ToArgs(), ["next_sequence"] = 0L, ["cursor"] = null, ["exhausted"] = false };
            WriteAtomic(manifestPath, manifest, false);
        }
        var watch = Stopwatch.StartNew();
        long pagesThisRun = 0;
        string reason = "deadline-exceeded";
        string? stopScope = "collection";
        while (watch.Elapsed < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (manifest["exhausted"]!.Value<bool>()) { reason = "exhausted"; break; }
            long sequence = manifest["next_sequence"]!.Value<long>();
            var query = original with { Cursor = manifest["cursor"]?.Value<string>(), Sequence = sequence };
            if (!query.IsValid) throw new InvalidOperationException("History checkpoint is invalid.");
            string pagePath = Path.Combine(root, "page-" + sequence.ToString("D19", CultureInfo.InvariantCulture) + ".ndjson");
            JObject page;
            if (File.Exists(pagePath))
            {
                // A crash after page rename but before manifest rename is safe: adopt the
                // exact already committed page instead of appending it a second time.
                page = ReadJson(pagePath);
                if (!ValidatePage(page, query)) throw new InvalidOperationException("History committed page is invalid.");
            }
            else
            {
                TimeSpan remaining = deadline - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                DiagnosticResult result = await executor.ExecuteAsync(profile, route, DiagnosticRequest.HistoryRequest(query), identity, remaining < timeout ? remaining : timeout, cancellationToken).ConfigureAwait(false);
                if (result.Kind != DiagnosticResultKind.Success)
                {
                    reason = result.Kind == DiagnosticResultKind.Timeout ? "deadline-exceeded" : result.Error ?? result.Code;
                    stopScope = result.Kind == DiagnosticResultKind.Timeout ? (remaining <= timeout ? "collection" : "request") : null;
                    break;
                }
                page = result.Data as JObject ?? throw new InvalidOperationException("History response is invalid.");
                if (!ValidatePage(page, query)) throw new InvalidOperationException("History response is invalid.");
                WriteAtomic(pagePath, page, false);
            }
            if (manifest["coverage"] is JObject prior)
                foreach (string counter in new[] { "total_records_scanned", "total_records_returned", "total_bytes_scanned", "files_opened", "unknown_time_records", "omitted_records" })
                    if (prior[counter]?.Type != JTokenType.Integer || page["coverage"]![counter]!.Value<long>() < prior[counter]!.Value<long>()) throw new InvalidOperationException("History counters regressed.");
            if (manifest["coverage"] is JObject indexedPrior && indexedPrior["scan_accounting"] is not null)
                foreach (string counter in new[] { "index_bytes_reused", "index_records_reused", "indexed_files_skipped", "gzip_verifications_reused" })
                    if (page["coverage"]![counter]?.Type != JTokenType.Integer || page["coverage"]![counter]!.Value<long>() < indexedPrior[counter]!.Value<long>()) throw new InvalidOperationException("History index counters regressed.");
            manifest["cursor"] = page["cursor"]!.DeepClone();
            manifest["next_sequence"] = sequence + 1;
            manifest["exhausted"] = !page["has_more"]!.Value<bool>();
            manifest["complete"] = page["complete"]!.DeepClone();
            manifest["coverage"] = page["coverage"]!.DeepClone();
            WriteAtomic(manifestPath, manifest, true);
            pagesThisRun++;
        }
        if (manifest["exhausted"]!.Value<bool>())
        {
            reason = "exhausted"; stopScope = null;
            // Closing is best effort; a missed close is still reclaimed by the idle lease.
            if (!cancellationToken.IsCancellationRequested && deadline - watch.Elapsed > TimeSpan.Zero)
            {
                var closeQuery = original with { Cursor = manifest["cursor"]!.Value<string>(), Sequence = Math.Max(0, manifest["next_sequence"]!.Value<long>() - 1) };
                await executor.ExecuteAsync(profile, route, DiagnosticRequest.HistoryRequest(closeQuery, true), identity, deadline - watch.Elapsed < TimeSpan.FromSeconds(1) ? deadline - watch.Elapsed : TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        else if (cancellationToken.IsCancellationRequested) { reason = "cancelled"; stopScope = "caller"; }
        return new JObject
        {
            ["profile"] = profile.Name, ["query_id"] = original.QueryId, ["pages_committed"] = manifest["next_sequence"]!.DeepClone(),
            ["pages_this_run"] = pagesThisRun, ["has_more"] = !manifest["exhausted"]!.Value<bool>(),
            ["complete"] = manifest["complete"]?.DeepClone() ?? new JValue(false), ["stop_reason"] = reason, ["stop_scope"] = stopScope,
            ["coverage"] = manifest["coverage"]?.DeepClone(),
            ["resume"] = "Repeat the same collect command and private output directory. After cursor expiry/restart, preserve this evidence and start a fresh query in a new directory; record IDs support external deduplication."
        };
    }

    /// <summary>Validates peer history progress before it can advance a local cursor or checkpoint.</summary>
    public static bool ValidatePage(JObject page, DiagnosticHistoryQuery query)
    {
        try
        {
            if (page["schema"]?.Value<string>() != "history_v1" || page["source"]?.Value<string>() != query.Source || page["query_id"]?.Value<string>() != query.QueryId ||
                page["cursor"]?.Type != JTokenType.String || !DiagnosticHistoryQuery.IsId(page["cursor"]!.Value<string>()) || query.Cursor is not null && page["cursor"]!.Value<string>() != query.Cursor ||
                page["sequence"]?.Type != JTokenType.Integer || page["sequence"]!.Value<long>() != query.Sequence ||
                page["has_more"]?.Type != JTokenType.Boolean || page["complete"]?.Type != JTokenType.Boolean ||
                page["since"]?.Type != JTokenType.String || page["until"]?.Type != JTokenType.String ||
                !DiagnosticHistoryQuery.TryTime(page["since"]!.Value<string>(), out DateTimeOffset since) || since != query.Since ||
                !DiagnosticHistoryQuery.TryTime(page["until"]!.Value<string>(), out DateTimeOffset until) || until != query.Until ||
                page["records"] is not JArray records || records.Count > query.PageLines || page["coverage"] is not JObject coverage || coverage["gaps"] is not JArray gaps) return false;
            foreach (string key in new[] { "page_records_scanned", "total_records_scanned", "total_records_returned", "total_bytes_scanned", "files_opened", "unknown_time_records", "omitted_records" })
                if (coverage[key]?.Type != JTokenType.Integer || coverage[key]!.Value<long>() < 0) return false;
            if (coverage["unknown_time_policy"] is not null && coverage["unknown_time_policy"]!.Value<string>() != query.UnknownTime ||
                query.UnknownTime != "include" && coverage["unknown_time_policy"]?.Value<string>() != query.UnknownTime) return false;
            if (coverage["unknown_time_policy"] is not null)
            {
                if (coverage["unknown_time_excluded"]?.Type != JTokenType.Integer || coverage["unknown_time_excluded"]!.Value<long>() != (query.UnknownTime == "report-only" ? coverage["unknown_time_records"]!.Value<long>() : 0)) return false;
            }
            if (coverage["compressed_files_opened"] is not null && (coverage["compressed_files_opened"]!.Type != JTokenType.Integer || coverage["compressed_files_opened"]!.Value<long>() < 0 || coverage["compressed_files_opened"]!.Value<long>() > coverage["files_opened"]!.Value<long>())) return false;
            if (coverage["warnings"] is not null && (coverage["warnings"] is not JArray warnings || warnings.Count > 64 || warnings.Any(w => w.Type != JTokenType.String || w.Value<string>()!.Length > 80))) return false;
            if (coverage["start_boundary"] is not null && coverage["start_boundary"]!.Value<string>() is not ("no_timestamped_records" or "observed_at_or_before_since" or "unproven_before_oldest_observed")) return false;
            foreach (string counter in new[] { "gzip_files_verified", "gzip_integrity_failures" })
                if (coverage[counter] is not null && (coverage[counter]!.Type != JTokenType.Integer || coverage[counter]!.Value<long>() < 0)) return false;
            if (coverage["gzip_files_verified"] is not null && (coverage["compressed_files_opened"]?.Type != JTokenType.Integer || coverage["gzip_files_verified"]!.Value<long>() > coverage["compressed_files_opened"]!.Value<long>())) return false;
            if (coverage["gzip_integrity_failures"]?.Value<long>() > 0 && gaps.Count == 0) return false;
            string[] indexCounters = { "index_bytes_reused", "index_records_reused", "page_index_records_reused", "indexed_files_skipped", "gzip_verifications_reused" };
            if (coverage["scan_accounting"] is not null || indexCounters.Any(key => coverage[key] is not null))
            {
                if (coverage["scan_accounting"]?.Value<string>() != "source_reads_plus_validated_index") return false;
                foreach (string counter in indexCounters)
                    if (coverage[counter]?.Type != JTokenType.Integer || coverage[counter]!.Value<long>() < 0) return false;
                if (coverage["index_bytes_reused"]!.Value<long>() > coverage["total_bytes_scanned"]!.Value<long>() ||
                    coverage["index_records_reused"]!.Value<long>() > coverage["total_records_scanned"]!.Value<long>() - coverage["total_records_returned"]!.Value<long>() ||
                    coverage["page_index_records_reused"]!.Value<long>() > coverage["page_records_scanned"]!.Value<long>() ||
                    coverage["page_index_records_reused"]!.Value<long>() > coverage["index_records_reused"]!.Value<long>() ||
                    coverage["indexed_files_skipped"]!.Value<long>() > coverage["files_opened"]!.Value<long>() ||
                    coverage["gzip_verifications_reused"]!.Value<long>() > coverage["indexed_files_skipped"]!.Value<long>() ||
                    coverage["gzip_files_verified"]?.Type != JTokenType.Integer || coverage["gzip_verifications_reused"]!.Value<long>() > coverage["gzip_files_verified"]!.Value<long>()) return false;
            }
            bool more = page["has_more"]!.Value<bool>();
            if (coverage["sources_exhausted"]?.Type != JTokenType.Boolean || coverage["sources_exhausted"]!.Value<bool>() == more || coverage["requested_window_complete"]?.Type != JTokenType.Null || coverage["retention_attested"]?.Type != JTokenType.Boolean || coverage["retention_attested"]!.Value<bool>() ||
                coverage["scope"]?.Value<string>() != "configured_retained_sources" || coverage["consistency"]?.Value<string>() != "per_file_open_boundary_not_atomic" || gaps.Count > 64 || gaps.Any(g => g.Type != JTokenType.String || g.Value<string>()!.Length > 80)) return false;
            if (page["complete"]!.Value<bool>() != (!more && gaps.Count == 0) || coverage["total_records_returned"]!.Value<long>() < records.Count || coverage["total_records_scanned"]!.Value<long>() < coverage["total_records_returned"]!.Value<long>() ||
                (coverage["unknown_time_records"]!.Value<long>() != 0 || coverage["omitted_records"]!.Value<long>() != 0) && gaps.Count == 0) return false;
            if (more ? page["next_sequence"]?.Type != JTokenType.Integer || page["next_sequence"]!.Value<long>() != query.Sequence + 1 : page["next_sequence"]?.Type != JTokenType.Null) return false;
            if (page["complete"]!.Value<bool>() && (more || gaps.Count != 0)) return false;
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            DateTimeOffset? minimum = null, maximum = null;
            foreach (JToken record in records)
            {
                if (record is not JObject row || row["id"]?.Type != JTokenType.String || row["text"]?.Type != JTokenType.String || row["content_omitted"]?.Type != JTokenType.Boolean || row["source_file"]?.Type != JTokenType.String || row["source_file"]!.Value<string>()!.Length != 24 || row["byte_offset"]?.Type != JTokenType.Integer || row["byte_offset"]!.Value<long>() < 0 || !ids.Add(row["id"]!.Value<string>()!) || row["id"]!.Value<string>()!.Length > 80) return false;
                if (row["source_encoding"] is not null || row["offset_kind"] is not null)
                {
                    string? encoding = row["source_encoding"]?.Value<string>();
                    if (encoding is not ("utf8" or "gzip") || row["offset_kind"]?.Value<string>() != (encoding == "gzip" ? "uncompressed_bytes" : "file_bytes")) return false;
                }
                string file = row["source_file"]!.Value<string>()!;
                if (!file.All(c => char.IsAsciiHexDigit(c)) || row["id"]!.Value<string>() != file + ":" + row["byte_offset"]!.Value<long>().ToString(CultureInfo.InvariantCulture)) return false;
                if (row["time_status"]?.Value<string>() == "known")
                {
                    if (row["timestamp"]?.Type != JTokenType.String || !DiagnosticHistoryQuery.TryTime(row["timestamp"]!.Value<string>(), out DateTimeOffset at) || at < query.Since || at >= query.Until) return false;
                    minimum = minimum is null || at < minimum ? at : minimum; maximum = maximum is null || at > maximum ? at : maximum;
                }
                else if (query.UnknownTime == "report-only" || row["time_status"]?.Value<string>() != "unknown" || row["timestamp"]?.Type != JTokenType.Null) return false;
            }
            foreach (string prefix in new[] { "page", "scanned" })
            {
                var from = coverage[prefix + "_observed_from"]; var to = coverage[prefix + "_observed_to"];
                if (from?.Type == JTokenType.Null && to?.Type == JTokenType.Null) continue;
                if (from?.Type != JTokenType.String || to?.Type != JTokenType.String || !DiagnosticHistoryQuery.TryTime(from.Value<string>(), out var start) || !DiagnosticHistoryQuery.TryTime(to.Value<string>(), out var end) || start > end || prefix == "page" && (start < query.Since || end >= query.Until)) return false;
            }
            if (minimum.HasValue)
            {
                if (!DiagnosticHistoryQuery.TryTime(coverage["page_observed_from"]?.Value<string>(), out var first) || first != minimum || !DiagnosticHistoryQuery.TryTime(coverage["page_observed_to"]?.Value<string>(), out var last) || last != maximum) return false;
            }
            else if (coverage["page_observed_from"]?.Type != JTokenType.Null || coverage["page_observed_to"]?.Type != JTokenType.Null) return false;
            return Encoding.UTF8.GetByteCount(page.ToString(Formatting.None)) <= 48 * 1024;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException) { return false; }
    }

    private static FileStream AcquireWriter(string directory)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Private export requires POSIX.");
        string path = Path.Combine(directory, ".writer.lock");
        try
        {
            using var created = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
        catch (IOException) { /* Existing lock is descriptor-checked below. */ }
        if (!CompanionFileAccess.TryOpenRegularFile(path, true, out FileStream? stream, out _) || stream is null) throw new InvalidOperationException("Unsafe history writer lock.");
        if (flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 6) != 0)
        { stream.Dispose(); throw new InvalidOperationException("History export already has a writer."); }
        return stream; // POSIX releases flock on close, including process exit.
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int descriptor, int operation);

    private static void ValidateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Private history export requires a supported POSIX filesystem.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new DirectoryInfo(current).LinkTarget is not null) throw new InvalidOperationException("History output cannot traverse symlinks.");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw new InvalidOperationException("History output must be owner-only.");
    }

    private static JObject ReadJson(string path)
    {
        if (!CompanionFileAccess.TryOpenRegularFile(path, true, out FileStream? stream, out CompanionFileMetadata metadata) || stream is null || metadata.Length > 64 * 1024)
        { stream?.Dispose(); throw new InvalidOperationException("History output file is unsafe."); }
        using var source = stream;
        using var text = new StreamReader(source, new UTF8Encoding(false, true));
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 };
        JObject value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidOperationException("History output contains trailing data.");
        return value;
    }

    private static void WriteAtomic(string path, JObject value, bool overwrite)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Private export requires POSIX.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None) + "\n");
            using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
