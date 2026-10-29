using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Reads only startup-configured log roots and basename globs. No request value
/// is interpreted as a path, pattern, process name, or filesystem selector.
/// </summary>
public sealed class BoundedLogReader
{
    /// <summary>Largest total input consumed while gathering one log response.</summary>
    public const int MaximumInputBytes = 512 * 1024;

    /// <summary>Largest number of sanitized log lines returned in one response.</summary>
    public const int MaximumLines = 500;

    private const int MaximumCandidates = 128;
    private const int MaximumDirectories = 1024;
    private const int MaximumEntries = 512;
    private const int MaximumQueuedDirectories = 128;
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly CompanionProfileConfiguration _profile;

    /// <summary>Creates a reader bound permanently to one configured profile.</summary>
    public BoundedLogReader(CompanionProfileConfiguration profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>Collects bounded, redacted evidence from configured log sources.</summary>
    public JObject Read(CancellationToken cancellationToken, int maximumLines = MaximumLines, string? grep = null, TimeSpan? deadline = null)
    {
        if (maximumLines is < 1 or > MaximumLines || (grep is not null && (grep.Length == 0 || Encoding.UTF8.GetByteCount(grep) > 512)))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLines));
        }

        TimeSpan limit = deadline.GetValueOrDefault(DefaultDeadline);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(10))
        {
            limit = DefaultDeadline;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        var result = new JObject
        {
            ["source"] = "logs",
            ["status"] = "missing",
            ["files"] = new JArray(),
            ["lines"] = new JArray(),
            ["inputTruncated"] = false,
            ["lineTruncated"] = false,
            ["responseTruncated"] = false,
            ["searchTruncated"] = false,
            ["truncated"] = false,
            ["multilineContinuationLines"] = 0
        };
        var files = (JArray)result["files"]!;
        var lines = (JArray)result["lines"]!;
        var candidates = new List<LogCandidate>();
        int deniedRoots = 0;
        int missingRoots = 0;
        int remainingEntries = MaximumEntries;
        bool traversalTruncated = false;

        foreach (string root in _profile.LogRoots)
        {
            if (Expired(stopwatch, limit, cancellationToken))
            {
                result["status"] = "deadline";
                result["inputTruncated"] = true;
                result["searchTruncated"] = grep is not null;
                result["truncated"] = true;
                EnforceResponseCap(result);
                return result;
            }

            RootEnumeration enumeration = EnumerateRoot(root, ref remainingEntries, cancellationToken, stopwatch, limit);
            traversalTruncated |= enumeration.Truncated;
            if (enumeration.Status == "denied")
            {
                deniedRoots++;
            }
            else if (enumeration.Status == "missing")
            {
                missingRoots++;
            }
            candidates.AddRange(enumeration.Candidates);
            if (candidates.Count >= MaximumCandidates)
            {
                result["inputTruncated"] = true;
                break;
            }
        }
        result["traversalTruncated"] = traversalTruncated;
        candidates = candidates.Select((candidate, index) => candidate with { Label = "log-" + (index + 1).ToString(CultureInfo.InvariantCulture) }).ToList();

        if (candidates.Count == 0)
        {
            result["status"] = deniedRoots > 0 && deniedRoots + missingRoots == _profile.LogRoots.Count ? "denied" : "missing";
            EnforceResponseCap(result);
            return result;
        }

        int remainingBytes = MaximumInputBytes;
        int continuationLines = 0;
        bool lineTruncated = false;
        bool lineLimitTruncated = false;
        bool inputTruncated = false;
        bool searchTruncated = false;
        foreach (LogCandidate candidate in candidates
            .OrderByDescending(item => item.LastWriteUtc)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(MaximumCandidates))
        {
            if (Expired(stopwatch, limit, cancellationToken) || lines.Count >= MaximumLines || remainingBytes <= 0)
            {
                inputTruncated = true;
                break;
            }

            CompanionFileRead fileRead = CompanionFileRead.Read(candidate.Path, remainingBytes, cancellationToken, stopwatch, limit);
            if (fileRead.Status == "denied")
            {
                files.Add(FileMetadata("denied", candidate, null, false));
                continue;
            }
            if (fileRead.Status != "available")
            {
                files.Add(FileMetadata(fileRead.Status, candidate, null, false));
                continue;
            }

            remainingBytes -= fileRead.BytesRead;
            inputTruncated |= fileRead.Truncated;
            string state = IsRotated(candidate.Name) ? "rotated" : "active";
            // Rotation can replace a path after enumeration. Attribute freshness
            // to the opened file that supplied these bytes, not the earlier entry.
            bool? stale = fileRead.ModifiedUtc.HasValue ? DateTimeOffset.UtcNow - fileRead.ModifiedUtc.Value > StaleAfter : null;
            files.Add(FileMetadata(state, candidate, stale, fileRead.Truncated, fileRead.ModifiedUtc));
            int remainingLines = maximumLines - lines.Count;
            string[] captured = SplitTailLines(fileRead.Text, fileRead.StartedMidFile, int.MaxValue, out bool partialLine, out int continuationCount);
            continuationLines += continuationCount;
            lineTruncated |= partialLine;
            IEnumerable<string> selected = captured;
            if (grep is not null)
            {
                string[] matches = captured.Where(line => line.Contains(grep, StringComparison.OrdinalIgnoreCase)).ToArray();
                searchTruncated |= fileRead.Truncated || matches.Length > remainingLines;
                selected = matches.TakeLast(remainingLines);
            }
            else
            {
                lineLimitTruncated |= captured.Length > remainingLines;
                selected = captured.TakeLast(remainingLines);
            }
            foreach (string line in selected)
            {
                lines.Add(DiagnosticReplyValidator.SanitizeEvidence(line));
            }
        }

        result["status"] = files.OfType<JObject>().Any(file => file["state"]?.Value<string>() is "active" or "rotated") ? "active" : "denied";
        bool inputWasTruncated = result["inputTruncated"]!.Value<bool>() || inputTruncated;
        result["inputTruncated"] = inputWasTruncated;
        result["lineTruncated"] = lineTruncated || lineLimitTruncated;
        result["multilineContinuationLines"] = continuationLines;
        result["searchTruncated"] = grep is not null && (searchTruncated || inputWasTruncated || traversalTruncated);
        EnforceResponseCap(result);
        result["truncated"] = result["inputTruncated"]!.Value<bool>() || result["traversalTruncated"]!.Value<bool>() || result["lineTruncated"]!.Value<bool>() || result["responseTruncated"]!.Value<bool>() || result["searchTruncated"]!.Value<bool>();
        return result;
    }

    private bool MatchesConfiguredGlob(string name)
    {
        return _profile.LogGlobs.Any(glob => FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: OperatingSystem.IsWindows()));
    }

    private static bool IsRotated(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".old", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) || (extension.Length > 1 && extension[1..].All(char.IsDigit));
    }

    private static JObject FileMetadata(string state, LogCandidate candidate, bool? stale, bool truncated, DateTimeOffset? modifiedUtc = null)
    {
        var metadata = new JObject
        {
            ["label"] = candidate.Label,
            ["state"] = state,
            ["truncated"] = truncated
        };
        if (stale.HasValue)
        {
            metadata["freshness"] = stale.Value ? "stale" : "fresh";
            metadata["fileModifiedAtUtc"] = modifiedUtc;
        }
        return metadata;
    }

    private RootEnumeration EnumerateRoot(string configuredRoot, ref int remainingEntries, CancellationToken cancellationToken, Stopwatch stopwatch, TimeSpan deadline)
    {
        try
        {
            string canonicalRoot = CanonicalExistingDirectory(configuredRoot);
            var pending = new Queue<string>();
            pending.Enqueue(canonicalRoot);
            var visited = new HashSet<string>(PathComparer);
            var files = new List<LogCandidate>();
            int directories = 0;
            bool truncated = false;
            while (pending.Count > 0 && directories < MaximumDirectories && files.Count < MaximumCandidates && !Expired(stopwatch, deadline, cancellationToken))
            {
                string current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }
                directories++;
                foreach (string path in Directory.EnumerateFileSystemEntries(current, "*", new EnumerationOptions { RecurseSubdirectories = false, ReturnSpecialDirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
                {
                    if (Expired(stopwatch, deadline, cancellationToken))
                    {
                        return new RootEnumeration("deadline", files, true);
                    }
                    if (remainingEntries-- <= 0)
                    {
                        return new RootEnumeration("available", files, true);
                    }
                    if (CompanionFileRead.HasSymlinkComponent(path))
                    {
                        truncated = true;
                        continue;
                    }
                    FileAttributes attributes = File.GetAttributes(path);
                    string canonical = Path.GetFullPath(path);
                    if (!IsWithin(canonicalRoot, canonical))
                    {
                        truncated = true;
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (pending.Count < MaximumQueuedDirectories)
                        {
                            pending.Enqueue(canonical);
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                    else
                    {
                        var info = new FileInfo(canonical);
                        if (MatchesConfiguredGlob(info.Name))
                        {
                            files.Add(new LogCandidate(canonical, info.Name, string.Empty, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                            if (files.Count >= MaximumCandidates)
                            {
                                return new RootEnumeration("available", files, true);
                            }
                        }
                    }
                }
            }
            return new RootEnumeration("available", files, truncated || pending.Count > 0 || directories >= MaximumDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            return new RootEnumeration("missing", Array.Empty<LogCandidate>(), false);
        }
        catch (FileNotFoundException)
        {
            return new RootEnumeration("missing", Array.Empty<LogCandidate>(), false);
        }
        catch (UnauthorizedAccessException)
        {
            return new RootEnumeration("denied", Array.Empty<LogCandidate>(), false);
        }
        catch (InvalidOperationException)
        {
            return new RootEnumeration("blocked", Array.Empty<LogCandidate>(), true);
        }
        catch (IOException)
        {
            return new RootEnumeration("blocked", Array.Empty<LogCandidate>(), true);
        }
    }

    private static string CanonicalExistingDirectory(string path)
    {
        CompanionConfiguration.ValidateNoSymlinkComponents(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException();
        }
        return CanonicalExistingEntry(directory);
    }

    private static string CanonicalExistingEntry(FileSystemInfo entry)
    {
        FileSystemInfo resolved = entry.LinkTarget is null ? entry : entry.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException();
        return Path.GetFullPath(resolved.FullName);
    }

    private static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Expired(Stopwatch stopwatch, TimeSpan deadline, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested || stopwatch.Elapsed > deadline;
    }

    private static string[] SplitTailLines(string text, bool startedMidFile, int capacity, out bool partialLine, out int continuationLines)
    {
        partialLine = startedMidFile;
        continuationLines = 0;
        string[] values = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int first = startedMidFile && values.Length > 0 ? 1 : 0;
        IEnumerable<string> tail = values.Skip(first).Where(value => value.Length > 0).TakeLast(capacity);
        var output = new List<string>();
        foreach (string value in tail)
        {
            if (value.Length > 16 * 1024)
            {
                output.Add(value[..(16 * 1024)] + " [truncated]");
                partialLine = true;
            }
            else
            {
                output.Add(value);
            }
            if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || value.StartsWith("at ", StringComparison.Ordinal)))
            {
                continuationLines++;
            }
        }
        return output.ToArray();
    }

    private static void EnforceResponseCap(JObject result)
    {
        JArray lines = (JArray)result["lines"]!;
        int limit = 48 * 1024;
        while (lines.Count > 0 && Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > limit)
        {
            lines.RemoveAt(0);
            result["responseTruncated"] = true;
        }
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > limit)
        {
            result["files"] = new JArray();
            result["lines"] = new JArray();
            result["responseTruncated"] = true;
        }
    }

    private sealed record RootEnumeration(string Status, IReadOnlyList<LogCandidate> Candidates, bool Truncated);
    private sealed record LogCandidate(string Path, string Name, string Label, DateTimeOffset LastWriteUtc);
}

internal sealed class CompanionFileRead
{
    private static readonly Encoding TolerantUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private CompanionFileRead(string status, string text, int bytesRead, bool truncated, bool startedMidFile, DateTimeOffset? modifiedUtc)
    {
        Status = status;
        Text = text;
        BytesRead = bytesRead;
        Truncated = truncated;
        StartedMidFile = startedMidFile;
        ModifiedUtc = modifiedUtc;
    }

    internal string Status { get; }
    internal string Text { get; }
    internal int BytesRead { get; }
    internal bool Truncated { get; }
    internal bool StartedMidFile { get; }
    internal DateTimeOffset? ModifiedUtc { get; }

    internal static CompanionFileRead Read(string path, int maximumBytes, CancellationToken cancellationToken, Stopwatch stopwatch, TimeSpan deadline)
    {
        try
        {
            if (HasSymlinkComponent(path) || !CompanionFileAccess.TryOpenRegularFile(path, requireOwnerOnlyPermissions: false, out FileStream? stream, out CompanionFileMetadata metadata) || stream is null)
            {
                return new CompanionFileRead("blocked", string.Empty, 0, false, false, null);
            }
            using (stream)
            {
                int count = checked((int)Math.Min(metadata.Length, Math.Max(0, maximumBytes)));
                bool truncated = metadata.Length > count;
                if (truncated)
                {
                    stream.Seek(-count, SeekOrigin.End);
                }
                var bytes = new byte[count];
                int offset = 0;
                while (offset < count && !cancellationToken.IsCancellationRequested && stopwatch.Elapsed <= deadline)
                {
                    int read = stream.Read(bytes, offset, count - offset);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }
                bool deadlineExceeded = offset < count;
                return new CompanionFileRead(deadlineExceeded ? "deadline" : "available", TolerantUtf8.GetString(bytes, 0, offset), offset, truncated || deadlineExceeded, truncated, metadata.ModifiedUtc);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new CompanionFileRead("denied", string.Empty, 0, false, false, null);
        }
        catch (IOException)
        {
            return new CompanionFileRead("missing", string.Empty, 0, false, false, null);
        }
    }

    internal static bool HasSymlinkComponent(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        return IsSymlink(parent) || IsSymlink(fullPath);
    }

    private static bool IsSymlink(string? path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return false;
        }
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

}
