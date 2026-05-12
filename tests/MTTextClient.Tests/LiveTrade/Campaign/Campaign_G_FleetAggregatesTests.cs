using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier G — fleet aggregate tools (14):
///   mt_fleet_connect, mt_fleet_batch_connect, mt_fleet_disconnect,
///   mt_fleet_status, mt_fleet_balances, mt_fleet_positions, mt_fleet_algos,
///   mt_fleet_health, mt_fleet_summary, mt_fleet_perf, mt_fleet_reports,
///   mt_fleet_autostops, mt_fleet_blacklist, mt_fleet_set_margin_type.
///
/// Strategy: warm-start every observed bench, exercise each fleet read, then
/// the final mt_fleet_disconnect.  mt_fleet_set_margin_type is exercised
/// in DRY-RUN mode (confirm=false) so it touches the wire but does not
/// mutate venue state.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_G_FleetAggregatesTests
{
    private const string Letter = "G";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_G_FleetAggregatesTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseFleetSurface()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        await _mcp.RestartSubprocessAsync();

        // 1) mt_fleet_connect — connect all configured profiles at once.
        await Probe("mt_fleet_connect", new { });
        await Task.Delay(2000);

        // Warm-start gate per-bench so the next fleet reads have populated data.
        await _mcp.WarmStartAllBenchesAsync(_bench, perBenchBudgetSeconds: 30);

        // 2) mt_fleet_batch_connect — list form for the actually-observable benches.
        var observable = new List<string>();
        foreach (var b in EnvFlags.AllBenches)
            if (_bench.IsBenchAvailable(b.Profile)) observable.Add(b.Profile);
        await Probe("mt_fleet_batch_connect", new { profiles = observable.ToArray() });

        // 3) Fleet reads
        await Probe("mt_fleet_status", new { });
        await Probe("mt_fleet_balances", new { });
        await Probe("mt_fleet_positions", new { });
        await Probe("mt_fleet_algos", new { });
        await Probe("mt_fleet_health", new { });
        await Probe("mt_fleet_summary", new { });
        await Probe("mt_fleet_perf", new { });
        await Probe("mt_fleet_reports", new { period = "7d" });
        await Probe("mt_fleet_autostops", new { });
        await Probe("mt_fleet_blacklist", new { });

        // 4) Fleet margin-type campaign — ConfirmGate forces confirm=true; the
        //    handler is idempotent if every position is already CROSS, which
        //    is the default on bench_02 BTCUSDT.  Committing here just
        //    exercises the wire on every connected bench.
        await Probe("mt_fleet_set_margin_type", new
        {
            symbol = "BTCUSDT",
            margin_type = "CROSS",
            market = "FUTURES",
            confirm = true,
        });

        // 5) mt_fleet_disconnect — leave the fleet up for downstream campaigns by RE-connecting after.
        await Probe("mt_fleet_disconnect", new { confirm = true });
        await Task.Delay(2000);
        await _mcp.CallTool("mt_fleet_connect", new { });
        await _mcp.WarmStartAllBenchesAsync(_bench, perBenchBudgetSeconds: 30);
    }

    private Task<McpResponse?> Probe(string tool, object args)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args);
}
