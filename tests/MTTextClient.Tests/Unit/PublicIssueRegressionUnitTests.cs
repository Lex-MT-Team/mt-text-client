using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using MTShared.Algorithms;
using MTShared.Network;
using MTShared.Network.Notifications;
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
    [Fact]
    [Trait("Category", "Unit")]
    public void Algorithm_snapshots_replace_live_state_and_preserve_templates()
    {
        var store = new AlgorithmStore();
        void Apply(AlgorithmListData data) => store.ProcessData(
            NetworkMessageType.ALGORITHMS_RESULT, new AlgorithmListEventData { Data = data });
        Apply(new AlgorithmListData
        {
            isConfigList = true,
            algorithms = new() { new() { id = 99, signature = "SG" } },
        });
        Apply(new AlgorithmListData
        {
            algorithms = new() { new() { id = 1 }, new() { id = 2 } },
            groups = new() { new() { id = 10 } },
        });
        Apply(new AlgorithmListData { algorithms = new() { new() { id = 2 } } });
        store.GetAll().Select(a => a.id).Should().Equal(2);
        store.GroupCount.Should().Be(0);
        store.ConfigTemplateCount.Should().Be(1);
        Apply(new AlgorithmListData());
        store.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Algorithm_folder_deltas_preserve_other_folders_and_apply_removal()
    {
        var store = new AlgorithmStore();
        store.ProcessData(NetworkMessageType.ALGORITHMS_RESULT, new AlgorithmFoldersAddedEventData
        {
            Folders = new() { new() { id = 10, name = "first" }, new() { id = 20, name = "second" } },
        });
        store.ProcessData(NetworkMessageType.ALGORITHMS_RESULT, new AlgorithmFoldersUpdatedEventData
        {
            Folders = new() { new() { id = 20, name = "renamed" } },
        });
        store.FindGroupById(10)!.name.Should().Be("first");
        store.FindGroupById(20)!.name.Should().Be("renamed");
        store.ProcessData(NetworkMessageType.ALGORITHMS_RESULT, new AlgorithmFoldersRemovedEventData
        {
            Folders = new() { new() { id = 10 } },
        });
        store.GetAllGroups().Select(g => g.id).Should().Equal(20);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Imported_folder_with_existing_id_requests_add_and_preserves_destination()
    {
        using var connection = new CoreConnection(new ServerProfile());
        connection.AlgoStore.ProcessData(NetworkMessageType.ALGORITHMS_RESULT,
            new AlgorithmFoldersAddedEventData
            {
                Folders = new List<AlgorithmGroupData> { new() { id = 10, name = "existing" } }
            });
        var imported = new AlgorithmGroupData { id = 10, name = "imported" };
        MethodInfo build = typeof(CoreConnection).GetMethod("BuildGroupRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var request = build.Invoke(connection, new object[] { imported, AlgoActionType.ADD_GROUP });
        request.Should().BeOfType<AlgorithmFolderAddRequestData>();
        connection.AlgoStore.GetAllGroups().Single().name.Should().Be("existing");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("VELVETUSDT", "velvetusdt")]
    [InlineData("VelvetUsdt", "velvetusdt")]
    [InlineData("velvetusdt", "velvetusdt")]
    public void Panic_sell_normalizes_the_wire_symbol(string symbol, string expected)
    {
        MethodInfo? normalize = typeof(CoreConnection).GetMethod(
            "NormalizePanicSellSymbol", BindingFlags.NonPublic | BindingFlags.Static);
        normalize.Should().NotBeNull();

        string actual = (string)normalize!.Invoke(null, new object[] { symbol })!;

        actual.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Panic_sell_prefers_the_open_position_market_over_a_colliding_pair()
    {
        AccountStore account = CreatePanicSellAccount("velvetusdt", 1);
        var exchange = new ExchangeInfoStore();
        AddPanicSellPair(exchange, MarketType.SPOT);
        exchange.GetTradePair("VELVETUSDT")!.MarketType.Should().Be(MarketType.SPOT);

        MarketType market = ResolvePanicSellMarket(account, exchange);

        market.Should().Be(MarketType.FUTURES);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("velvetusdt", 0)]
    [InlineData("btcusdt", 1)]
    public void Panic_sell_uses_the_pair_market_without_a_matching_open_position(
        string positionSymbol, int positionAmount)
    {
        AccountStore account = CreatePanicSellAccount(positionSymbol, positionAmount);
        var exchange = new ExchangeInfoStore();
        AddPanicSellPair(exchange, MarketType.SPOT);

        MarketType market = ResolvePanicSellMarket(account, exchange);

        market.Should().Be(MarketType.SPOT);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Panic_sell_uses_futures_when_both_caches_are_empty()
    {
        MarketType market = ResolvePanicSellMarket(new AccountStore(), new ExchangeInfoStore());

        market.Should().Be(MarketType.FUTURES);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Panic_sell_returns_failure_for_a_negative_core_acknowledgement()
    {
        var acknowledgement = new PanicSellNotificationData
        {
            success = false,
            message = "There was nothing to sell"
        };

        CommandResult result = ConvertPanicSellAcknowledgement(acknowledgement, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("There was nothing to sell");
        result.Data.Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void Panic_sell_preserves_activation_in_successful_results(bool activate)
    {
        var acknowledgement = new PanicSellNotificationData
        {
            success = true,
            message = "Request accepted"
        };

        CommandResult result = ConvertPanicSellAcknowledgement(acknowledgement, activate);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Request accepted");
        JObject data = JObject.FromObject(result.Data!);
        data["Activated"]!.Value<bool>().Should().Be(activate);
        data["Symbol"]!.Value<string>().Should().Be("VELVETUSDT");
        data["Action"]!.Value<string>().Should().Be("PANIC_SELL");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Panic_sell_keeps_timeout_uncertainty_in_the_result_message()
    {
        CommandResult result = ConvertPanicSellAcknowledgement(null, true);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("timed out");
        result.Data.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Send_and_wait_returns_an_immediate_reply_without_waiting_for_timeout()
    {
        using var connection = new CoreConnection(new ServerProfile());
        connection.Circuit.RecordFailure();
        connection.Circuit.RecordFailure();
        var replied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> waiting = Task.Run(() => InvokeSendAndWait(connection, callback =>
        {
            callback("accepted");
            replied.SetResult(true);
        }, timeoutMs: 30_000));

        try
        {
            await replied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The reply is already available; a fixed sleep would miss this watchdog.
            string? result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

            result.Should().Be("accepted");
            connection.Circuit.ConsecutiveFails.Should().Be(0);
            connection.Circuit.IsClosed.Should().BeTrue();
            connection.RateLimit.TotalAllowed.Should().Be(1);
        }
        finally
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(40));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Send_and_wait_completes_on_a_delayed_reply_and_ignores_duplicates()
    {
        using var connection = new CoreConnection(new ServerProfile());
        var registered = new TaskCompletionSource<Action<string?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> waiting = Task.Run(() => InvokeSendAndWait(connection,
            callback => registered.SetResult(callback), timeoutMs: 30_000));

        try
        {
            Action<string?> callback = await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            waiting.IsCompleted.Should().BeFalse("no reply has arrived and the timeout has not elapsed");

            callback("first reply");
            Action duplicate = () => callback("duplicate reply");
            duplicate.Should().NotThrow();
            string? result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

            result.Should().Be("first reply");
            connection.RateLimit.TotalAllowed.Should().Be(1);
            connection.Circuit.ConsecutiveFails.Should().Be(0);

            // A callback retained by the sender must not affect a completed request.
            connection.Circuit.RecordFailure();
            duplicate.Should().NotThrow();
            (await waiting).Should().Be("first reply");
            connection.Circuit.ConsecutiveFails.Should().Be(1);
        }
        finally
        {
            await CompleteAndObserveWait(waiting, registered.Task);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Send_and_wait_counts_a_timeout_once_and_ignores_late_replies()
    {
        using var connection = new CoreConnection(new ServerProfile());
        connection.Circuit.RecordFailure();
        var registered = new TaskCompletionSource<Action<string?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> waiting = Task.Run(() => InvokeSendAndWait(connection,
            callback => registered.SetResult(callback), timeoutMs: 500));

        try
        {
            Action<string?> callback = await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string? result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

            result.Should().BeNull();
            connection.Circuit.ConsecutiveFails.Should().Be(2);
            connection.RateLimit.TotalAllowed.Should().Be(1);

            Action lateReply = () => callback("late reply");
            lateReply.Should().NotThrow();
            lateReply.Should().NotThrow();
            (await waiting).Should().BeNull();
            connection.Circuit.ConsecutiveFails.Should().Be(2);
        }
        finally
        {
            await CompleteAndObserveWait(waiting, registered.Task);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Send_and_wait_does_not_send_or_consume_a_token_when_the_circuit_is_open()
    {
        using var connection = new CoreConnection(new ServerProfile());
        for (int i = 0; i < 5; i++)
        {
            connection.Circuit.RecordFailure();
        }
        connection.Circuit.IsOpen.Should().BeTrue();
        bool sent = false;

        string? result = InvokeSendAndWait(connection, callback =>
        {
            sent = true;
            callback("unexpected reply");
        }, timeoutMs: 30_000);

        result.Should().BeNull();
        sent.Should().BeFalse();
        connection.RateLimit.TotalAllowed.Should().Be(0);
        connection.Circuit.ConsecutiveFails.Should().Be(5);
        connection.Circuit.TotalRejected.Should().Be(1);
    }

    private static string? InvokeSendAndWait(
        CoreConnection connection, Action<Action<string?>> send, int timeoutMs)
    {
        MethodInfo? wait = typeof(CoreConnection).GetMethod(
            "SendAndWait", BindingFlags.NonPublic | BindingFlags.Instance);
        wait.Should().NotBeNull();
        return (string?)wait!.MakeGenericMethod(typeof(string)).Invoke(connection,
            new object[] { send, timeoutMs });
    }

    private static async Task CompleteAndObserveWait(
        Task<string?> waiting, Task<Action<string?>> registered)
    {
        // Release the worker even if a broken timeout never completes its wait.
        if (!waiting.IsCompleted && registered.IsCompletedSuccessfully)
        {
            try
            {
                (await registered)(null);
            }
            catch (InvalidOperationException)
            {
                // Completion may have won the race while the test was unwinding.
            }
        }

        try
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(40));
        }
        catch when (waiting.IsFaulted)
        {
            // Observe worker failures without replacing the test's original failure.
            _ = waiting.Exception;
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("SPOT", 100)]
    [InlineData("FUTURES", 200)]
    public void Pair_detail_respects_the_requested_market(string market, double price)
    {
        using var connection = CreateCollidingPairConnection();
        var command = new ExchangeCommand(null!);
        MethodInfo detail = typeof(ExchangeCommand).GetMethod("PairDetail",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = (CommandResult)detail.Invoke(command,
            new object[] { "test-server", connection.ExchangeInfoStore, new[] { "ETHUSDT", market } })!;

        result.Success.Should().BeTrue();
        JObject data = JObject.FromObject(result.Data!);
        data["MarketType"]!.Value<string>().Should().Be(market);
        double.Parse(data["TickerPrice"]!.Value<string>()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(price);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Pair_detail_rejects_an_unknown_market()
    {
        using var connection = CreateCollidingPairConnection();
        var command = new ExchangeCommand(null!);
        MethodInfo detail = typeof(ExchangeCommand).GetMethod("PairDetail",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = (CommandResult)detail.Invoke(command,
            new object[] { "test-server", connection.ExchangeInfoStore, new[] { "ETHUSDT", "invalid" } })!;

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("market");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("SPOT", 100)]
    [InlineData("FUTURES", 200)]
    public void Per_symbol_ticker_cache_fallback_respects_the_requested_market(string market, double price)
    {
        using var connection = CreateCollidingPairConnection();
        var command = new ExchangeCommand(null!);
        MethodInfo ticker = typeof(ExchangeCommand).GetMethod("Ticker24",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = (CommandResult)ticker.Invoke(command,
            new object[] { connection, new[] { "ETHUSDT", market } })!;

        result.Success.Should().BeTrue();
        JObject data = JObject.FromObject(result.Data!);
        data["MarketType"]!.Value<string>().Should().Be(market);
        data["LastPrice"]!.Value<double>().Should().Be(price);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Per_symbol_ticker_does_not_use_a_different_markets_cached_price()
    {
        using var connection = new CoreConnection(new ServerProfile { Name = "test-server" });
        var pairs = new TradePairListData { marketType = MarketType.FUTURES };
        pairs.TradePairs["ethusdt"] = new TradePairData
        {
            symbol = "ethusdt", marketType = MarketType.FUTURES, TickerPrice = 200,
        };
        connection.ExchangeInfoStore.ProcessData(NetworkMessageType.TRADE_PAIR_LIST_RESULT, pairs);
        var command = new ExchangeCommand(null!);
        MethodInfo ticker = typeof(ExchangeCommand).GetMethod("Ticker24",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var result = (CommandResult)ticker.Invoke(command,
            new object[] { connection, new[] { "ETHUSDT", "SPOT" } })!;

        result.Success.Should().BeFalse();
    }

    private static CoreConnection CreateCollidingPairConnection()
    {
        var connection = new CoreConnection(new ServerProfile { Name = "test-server" });
        foreach (MarketType market in new[] { MarketType.SPOT, MarketType.FUTURES })
        {
            var list = new TradePairListData { marketType = market };
            list.TradePairs["ethusdt"] = new TradePairData
            {
                symbol = "ethusdt", marketType = market, baseAsset = "eth", quoteAsset = "usdt",
                minQty = market == MarketType.SPOT ? 1 : 0.001,
                TickerPrice = market == MarketType.SPOT ? 100 : 200,
            };
            connection.ExchangeInfoStore.ProcessData(NetworkMessageType.TRADE_PAIR_LIST_RESULT, list);
        }
        return connection;
    }

    private static AccountStore CreatePanicSellAccount(string symbol, int amount)
    {
        var positions = new PositionListData(MarketType.FUTURES);
        positions.positions[symbol] = new ConcurrentDictionary<PositionSide, PositionData>();
        positions.positions[symbol][PositionSide.BOTH] = new PositionData
        {
            symbol = symbol,
            marketType = MarketType.FUTURES,
            positionSide = PositionSide.BOTH,
            positionAmount = amount
        };
        var account = new AccountStore();
        account.ProcessData(NetworkMessageType.UDS_POSITIONS_RESULT, positions);
        return account;
    }

    private static void AddPanicSellPair(ExchangeInfoStore exchange, MarketType market)
    {
        var pairs = new TradePairListData { marketType = market };
        pairs.TradePairs["velvetusdt"] = new TradePairData
        {
            symbol = "velvetusdt",
            marketType = market
        };
        exchange.ProcessData(NetworkMessageType.TRADE_PAIR_LIST_RESULT, pairs);
    }

    private static MarketType ResolvePanicSellMarket(AccountStore account, ExchangeInfoStore exchange)
    {
        MethodInfo? resolve = typeof(OrdersCommand).GetMethod(
            "ResolvePanicSellMarketType", BindingFlags.NonPublic | BindingFlags.Static);
        resolve.Should().NotBeNull();
        return (MarketType)resolve!.Invoke(null, new object[] { account, exchange, "VELVETUSDT" })!;
    }

    private static CommandResult ConvertPanicSellAcknowledgement(
        PanicSellNotificationData? acknowledgement, bool activate)
    {
        object? notification = null;
        if (acknowledgement != null)
        {
            MethodInfo? convert = typeof(CoreConnection).GetMethod(
                "BuildPanicSellNotification", BindingFlags.NonPublic | BindingFlags.Static);
            convert.Should().NotBeNull();
            notification = convert!.Invoke(null, new object[] { acknowledgement });
        }

        MethodInfo? build = typeof(OrdersCommand).GetMethod(
            "BuildPanicSellResult", BindingFlags.NonPublic | BindingFlags.Static);
        build.Should().NotBeNull();
        return (CommandResult)build!.Invoke(null,
            new object?[] { "test-server", "VELVETUSDT", activate, notification })!;
    }

}
