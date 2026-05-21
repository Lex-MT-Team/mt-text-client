using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier D — algorithm lifecycle verbs on bench_02 (19 tools):
///   start, stop, start_all, stop_all, start_verified, save, save_start,
///   rename, copy, config_set, batch_start, batch_stop, batch_config,
///   clone_group, delete_group, group_by_name, tpsl_change, toggle_debug,
///   profiling.
///
/// Strategy: discover an existing algo on bench_02 (earlier campaigns
/// seeded it).  If none present, mt_algos_create one disposable
/// SHOTS algo specifically for this campaign, then exercise every verb
/// against that id.  Cleanup: deliberately none — leaves campaign-created
/// algos in place per the bench-data-retention policy.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_D_AlgosLifecycleTests
{
    private const string Letter = "D";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_D_AlgosLifecycleTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseAlgosLifecycle()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        var algoId = await DiscoverOrCreateAlgoIdAsync();
        var groupId = await DiscoverAnyGroupIdAsync();

        // ── Single-algo verbs ──
        await Probe("mt_algos_rename",
            new { id = algoId, name = $"campaignD_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", profile = Profile });
        await Probe("mt_algos_config_set",
            new { id = algoId, key = "delayMs", value = "100", profile = Profile });
        await Probe("mt_algos_save", new { id = algoId, profile = Profile });
        await Probe("mt_algos_toggle_debug", new { id = algoId, profile = Profile });
        await Probe("mt_algos_start", new { id = algoId, profile = Profile });
        await Task.Delay(1500);
        await Probe("mt_algos_start_verified",
            new { id = algoId, wait_secs = "3", profile = Profile });
        await Probe("mt_algos_stop", new { id = algoId, profile = Profile });
        await Probe("mt_algos_save_start", new { id = algoId, profile = Profile });
        await Probe("mt_algos_stop", new { id = algoId, profile = Profile });

        // ── Batch verbs ──
        await Probe("mt_algos_batch_config",
            new { algo = "campaignD", key = "delayMs", value = "150",
                  profiles = new[] { Profile } });
        await Probe("mt_algos_batch_start",
            new { algo = "campaignD", profiles = new[] { Profile } });
        await Task.Delay(1500);
        await Probe("mt_algos_batch_stop",
            new { algo = "campaignD", profiles = new[] { Profile } });

        // ── Bulk start_all / stop_all (every running algo on bench_02) ──
        await Probe("mt_algos_start_all", new { confirm = true, profile = Profile });
        await Task.Delay(1500);
        await Probe("mt_algos_stop_all", new { confirm = true, profile = Profile });

        // ── Groups ──
        if (!string.IsNullOrEmpty(groupId))
        {
            await Probe("mt_algos_clone_group", new { group_id = groupId, profile = Profile });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_algos_clone_group",
                "No algorithm group exists on bench_02; nothing to clone.");
        }
        // group_by_name — search by a substring that's likely to match something.
        await Probe("mt_algos_group_by_name", new { name = "default", profile = Profile });

        // delete_group: only attempt if we cloned one above; otherwise blocker.
        // Safer to skip outright — destroying a real group on bench_02 wipes
        // Stage-1 / earlier campaign seed state.  Record as documented.
        CampaignEvidence.RecordBlocker(Letter, "mt_algos_delete_group",
            "destructive; would wipe Stage-1 seed group on bench_02 — not safe to exercise in this campaign");

        // ── Copy (cross-profile) — copy this algo to bench_03 ──
        if (_bench.IsBenchAvailable("bench_03"))
        {
            await _mcp.CallTool("mt_connect", new { profile = "bench_03" }, TimeSpan.FromSeconds(20));
            await Probe("mt_algos_copy", new
            {
                id = algoId,
                source_profile = Profile,
                destination_profile = "bench_03",
                confirm = true,
            });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_algos_copy",
                "bench_03 not available; cross-profile copy target absent.");
        }

        // ── tpsl_change (fire-and-forget) ──
        await Probe("mt_algos_tpsl_change", new
        {
            tp_enabled = true, tp_pct = 1.5, sl_enabled = true, sl_pct = 1.0,
            trailing_enabled = false, profile = Profile,
        });

        // ── profiling (request-only; result lands in events) ──
        // algo_id is Int64 on the wire; pass as long so JSON serialises a numeric value.
        await Probe("mt_algos_profiling",
            new { symbol = "BTCUSDT", algo_id = long.Parse(algoId), market = "FUTURES", profile = Profile });
    }

    private Task<McpResponse?> Probe(string tool, object args)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args, profile: Profile);

    private async Task<string> DiscoverOrCreateAlgoIdAsync()
    {
        var listed = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (listed.ParsedBody is { } body &&
            body.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Array &&
            data.GetArrayLength() > 0)
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
        // No algos — try to create one via mt_algos_create with algo_type=SHOTS auto-discover.
        var create = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            no_dry_run = true,
            confirm = true,
            new_name = $"campaignD_seed_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
        }, TimeSpan.FromSeconds(60));
        if (create.ParsedBody is { } cb && cb.TryGetProperty("data", out var d))
        {
            if (d.TryGetProperty("created_id", out var cid))
                return cid.ValueKind == System.Text.Json.JsonValueKind.Number ? cid.GetInt64().ToString() : (cid.GetString() ?? "");
            if (d.TryGetProperty("id", out var idv))
                return idv.ValueKind == System.Text.Json.JsonValueKind.Number ? idv.GetInt64().ToString() : (idv.GetString() ?? "");
        }
        throw new InvalidOperationException("Could not discover or create any algorithm on bench_02 for Campaign D.");
    }

    private async Task<string?> DiscoverAnyGroupIdAsync()
    {
        var resp = await _mcp.CallTool("mt_algos_groups", new { profile = Profile });
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
