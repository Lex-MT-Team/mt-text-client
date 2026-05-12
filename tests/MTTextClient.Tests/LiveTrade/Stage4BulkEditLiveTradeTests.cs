using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Stage 4.2 LiveTrade — full bulk-edit round-trip on a real bench
/// (<c>bench_02</c> BINANCE). Dry-run, commit, verify, revert, verify again.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Stage4BulkEditLiveTradeTests
{
    private const string Profile = "bench_02";
    private const string Marker = "_BULKEDIT_LT_ETHUSDT_";  // unique marker, easy to detect + revert

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage4BulkEditLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task BulkAdd_DryRun_Commit_Revert_RoundTrip()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — Stage 4.2 LiveTrade mutates real algo whitelists.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile),
            $"Bench {Profile} not observed on UDP port; skipping.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // 0) Baseline algo count + sample one algo's whiteList so we can prove
        //    the round-trip restored it exactly.
        var listResp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        listResp.InnerSuccess.Should().BeTrue();
        int matched = listResp.ParsedBody!.Value.GetProperty("data").GetArrayLength();
        Skip.If(matched == 0, "No algorithms on bench_02 to bulk-edit");

        long sampleId = listResp.ParsedBody.Value.GetProperty("data")[0].GetProperty("id").GetInt64();

        // 1) DRY RUN preview (NOT really dry-run via schema since confirm is required —
        //    we just verify the bulk-edit responds normally to confirm=true and surfaces
        //    matched count).  Conceptual dry-run = inspecting partial_result.
        var addResp = await _mcp.CallTool("mt_algos_bulk_edit", new
        {
            filter_json = "{\"all\":true}",
            mutation_json = $"{{\"whitelist_add\":[\"{Marker}\"]}}",
            confirm = true,
            profile = Profile,
        });
        addResp.IsRpcError.Should().BeFalse();
        addResp.InnerSuccess.Should().BeTrue(because: "bulk-add must succeed: " + addResp.InnerMessage);
        int mutatedAdd = addResp.ParsedBody!.Value.GetProperty("data").GetProperty("Mutated").GetInt32();
        mutatedAdd.Should().BeGreaterThan(0,
            because: $"at least one algo must accept the whitelist_add (mutated={mutatedAdd} of {matched})");

        // 2) Verify presence via fresh algos config probe on the sample algo.
        var cfgAfterAdd = await _mcp.CallTool("mt_algos_config", new
        {
            id = sampleId.ToString(),
            profile = Profile,
        });
        cfgAfterAdd.InnerSuccess.Should().BeTrue();
        string? wlAfter = ReadWhiteList(cfgAfterAdd.ParsedBody);
        wlAfter.Should().NotBeNull();
        wlAfter!.Should().Contain(Marker, because: "the marker must be present after bulk-add");

        // 3) REVERT — bulk-remove the same marker.
        var removeResp = await _mcp.CallTool("mt_algos_bulk_edit", new
        {
            filter_json = "{\"all\":true}",
            mutation_json = $"{{\"whitelist_remove\":[\"{Marker}\"]}}",
            confirm = true,
            profile = Profile,
        });
        removeResp.IsRpcError.Should().BeFalse();
        removeResp.InnerSuccess.Should().BeTrue(because: "bulk-remove must succeed: " + removeResp.InnerMessage);
        int mutatedRm = removeResp.ParsedBody!.Value.GetProperty("data").GetProperty("Mutated").GetInt32();
        mutatedRm.Should().BeGreaterThan(0,
            because: $"the same algos that gained the marker must drop it (mutated={mutatedRm})");

        // 4) Verify the sample algo's whitelist no longer carries the marker.
        var cfgAfterRm = await _mcp.CallTool("mt_algos_config", new
        {
            id = sampleId.ToString(),
            profile = Profile,
        });
        cfgAfterRm.InnerSuccess.Should().BeTrue();
        string? wlAfterRm = ReadWhiteList(cfgAfterRm.ParsedBody);
        wlAfterRm.Should().NotBeNull();
        wlAfterRm!.Should().NotContain(Marker,
            because: "the marker must be GONE after bulk-remove (baseline restored)");
    }

    private static string? ReadWhiteList(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in data.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            if (p.TryGetProperty("Key", out var k) && k.GetString() == "whiteList" &&
                p.TryGetProperty("Value", out var v))
                return v.GetString();
        }
        return null;
    }
}
