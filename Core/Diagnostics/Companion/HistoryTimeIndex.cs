using System;
using System.Collections.Generic;
using System.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>Bounded, memory-only time summaries of fully read, unchanged source generations.</summary>
internal sealed class HistoryTimeIndex
{
    internal const int MaximumFiles = 128;
    internal const int MaximumBlocks = 8192;
    internal const int MaximumFileBlocks = 1024;
    internal const int BlockBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Entry Value, long Use)> _entries = new(StringComparer.Ordinal);
    private long _clock;
    private int _blocks;

    internal static bool Eligible(CompanionFileMetadata metadata) => metadata.LinkCount == 1 && metadata.ModifiedUtc is not null && metadata.ChangedSeconds > 0 && metadata.ChangedNanoseconds is >= 0 and < 1000000000;

    internal Entry? Get(string id, CompanionFileMetadata metadata)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var found)) return null;
            if (!Eligible(metadata) || found.Value.Metadata != metadata) { Remove(id); return null; }
            _entries[id] = (found.Value, ++_clock);
            return found.Value;
        }
    }

    internal void Put(string id, Entry entry)
    {
        if (!Eligible(entry.Metadata)) return;
        lock (_gate)
        {
            Remove(id);
            while (_entries.Count >= MaximumFiles || _blocks + entry.Blocks.Length > MaximumBlocks)
                Remove(_entries.MinBy(pair => pair.Value.Use).Key);
            _entries.Add(id, (entry, ++_clock)); _blocks += entry.Blocks.Length;
        }
    }

    private void Remove(string id)
    {
        if (_entries.Remove(id, out var old)) _blocks -= old.Value.Blocks.Length;
    }

    internal void Clear() { lock (_gate) { _entries.Clear(); _blocks = 0; } }

    internal sealed record Entry(CompanionFileMetadata Metadata, long Bytes, long Records, DateTimeOffset Minimum, DateTimeOffset Maximum, bool Gzip, Block[] Blocks)
    {
        internal bool Outside(DiagnosticHistoryQuery query) => Maximum < query.Since || Minimum >= query.Until;
    }

    internal readonly record struct Block(long Start, long End, long Records, DateTimeOffset Minimum, DateTimeOffset Maximum, DateTimeOffset? InheritedBefore, DateTimeOffset? InheritedAfter)
    {
        internal bool Outside(DiagnosticHistoryQuery query) => Maximum < query.Since || Minimum >= query.Until;
    }

    internal sealed class Builder(CompanionFileMetadata metadata, bool gzip)
    {
        private readonly List<Block> _blocks = new();
        private readonly bool _gzip = gzip;
        private bool _fileOnly = gzip;
        private long _records, _blockRecords, _start, _end;
        private DateTimeOffset? _minimum, _maximum, _blockMinimum, _blockMaximum, _before, _after;

        internal void Add(long start, long end, DateTimeOffset timestamp, DateTimeOffset? before, DateTimeOffset? after)
        {
            _records++; _end = end;
            _minimum = Min(_minimum, timestamp); _maximum = Max(_maximum, timestamp);
            if (_fileOnly) return;
            if (_blockRecords == 0) { _start = start; _before = before; }
            _blockRecords++; _after = after;
            _blockMinimum = Min(_blockMinimum, timestamp); _blockMaximum = Max(_blockMaximum, timestamp);
            if (end - _start >= BlockBytes) Flush();
        }

        private void Flush()
        {
            if (_blockRecords == 0 || _fileOnly) return;
            if (_blocks.Count >= MaximumFileBlocks) { _blocks.Clear(); _fileOnly = true; return; }
            _blocks.Add(new Block(_start, _end, _blockRecords, _blockMinimum!.Value, _blockMaximum!.Value, _before, _after));
            _blockRecords = 0; _blockMinimum = _blockMaximum = null;
        }

        internal Entry? Finish()
        {
            if (_minimum is null || _maximum is null) return null;
            Flush();
            return new Entry(metadata, _end, _records, _minimum.Value, _maximum.Value, _gzip, _blocks.ToArray());
        }
        private static DateTimeOffset Min(DateTimeOffset? a, DateTimeOffset b) => a is null || b < a ? b : a.Value;
        private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is null || b > a ? b : a.Value;
    }
}
