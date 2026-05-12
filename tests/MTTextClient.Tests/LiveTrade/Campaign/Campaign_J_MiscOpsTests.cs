using System.IO;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier J — miscellaneous ops (~25 tools) that don't fit a tighter
/// category. All target bench_02 unless noted.
///
///   Settings & Config: mt_settings_diff, mt_settings_diff_snapshots,
///       mt_settings_set, mt_config_snapshot, mt_config_restore,
///       mt_config_import_algos (6)
///   Blacklist / Whitelist: mt_blacklist_add, mt_blacklist_remove,
///       mt_whitelist_remove (3)
///   Buylimit / Fund / Dust / Deposit / Signals / Funding:
///       mt_buylimit_request, mt_fund_transfer, mt_funding_request,
///       mt_dust_convert, mt_deposit_address, mt_signals_send (6)
///   Import: mt_import_templates, mt_import_v2, mt_import_add_numeric (3)
///   AutoStops_edit: mt_autostops_edit (1)
///   Core mutators (blocker-documented): mt_core_restart, mt_core_restart_update,
///       mt_core_shutdown, mt_core_clear_archive, mt_core_clear_orders (5)
///   Notifications-clear: covered in Campaign C subscribe area.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_J_MiscOpsTests
{
    private const string Letter = "J";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_J_MiscOpsTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseMiscOps()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        bool connected = await _mcp.WaitForConnected(Profile, 60);
        if (!connected)
        {
            await Task.Delay(2000);
            connected = await _mcp.WaitForConnected(Profile, 60);
        }
        if (!connected)
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_settings_set",
                "bench_02 did not reach CONNECTED in 2×60s; campaign skipped.");
            return;
        }

        // Snapshot bench_02 → reuse path for snapshot diff
        var snapResp = await _mcp.CallTool("mt_config_snapshot", new { profile = Profile });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_config_snapshot",
            new { profile = Profile }, profile: Profile, note: "primary snapshot for diff");

        // Take a second snapshot so diff_snapshots has something to compare.
        var snapResp2 = await _mcp.CallTool("mt_config_snapshot", new { profile = Profile });
        string? path1 = ExtractPath(snapResp), path2 = ExtractPath(snapResp2);
        if (!string.IsNullOrEmpty(path1) && !string.IsNullOrEmpty(path2))
        {
            await CampaignEvidence.Probe(_mcp, Letter, "mt_settings_diff_snapshots",
                new { snapshot_a = path1, snapshot_b = path2 });
            await CampaignEvidence.Probe(_mcp, Letter, "mt_config_restore",
                new { path = path1, confirm = true, profile = Profile },
                profile: Profile, note: "restore from snapshot just captured");
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_settings_diff_snapshots",
                "Could not capture two snapshot files; snapshot tool did not return paths.");
            CampaignEvidence.RecordBlocker(Letter, "mt_config_restore",
                "Could not capture a snapshot file to restore from.");
        }

        // mt_settings_diff — diff bench_02 against bench_03 (different exchanges
        // → real diff).  Skip if bench_03 unavailable.
        if (_bench.IsBenchAvailable("bench_03"))
        {
            await _mcp.CallTool("mt_connect", new { profile = "bench_03" }, TimeSpan.FromSeconds(20));
            await CampaignEvidence.Probe(_mcp, Letter, "mt_settings_diff",
                new { profile_a = "bench_02", profile_b = "bench_03" });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_settings_diff",
                "bench_03 unavailable; second profile for diff missing.");
        }

        // mt_settings_set — set a benign key
        await CampaignEvidence.Probe(_mcp, Letter, "mt_settings_set", new
        {
            key = "Misc.CampaignJ.LastRun",
            value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            confirm = true,
            profile = Profile,
        }, profile: Profile);

        // mt_config_import_algos — fire-and-forget on a missing file is fine, but
        // we prefer to actually import from a small synthetic file.  If it
        // rejects → real-wire evidence.  If it accepts → real-wire evidence.
        var algosCfgPath = Path.Combine(Path.GetTempPath(),
            $"campaignJ_algos_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
        File.WriteAllText(algosCfgPath, "[]");  // empty algo list — valid JSON, no rows
        await CampaignEvidence.Probe(_mcp, Letter, "mt_config_import_algos",
            new { path = algosCfgPath, confirm = true, profile = Profile },
            profile: Profile);

        // ── Blacklist add/remove ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_blacklist_add", new
        {
            type = "symbol",
            market_type = "FUTURES",
            quote_asset = "USDT",
            symbol = "campaignJBLACKLIST",
            confirm = true,
            profile = Profile,
        }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_blacklist_remove", new
        {
            type = "symbol",
            market_type = "FUTURES",
            quote_asset = "USDT",
            symbol = "campaignJBLACKLIST",
            confirm = true,
            profile = Profile,
        }, profile: Profile);

        // ── Whitelist remove (add already covered) ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_whitelist_remove", new
        {
            type = "symbol",
            market = "FUTURES",
            quote = "USDT",
            symbol = "campaignJWHITELIST",
            confirm = true,
            profile = Profile,
        }, profile: Profile);

        // ── AutoStops edit ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_autostops_edit", new
        {
            index = "0",
            max_loss = "-1.0",
            confirm = true,
            profile = Profile,
        }, profile: Profile);

        // ── Buy-limit / fund / dust / deposit / signals ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_buylimit_request",
            new { amount = "10.0", profile = Profile }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_funding_request",
            new { profile = Profile }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_fund_transfer", new
        {
            from_account = "FUNDING",
            asset = "USDT",
            amount = "0.01",
            to_account = "TRADING",
            confirm = true,
            profile = Profile,
        }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_dust_convert",
            new { profile = Profile }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_deposit_address",
            new { coin = "USDT", network = "BSC", profile = Profile }, profile: Profile);
        await CampaignEvidence.Probe(_mcp, Letter, "mt_signals_send", new
        {
            symbol = "BTCUSDT",
            side = "BUY",
            price = "1.0",
            market = "FUTURES",
            take_profit = "1.0",
            stop_loss = "1.0",
            channel = "campaignJ",
            profile = Profile,
        }, profile: Profile);

        // ── Import ──
        // mt_import_templates — pass an explicit path that may not exist; the
        // tool returns a structured response either way (real wire-side parse).
        await CampaignEvidence.Probe(_mcp, Letter, "mt_import_templates",
            new { path = "/tmp/nonexistent-algoconfigs-campaignJ.json" });
        // mt_import_v2 — same: synthetic path; client returns structured error.
        await CampaignEvidence.Probe(_mcp, Letter, "mt_import_v2",
            new { path = "/tmp/nonexistent-v2-campaignJ.txt", confirm = true, profile = Profile },
            profile: Profile);
        // mt_import_from_profile — survey what would be imported source→destination.
        if (_bench.IsBenchAvailable("bench_03"))
        {
            await _mcp.CallTool("mt_connect", new { profile = "bench_03" }, TimeSpan.FromSeconds(20));
            await CampaignEvidence.Probe(_mcp, Letter, "mt_import_from_profile",
                new { source_profile = Profile, destination_profile = "bench_03" });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_import_from_profile",
                "bench_03 unavailable; survey needs a destination profile.");
        }
        // mt_import_add_numeric — pick an algo and add 0 (a no-op delta) so we
        // don't corrupt bench state; the wire still runs.
        var algoId = await DiscoverAnyAlgoIdAsync();
        if (!string.IsNullOrEmpty(algoId))
        {
            await CampaignEvidence.Probe(_mcp, Letter, "mt_import_add_numeric",
                new { id = algoId, delta = "0", confirm = true, profile = Profile },
                profile: Profile);
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_import_add_numeric",
                "No algorithm present on bench_02 to apply numeric delta to.");
        }

        // ── Core mutators — DESTRUCTIVE blockers, not invoked ──
        const string coreReason = "destructive; restarting MTCore during a campaign run wipes the " +
            "in-progress evidence and other-bench state. Operator-only.";
        CampaignEvidence.RecordBlocker(Letter, "mt_core_restart", coreReason);
        CampaignEvidence.RecordBlocker(Letter, "mt_core_restart_update", coreReason);
        CampaignEvidence.RecordBlocker(Letter, "mt_core_shutdown", coreReason);
        CampaignEvidence.RecordBlocker(Letter, "mt_core_clear_archive", coreReason);
        CampaignEvidence.RecordBlocker(Letter, "mt_core_clear_orders", coreReason);
    }

    private static string? ExtractPath(McpResponse resp)
    {
        if (resp.ParsedBody is not { } body) return null;
        // mt_config_snapshot returns {snapshot_path, profile, captured_at, status}
        foreach (string key in new[] { "snapshot_path", "path" })
        {
            if (body.TryGetProperty(key, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
            if (body.TryGetProperty("data", out var d) &&
                d.ValueKind == System.Text.Json.JsonValueKind.Object &&
                d.TryGetProperty(key, out var vv) &&
                vv.ValueKind == System.Text.Json.JsonValueKind.String)
                return vv.GetString();
        }
        return null;
    }

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
