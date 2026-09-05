using System.Reflection;
using FluentAssertions;
using MTShared.Algorithms;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class PublicIssueRegressionUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue44_import_templates_from_live_core_args_not_stale_file()
    {
        // The reporter's path: a V2 file exported from an older core carries an
        // argument set that predates the connected core, which rejects it on
        // start. Seeding the parser from the core's live config-list template
        // (current-version argsJson) makes the imported algorithm carry the full
        // current argument set, with the file's explicit overrides applied.
        var parser = new V2FormatParser(""); // no bundled file
        parser.SetTemplates(new[]
        {
            new AlgorithmData
            {
                name = "Shots Group",
                signature = "SG",
                groupType = AlgorithmGroupType.SHOTS,
                isTradingAlgo = true,
                // current core arg set — includes a NEW arg older files lack
                argsJson = """{"Arguments":{"distance":{"value":1.0},"toggleRiskLimitFilter":{"value":false}}}""",
            },
        });

        const string v2 = "VERSION: 2\n###START###\nalgorithmName=0=Shots Group;\nversion=0=9;\ngroupId=0=0;\ndistance=4=5.5;\n";
        V2FormatParser.ParseResult result = parser.Parse(v2);

        result.Algorithms.Should().HaveCount(1);
        var args = JObject.Parse(result.Algorithms[0].argsJson!)["Arguments"] as JObject;
        args.Should().NotBeNull();
        // file override applied
        args!["distance"]!["value"]!.Value<double>().Should().Be(5.5);
        // new current-version arg present (would be missing from a stale file)
        args.Should().ContainKey("toggleRiskLimitFilter");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Import_parsers_are_isolated_so_concurrent_imports_cannot_cross_contaminate()
    {
        // Parallel dispatch runs different-profile imports concurrently. Each
        // reseeds its parser from its OWN connected core (SetTemplates). If the
        // parser were shared, one venue's template set would overwrite another's
        // mid-flight and algos would be SAVEd with the wrong core's argument set
        // (a silent reintroduction of #44). ImportCommand now uses a per-call
        // parser; this pins the property that makes that safe — two parser
        // instances are fully independent.
        const string v2 = "VERSION: 2\n###START###\nalgorithmName=0=X;\nversion=0=9;\ngroupId=0=0;\ndistance=4=1.0;\n";

        var bybit = new V2FormatParser("");
        bybit.SetTemplates(new[] { new AlgorithmData { name = "X", signature = "SA",
            groupType = AlgorithmGroupType.SHOTS,
            argsJson = """{"Arguments":{"distance":{"value":0},"venueMarker":{"value":"BYBIT"}}}""" } });

        var binance = new V2FormatParser("");
        binance.SetTemplates(new[] { new AlgorithmData { name = "X", signature = "SA",
            groupType = AlgorithmGroupType.SHOTS,
            argsJson = """{"Arguments":{"distance":{"value":0},"venueMarker":{"value":"BINANCE"}}}""" } });

        // Interleave the reseed+parse the way two concurrent imports would.
        var a = JObject.Parse(bybit.Parse(v2).Algorithms[0].argsJson!)["Arguments"]!;
        var b = JObject.Parse(binance.Parse(v2).Algorithms[0].argsJson!)["Arguments"]!;

        a["venueMarker"]!["value"]!.Value<string>().Should().Be("BYBIT");
        b["venueMarker"]!["value"]!.Value<string>().Should().Be("BINANCE",
            because: "each parser keeps its own core's templates — no cross-contamination");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue44_create_does_not_inject_synthetic_args_into_wire()
    {
        // MTCore 0.7.24554's argument parser rejects unknown synthetic keys in
        // the algorithm arguments, so a created algorithm carrying the old
        // `_mcp_metadata` block could not be started (Value cannot be null).
        // The create path's evidence helper must count passthrough args WITHOUT
        // mutating argsJson.
        const string argsJson = """{"Arguments":{"info":{"value":"x"},"customField":{"value":1}}}""";
        var algo = new AlgorithmData { argsJson = argsJson };

        MethodInfo count = typeof(AlgosCommand).GetMethod(
            "CountTemplateUnknownArgs", BindingFlags.NonPublic | BindingFlags.Static)!;
        int unknown = (int)count.Invoke(null, new object[] { algo })!;

        // argsJson is untouched — no synthetic key injected.
        algo.argsJson.Should().Be(argsJson);
        algo.argsJson.Should().NotContain("_mcp");
        // customField is unknown to the MCP layer; info is known.
        unknown.Should().BeGreaterThan(0);
    }

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

        var listEvent = new AlgorithmListEventData
        {
            Data = new AlgorithmListData
            {
                algorithms = new List<AlgorithmData>
                {
                    new() { id = 7, name = "MW", signature = "MW" },
                },
            },
        };
        store.ProcessData(NetworkMessageType.ALGORITHMS_RESULT, listEvent);

        store.LastUpdateUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        store.Count.Should().Be(1);

        store.Clear();
        store.LastUpdateUtc.Should().Be(default);
    }
}
