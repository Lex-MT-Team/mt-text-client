using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier B — in-process telemetry & events surface (8 tools):
///   mt_events_poll, mt_events_status,
///   mt_metrics_get, mt_rate_status,
///   mt_perf_subscribe, mt_perf_unsubscribe,
///   mt_connection_tag, mt_connection_tags.
///
/// These tools are real in-process responses but DO require some prior MCP
/// activity to have non-empty data (events buffer, metrics counter).  We
/// drive a few warm-up reads against bench_02 first, then exercise each
/// telemetry surface.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_B_TelemetryTests
{
    private const string Letter = "B";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_B_TelemetryTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseTelemetrySurface()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        var connected = await _mcp.WaitForConnected(Profile, 60);
        connected.Should().BeTrue($"{Profile} must reach CONNECTED for telemetry counters to populate");

        // Generate some metric activity.
        await _mcp.CallTool("mt_status", new { });
        await _mcp.CallTool("mt_account_balance", new { profile = Profile });
        await _mcp.CallTool("mt_algos_list", new { profile = Profile });

        // ── Events ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_events_status", new { });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_events_poll",
            new { since_seq = 0, n = 20 });

        // ── Metrics / rate ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_metrics_get", new { });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_rate_status", new { });

        // ── Performance subscription pair ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_perf_subscribe",
            new { profile = Profile, market = "FUTURES" }, profile: Profile);
        await Task.Delay(800);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_perf_unsubscribe",
            new { profile = Profile }, profile: Profile);

        // ── Connection tags ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_connection_tag", new
        {
            profile = Profile,
            key = "campaign",
            value = "B-2026-05-12",
        }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_connection_tags",
            new { profile = Profile }, profile: Profile);
    }
}
