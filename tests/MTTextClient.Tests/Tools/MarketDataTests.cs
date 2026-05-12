using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Real-time market data subscriptions. 16 tools: depth/klines/markprice/
/// status/ticker/trades, each with subscribe/unsubscribe pair, plus the
/// non-subscribe reads (depth, klines, markprice, ticker, trades).
///
/// We test one subscribe/unsubscribe pair per channel; full coverage of all
/// 16 is left to feature iterations that touch the marketdata surface.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class MarketDataTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public MarketDataTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_marketdata_status_returns_success_with_text_listing_channels()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_marketdata_status",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // The handler returns text-only Ok (no structured data). The text body
        // lists the channel summaries: Trades, Depth, Klines, MarkPrice.
        // Strengthening: assert the message contains at least one channel name.
        resp.ParsedBody!.Value.TryGetProperty("message", out var msg).Should().BeTrue();
        string text = msg.GetString() ?? "";
        text.Should().ContainAny(new[] { "Trades", "Depth", "Klines", "MarkPrice", "Subscriptions" },
            because: "the marketdata status text lists the per-channel subscription state");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_marketdata_depth_subscribe_unsubscribe_roundtrip_both_success()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var args = new { symbol = "btcusdt", profile = EnvFlags.DefaultBenchProfile };
        var sub = await _mcp.CallTool("mt_marketdata_depth_subscribe", args);
        sub.InnerSuccess.Should().BeTrue();
        sub.ParsedBody!.Value.TryGetProperty("success", out var ss).Should().BeTrue();
        ss.GetBoolean().Should().BeTrue();
        // The subscribe response message echoes the symbol — verify the channel
        // identifier is round-tripped to the operator (no opaque ID is emitted).
        sub.ParsedBody!.Value.TryGetProperty("message", out var smsg).Should().BeTrue();
        // The MCP server upper-cases the symbol before placing it in the response
        // text (real format: "Subscribed to depth for BTCUSDT (FUTURES) on bench_01.").
        // Use a case-insensitive contains check.
        (smsg.GetString() ?? "").ToUpperInvariant().Should().Contain("BTCUSDT",
            because: "subscribe response should echo the symbol (uppercase) so the operator can correlate it");

        var unsub = await _mcp.CallTool("mt_marketdata_depth_unsubscribe", args);
        unsub.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_marketdata_klines_subscribe_unsubscribe_roundtrip_both_success()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var args = new { symbol = "btcusdt", interval = "1m", profile = EnvFlags.DefaultBenchProfile };
        (await _mcp.CallTool("mt_marketdata_klines_subscribe", args)).InnerSuccess.Should().BeTrue();
        (await _mcp.CallTool("mt_marketdata_klines_unsubscribe", args)).InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_marketdata_markprice_subscribe_unsubscribe_roundtrip_both_success()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var args = new { symbol = "btcusdt", profile = EnvFlags.DefaultBenchProfile };
        (await _mcp.CallTool("mt_marketdata_markprice_subscribe", args)).InnerSuccess.Should().BeTrue();
        (await _mcp.CallTool("mt_marketdata_markprice_unsubscribe", args)).InnerSuccess.Should().BeTrue();
    }
}
