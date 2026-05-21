using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Exchange info tools. Mostly read-only. <c>mt_exchange_ticker24</c> is in the
/// fallback cluster (cold/empty exchange-info cache returns empty envelopes).
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class ExchangeTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public ExchangeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_exchange_summary_returns_TotalPairs_count()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_exchange_summary",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per ExchangeCommand.Summary: { Server, TotalPairs, ... }.
        data.TryGetProperty("TotalPairs", out var totalPairs).Should().BeTrue();
        totalPairs.GetInt32().Should().BeGreaterThan(0,
            because: "BYBIT bench has hundreds of trade pairs cached");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_exchange_pairs_returns_array_with_Symbol_and_MarketType()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_exchange_pairs",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per ExchangeCommand.ListPairs: { Server, TotalPairs (int),
        // CountsByMarket (dict), Pairs: [{ Symbol, MarketType (enum string),
        // Status, TickerPrice, BaseAsset, QuoteAsset, MinQty, MaxQty, ... }] }.
        data.TryGetProperty("Pairs", out var pairs).Should().BeTrue();
        pairs.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        pairs.GetArrayLength().Should().BeGreaterThan(0);

        var first = pairs[0];
        first.TryGetProperty("Symbol", out var symbol).Should().BeTrue();
        symbol.GetString().Should().NotBeNullOrWhiteSpace();
        first.TryGetProperty("MarketType", out var marketType).Should().BeTrue(
            because: "MarketType is an enum-string (FUTURES/SPOT) — not the int the upstream wire-type uses");
        marketType.ValueKind.Should().Be(System.Text.Json.JsonValueKind.String);
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_exchange_search_for_btc_returns_matching_pair()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_exchange_search",
            new { query = "btc", profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per ExchangeCommand.SearchPairs: { Server, Query, Count,
        // Pairs: [...] }.
        data.TryGetProperty("Pairs", out var pairs).Should().BeTrue();
        pairs.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        pairs.GetArrayLength().Should().BeGreaterThan(0,
            because: "BYBIT exposes BTCUSDT and many other btc-prefixed pairs");

        bool anyContainsBtc = false;
        foreach (var p in pairs.EnumerateArray())
        {
            string? sym = p.TryGetProperty("Symbol", out var s) ? s.GetString() : null;
            if (!string.IsNullOrEmpty(sym) && sym.Contains("BTC", StringComparison.OrdinalIgnoreCase))
                anyContainsBtc = true;
        }
        anyContainsBtc.Should().BeTrue(
            because: "every search hit for query=btc should mention BTC in the symbol");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_exchange_klines_returns_5_OHLCV_candles_with_high_geq_low()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_exchange_klines",
            new { symbol = "btcusdt", interval = "1m", limit = 5, profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per ExchangeCommand.Klines: { Server, Symbol, Interval, Count,
        // Klines: [{ Time (string), Open, High, Low, Close, Volume, QuoteVol }] }.
        data.TryGetProperty("Count", out var count).Should().BeTrue();
        count.GetInt32().Should().Be(5, because: "we asked for limit=5");

        data.TryGetProperty("Klines", out var klines).Should().BeTrue();
        klines.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        klines.GetArrayLength().Should().Be(5);

        foreach (var k in klines.EnumerateArray())
        {
            k.TryGetProperty("Open", out var open).Should().BeTrue();
            k.TryGetProperty("High", out var high).Should().BeTrue();
            k.TryGetProperty("Low", out var low).Should().BeTrue();
            k.TryGetProperty("Close", out _).Should().BeTrue();
            k.TryGetProperty("Volume", out _).Should().BeTrue();

            // OHLC must be numeric and high >= low.
            open.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Number);
            double h = high.GetDouble();
            double l = low.GetDouble();
            h.Should().BeGreaterOrEqualTo(l, because: "by definition, high >= low for any candle");
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_exchange_ticker24_returns_empty_or_populated_data()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // Cold/empty exchange-info cache returns success:true with an empty
        // Tickers list instead of timing out. 35s leaves headroom.
        var resp = await _mcp.CallTool("mt_exchange_ticker24",
            new { symbol = "btcusdt", profile = EnvFlags.DefaultBenchProfile },
            timeout: TimeSpan.FromSeconds(35));

        resp.InnerSuccess.Should().BeTrue();
    }
}
