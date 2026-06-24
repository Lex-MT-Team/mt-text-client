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
    // Issue #34 was originally fixed (PR #41) by writing the
    // AutoStopAlgorithm.Balance.Filters settings blob as a real
    // AutoStopAlgorithmData[]. MTCore 0.7.24554 removed that type and the
    // settings-blob model entirely in favour of the live AUTO_STOP
    // request/event subsystem, so the parser these tests guarded no longer
    // exists. The regression guard now pins the new wire model: the store
    // ingests the core's AutoStopListEvent snapshot keyed by id, and a balance
    // request carries the real vendor AutoStopOnBalanceData (not a client blob).
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_autostop_store_ingests_balance_snapshot_by_id()
    {
        var store = new AutoStopStore();
        store.HasData.Should().BeFalse();

        var snapshot = new AutoStopListEvent
        {
            AutoStopsOnBalance = new List<AutoStopOnBalanceData>
            {
                new() { id = 123, name = "risk guard", marketType = MarketType.FUTURES, maxLoss = -5, asset = "usdt", keywords = "btcusdt", panicSellIfTriggered = true, isRunning = true },
                new() { id = 456, name = "second", marketType = MarketType.FUTURES, maxLoss = -10, asset = "usdt", isRunning = false },
            },
            AutoStopsOnReports = new List<AutoStopOnReportsData>(),
        };
        store.ProcessEvent(snapshot);

        store.HasData.Should().BeTrue();
        store.Balance.Should().HaveCount(2);
        store.FindBalanceById(123)!.maxLoss.Should().Be(-5);
        store.FindBalanceById(123)!.keywords.Should().Be("btcusdt");
        store.FindBalanceById(999).Should().BeNull();

        // Removed event drops by id; the snapshot order is stable by id.
        store.ProcessEvent(new AutoStopOnBalanceRemovedEvent { AutoStopIds = new List<long> { 123 } });
        store.Balance.Should().ContainSingle().Which.id.Should().Be(456);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_balance_request_carries_real_vendor_type()
    {
        var autostop = new AutoStopOnBalanceData { id = 0, name = "g", marketType = MarketType.FUTURES, maxLoss = -5, asset = "usdt" };
        var req = new AutoStopOnBalanceAddRequestData { AutoStop = autostop };

        // The request wraps the genuine MTShared.Network.AutoStopOnBalanceData —
        // no client-invented JSON blob — and self-stamps its RequestType.
        req.AutoStop.Should().BeSameAs(autostop);
        req.RequestType.Should().Be(nameof(AutoStopOnBalanceAddRequestData));
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
}
