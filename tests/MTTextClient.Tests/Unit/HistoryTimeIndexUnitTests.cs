extern alias companion;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using Index = companion::MTTextClient.Core.Diagnostics.Companion.HistoryTimeIndex;
using Metadata = companion::MTTextClient.Core.Diagnostics.Companion.CompanionFileMetadata;
using Reader = companion::MTTextClient.Core.Diagnostics.Companion.HistoryReader;
using Query = companion::MTTextClient.Core.Diagnostics.DiagnosticHistoryQuery;

namespace MTTextClient.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class HistoryTimeIndexUnitTests
{
    [SkippableFact]
    public void WarmPlainWindowSkipsBlocksButPreservesOutOfOrderAndInheritedTimes()
    {
        using var f = new HistoryReaderUnitTests.Fixture(); var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        File.WriteAllLines(f.Log, Enumerable.Range(0, 30000).Select(i => $"{start.AddSeconds(i):O} row-{i} " + new string('x',180)).Concat(new[] { "2026-01-01T00:01:02Z late-out-of-order", " at inherited-frame" }));
        using var reader = f.Reader(); Drain(reader, f.Query());
        Query narrow = f.Query() with { Since = start.AddMinutes(1), Until = start.AddMinutes(2), PageLines = 5 };
        var warm = Drain(reader, narrow);
        using var coldReader = f.Reader(); var cold = Drain(coldReader, narrow with { QueryId = Guid.NewGuid().ToString("N") });
        warm.Rows.Select(r => r["id"]!.Value<string>()).Should().Equal(cold.Rows.Select(r => r["id"]!.Value<string>()));
        warm.Rows.Should().HaveCount(62); warm.Last["complete"]!.Value<bool>().Should().BeTrue();
        warm.Last["coverage"]!["index_bytes_reused"]!.Value<long>().Should().BeGreaterThan(new FileInfo(f.Log).Length / 2);
        warm.Last["coverage"]!["total_records_scanned"]!.Should().BeEquivalentTo(cold.Last["coverage"]!["total_records_scanned"]);
    }

    [SkippableFact]
    public void WarmArchiveOutsideWindowUsesVerifiedSummaryWithoutDecompression()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        HistoryArchiveUnitTests.Compress(f.Log+".gz", string.Concat(Enumerable.Repeat("2026-01-01T01:00:00Z archive\n", 20000)));
        File.WriteAllText(f.Log,"2026-01-02T01:00:00Z current\n"); using var reader = f.Reader(); Drain(reader,f.Query());
        var warm=Drain(reader,f.Query() with { Since=DateTimeOffset.Parse("2026-01-02T00:00:00Z") });
        warm.Rows.Should().ContainSingle(); warm.Last["coverage"]!["indexed_files_skipped"]!.Value<int>().Should().Be(1);
        warm.Last["coverage"]!["gzip_verifications_reused"]!.Value<int>().Should().Be(1);
        warm.Last["complete"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("append")]
    [InlineData("rewrite-restore-mtime")]
    [InlineData("replace")]
    [InlineData("truncate")]
    [InlineData("permissions")]
    public void ChangedGenerationNeverReusesAnOldFileSummary(string change)
    {
        using var f=new HistoryReaderUnitTests.Fixture(); File.WriteAllText(f.Log,"2026-01-01T01:00:00Z old\n");
        using var reader=f.Reader(); Drain(reader,f.Query()); DateTime stamp=File.GetLastWriteTimeUtc(f.Log);
        if(change=="append") File.AppendAllText(f.Log,"2026-01-02T01:00:00Z new\n");
        else if(change=="permissions") File.SetUnixFileMode(f.Log,UnixFileMode.UserRead);
        else { if(change=="replace") File.Move(f.Log,f.Log+".old"); File.WriteAllText(f.Log,change=="truncate"? "": "2026-01-02T01:00:00Z new\n"); if(change=="rewrite-restore-mtime") File.SetLastWriteTimeUtc(f.Log,stamp); }
        var warm=Drain(reader,f.Query() with { Since=DateTimeOffset.Parse("2026-01-02T00:00:00Z") });
        warm.Last["coverage"]!["index_bytes_reused"]!.Value<long>().Should().Be(0);
        warm.Rows.Should().HaveCount(change is "truncate" or "permissions" ? 0 : 1);
    }

    [SkippableFact]
    public void UnknownTimeAndDamagedArchiveNeverBecomeIndexShortcuts()
    {
        using var f=new HistoryReaderUnitTests.Fixture(); File.WriteAllText(f.Log,"unknown\n2026-01-01T01:00:00Z old\n");
        HistoryArchiveUnitTests.Compress(f.Log+".gz","2026-01-01T01:00:00Z broken"); var bytes=File.ReadAllBytes(f.Log+".gz"); File.WriteAllBytes(f.Log+".gz",bytes[..^6]);
        using var reader=f.Reader(); Drain(reader,f.Query()); var second=Drain(reader,f.Query() with { Since=DateTimeOffset.Parse("2026-01-02T00:00:00Z"),UnknownTime="report-only" });
        second.Last["coverage"]!["index_bytes_reused"]!.Value<long>().Should().Be(0); second.Last["complete"]!.Value<bool>().Should().BeFalse();
        second.Last["coverage"]!["unknown_time_records"]!.Value<long>().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public void PartiallyReadFilesAndRestartedReadersRemainCold()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T01:00:00Z entry", 10000));
        using (var reader = f.Reader())
        {
            reader.Read(f.Query() with { PageLines = 1 }, CancellationToken.None);
            var second = Drain(reader, f.Query() with { Since = DateTimeOffset.Parse("2026-01-02T00:00:00Z") });
            second.Last["coverage"]!["index_bytes_reused"]!.Value<long>().Should().Be(0);
        }
        using var restarted = f.Reader();
        Drain(restarted, f.Query()).Last["coverage"]!["index_bytes_reused"]!.Value<long>().Should().Be(0);
    }

    [SkippableFact]
    public void MutationDuringWarmCursorRefusesFurtherPages()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T01:00:00Z entry", 10000));
        using var reader = f.Reader(); Drain(reader, f.Query());
        var query = f.Query() with { PageLines = 1 }; var page = reader.Read(query, CancellationToken.None);
        File.AppendAllText(f.Log, "2026-01-01T02:00:00Z added\n");
        Action next = () => reader.Read(HistoryReaderUnitTests.Next(query, page), CancellationToken.None);
        next.Should().Throw<companion::MTTextClient.Core.Diagnostics.Companion.HistoryReadException>().WithMessage("history-source-changed");
    }

    [SkippableFact]
    public void CacheEvictionAndGenerationChecksFallBackWithoutRetainingContent()
    {
        var cache = new Index(); var stamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var metadata = new Metadata(1, stamp, 1, 1, ChangedSeconds: 1);
        var entry = new Index.Entry(metadata, 1, 1, stamp, stamp, false, Array.Empty<Index.Block>());
        for (int i = 0; i < Index.MaximumFiles; i++) cache.Put(i.ToString(), entry);
        cache.Get("0", metadata).Should().NotBeNull(); cache.Put("extra", entry);
        cache.Get("1", metadata).Should().BeNull(); cache.Get("0", metadata).Should().NotBeNull();
        cache.Get("0", metadata with { ChangedNanoseconds = 1 }).Should().BeNull();
        cache.Get("0", metadata).Should().BeNull("an invalid generation is removed, not revived");
        cache.Clear(); cache.Get("extra", metadata).Should().BeNull();
        var blocks = new Index.Block[Index.MaximumFileBlocks];
        for (int i = 0; i < 9; i++) cache.Put(i.ToString(), entry with { Blocks = blocks });
        cache.Get("0", metadata).Should().BeNull("the aggregate block budget evicts the oldest file");
        cache.Get("8", metadata).Should().NotBeNull();
    }

    [SkippableFact]
    public void OversizedIndexRetainsOnlyWholeFileSummary()
    {
        var stamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var builder = new Index.Builder(new Metadata(1, stamp, 1, 1, ChangedSeconds: 1), false);
        for (int i = 0; i < Index.MaximumFileBlocks + 10; i++)
            builder.Add((long)i * Index.BlockBytes, (long)(i + 1) * Index.BlockBytes, stamp.AddSeconds(-i), stamp, stamp);
        var entry = builder.Finish()!; entry.Blocks.Should().BeEmpty();
        entry.Records.Should().Be(Index.MaximumFileBlocks + 10);
        entry.Minimum.Should().Be(stamp.AddSeconds(-(Index.MaximumFileBlocks + 9)));
    }

    [SkippableFact]
    public void MultilineInheritanceSurvivesSeekingToOverlappingBlock()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        string old = "2026-01-01T01:00:00Z old\n";
        File.WriteAllText(f.Log, old + string.Concat(Enumerable.Repeat(" at stack-frame " + new string('x', 100) + "\n", 6000)) + "2026-01-02T01:00:00Z wanted\n at wanted-stack\n");
        using var reader = f.Reader(); Drain(reader, f.Query());
        var query = f.Query() with { Since = DateTimeOffset.Parse("2026-01-02T00:00:00Z") };
        var warm = Drain(reader, query); using var coldReader = f.Reader(); var cold = Drain(coldReader, query);
        warm.Rows.Should().HaveCount(2); warm.Rows.Should().BeEquivalentTo(cold.Rows);
        warm.Last["coverage"]!["index_records_reused"]!.Value<long>().Should().BeGreaterThan(1000);
    }

    internal static (List<JToken> Rows,JObject Last) Drain(Reader reader,Query query)
    {
        var rows=new List<JToken>(); JObject page;
        do { page=reader.Read(query,CancellationToken.None); rows.AddRange(page["records"]!); query=HistoryReaderUnitTests.Next(query,page); } while(page["has_more"]!.Value<bool>());
        return(rows,page);
    }
}
