using System.Reflection;
using FluentAssertions;
using MTShared.Algorithms;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Commands;
using MTTextClient.Core;
using Newtonsoft.Json;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class PublicIssueRegressionUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_autostops_parser_accepts_real_mtshared_array_shape()
    {
        string raw = JsonConvert.SerializeObject(new[]
        {
            new AutoStopAlgorithmData
            {
                id = 123,
                info = "risk guard",
                marketType = MarketType.FUTURES,
                minMargin = -5,
                isRunning = true,
                asset = "usdt",
                panicIfTriggered = true,
                timeFrame = AutoStopsTimeFrame.D1,
                symbolFilter = "btcusdt",
                marketTypes = new List<MarketType>(),
            }
        });

        (bool ok, List<AutoStopAlgorithmData> filters, string? error) = InvokeAutoStopsParser(raw);

        ok.Should().BeTrue(error);
        filters.Should().ContainSingle();
        filters[0].minMargin.Should().Be(-5);
        filters[0].symbolFilter.Should().Be("btcusdt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_autostops_parser_rejects_legacy_wrapper_shape()
    {
        const string raw = """
        {"isEnabled":true,"Values":[{"isEnabled":true,"valueRange":{"min":-5.0}}]}
        """;

        (bool ok, List<AutoStopAlgorithmData> filters, string? error) = InvokeAutoStopsParser(raw);

        ok.Should().BeFalse();
        filters.Should().BeEmpty();
        error.Should().Contain("legacy mt-text-client wrapper");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue17_report_csv_includes_trade_context_fields()
    {
        ReportData report = MakeReport();

        string csv = ReportCsvExporter.GenerateCsv(new List<ReportData> { report }, "srv");

        // Header carries the four restored columns plus DistanceAtOrder.
        csv.Should().Contain("AlgoInfo,OrderComment,OrderOpenByComment,AlgoSource");
        csv.Should().Contain("DistanceAtOrder");
        // Row values, not just header text.
        csv.Should().Contain("closed by strategy");
        csv.Should().Contain("operator label");
        csv.Should().Contain("SG: operator label");
        csv.Should().Contain("0.123456");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("SG", "open label", "SG: open label")]
    [InlineData("SG", "", "SG")]
    [InlineData("00", "open label", "Manual: open label")]
    [InlineData(null, "", "Manual")]
    public void Issue17_algo_source_fallback_chain(
        string? signature, string openComment, string expected)
    {
        // Pins the {signature}: {info|orderOpenByComment|algoName} composite
        // used by both the per-trade JSON records and the CSV export, with
        // blank/"00" signatures normalized to Manual. The AlgorithmInfo.info
        // priority branch is not constructible in-process (the property
        // resolves through the shared library's own lookup), so it is
        // exercised on bench data; the openComment fallback and signature
        // normalization are pinned here.
        var report = new ReportData
        {
            orderInfo = new OrderInfoData
            {
                signature = signature!,
                orderOpenByComment = openComment,
            },
        };

        MethodInfo method = typeof(ReportsCommand).GetMethod(
            "BuildAlgoSource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        string actual = (string)method.Invoke(null, new object[] { report })!;

        actual.Should().Be(expected);
    }

    private static ReportData MakeReport() => new()
    {
        id = 1,
        reportOpenTime = 1_700_000_000_000,
        reportTime = 1_700_000_001_000,
        marketType = MarketType.FUTURES,
        symbol = "btcusdt",
        orderSideType = OrderSideType.BUY,
        priceOpen = 100,
        priceClose = 101,
        orderInfo = new OrderInfoData
        {
            algorithmId = 42,
            signature = "SG",
            orderComment = "closed by strategy",
            orderOpenByComment = "operator label",
            algorithmGroupType = AlgorithmGroupType.SHOTS,
        },
        distanceAtOrder = 0.123456,
    };

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue40_algorithm_store_tracks_last_wire_update()
    {
        // mt_algos_snapshot's freshness fields (source/age_ms/last_update)
        // ride this store-level timestamp; pin that it is unset before any
        // wire drop, set by ProcessData, and reset by Clear().
        var store = new AlgorithmStore();
        store.LastUpdateUtc.Should().Be(default);

        var listData = new AlgorithmListData
        {
            algorithms = new List<AlgorithmData>
            {
                new() { id = 7, name = "MW", signature = "MW" },
            },
        };
        store.ProcessData(NetworkMessageType.ALGORITHM_LIST_RESULT, listData);

        store.LastUpdateUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        store.Count.Should().Be(1);

        store.Clear();
        store.LastUpdateUtc.Should().Be(default);
    }

    private static (bool Ok, List<AutoStopAlgorithmData> Filters, string? Error) InvokeAutoStopsParser(string raw)
    {
        MethodInfo method = typeof(AutoStopsCommand).GetMethod(
            "TryParseBalanceFilters",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = method.Invoke(null, new object?[] { raw })!;
        Type t = result.GetType();
        return (
            (bool)t.GetField("Item1")!.GetValue(result)!,
            (List<AutoStopAlgorithmData>)t.GetField("Item2")!.GetValue(result)!,
            (string?)t.GetField("Item3")!.GetValue(result));
    }
}
