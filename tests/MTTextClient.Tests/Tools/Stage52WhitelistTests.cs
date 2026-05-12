using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 5.2 — Smoke coverage for the profile-level WhiteList typed CRUD.
/// Five tools: list (read-only), add / remove (single), bulk_add / bulk_remove
/// (csv).  All four mutating tools are confirm-required.  Each typed entry is
/// {MarketType, QuoteAsset, Symbol?, TimeFilter:{}} — mirrors BlackList shape.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage52WhitelistTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage52WhitelistTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_whitelist_add_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable; skipping.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_whitelist_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt", symbol = "btcusdt",
            profile = Profile,
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue(because: "ConfirmGate rejects whitelist mutations without confirm");
    }

    [SkippableFact]
    public async Task mt_whitelist_remove_nonexistent_returns_structured_not_found()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable; skipping.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_whitelist_remove", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "nopesentinel000",
            confirm = true,
            profile = Profile,
        });
        resp.InnerSuccess.Should().BeFalse();
        resp.InnerMessage!.Should().Contain("not_found");
    }

    [SkippableFact]
    public async Task mt_whitelist_round_trip_single_add_then_remove_restores_baseline()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable; skipping.");
        await _mcp.WaitForConnected(Profile);

        // bench_02 carries pre-existing entries; we add a sentinel and verify the
        // count grew by 1, then remove and verify restore.
        int before = await ReadSymbolCount(Profile);

        var addResp = await _mcp.CallTool("mt_whitelist_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "wlsmoketest",
            confirm = true,
            profile = Profile,
        });
        addResp.IsRpcError.Should().BeFalse();
        addResp.InnerSuccess.Should().BeTrue(because: "single add: " + addResp.InnerMessage);
        int after = await ReadSymbolCount(Profile);
        after.Should().Be(before + 1, because: "single add must grow the typed WhiteList.Symbols by 1");

        var rmResp = await _mcp.CallTool("mt_whitelist_remove", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "wlsmoketest",
            confirm = true,
            profile = Profile,
        });
        rmResp.IsRpcError.Should().BeFalse();
        rmResp.InnerSuccess.Should().BeTrue();

        (await ReadSymbolCount(Profile)).Should().Be(before,
            because: "remove must restore the baseline");
    }

    [SkippableFact]
    public async Task mt_whitelist_add_already_present_is_idempotent_no_change()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable; skipping.");
        await _mcp.WaitForConnected(Profile);

        var first = await _mcp.CallTool("mt_whitelist_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "wlidem", confirm = true, profile = Profile,
        });
        first.InnerSuccess.Should().BeTrue(because: "first add must succeed: " + first.InnerMessage);

        var second = await _mcp.CallTool("mt_whitelist_add", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "wlidem", confirm = true, profile = Profile,
        });
        second.InnerSuccess.Should().BeTrue(because: "idempotent re-add must not fail");
        second.InnerMessage!.Should().Contain("no_change");

        // Cleanup.
        await _mcp.CallTool("mt_whitelist_remove", new
        {
            type = "symbol", market = "FUTURES", quote = "usdt",
            symbol = "wlidem", confirm = true, profile = Profile,
        });
    }

    private async Task<int> ReadSymbolCount(string profile)
    {
        var resp = await _mcp.CallTool("mt_whitelist_list", new { profile });
        if (!resp.InnerSuccess) return -1;
        return resp.ParsedBody!.Value.GetProperty("data").GetProperty("SymbolCount").GetInt32();
    }
}
