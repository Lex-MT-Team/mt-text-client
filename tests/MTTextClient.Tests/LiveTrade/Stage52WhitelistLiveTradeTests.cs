using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Stage 5.2 LiveTrade — full profile-level WhiteList CRUD round-trip on a
/// real bench (bench_02 BINANCE FUTURES). Adds one entry, bulk-adds two more,
/// bulk-removes all three, verifies baseline restored exactly.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Stage52WhitelistLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage52WhitelistLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task FullCrudRoundTrip_Add_BulkAdd_BulkRemove_RestoresBaseline()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — Stage 5.2 LiveTrade mutates profile WhiteList settings.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile),
            $"Bench {Profile} not observed on UDP port; skipping.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        int before = await ReadCount(Profile);

        // 1) Single add — ETHUSDT under FUTURES/usdt.
        var addOne = await _mcp.CallTool("mt_whitelist_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "ethusdt",
            confirm = true, profile = Profile,
        });
        addOne.IsRpcError.Should().BeFalse();
        addOne.InnerSuccess.Should().BeTrue(because: "single add: " + addOne.InnerMessage);
        (await ReadCount(Profile)).Should().Be(before + 1);

        // 2) Bulk add — SOLUSDT, XRPUSDT under FUTURES/usdt.
        var addMany = await _mcp.CallTool("mt_whitelist_bulk_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbols = "solusdt,xrpusdt",
            confirm = true, profile = Profile,
        });
        addMany.IsRpcError.Should().BeFalse();
        addMany.InnerSuccess.Should().BeTrue(because: "bulk add: " + addMany.InnerMessage);
        (await ReadCount(Profile)).Should().Be(before + 3);

        // 3) Bulk remove all three.
        var rmMany = await _mcp.CallTool("mt_whitelist_bulk_remove", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbols = "ethusdt,solusdt,xrpusdt",
            confirm = true, profile = Profile,
        });
        rmMany.IsRpcError.Should().BeFalse();
        rmMany.InnerSuccess.Should().BeTrue(because: "bulk remove: " + rmMany.InnerMessage);

        // 4) Baseline restored.
        (await ReadCount(Profile)).Should().Be(before,
            because: "Stage 5.2 CRUD round-trip must leave WhiteList.Symbols at the same count it found");
    }

    private async Task<int> ReadCount(string profile)
    {
        var resp = await _mcp.CallTool("mt_whitelist_list", new { profile });
        if (!resp.InnerSuccess) return -1;
        return resp.ParsedBody!.Value.GetProperty("data").GetProperty("SymbolCount").GetInt32();
    }
}
