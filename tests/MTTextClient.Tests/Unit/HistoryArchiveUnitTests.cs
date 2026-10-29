extern alias companion;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using Query = companion::MTTextClient.Core.Diagnostics.DiagnosticHistoryQuery;

namespace MTTextClient.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class HistoryArchiveUnitTests
{
    [SkippableFact]
    public void GzipCounterpartsAreIncludedWithStableLogicalOffsetsAndReplay()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        string[] lines = Enumerable.Range(0, 20000).Select(i => $"2026-01-01T08:00:00Z archive-{i} " + new string('x', 100)).ToArray();
        Compress(f.Log + ".gz", string.Join('\n', lines) + "\n");
        File.WriteAllText(f.Log, "2026-01-02T08:00:00Z current\n");
        using var reader = f.Reader(); Query query = f.Query(); int count = 0; var ids = new HashSet<string>(); JObject page; long offset = 0;
        do
        {
            page = reader.Read(query, CancellationToken.None);
            reader.Read(query, CancellationToken.None).ToString().Should().Be(page.ToString());
            foreach (JToken row in page["records"]!)
            {
                ids.Add(row["id"]!.Value<string>()!).Should().BeTrue(); count++;
                if (row["source_encoding"]?.Value<string>() == "gzip")
                { row["byte_offset"]!.Value<long>().Should().Be(offset); offset += Encoding.UTF8.GetByteCount(row["text"]!.Value<string>()!) + 1; }
            }
            query = HistoryReaderUnitTests.Next(query, page);
        } while (page["has_more"]!.Value<bool>());
        count.Should().Be(20001); page["complete"]!.Value<bool>().Should().BeTrue();
        page["coverage"]!["compressed_files_opened"]!.Value<int>().Should().Be(1);
    }

    [SkippableTheory]
    [InlineData("crc")]
    [InlineData("truncated")]
    [InlineData("invalid")]
    public void BrokenArchivesProduceExplicitGapWithoutBinaryLeak(string damage)
    {
        using var f = new HistoryReaderUnitTests.Fixture(); string path = f.Log + ".gz";
        Compress(path, string.Concat(Enumerable.Repeat("2026-01-01T08:00:00Z safe\n", 1000)));
        byte[] bytes = File.ReadAllBytes(path);
        if (damage == "truncated") bytes = bytes[..^6];
        else if (damage == "crc") bytes[^8] ^= 128;
        else bytes = new byte[] { 0xff, 0xfe, 0x01, 0x02 };
        File.WriteAllBytes(path, bytes); using var reader = f.Reader(); Query query = f.Query(); JObject page;
        do { page = reader.Read(query, CancellationToken.None); query = HistoryReaderUnitTests.Next(query, page); } while (page["has_more"]!.Value<bool>());
        page["complete"]!.Value<bool>().Should().BeFalse(); page["coverage"]!["gaps"]!.ToString().Should().Contain("gzip");
    }

    [SkippableFact]
    public void OldGuardianPrefixUsesLargerProgressSlicesWithoutTimestampSeeking()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllText(f.Events, string.Concat(Enumerable.Repeat("2025-05-01T08:00:00Z old " + new string('x', 180) + "\n", 50000)) + "2026-01-01T08:00:00Z inside\n");
        using var reader = f.Reader(); Query query = f.Query("guardian-events"); int pages = 0, found = 0; JObject page;
        do { page = reader.Read(query, CancellationToken.None); found += page["records"]!.Count(); pages++; query = HistoryReaderUnitTests.Next(query, page); } while (page["has_more"]!.Value<bool>());
        found.Should().Be(1); pages.Should().BeLessThan(12); page["complete"]!.Value<bool>().Should().BeTrue();
        page["coverage"]!["start_boundary"]!.Value<string>().Should().Be("observed_at_or_before_since");
    }

    [SkippableFact]
    public void UnknownPolicyIsExplicitBoundAndDoesNotHideCoverageGaps()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllText(f.Log, "orphan without time\n2026-01-01T08:00:00Z inside\n");
        using var reader = f.Reader(); Query query = f.Query() with { UnknownTime = "report-only" };
        JObject page = reader.Read(query, CancellationToken.None);
        page["records"]!.Count().Should().Be(1); page["coverage"]!["unknown_time_excluded"]!.Value<int>().Should().Be(1);
        page["coverage"]!["gaps"]!.ToString().Should().Contain("unknown_time"); page["complete"]!.Value<bool>().Should().BeFalse();
        Action changed = () => reader.Read(query with { UnknownTime = "include" }, CancellationToken.None);
        changed.Should().Throw<companion::MTTextClient.Core.Diagnostics.Companion.HistoryReadException>().WithMessage("history-query-mismatch");
        JObject included = reader.Read(f.Query(), CancellationToken.None); included["records"]!.Count().Should().Be(2);
    }

    [SkippableFact]
    public void TerminalReplayDoesNotConsumeActiveSlotsAndIsBounded()
    {
        using var f = new HistoryReaderUnitTests.Fixture(); File.WriteAllText(f.Log, "2026-01-01T08:00:00Z one\n");
        using var reader = f.Reader(); Query first = f.Query(); JObject firstPage = reader.Read(first, CancellationToken.None);
        for (int i = 0; i < 10; i++) reader.Read(f.Query(), CancellationToken.None)["has_more"]!.Value<bool>().Should().BeFalse();
        reader.Read(first, CancellationToken.None).ToString().Should().Be(firstPage.ToString());
        for (int i = 0; i < 20; i++) reader.Read(f.Query(), CancellationToken.None);
        Query expired = first with { Cursor = firstPage["cursor"]!.Value<string>() };
        Action replay = () => reader.Read(expired, CancellationToken.None);
        replay.Should().Throw<companion::MTTextClient.Core.Diagnostics.Companion.HistoryReadException>().WithMessage("history-restart-required");
    }

    [SkippableFact]
    public async Task DistinctCursorsCanReadDifferentSourcesConcurrently()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllText(f.Log, string.Concat(Enumerable.Repeat("2025-01-01T00:00:00Z old\n", 400000)));
        File.WriteAllText(f.Events, "2026-01-01T08:00:00Z guardian\n"); using var reader = f.Reader();
        using var start = new ManualResetEventSlim();
        var a = Task.Run(() => { start.Wait(); return reader.Read(f.Query(), CancellationToken.None); });
        var b = Task.Run(() => { start.Wait(); return reader.Read(f.Query("guardian-events"), CancellationToken.None); });
        start.Set(); JObject[] pages = await Task.WhenAll(a,b); pages[1]["records"]!.Count().Should().Be(1);
    }

    [SkippableFact]
    public void ConcatenatedMembersNeverClaimVerifiedArchiveIntegrity()
    {
        using var f = new HistoryReaderUnitTests.Fixture(); string a = f.Log + ".gz", b = f.Log + ".member.tmp";
        Compress(a, "2026-01-01T08:00:00Z first\n"); Compress(b, "2026-01-01T08:00:00Z second\n");
        using (var output = new FileStream(a, FileMode.Append)) output.Write(File.ReadAllBytes(b));
        using var reader = f.Reader(); Query query = f.Query(); JObject page;
        do { page = reader.Read(query, CancellationToken.None); query = HistoryReaderUnitTests.Next(query,page); } while (page["has_more"]!.Value<bool>());
        page["complete"]!.Value<bool>().Should().BeFalse(); page["coverage"]!["gaps"]!.ToString().Should().Contain("gzip");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnfinishedArchiveTailIsOmittedButVerifiedUnterminatedLineIsPreserved(bool truncate)
    {
        using var f = new HistoryReaderUnitTests.Fixture(); string path = f.Log + ".gz";
        string text = "2026-01-01T08:00:00Z full\n2026-01-01T08:00:01Z partial-fragment-canary";
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.NoCompression)) gzip.Write(Encoding.UTF8.GetBytes(text));
        if (truncate) { byte[] bytes = File.ReadAllBytes(path); File.WriteAllBytes(path, bytes[..^14]); }
        using var reader = f.Reader(); Query query = f.Query(); JObject page; var rows = new List<JToken>();
        do { page = reader.Read(query, CancellationToken.None); rows.AddRange(page["records"]!); query = HistoryReaderUnitTests.Next(query,page); } while (page["has_more"]!.Value<bool>());
        if (truncate)
        {
            page["complete"]!.Value<bool>().Should().BeFalse();
            page["coverage"]!["gaps"]!.ToString().Should().Contain("gzip_integrity_failed_or_unsupported");
            page["coverage"]!["gzip_integrity_failures"]!.Value<int>().Should().Be(1);
            rows.Should().Contain(r => r["content_omitted"]!.Value<bool>());
            string.Join("\n", rows.Select(r => r["text"]!.Value<string>())).Should().NotContain("partial-fragment");
        }
        else
        {
            page["complete"]!.Value<bool>().Should().BeTrue();
            page["coverage"]!["gzip_files_verified"]!.Value<int>().Should().Be(1);
            rows.Last()["text"]!.Value<string>().Should().EndWith("partial-fragment-canary");
        }
    }

    internal static void Compress(string path, string text)
    {
        using var file = File.Create(path); using var gzip = new GZipStream(file, CompressionLevel.Fastest); gzip.Write(Encoding.UTF8.GetBytes(text));
    }
}
