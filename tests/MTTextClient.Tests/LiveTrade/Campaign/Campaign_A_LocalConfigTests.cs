using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier A — pure client-side tools that manipulate
/// ~/.config/mt-textclient/profiles.json + folders.json, plus vault and
/// watchdog. No MTCore wire calls required.
///
/// Tools targeted (18):
///   mt_profiles_list/add/edit/delete/move/import_csv (6)
///   mt_folders_list/add/edit/delete (4)
///   mt_use, mt_disconnect (2)
///   mt_vault_store_profile/list/get/delete (4)
///   mt_watchdog_status/token_update (2)
///
/// Mutations use a throwaway folder name + profile name prefixed with
/// "campaignA-" so re-runs are idempotent and never collide with the bench_*
/// profiles in the active config.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_A_LocalConfigTests
{
    private const string Letter = "A";
    private readonly McpFixture _mcp;

    public Campaign_A_LocalConfigTests(McpFixture mcp) { _mcp = mcp; }

    [SkippableFact]
    public async Task ExerciseLocalConfigSurface()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        await _mcp.RestartSubprocessAsync();

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string folder = $"campaignA_folder_{ts}";
        string folderRenamed = $"campaignA_folder_renamed_{ts}";
        string profile = $"campaignA_profile_{ts}";
        string profileRenamed = $"campaignA_profile_renamed_{ts}";

        // ── Folders ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_folders_list", new { });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_folders_add",
            new { name = folder, confirm = true });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_folders_edit",
            new { old_name = folder, new_name = folderRenamed, confirm = true });

        // ── Profiles ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_list", new { });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_add", new
        {
            name = profile,
            address = "127.0.0.1",
            port = "4099",
            token = "campaign_token_dummy",
            exchange = "BINANCE",
            folder = folderRenamed,
            confirm = true,
        });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_edit", new
        {
            name = profile,
            port = "4098",
            rename = profileRenamed,
            confirm = true,
        });
        // Stage 5.3 dispatcher rejects empty-string folder as "missing" — use a
        // real folder name so the wire path runs.  Folder we created above is
        // still alive; reuse it.
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_move", new
        {
            name = profileRenamed,
            folder = folderRenamed,
            confirm = true,
        });

        // ── Import CSV — build a tiny on-disk CSV and re-import ──
        var csvPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"campaignA_import_{ts}.csv");
        System.IO.File.WriteAllText(csvPath, string.Join("\n", new[]
        {
            "name,address,port,token,exchange,folder",
            $"campaignA_csv_a_{ts},127.0.0.1,4097,csv_dummy_a,BINANCE,",
            $"campaignA_csv_b_{ts},127.0.0.1,4096,csv_dummy_b,OKX,",
        }));
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_import_csv",
            new { path = csvPath, confirm = true });

        // ── mt_use / mt_disconnect — operate on bench_02 (the test target) ──
        // mt_use switches the *active* connection in client memory (no wire call).
        // mt_disconnect terminates the existing UDP transport for a profile.
        await CampaignEvidence.Probe(_mcp, Letter, "mt_use",
            new { profile = "bench_02" });
        // Connect first so disconnect has something to act on.
        await _mcp.CallTool("mt_connect", new { profile = "bench_02" }, TimeSpan.FromSeconds(20));
        await CampaignEvidence.Probe(_mcp, Letter, "mt_disconnect",
            new { profile = "bench_02" });

        // ── Cleanup the throwaway profile + folder we created ──
        await CampaignEvidence.Probe(_mcp, Letter, "mt_profiles_delete",
            new { name = profileRenamed, confirm = true });
        await CampaignEvidence.Probe(_mcp, Letter, "mt_folders_delete",
            new { name = folderRenamed, confirm = true });

        // ── Vault — only attempt if VAULT_TOKEN is set; otherwise record blocker ──
        bool vaultOk = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_TOKEN"))
                    && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_ADDR"));
        if (vaultOk)
        {
            string vName = $"campaignA_vault_{ts}";
            await CampaignEvidence.Probe(_mcp, Letter, "mt_vault_store_profile",
                new { name = vName, api_key = "DUMMY_KEY", api_secret = "DUMMY_SECRET" });
            await CampaignEvidence.Probe(_mcp, Letter, "mt_vault_list_profiles", new { });
            await CampaignEvidence.Probe(_mcp, Letter, "mt_vault_get_profile",
                new { name = vName });
            await CampaignEvidence.Probe(_mcp, Letter, "mt_vault_delete_profile",
                new { name = vName, confirm = true });
        }
        else
        {
            const string reason = "VAULT_ADDR / VAULT_TOKEN not set in env; HashiCorp Vault unreachable from this host.";
            CampaignEvidence.RecordBlocker(Letter, "mt_vault_store_profile", reason);
            CampaignEvidence.RecordBlocker(Letter, "mt_vault_list_profiles", reason);
            CampaignEvidence.RecordBlocker(Letter, "mt_vault_get_profile", reason);
            CampaignEvidence.RecordBlocker(Letter, "mt_vault_delete_profile", reason);
        }

        // ── Watchdog (placeholder per registry) ──
        // The handlers exist but the WatchdogConnection layer is not yet wired.
        // Calling these returns a real structured response, but the underlying
        // wire is intentionally not implemented (per docs/watchdog-integration.md).
        // We still probe so the operator sees the real shape of the response.
        await CampaignEvidence.Probe(_mcp, Letter, "mt_watchdog_status",
            new { }, note: "placeholder — WatchdogConnection not wired");
        await CampaignEvidence.Probe(_mcp, Letter, "mt_watchdog_token_update",
            new { token = "campaign_dummy", confirm = true },
            note: "placeholder — WatchdogConnection not wired");

        // ── Cleanup CSV-imported rows ──
        await _mcp.CallTool("mt_profiles_delete",
            new { name = $"campaignA_csv_a_{ts}", confirm = true });
        await _mcp.CallTool("mt_profiles_delete",
            new { name = $"campaignA_csv_b_{ts}", confirm = true });

        // Test passes if we got here — the campaign records evidence, not asserts.
        true.Should().BeTrue();
    }
}
