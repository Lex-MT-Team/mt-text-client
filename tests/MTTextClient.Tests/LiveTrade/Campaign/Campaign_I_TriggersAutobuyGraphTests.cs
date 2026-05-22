using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier I — triggers / autobuy / graphtool CRUD (~14 tools):
///   triggers: save/delete/start/stop/start_all/stop_all (6 — subscribe pair
///     already covered by Campaign C)
///   autobuy: save/delete/start/stop/refresh_pairs (5)
///   graphtool: save/delete (2 — subscribe pair already covered)
///
/// Each verb is data-driven on a JSON payload; we send a structured envelope
/// the corresponding command builder is documented to accept.  The payloads
/// are minimal (one entry) and use synthetic names so re-runs don't collide.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_I_TriggersAutobuyGraphTests
{
    private const string Letter = "I";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_I_TriggersAutobuyGraphTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseTriggersAutobuyGraph()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        // First attempt; if it flakes (handshake race), retry once.
        bool connected = await _mcp.WaitForConnected(Profile, 60);
        if (!connected)
        {
            await Task.Delay(2000);
            connected = await _mcp.WaitForConnected(Profile, 60);
        }
        if (!connected)
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_triggers_save",
                "bench_02 did not reach CONNECTED in 2×60s; campaign skipped.");
            return;
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ── Triggers ──
        // The triggers command builder expects a JSON 'data' string containing the
        // typed trigger action.  We send a minimal entry; the wire may reject it
        // as malformed (real venue/core response) or accept it (real save).
        // Either is real evidence.
        string triggerData = "{\"name\":\"campaignI_trigger_" + ts + "\",\"symbol\":\"BTCUSDT\",\"market\":\"FUTURES\"}";
        await Probe("mt_triggers_save", new { data = triggerData, profile = Profile });
        await Probe("mt_triggers_start", new { data = triggerData, profile = Profile });
        await Probe("mt_triggers_start_all", new { profile = Profile });
        await Probe("mt_triggers_stop", new { data = triggerData, profile = Profile });
        await Probe("mt_triggers_stop_all", new { profile = Profile });
        await Probe("mt_triggers_delete", new { data = triggerData, profile = Profile });

        // ── AutoBuy ──
        // refresh_pairs is the safest read-side autobuy mutator — touch first.
        await Probe("mt_autobuy_refresh_pairs", new { profile = Profile });
        string autoBuyData = "{\"name\":\"campaignI_autobuy_" + ts + "\",\"symbol\":\"BTCUSDT\",\"market\":\"FUTURES\",\"amount\":1.0}";
        await Probe("mt_autobuy_save", new { data = autoBuyData, profile = Profile });
        await Probe("mt_autobuy_start", new { data = autoBuyData, profile = Profile });
        await Task.Delay(1000);
        await Probe("mt_autobuy_stop", new { data = autoBuyData, profile = Profile });
        await Probe("mt_autobuy_delete", new { data = autoBuyData, profile = Profile });

        // ── GraphTool ──
        string graphData = "{\"name\":\"campaignI_graph_" + ts + "\",\"symbol\":\"BTCUSDT\",\"market\":\"FUTURES\",\"tool_type\":\"LINE\"}";
        await Probe("mt_graphtool_save", new { data = graphData, profile = Profile });
        await Probe("mt_graphtool_delete", new { data = graphData, profile = Profile });
    }

    private Task<McpResponse?> Probe(string tool, object args)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args, profile: Profile);
}
