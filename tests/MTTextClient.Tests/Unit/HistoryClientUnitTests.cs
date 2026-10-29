extern alias companion;
using System.Text;
using System.Runtime.InteropServices;
using FluentAssertions;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.MCP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Reader = companion::MTTextClient.Core.Diagnostics.Companion.HistoryReader;
using ServerQuery = companion::MTTextClient.Core.Diagnostics.DiagnosticHistoryQuery;

namespace MTTextClient.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class HistoryClientUnitTests
{
    [SkippableFact]
    public async Task HistoryStreaming_Commits76000RecordsAndResumesAfterPageBeforeManifestCrash()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        File.WriteAllLines(f.Log, Enumerable.Range(0, 76000).Select(i => $"{DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(i * 2):O} record-{i} " + new string('x', 180)));
        using var reader = f.Reader(); var executor = new Adapter(reader); var query = Query(); string output = Path.Combine(f.Root, "private output");
        executor.FailAt = 2;
        JObject partial = await Export(executor, query, output);
        partial["has_more"]!.Value<bool>().Should().BeTrue();
        string oldManifest = File.ReadAllText(Path.Combine(output, "manifest.json"));
        executor.FailAt = 3;
        await Export(executor, query, output);
        // Simulate a process crash after page rename, before checkpoint rename.
        File.WriteAllText(Path.Combine(output, "manifest.json"), oldManifest);
        executor.FailAt = null;
        JObject final = await Export(executor, query, output);
        final["complete"]!.Value<bool>().Should().BeTrue();
        final["coverage"]!["total_records_returned"]!.Value<int>().Should().Be(76000);
        var ids = new HashSet<string>(); int count = 0;
        foreach (string file in Directory.EnumerateFiles(output, "page-*.ndjson"))
        {
            var page = JObject.Parse(File.ReadAllText(file));
            foreach (JToken record in page["records"]!) { ids.Add(record["id"]!.Value<string>()!).Should().BeTrue(); count++; }
            if (!OperatingSystem.IsWindows()) (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(0);
        }
        count.Should().Be(76000);
        (await Export(executor, query, output))["pages_this_run"]!.Value<int>().Should().Be(0);
        Func<Task> changedQuery = async () => await Export(executor, query with { Until = query.Until.AddDays(1) }, output);
        await changedQuery.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableTheory]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("loose")]
    public async Task HistoryStreaming_UnsafeCheckpointDoesNotOpenOrHang(string kind)
    {
        using var f = new HistoryReaderUnitTests.Fixture(); using var reader = f.Reader();
        string output = Path.Combine(f.Root, "output"); Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string manifest = Path.Combine(output, "manifest.json");
        if (kind == "symlink") File.CreateSymbolicLink(manifest, f.Log);
        else if (kind == "fifo") mkfifo(manifest, 384).Should().Be(0);
        else { File.WriteAllText(manifest, "{}"); File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); }
        Func<Task> act = async () => await Export(new Adapter(reader), Query(), output);
        await act.Should().ThrowAsync<Exception>();
    }

    [SkippableFact]
    public async Task HistoryStreaming_CancelAndExpiryPreserveCheckpointWithoutRestart()
    {
        using var f = new HistoryReaderUnitTests.Fixture(); var clock = new HistoryReaderUnitTests.ManualClock(); using var reader = f.Reader(clock);
        File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z record", 1000));
        var executor = new Adapter(reader) { FailAt = 1 }; var query = Query(); string output = Path.Combine(f.Root, "output");
        var partial = await Export(executor, query, output); partial["pages_committed"]!.Value<int>().Should().Be(1);
        clock.Now += TimeSpan.FromMinutes(6); executor.FailAt = null;
        var expired = await Export(executor, query, output);
        expired["stop_reason"]!.Value<string>().Should().Be("history-restart-required");
        expired["pages_committed"]!.Value<int>().Should().Be(1);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await DiagnosticHistoryExport.CollectAsync(executor, Profile(), Route(), Identity(), query, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), output, cancellation.Token);
        cancelled["stop_reason"]!.Value<string>().Should().Be("cancelled");
    }

    [SkippableFact]
    public void HistoryMcpAndCliUseSameValidatedQueryAndPreserveOutputArguments()
    {
        var query = Query() with { UnknownTime = "report-only" }; JObject args = query.ToArgs(); args["profile"] = "synthetic"; args["diag_port"] = 4343;
        string mapped = McpServer.MapToolToCommand("mt_diag_history_page", args)!;
        mapped.Should().StartWith("diag history page synthetic");
        var capture = new Capture(); var registry = new CommandRegistry(); registry.Register(new DiagnosticCommand(capture, () => new() { Profile() }));
        var response = registry.Dispatch(mapped); response.Success.Should().BeTrue("{0}; mapped={1}", response.Message, mapped); capture.Last!.History.Should().Be(query);
        args["since"] = "2026-01-01T00:00:00Z --output bad";
        McpServer.MapToolToCommand("mt_diag_history_page", args).Should().BeNull();
        args = query.ToArgs(); args["profile"] = "synthetic"; args["path"] = "/arbitrary";
        McpServer.MapToolToCommand("mt_diag_history_page", args).Should().BeNull();
        var arbitrary = new ArgumentCapture(); var commands = new CommandRegistry(); commands.Register(arbitrary);
        commands.DispatchArguments(new[] { "capture", "folder with spaces and \"quotes\"" }).Success.Should().BeTrue();
        arbitrary.Value.Should().Be("folder with spaces and \"quotes\"");
    }

    [SkippableTheory]
    [InlineData("policy")]
    [InlineData("excluded")]
    [InlineData("encoding")]
    [InlineData("compressed")]
    [InlineData("warnings")]
    [InlineData("boundary")]
    [InlineData("verified-archive")]
    [InlineData("failed-archive")]
    [InlineData("index-accounting")]
    [InlineData("index-bytes")]
    [InlineData("index-records")]
    [InlineData("index-page")]
    [InlineData("index-files")]
    [InlineData("index-gzip")]
    [InlineData("index-missing")]
    public void HistoryArchiveMetadataMustBeConsistentBeforeCheckpointing(string corruption)
    {
        using var f = new HistoryReaderUnitTests.Fixture(); File.WriteAllText(f.Log, "2026-01-01T08:00:00Z entry\n"); using var reader = f.Reader();
        var query = Query() with { UnknownTime = "report-only" }; JObject page = reader.Read(ToServer(query), CancellationToken.None);
        DiagnosticHistoryExport.ValidatePage(page, query).Should().BeTrue();
        switch (corruption)
        {
            case "index-accounting": page["coverage"]!["scan_accounting"] = "all_fresh"; break;
            case "index-bytes": page["coverage"]!["index_bytes_reused"] = long.MaxValue; break;
            case "index-records": page["coverage"]!["index_records_reused"] = 1; break;
            case "index-page": page["coverage"]!["page_index_records_reused"] = 1; break;
            case "index-files": page["coverage"]!["indexed_files_skipped"] = 100; break;
            case "index-gzip": page["coverage"]!["gzip_verifications_reused"] = 1; break;
            case "index-missing": ((JObject)page["coverage"]!).Remove("index_bytes_reused"); break;
            case "policy": page["coverage"]!["unknown_time_policy"] = "include"; break;
            case "excluded": page["coverage"]!["unknown_time_excluded"] = -1; break;
            case "encoding": page["records"]![0]!["offset_kind"] = "uncompressed_bytes"; break;
            case "compressed": page["coverage"]!["compressed_files_opened"] = 100; break;
            case "warnings": page["coverage"]!["warnings"] = new JArray(new JObject()); break;
            case "boundary": page["coverage"]!["start_boundary"] = "retention-guaranteed"; break;
            case "verified-archive": page["coverage"]!["gzip_files_verified"] = 1; break;
            case "failed-archive": page["coverage"]!["gzip_integrity_failures"] = 1; break;
        }
        DiagnosticHistoryExport.ValidatePage(page, query).Should().BeFalse();
    }

    [SkippableTheory]
    [InlineData("cursor")]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("unknown")]
    [InlineData("counter")]
    [InlineData("complete")]
    public void HistoryForgedPageCannotAdvanceCheckpoint(string corruption)
    {
        using var f = new HistoryReaderUnitTests.Fixture(); File.WriteAllText(f.Log, "2026-01-01T08:00:00Z entry\n"); using var reader = f.Reader();
        var query = Query(); JObject page = reader.Read(ToServer(query), CancellationToken.None);
        DiagnosticHistoryExport.ValidatePage(page, query).Should().BeTrue();
        switch (corruption)
        {
            case "cursor": page["cursor"] = "forged"; break;
            case "source": page["source"] = "guardian-events"; break;
            case "sequence": page["next_sequence"] = 999; break;
            case "unknown": page["records"]![0]!["time_status"] = "unknown"; break;
            case "counter": page["coverage"]!["total_records_scanned"] = -1; break;
            case "complete": page["coverage"]!["gaps"] = new JArray("unknown_time"); break;
        }
        DiagnosticHistoryExport.ValidatePage(page, query).Should().BeFalse();
    }

    [SkippableFact]
    public async Task HistoryFleet77PreservesIndependentProgressAndNeverCallsUnselectedProfiles()
    {
        var capture = new Capture(); var profiles = Enumerable.Range(0, 77).Select(i => { var p = Profile(); p.Name = "profile-" + i; return p; }).ToArray();
        var queries = profiles.ToDictionary(p => p.Name, p => Query() with { QueryId = Guid.NewGuid().ToString("N") });
        var request = new DiagnosticFleetRequest(profiles.Select(p => p.Name).ToArray(), DiagnosticRequest.HistoryRequest(queries.Values.First()), 4343, 8, TimeSpan.FromSeconds(5), false) { HistoryQueries = queries };
        var result = await new DiagnosticFleetCollector(capture).CollectAsync(request, profiles, CancellationToken.None);
        result.Coverage.Validated.Should().Be(77); capture.Queries.Count.Should().Be(77);
        foreach (var pair in queries) capture.Queries[pair.Key].Should().Be(pair.Value);
        var mcpArgs = new JObject { ["requests"] = new JArray(queries.Select(pair => { var row = pair.Value.ToArgs(); row["profile"] = pair.Key; return row; })), ["diag_port"] = 4343 };
        string commandLine = McpServer.MapToolToCommand("mt_diag_history_fleet", mcpArgs)!;
        var registry = new CommandRegistry(); registry.Register(new DiagnosticCommand(capture, () => profiles.ToList()));
        registry.Dispatch(commandLine).Success.Should().BeTrue();
        mcpArgs["requests"] = new JArray(mcpArgs["requests"]![0]!.DeepClone(), mcpArgs["requests"]![0]!.DeepClone());
        McpServer.MapToolToCommand("mt_diag_history_fleet", mcpArgs).Should().BeNull();
    }

    [SkippableFact]
    public async Task HistoryStreamingExclusiveWriterAndBlockedPagePreserveEvidence()
    {
        using var f = new HistoryReaderUnitTests.Fixture(); File.WriteAllLines(f.Log, Enumerable.Repeat("2026-01-01T08:00:00Z entry", 1000));
        using var reader = f.Reader(); var query = Query(); string output = Path.Combine(f.Root, "output");
        var hold = new HoldingExecutor(); Task<JObject> first = Export(hold, query, output);
        await hold.Entered.Task;
        try { Func<Task> second = async () => await Export(new Adapter(reader), query, output); await second.Should().ThrowAsync<InvalidOperationException>(); }
        finally { hold.Release.TrySetResult(); }
        await first;
        // A nonregular committed-page destination fails closed without advancing manifest.
        Directory.CreateDirectory(Path.Combine(output, "page-0000000000000000000.ndjson"));
        Func<Task> blocked = async () => await Export(new Adapter(reader), query, output); await blocked.Should().ThrowAsync<IOException>();
        JObject.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))["next_sequence"]!.Value<int>().Should().Be(0);
    }
    private sealed class HoldingExecutor : IDiagnosticExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DiagnosticResult> ExecuteAsync(ServerProfile p, DiagnosticRoute r, DiagnosticRequest q, DiagnosticIdentityEvidence i, TimeSpan t, CancellationToken c)
        { Entered.TrySetResult(); await Release.Task; return Result(null, "timeout"); }
    }

    [SkippableFact]
    public void HistoryRpcParsingPreservesExplicitOffsetStringsBeforeMapping()
    {
        JObject arguments = Query().ToArgs(); arguments["profile"] = "synthetic"; arguments["since"] = "2026-01-01T03:00:00+03:00";
        var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = "mt_diag_history_page", ["arguments"] = arguments } };
        var parser = typeof(McpServer).GetMethod("ParseRequestLine", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var parsed = (JObject)parser.Invoke(null, new object[] { request.ToString(Formatting.None) })!;
        var actual = (JObject)parsed["params"]!["arguments"]!;
        actual["since"]!.Type.Should().Be(JTokenType.String); actual["since"]!.Value<string>().Should().Be("2026-01-01T03:00:00+03:00");
        McpServer.MapToolToCommand("mt_diag_history_page", actual).Should().NotBeNull();
    }

    [SkippableTheory]
    [InlineData("--deadline-ms", "60001", "--deadline-ms must be between 1 and 60000")]
    [InlineData("--deadline-ms", "garbage", "--deadline-ms must be between 1 and 60000")]
    [InlineData("--timeout-ms", "0", "--timeout-ms must be between 1 and 15000")]
    [InlineData("--output", "", "--output requires a non-empty private directory")]
    public void HistoryCliReportsOptionWithoutEchoingValue(string option, string value, string expected)
    {
        var capture = new Capture(); using var command = new DiagnosticCommand(capture, () => new() { Profile() });
        var args = new List<string> { "history", "collect", "synthetic", "--source", "logs", "--since", "2026-01-01T00:00:00Z", "--until", "2026-01-02T00:00:00Z", "--query-id", Guid.NewGuid().ToString("N"), "--diag-port", "4343", option, value };
        CommandResult result = command.Execute(args.ToArray()); result.Success.Should().BeFalse(); result.Message.Should().Contain(expected); capture.Last.Should().BeNull();
    }

    [SkippableFact]
    public void HistoryCliMissingOutputValueIsSpecific()
    {
        using var command = new DiagnosticCommand(new Capture(), () => new() { Profile() });
        var result = command.Execute(new[] { "history", "collect", "synthetic", "--output" });
        result.Message.Should().Contain("--output requires a non-empty private directory");
    }

    [SkippableFact]
    public async Task HistoryCollectionNormalizesDeadlineStopWithExplicitScope()
    {
        using var f = new HistoryReaderUnitTests.Fixture();
        var failure = (DiagnosticResult)typeof(DiagnosticResult).GetMethod("Failure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object?[] { DiagnosticResultKind.Timeout, "deadline-exceeded", null })!;
        foreach (bool collection in new[] { false, true })
        {
            var result = await DiagnosticHistoryExport.CollectAsync(new TimeoutExecutor(failure), Profile(), Route(), Identity(), Query(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(collection ? 1 : 60), Path.Combine(f.Root, Guid.NewGuid().ToString("N")), CancellationToken.None);
            result["stop_reason"]!.Value<string>().Should().Be("deadline-exceeded");
            result["stop_scope"]!.Value<string>().Should().Be(collection ? "collection" : "request");
            result["pages_committed"]!.Value<int>().Should().Be(0);
        }
    }

    private sealed class TimeoutExecutor(DiagnosticResult result) : IDiagnosticExecutor
    {
        public Task<DiagnosticResult> ExecuteAsync(ServerProfile p, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence identity, TimeSpan timeout, CancellationToken token) => Task.FromResult(result);
    }

    private static DiagnosticResult Result(JToken? data, string? error = null, string profile = "synthetic")
    {
        var envelope = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "history-page", profile, new JObject(), error is null, error ?? "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data, error);
        return DiagnosticReplyValidator.ValidateNative(envelope, profile, Route(), new DiagnosticIdentityEvidence(profile, null, new[] { profile }), "synthetic-only");
    }
    private static DiagnosticHistoryQuery Query() => new("logs", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 500, Guid.NewGuid().ToString("N"));
    private static ServerQuery ToServer(DiagnosticHistoryQuery q) => new(q.Source, q.Since, q.Until, q.PageLines, q.QueryId, q.Cursor, q.Sequence, q.UnknownTime);
    private static ServerProfile Profile() => new() { Name = "synthetic", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-only", Exchange = MTShared.Types.ExchangeType.BINANCE };
    private static DiagnosticRoute Route() => new("127.0.0.1", 4343, "explicit");
    private static DiagnosticIdentityEvidence Identity() => new("synthetic", null, new[] { "synthetic" });
    private static Task<JObject> Export(IDiagnosticExecutor executor, DiagnosticHistoryQuery query, string output) => DiagnosticHistoryExport.CollectAsync(executor, Profile(), Route(), Identity(), query, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), output, CancellationToken.None);
    private sealed class Adapter(Reader reader) : IDiagnosticExecutor
    {
        internal long? FailAt;
        public Task<DiagnosticResult> ExecuteAsync(ServerProfile p, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence identity, TimeSpan timeout, CancellationToken token)
        {
            if (request.History!.Sequence == FailAt) return Task.FromResult(Result(null, "timeout"));
            try { return Task.FromResult(Result(request.Operation == DiagnosticOperation.HistoryClose ? reader.Close(ToServer(request.History)) : reader.Read(ToServer(request.History), token), profile: p.Name)); }
            catch (companion::MTTextClient.Core.Diagnostics.Companion.HistoryReadException e) { return Task.FromResult(Result(null, e.Message)); }
        }
    }
    private sealed class Capture : IDiagnosticExecutor
    {
        internal DiagnosticRequest? Last;
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticHistoryQuery> Queries = new();
        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence identity, TimeSpan timeout, CancellationToken token)
        { Last = request; Queries[profile.Name] = request.History!; return Task.FromResult(Result(new JObject(), profile: profile.Name)); }
    }
    private sealed class ArgumentCapture : ICommand
    {
        public string Name => "capture"; public string Description => "test"; public string Usage => "capture value";
        internal string? Value;
        public CommandResult Execute(string[] args) { Value = args.Single(); return new CommandResult { Success = true }; }
    }
    [DllImport("libc", SetLastError = true)] private static extern int mkfifo(string path, uint mode);
}
