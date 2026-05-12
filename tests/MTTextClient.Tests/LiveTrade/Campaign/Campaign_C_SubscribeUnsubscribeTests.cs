using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier C — subscribe/unsubscribe pairs across the subscription
/// surface (~30 tools), exercised against bench_02 BINANCE.
///
/// Categories:
///   marketdata: trades/depth/markprice/klines/ticker (5 pairs = 10)
///   tpsl: subscribe / unsubscribe (2)
///   triggers: subscribe / unsubscribe (2)
///   autobuy: subscribe / unsubscribe (2)
///   alerts_history: subscribe / unsubscribe + alerts_unsubscribe (3)
///   notifications: subscribe / unsubscribe / clear (3)
///   graphtool: subscribe / unsubscribe (2)
///   livemarkets: subscribe / unsubscribe (2)
///   profiling: subscribe / unsubscribe (2)
///   monitor: start / stop / status / health / stats / performance (6)
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_C_SubscribeUnsubscribeTests
{
    private const string Letter = "C";
    private const string Profile = "bench_02";
    private const string Symbol = "BTCUSDT";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_C_SubscribeUnsubscribeTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseSubscribeSurface()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // ── Marketdata subscribe/unsubscribe pairs (5×2=10) ──
        await Probe("mt_marketdata_trades_subscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_trades_unsubscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_depth_subscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_depth_unsubscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_markprice_subscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_markprice_unsubscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_klines_subscribe", new { symbol = Symbol, interval = "1m", market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_klines_unsubscribe", new { symbol = Symbol, interval = "1m", market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_ticker_subscribe", new { market = "FUTURES", profile = Profile });
        await Probe("mt_marketdata_ticker_unsubscribe", new { market = "FUTURES", profile = Profile });

        // ── TPSL ──
        await Probe("mt_tpsl_subscribe", new { profile = Profile });
        await Probe("mt_tpsl_unsubscribe", new { profile = Profile });

        // ── Triggers ──
        await Probe("mt_triggers_subscribe", new { profile = Profile });
        await Probe("mt_triggers_unsubscribe", new { profile = Profile });

        // ── AutoBuy ──
        await Probe("mt_autobuy_subscribe", new { profile = Profile });
        await Probe("mt_autobuy_unsubscribe", new { profile = Profile });

        // ── Alerts history / alerts unsubscribe ──
        await Probe("mt_alerts_history_subscribe", new { profile = Profile });
        await Probe("mt_alerts_history_unsubscribe", new { profile = Profile });
        await _mcp.CallTool("mt_alerts_subscribe", new { profile = Profile });
        await Probe("mt_alerts_unsubscribe", new { profile = Profile });

        // ── Notifications ──
        await Probe("mt_notifications_subscribe", new { profile = Profile });
        await Probe("mt_notifications_clear", new { profile = Profile });
        await Probe("mt_notifications_unsubscribe", new { profile = Profile });

        // ── GraphTool ──
        await Probe("mt_graphtool_subscribe", new { profile = Profile });
        await Probe("mt_graphtool_unsubscribe", new { profile = Profile });

        // ── LiveMarkets ──
        await Probe("mt_livemarkets_subscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });
        await Probe("mt_livemarkets_unsubscribe", new { symbol = Symbol, market = "FUTURES", profile = Profile });

        // ── Profiling (needs an algo_id — discover one) ──
        string? anyAlgoId = await DiscoverAnyAlgoIdAsync();
        if (!string.IsNullOrEmpty(anyAlgoId))
        {
            await Probe("mt_profiling_subscribe",
                new { symbol = Symbol, algo_id = anyAlgoId, market = "FUTURES", profile = Profile });
            await Probe("mt_profiling_unsubscribe",
                new { symbol = Symbol, algo_id = anyAlgoId, market = "FUTURES", profile = Profile });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_profiling_subscribe",
                "No algorithm present on bench_02 to attach profiling subscription.");
            CampaignEvidence.RecordBlocker(Letter, "mt_profiling_unsubscribe",
                "No algorithm present on bench_02 to attach profiling subscription.");
        }

        // ── Monitor (start/stop/status/health/stats/performance) ──
        await Probe("mt_monitor_start", new { profile = Profile });
        await Task.Delay(2000); // give the monitor a couple of snapshots
        await Probe("mt_monitor_status", new { profile = Profile });
        await Probe("mt_monitor_health", new { profile = Profile });
        await Probe("mt_monitor_stats", new { profile = Profile });
        await Probe("mt_monitor_performance", new { count = "5", profile = Profile });
        await Probe("mt_monitor_stop", new { profile = Profile });
    }

    private Task<McpResponse?> Probe(string tool, object args)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args, profile: Profile);

    private async Task<string?> DiscoverAnyAlgoIdAsync()
    {
        var resp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (resp.ParsedBody is { } body &&
            body.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                if (row.TryGetProperty("id", out var id))
                {
                    var s = id.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? id.GetInt64().ToString()
                        : id.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        return null;
    }
}
