using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 6.9 — Smoke for mt_exchange_funding_rate + mt_exchange_leverage_brackets.
///
/// Discovery findings:
///   • Funding rate fields live on <c>MTShared.LiveMarket.LiveMarketMetrics</c>
///     (LastFundingRate Single, LastFundingTime Int64, NextFundingRate Single,
///     NextFundingTime Int64).  Pushed via the existing live-markets subscription;
///     no standalone SendFundingRateRequest RPC.
///   • LeverageBracket* types do NOT exist in MTShared.  Closest match:
///     <c>MTShared.Network.LeverageInfoUpdateData</c> with three dictionaries
///     (leverages, maxLeverages, riskLimits) — not currently surfaced via a
///     public store on CoreConnection.  The tool returns the per-position
///     leverage from AccountStore when available, plus a structured notice.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage69ExchangeFundingLeverageTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage69ExchangeFundingLeverageTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_exchange_funding_rate_returns_typed_fields_for_btcusdt()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_exchange_funding_rate", new
        {
            symbol = "btcusdt", market_type = "FUTURES", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(20));
        resp.IsRpcError.Should().BeFalse();
        // Funding rate may be 0 if the cache hasn't received a push yet — what
        // matters is that the call returns a structured payload with the
        // documented field names.  Soft-validate the shape; don't hard-assert
        // numeric values which are timing-dependent.
        if (resp.InnerSuccess == true)
        {
            var data = resp.ParsedBody!.Value.GetProperty("data");
            data.TryGetProperty("LastFundingRate", out _).Should().BeTrue();
            data.TryGetProperty("NextFundingRate", out _).Should().BeTrue();
            data.TryGetProperty("LastFundingTimeUnixMs", out _).Should().BeTrue();
            data.TryGetProperty("NextFundingTimeUnixMs", out _).Should().BeTrue();
            data.TryGetProperty("Source", out _).Should().BeTrue();
        }
        else
        {
            // Acceptable miss path: the subscription didn't push in 6s.  The
            // tool's error message must be the structured 'funding_rate_unavailable:'.
            resp.InnerMessage!.Should().Contain("funding_rate_unavailable");
        }
    }

    [SkippableFact]
    public async Task mt_exchange_leverage_brackets_returns_structured_notice_with_observed_leverage_or_null()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_exchange_leverage_brackets", new
        {
            symbol = "btcusdt", market_type = "FUTURES", profile = Profile,
        });
        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue(
            because: "leverage-brackets is purely read-only; it should always return a structured envelope");

        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.GetProperty("BracketsExposedByMtShared").GetBoolean().Should().BeFalse(
            because: "MTShared has NO LeverageBracket* type; the tool surfaces this honest finding");
        data.GetProperty("Notice").GetString()
            .Should().Contain("leverage_brackets_not_exposed_by_mtshared");
        data.TryGetProperty("ObservedPositionLeverage", out _).Should().BeTrue();
    }
}
