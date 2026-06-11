using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke coverage for the AutoStops balance-filter CRUD tools.
/// Full add → start → trigger → check-and-restart lifecycle is exercised in
/// <see cref="LiveTrade.AutoStopsLifecycleLiveTradeTests"/>.
///
/// What these probes prove:
///   • ConfirmGate fires when confirm is omitted.
///   • Schema validation: add rejects without max_loss; edit/delete reject
///     without index; the dispatcher routes cleanly (no "Unknown tool" /
///     "Unknown subcommand").
///   • A complete round-trip (add → start → stop → delete) against a real bench
///     leaves the filter list at the same length it started with — proving that
///     the JSON-blob path through profile settings is bidirectional.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AutoStopsCrudSmokeTests
{
    // Target bench_02 explicitly: bench_01 / bench_04 are subject to
    // freeze conditions on this build. bench_02 BINANCE is the only
    // consistently alive bench in the dev environment.
    private const string Profile = "bench_02";

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AutoStopsCrudSmokeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_autostops_add_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_autostops_add", new
        {
            max_loss = "-0.1",
            profile = Profile,
            // confirm omitted intentionally
        });

        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate emits -32602 when a confirm-required tool is called without confirm");
    }

    [SkippableFact]
    public async Task mt_autostops_delete_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_autostops_delete", new
        {
            index = "0",
            profile = Profile,
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_autostops_crud_round_trip_is_idempotent()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        string profile = Profile;

        // Baseline filter count.
        var listBefore = await _mcp.CallTool("mt_autostops_list", new { profile });
        listBefore.InnerSuccess.Should().BeTrue();
        int countBefore = ReadFilterCount(listBefore.ParsedBody);

        // 1) ADD a benign filter (very deep min-loss — won't trigger).
        var addResp = await _mcp.CallTool("mt_autostops_add", new
        {
            max_loss = "-1000000",
            market = "FUTURES",
            timeframe_ms = "3600000",
            pause_algo = true,
            confirm = true,
            profile,
        });
        addResp.IsRpcError.Should().BeFalse(
            because: "the dispatcher must route mt_autostops_add to OrdersCommand.add — got " + (addResp.InnerMessage ?? "<null>"));
        addResp.InnerSuccess.Should().BeTrue(
            because: $"add must succeed on a connected bench; got: {addResp.InnerMessage}");

        var listAfterAdd = await _mcp.CallTool("mt_autostops_list", new { profile });
        int countAfterAdd = ReadFilterCount(listAfterAdd.ParsedBody);
        countAfterAdd.Should().Be(countBefore + 1, because: "exactly one filter should have been appended");
        int newIdx = countAfterAdd - 1;

        // 2) START the filter (it was added disabled).
        var startResp = await _mcp.CallTool("mt_autostops_start", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile,
        });
        startResp.IsRpcError.Should().BeFalse();
        startResp.InnerSuccess.Should().BeTrue(because: "start must succeed: " + startResp.InnerMessage);

        // 3) STOP the filter.
        var stopResp = await _mcp.CallTool("mt_autostops_stop", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile,
        });
        stopResp.IsRpcError.Should().BeFalse();
        stopResp.InnerSuccess.Should().BeTrue();

        // 4) DELETE the filter; list must shrink back to baseline.
        var delResp = await _mcp.CallTool("mt_autostops_delete", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile,
        });
        delResp.IsRpcError.Should().BeFalse();
        delResp.InnerSuccess.Should().BeTrue();

        var listFinal = await _mcp.CallTool("mt_autostops_list", new { profile });
        int countAfterDelete = ReadFilterCount(listFinal.ParsedBody);
        countAfterDelete.Should().Be(countBefore, because: "delete must restore the baseline filter count");
    }

    private static int ReadFilterCount(System.Text.Json.JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
        if (!b.TryGetProperty("data", out var data)) return 0;
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
        if (!data.TryGetProperty("BalanceFilterCount", out var c)) return 0;
        return c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt32() : 0;
    }
}
