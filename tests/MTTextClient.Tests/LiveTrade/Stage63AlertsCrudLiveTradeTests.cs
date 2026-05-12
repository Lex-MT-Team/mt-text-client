using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Stage 6.3 LiveTrade — full save → list → stop → delete round-trip
/// against bench_02 BINANCE.  The alert is created with an
/// out-of-range ref price (1.0 USDT CROSSING DOWN on BTCUSDT FUTURES)
/// so it cannot trigger on real price action; we then exercise the
/// stop and delete paths and verify the AlertStore observes the
/// disappearance.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Stage63AlertsCrudLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage63AlertsCrudLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task Save_List_Stop_Delete_RestoresBaseline()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — Stage 6.3 LiveTrade mutates alerts state.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // Subscribe so list reflects the latest server state.
        var subResp = await _mcp.CallTool("mt_alerts_subscribe", new { profile = Profile });
        subResp.IsRpcError.Should().BeFalse();
        await Task.Delay(500);

        var listBefore = await _mcp.CallTool("mt_alerts_list", new { profile = Profile });
        int baselineCount = ExtractCountFromMarkdownTable(GetMessage(listBefore.ParsedBody));

        string alertName = $"stage63-livetrade-{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // 1) Save (create).
        var saveResp = await _mcp.CallTool("mt_alerts_save", new
        {
            name           = alertName,
            symbol         = "btcusdt",
            market_type    = "FUTURES",
            condition_type = "CROSSING",
            ref_price      = 1.0,
            direction      = "DOWN",
            repeat_type    = "ONLY_ONCE",
            profile        = Profile,
        });
        saveResp.IsRpcError.Should().BeFalse();
        saveResp.ParsedBody!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();

        // 2) List + locate the new alert by name.
        await Task.Delay(1500);
        var listAfter = await _mcp.CallTool("mt_alerts_list", new { profile = Profile });
        listAfter.IsRpcError.Should().BeFalse();
        (long alertId, bool wasRunning) = ExtractAlertIdAndState(GetMessage(listAfter.ParsedBody), alertName);
        alertId.Should().BeGreaterThan(0,
            because: $"the saved alert '{alertName}' must appear in the listed table with a server-assigned id. " +
                     "List message: " + GetMessage(listAfter.ParsedBody)?.Substring(0, Math.Min(400, GetMessage(listAfter.ParsedBody)?.Length ?? 0)));

        // 3) Stop the alert.
        var stopResp = await _mcp.CallTool("mt_alerts_set_running", new
        {
            running = false, alert_ids = alertId.ToString(),
            confirm = true, profile = Profile,
        });
        stopResp.IsRpcError.Should().BeFalse();
        stopResp.ParsedBody!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();

        // 4) Delete the alert (cleanup + the actual destructive path under test).
        var delResp = await _mcp.CallTool("mt_alerts_delete", new
        {
            alert_ids = alertId.ToString(), confirm = true, profile = Profile,
        });
        delResp.IsRpcError.Should().BeFalse();
        delResp.ParsedBody!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();

        // 5) Verify the alert is gone.
        await Task.Delay(1500);
        var listFinal = await _mcp.CallTool("mt_alerts_list", new { profile = Profile });
        (GetMessage(listFinal.ParsedBody) ?? "").Should().NotContain(alertName,
            because: "the deleted alert must no longer appear in the list");

        await WriteArtifact(new
        {
            Stage = "6.3",
            Profile,
            AlertName = alertName,
            AssignedAlertId = alertId,
            ServerSaveMessage = saveResp.ParsedBody!.Value.GetProperty("message").GetString(),
            ServerStopMessage = stopResp.ParsedBody!.Value.GetProperty("message").GetString(),
            ServerDeleteMessage = delResp.ParsedBody!.Value.GetProperty("message").GetString(),
            BaselineListCount = baselineCount,
            WasRunningBeforeStop = wasRunning,
            CrudPath = "save (CROSSING DOWN @1 USDT) → list → set_running(false) → delete → list verify gone",
            EndedAtUtc = System.DateTime.UtcNow,
        });
    }

    private static string? GetMessage(System.Text.Json.JsonElement? body)
    {
        if (body is not { } b) return null;
        if (!b.TryGetProperty("message", out var m)) return null;
        return m.GetString();
    }

    private static int ExtractCountFromMarkdownTable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        foreach (var line in text.Split('\n'))
            if (line.StartsWith("| ") && !line.StartsWith("| ID ") && !line.StartsWith("|---"))
                count++;
        return count;
    }

    private static (long id, bool running) ExtractAlertIdAndState(string? text, string name)
    {
        if (string.IsNullOrEmpty(text)) return (0, false);
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("| " + name + " |")) continue;
            var parts = line.Split('|');
            if (parts.Length < 6) continue;
            if (!long.TryParse(parts[1].Trim(), out long id)) continue;
            bool isRunning = parts[5].Trim().Equals("True", System.StringComparison.OrdinalIgnoreCase);
            return (id, isRunning);
        }
        return (0, false);
    }

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "stage6_3");
        Directory.CreateDirectory(dir);
        string fname = $"bench_02_{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
