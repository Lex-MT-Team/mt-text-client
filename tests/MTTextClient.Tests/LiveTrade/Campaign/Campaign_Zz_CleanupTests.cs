using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign cleanup pass — removes the small amount of state the
/// campaign deliberately left on bench_02 (per supervisor request):
///   • profile setting <c>Misc.CampaignJ.LastRun</c> from Campaign J
///   • any algorithm renamed by Campaign D (name prefix <c>campaignD_</c>)
///     restored to <c>"Shots Group"</c>
///   • any working order on BTCUSDT whose <c>client_order_id</c> begins
///     with <c>campaignE</c> cancelled
///
/// Runs after the main campaign as a Zz-suffixed test so xUnit collection
/// ordering naturally schedules it last in a full-class run; explicit
/// per-class --filter still selects it on its own.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_Zz_CleanupTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_Zz_CleanupTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task RemoveCampaignResidue()
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
        Skip.If(!connected, "bench_02 did not reach CONNECTED; cleanup deferred.");

        // 1) Delete the Misc.CampaignJ.LastRun profile setting if present.
        var settingDelResp = await _mcp.CallTool("mt_profile_settings_delete", new
        {
            keys = "Misc.CampaignJ.LastRun",
            confirm = true,
            profile = Profile,
        });
        WriteCleanup("settings_delete", settingDelResp);

        // 2) Restore renamed Campaign-D algos.  Pull the algo list and rename
        //    any with the campaignD_ prefix back to "Shots Group" (the original
        //    name on Lex_002 BINANCE — the only auto-generated algo on the bench).
        var algos = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (algos.ParsedBody is { } body &&
            body.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                string? id = ReadId(row);
                string? name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name) && name!.StartsWith("campaignD_"))
                {
                    var renameResp = await _mcp.CallTool("mt_algos_rename", new
                    {
                        id = id,
                        name = "Shots Group",
                        profile = Profile,
                    });
                    WriteCleanup($"rename_{id}", renameResp);
                }
            }
        }

        // 3) Cancel any working BTCUSDT order whose client_order_id starts with
        //    campaignE.  Iterate mt_orders_list and per-id mt_orders_cancel —
        //    cancel_all was already invoked at the end of Campaign E, but the
        //    move/split path may have spawned residual orders.
        var orders = await _mcp.CallTool("mt_orders_list", new { profile = Profile });
        var residualCount = 0;
        if (orders.ParsedBody is { } ob &&
            ob.TryGetProperty("data", out var od) &&
            od.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in od.EnumerateArray())
            {
                string? coid =
                    row.TryGetProperty("clientOrderId", out var c1) && c1.ValueKind == JsonValueKind.String ? c1.GetString() :
                    row.TryGetProperty("ClientOrderId", out var c2) && c2.ValueKind == JsonValueKind.String ? c2.GetString() :
                    null;
                if (string.IsNullOrEmpty(coid)) continue;
                if (coid!.StartsWith("campaignE", StringComparison.OrdinalIgnoreCase))
                {
                    residualCount++;
                    var cancelResp = await _mcp.CallTool("mt_orders_cancel", new
                    {
                        client_order_id = coid,
                        confirm = true,
                        profile = Profile,
                    });
                    WriteCleanup($"cancel_{coid}", cancelResp);
                }
            }
        }
        // Idempotent final sweep — cancel_all BTCUSDT to catch anything missed.
        await _mcp.CallTool("mt_orders_cancel_all", new
        {
            symbol = "BTCUSDT",
            confirm = true,
            profile = Profile,
        });

        // Record final state for the evidence doc.
        await _mcp.CallTool("mt_orders_list", new { profile = Profile });
        await _mcp.CallTool("mt_account_positions", new { profile = Profile });
    }

    private static string? ReadId(JsonElement row)
    {
        if (!row.TryGetProperty("id", out var idEl)) return null;
        return idEl.ValueKind == JsonValueKind.Number
            ? idEl.GetInt64().ToString()
            : idEl.GetString();
    }

    private static void WriteCleanup(string tag, McpResponse resp)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "campaign-2026-05-12");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "Zz_cleanup.jsonl");
        var rec = new
        {
            ts_utc = DateTime.UtcNow.ToString("o"),
            tag = tag,
            inner_success = !resp.IsRpcError && !resp.IsToolError && resp.InnerSuccess,
            inner_message = resp.InnerMessage,
            response = resp.Text,
        };
        System.IO.File.AppendAllText(path,
            System.Text.Json.JsonSerializer.Serialize(rec) + Environment.NewLine);
    }
}
