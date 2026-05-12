using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Stage 6.9 LiveTrade — real-bench verification of mt_exchange_funding_rate
/// and mt_exchange_leverage_brackets against bench_02 BINANCE FUTURES.
///
/// Both tools are read-only — no mutation, no rollback needed.  The artifact
/// captures observed values (or the documented unavailable path for funding,
/// and the structured "not_exposed_by_mtshared" notice for brackets) so the
/// PoW doc can show the actual wire path that ran on live infrastructure.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Stage69ExchangeFundingLeverageLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage69ExchangeFundingLeverageLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task FundingRate_And_LeverageBrackets_Exercise_Live_BenchO2_Binance()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — Stage 6.9 LiveTrade exercises live bench wire path.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // ── 1) Funding rate — exercise the LiveMarket subscription path on a
        //       real symbol that should be subscribed by default on bench_02.
        var fundingResp = await _mcp.CallTool("mt_exchange_funding_rate", new
        {
            symbol = "btcusdt", market_type = "FUTURES", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(20));
        fundingResp.IsRpcError.Should().BeFalse();

        double? lastFundingRate = null, nextFundingRate = null;
        long? lastFundingTimeMs = null, nextFundingTimeMs = null;
        string fundingPath = "miss";
        string? fundingSource = null;

        if (fundingResp.InnerSuccess == true)
        {
            var data = fundingResp.ParsedBody!.Value.GetProperty("data");
            lastFundingRate = data.GetProperty("LastFundingRate").GetDouble();
            nextFundingRate = data.GetProperty("NextFundingRate").GetDouble();
            lastFundingTimeMs = data.GetProperty("LastFundingTimeUnixMs").GetInt64();
            nextFundingTimeMs = data.GetProperty("NextFundingTimeUnixMs").GetInt64();
            fundingSource = data.GetProperty("Source").GetString();
            fundingPath = "hit";
        }
        else
        {
            fundingResp.InnerMessage!.Should().Contain("funding_rate_unavailable",
                because: "the miss path MUST surface the structured 'funding_rate_unavailable:' marker");
        }

        // ── 2) Leverage brackets — exercise the AccountStore.Positions path.
        //       Tool is purely read-only and always returns a structured envelope.
        var bracketsResp = await _mcp.CallTool("mt_exchange_leverage_brackets", new
        {
            symbol = "btcusdt", market_type = "FUTURES", profile = Profile,
        });
        bracketsResp.IsRpcError.Should().BeFalse();
        bracketsResp.InnerSuccess.Should().BeTrue(
            because: "leverage-brackets is purely read-only; live: " + bracketsResp.InnerMessage);

        var bracketsData = bracketsResp.ParsedBody!.Value.GetProperty("data");
        bracketsData.GetProperty("BracketsExposedByMtShared").GetBoolean().Should().BeFalse();
        var notice = bracketsData.GetProperty("Notice").GetString();
        notice.Should().Contain("leverage_brackets_not_exposed_by_mtshared");

        short? observedLeverage = null;
        if (bracketsData.TryGetProperty("ObservedPositionLeverage", out var olv) && olv.ValueKind != JsonValueKind.Null)
            observedLeverage = (short)olv.GetInt16();

        // ── 3) Artifact — captures both wire paths as actually observed.
        await WriteArtifact(new
        {
            Stage = "6.9",
            Profile,
            Symbol = "btcusdt",
            MarketType = "FUTURES",
            FundingRate = new
            {
                Path = fundingPath,
                LastFundingRate = lastFundingRate,
                NextFundingRate = nextFundingRate,
                LastFundingTimeUnixMs = lastFundingTimeMs,
                NextFundingTimeUnixMs = nextFundingTimeMs,
                Source = fundingSource,
                MissMessage = fundingPath == "miss" ? fundingResp.InnerMessage : null,
            },
            LeverageBrackets = new
            {
                BracketsExposedByMtShared = false,
                Notice = notice,
                ObservedPositionLeverage = observedLeverage,
            },
            EndedAtUtc = System.DateTime.UtcNow,
        });
    }

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "stage6_9");
        Directory.CreateDirectory(dir);
        string fname = $"bench_02_{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
