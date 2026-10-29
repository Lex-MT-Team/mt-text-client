extern alias companion;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using HistoryReader = companion::MTTextClient.Core.Diagnostics.Companion.HistoryReader;
using HistoryReadException = companion::MTTextClient.Core.Diagnostics.Companion.HistoryReadException;
using Query = companion::MTTextClient.Core.Diagnostics.DiagnosticHistoryQuery;
using Configuration = companion::MTTextClient.Core.Diagnostics.Companion.CompanionConfiguration;

namespace MTTextClient.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class HistoryReaderUnitTests
{
    [SkippableTheory]
    [InlineData("logs", 76000)]
    [InlineData("guardian-events", 20000)]
    public void HistoryTraversesWholeWindowBeyondTailCaps(string source, int count)
    {
        using var f = new Fixture();
        File.WriteAllLines(source == "logs" ? f.Log : f.Events, Enumerable.Range(0, count).Select(i => $"{DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(i * (source == "logs" ? 2 : 8)):O} record-{i} " + new string('x', 180)));
        using var reader = f.Reader();
        Query query = f.Query(source);
        long found = 0; var ids = new HashSet<string>(); JObject page;
        do
        {
            page = reader.Read(query, CancellationToken.None);
            Encoding.UTF8.GetByteCount(page.ToString(Newtonsoft.Json.Formatting.None)).Should().BeLessThan(48 * 1024);
            foreach (JToken record in page["records"]!) { ids.Add(record["id"]!.Value<string>()!).Should().BeTrue(); found++; }
            query = Next(query, page);
        } while (page["has_more"]!.Value<bool>());
        found.Should().Be(count);
        page["complete"]!.Value<bool>().Should().BeTrue();
        page["coverage"]!["requested_window_complete"]!.Type.Should().Be(JTokenType.Null);
    }

    [SkippableFact]
    public void HistoryReplayBindingCloseAndExpiryAreExplicit()
    {
        using var f = new Fixture();
        File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z record", 100));
        var clock = new ManualClock();
        using var reader = f.Reader(clock);
        Query query = f.Query() with { PageLines = 1 };
        JObject first = reader.Read(query, CancellationToken.None);
        reader.Read(query, CancellationToken.None).ToString().Should().Be(first.ToString());
        Query next = Next(query, first);
        Action mismatch = () => reader.Read(next with { Until = query.Until.AddDays(1) }, CancellationToken.None);
        mismatch.Should().Throw<HistoryReadException>().WithMessage("history-query-mismatch");
        reader.Read(next, CancellationToken.None)["records"]![0]!["byte_offset"]!.Value<long>().Should().BeGreaterThan(0);
        clock.Now = clock.Now.AddMinutes(6);
        Action expired = () => reader.Read(next, CancellationToken.None);
        expired.Should().Throw<HistoryReadException>().WithMessage("history-restart-required");
        reader.Close(next)["closed"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void HistoryDiscoversBeyondFileAndDirectoryEntryTailBudgets()
    {
        using var f = new Fixture();
        for (int i = 0; i < 700; i++) File.WriteAllText(Path.Combine(f.Root, "logs", $"noise-{i}.txt"), "noise");
        for (int i = 0; i < 140; i++) File.WriteAllText(Path.Combine(f.Root, "logs", $"part-{i}.log"), "2026-01-01T08:00:00Z record");
        using var reader = f.Reader(); Query query = f.Query(); int count = 0; JObject page;
        do { page = reader.Read(query, CancellationToken.None); count += ((JArray)page["records"]!).Count; query = Next(query, page); } while (page["has_more"]!.Value<bool>());
        count.Should().Be(140); page["complete"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void HistoryDoesNotDiscardUnknownOversizedOrNonmonotonicRecordsSilently()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Log, "2026-01-03T08:00:00Z outside\n2026-01-01T08:00:00Z inside token=synthetic-secret\n   at System.Type.Run()\nunknown\n" + new string('x', 600000) + "\n2026-01-01T09:00:00Z newest\n");
        using var reader = f.Reader(); Query query = f.Query(); var records = new JArray(); JObject page;
        do { page = reader.Read(query, CancellationToken.None); foreach (JToken row in page["records"]!) records.Add(row); query = Next(query, page); } while (page["has_more"]!.Value<bool>());
        records.ToString().Should().Contain("inside").And.Contain("newest").And.Contain("System.Type.Run").And.NotContain("outside").And.NotContain("synthetic-secret");
        page["complete"]!.Value<bool>().Should().BeFalse();
        page["coverage"]!["omitted_records"]!.Value<int>().Should().Be(1);
        page["coverage"]!["gaps"]!.ToString().Should().Contain("oversized_record").And.Contain("unknown_time");
    }

    [SkippableFact]
    public void HistoryTruncationAndSymlinksCannotClaimCompleteness()
    {
        using var f = new Fixture(); File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z record", 30));
        File.CreateSymbolicLink(Path.Combine(f.Root, "logs", "link.log"), f.Log);
        using var reader = f.Reader(); Query query = f.Query() with { PageLines = 1 };
        JObject first = reader.Read(query, CancellationToken.None); File.WriteAllText(f.Log, "short");
        JObject page = reader.Read(Next(query, first), CancellationToken.None);
        page["complete"]!.Value<bool>().Should().BeFalse();
        page["coverage"]!["gaps"]!.ToString().Should().Contain("file_changed");
    }

    [SkippableFact]
    public void HistoryCursorCapacitySequenceAndCrossListenerBindingAreEnforced()
    {
        using var f = new Fixture(); File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z entry", 20));
        using var reader = f.Reader(); using var other = f.Reader();
        Query query = f.Query() with { PageLines = 1 }; JObject first = reader.Read(query, CancellationToken.None); Query next = Next(query, first);
        Action cross = () => other.Read(next, CancellationToken.None); cross.Should().Throw<HistoryReadException>().WithMessage("history-restart-required");
        Action skip = () => reader.Read(next with { Sequence = 99 }, CancellationToken.None); skip.Should().Throw<HistoryReadException>().WithMessage("history-sequence-mismatch");
        for (int i = 0; i < 3; i++) reader.Read(f.Query() with { PageLines = 1 }, CancellationToken.None);
        Action excess = () => reader.Read(f.Query(), CancellationToken.None); excess.Should().Throw<HistoryReadException>().WithMessage("history-cursor-capacity");
        reader.Close(next); reader.Read(f.Query(), CancellationToken.None).Should().NotBeNull();
    }

    [SkippableTheory]
    [InlineData("append")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("replace")]
    public void HistoryMutationRetainsOpenedBoundaryAndReportsChanges(string change)
    {
        using var f = new Fixture(); File.WriteAllLines(f.Log, Enumerable.Range(0, 30).Select(i => "2026-01-01T08:00:00Z old-" + i));
        using var reader = f.Reader(); Query query = f.Query() with { PageLines = 1 }; JObject first = reader.Read(query, CancellationToken.None);
        if (change == "append") File.AppendAllText(f.Log, "2026-01-01T08:00:00Z appended\n");
        else if (change == "delete") File.Delete(f.Log);
        else { File.Move(f.Log, f.Log + ".old"); if (change == "replace") File.WriteAllText(f.Log, "2026-01-01T08:00:00Z replacement\n"); }
        query = Next(query, first); var rows = new List<string>(); JObject page;
        do { page = reader.Read(query, CancellationToken.None); rows.AddRange(page["records"]!.Select(r => r["text"]!.Value<string>()!)); query = Next(query, page); } while (page["has_more"]!.Value<bool>());
        rows.Should().HaveCount(29); rows.Should().NotContain(r => r.Contains("appended") || r.Contains("replacement"));
        if (change == "append")
        { page["complete"]!.Value<bool>().Should().BeTrue(); page["coverage"]!["warnings"]!.ToString().Should().Contain("file_grew_after_open_boundary"); }
        else { page["complete"]!.Value<bool>().Should().BeFalse(); page["coverage"]!["gaps"]!.Should().NotBeEmpty(); }
    }

    [SkippableTheory]
    [InlineData("2026-03-08 02:30:00 invalid-dst", "America/New_York")]
    [InlineData("2026-11-01 01:30:00 ambiguous-dst", "America/New_York")]
    [InlineData("2026-01-01 08:00:00 unknown-zone", null)]
    public void HistoryTimeDoesNotGuessTimezoneOrDst(string line, string? zone)
    {
        using var f = new Fixture(); f.Config["profiles"]![0]!["historyTimeZone"] = zone;
        if (zone is null) ((JObject)f.Config["profiles"]![0]!).Remove("historyTimeZone");
        File.WriteAllText(f.Log, line); using var reader = f.Reader();
        JObject page = reader.Read(f.Query(), CancellationToken.None);
        page["records"]![0]!["time_status"]!.Value<string>().Should().Be("unknown"); page["complete"]!.Value<bool>().Should().BeFalse();
    }

    [SkippableFact]
    public void HistoryGuardianJsonHalfOpenWindowAndRotatedSourcesAreExplicit()
    {
        using var f = new Fixture(); var profile = (JObject)f.Config["profiles"]![0]!;
        profile["guardianHistoryRoots"] = new JArray(Path.Combine(f.Root, "logs")); profile["guardianHistoryGlobs"] = new JArray("*.events");
        File.WriteAllText(f.Events, "{\"ts\":\"2026-01-01T00:00:00Z\",\"event\":\"first\"}\n{\"ts\":\"2026-01-03T00:00:00Z\",\"event\":\"exclusive\"}\n");
        File.WriteAllText(Path.Combine(f.Root, "logs", "rotated.events"), "{\"ts\":\"2026-01-01T10:00:00+02:00\",\"event\":\"rotated\"}\n");
        using var reader = f.Reader(); JObject page = reader.Read(f.Query("guardian-events"), CancellationToken.None);
        page["records"]!.Count().Should().Be(2); page.ToString().Should().Contain("rotated").And.NotContain("exclusive"); page["complete"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void HistoryInvalidUtf8AndHugeSecretCannotLeakFragmentsOrInheritedTime()
    {
        using var f = new Fixture();
        using (var output = File.Create(f.Log))
        {
            output.Write(Encoding.UTF8.GetBytes("2026-01-01T08:00:00Z before\n" + new string('x', 16380) + "synthetic-secret" + new string('y', 300000) + "\n"));
            output.Write(new byte[] { 0xff, 0xfe, 10 }); output.Write(Encoding.UTF8.GetBytes("   at Orphan.Run()\n2026-01-01T09:00:00Z after\n"));
        }
        using var reader = f.Reader(); Query query = f.Query(); var rows = new JArray(); JObject page;
        do { page = reader.Read(query, CancellationToken.None); foreach (JToken row in page["records"]!) rows.Add(row); query = Next(query, page); } while (page["has_more"]!.Value<bool>());
        rows.ToString().Should().NotContain("synthetic-secret").And.Contain("after");
        rows.Single(r => r["text"]!.Value<string>()!.Contains("Orphan"))["time_status"]!.Value<string>().Should().Be("unknown");
        page["coverage"]!["omitted_records"]!.Value<int>().Should().Be(2); page["complete"]!.Value<bool>().Should().BeFalse();
    }

    [SkippableFact]
    public void HistoryPermissionLossIsReportedEvenForAnOpenDescriptor()
    {
        using var f = new Fixture(); File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z entry", 10));
        using var reader = f.Reader(); Query query = f.Query() with { PageLines = 1 }; JObject first = reader.Read(query, CancellationToken.None);
        File.SetUnixFileMode(f.Log, UnixFileMode.None);
        try { reader.Read(Next(query, first), CancellationToken.None)["coverage"]!["gaps"]!.ToString().Should().Contain("file_permissions_changed"); }
        finally { File.SetUnixFileMode(f.Log, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    internal static Query Next(Query q, JObject page) => q with { Cursor = page["cursor"]!.Value<string>(), Sequence = page["next_sequence"]?.Value<long?>() ?? page["sequence"]!.Value<long>() };
    internal sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath() : Path.GetTempPath(), "history-synthetic-" + Guid.NewGuid().ToString("N"));
        internal string Log => Path.Combine(Root, "logs", "core.log");
        internal string Events => Path.Combine(Root, "guardian.events");
        internal JObject Config;
        internal Fixture()
        {
            Skip.If(OperatingSystem.IsWindows(), "Local companion file reads require a supported POSIX host.");
            Directory.CreateDirectory(Path.Combine(Root, "logs"));
            string auth = Path.Combine(Root, "core.auth"); File.WriteAllText(auth, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-history")));
            File.SetUnixFileMode(auth, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Config = new JObject { ["profiles"] = new JArray(new JObject { ["name"] = "synthetic", ["exchange"] = "BINANCE", ["corePort"] = 4242, ["diagnosticPort"] = 4343, ["coreAuthPath"] = auth, ["logRoots"] = new JArray(Path.Combine(Root, "logs")), ["logGlobs"] = new JArray("*.log"), ["guardianEventPath"] = Events, ["historyTimeZone"] = "UTC" }) };
        }
        internal HistoryReader Reader(TimeProvider? clock = null)
        {
            string path = Path.Combine(Root, "companion.json"); File.WriteAllText(path, Config.ToString());
            return new HistoryReader(Configuration.Load(path).Profiles.Single(), new[] { "synthetic-secret" }, clock);
        }
        internal Query Query(string source = "logs") => new(source, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 500, Guid.NewGuid().ToString("N"));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
