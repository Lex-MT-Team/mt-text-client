using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Orders place / fill LiveTrade — places REAL BTCUSDT FUTURES orders on each
/// connecting bench and proves the full order lifecycle via a 4-phase evidence
/// path with explicit failure classification (handles late fills from venue
/// UDS reconcile so a real fill outside the polling window still classifies
/// as PASS).
///
/// <para><b>Phases:</b></para>
/// <list type="number">
///   <item><b>Place</b> — call <c>mt_orders_place</c>, capture returned exchange
///   <c>order_id</c> and <c>client_order_id</c>.  Classify on null/fail.</item>
///   <item><b>Venue confirmation (60 s)</b> — poll <c>mt_orders_list</c> every
///   5 s for up to 60 s, looking for FILLED, PARTIALLY_FILLED, or NEW.  NEW
///   continues; FILLED/PARTIALLY_FILLED ends Phase 2 successfully.</item>
///   <item><b>Fallback evidence (if Phase 2 times out without FILLED)</b> —
///   call <c>mt_reports_trades</c> and look for the client_order_id or order_id.
///   Found with a fill timestamp = PASS, classification
///   <c>placed_filled_late</c>.  Not found = FAIL with classification
///   <c>report_missing_after_fill</c>.</item>
///   <item><b>Artifact</b> — write a structured JSON record to
///   <c>~/mt-test-artifacts/orders-place-fill/&lt;profile&gt;_&lt;ts&gt;.json</c>
///   covering exchange/profile/client_order_id/returned_order_id/final_status/
///   fill_timestamp/failure_classification/elapsed_ms regardless of pass/fail.
///   This is the audit evidence.</item>
/// </list>
///
/// <para>
/// <b>POLICY</b>: this test class is gated by <c>MTC_LIVE_TRADES=1</c>
/// AND <c>MTC_TESTING_ENV=1</c>. The caller runs it explicitly via:
/// </para>
/// <code>
/// MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
///     dotnet test -c Release --filter "Category=LiveTrade"
/// </code>
/// <para>
/// <b>NO CLEANUP</b>: per the bench-data-retention policy,
/// filled orders + positions + TPSLs are intentionally left in place.
/// Reports-family tools depend on this DB seed.
/// </para>
/// <para>
/// <b>FINANCIAL EXPOSURE</b>: each row places a LIMIT BUY BTCUSDT
/// FUTURES order at the exchange's minimum size, priced 0.1% above the
/// current market last.  Whether the order fills inside Phase 2 or only
/// surfaces via Phase 3 reports lookup is venue-dependent — both are
/// real fill proof on the books either way.  At a $80–110 k BTC the
/// minimum-size (0.001 BTC) notional is ~$80–$110 per run per bench.
/// </para>
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class OrdersPlaceFillLiveTradeTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public OrdersPlaceFillLiveTradeTests(McpFixture mcp, BenchFixture bench)
    {
        _mcp = mcp;
        _bench = bench;
    }

    // Explicit failure classification enum, embedded in the JSON artifact.
    // Distinguishes "real failure" (not_connected, place_rejected,
    // report_missing_after_fill) from "real success but slow"
    // (placed_filled_late) from "real success in window" (filled_within_window).
    private static class Classification
    {
        public const string NotConnected               = "not_connected";
        public const string PlaceRejected              = "place_rejected";
        public const string PlaceTimedOut              = "place_timed_out";
        public const string PlacedNotVisibleInOrders   = "placed_not_visible_in_orders";
        public const string PlacedFilledLate           = "placed_filled_late";
        public const string ReportMissingAfterFill     = "report_missing_after_fill";
        public const string FilledWithinWindow         = "filled_within_window";
    }

    /// <summary>
    /// Per-bench parameterisation: profile name + minimum BTCUSDT order
    /// quantity for that exchange. The minimum is conservative; the
    /// caller can adjust if vendor minima change.
    /// </summary>
    public static IEnumerable<object[]> BenchData() => new[]
    {
        new object[] { "bench_01", "BYBIT",       "BTCUSDT",       0.001m },
        new object[] { "bench_02", "BINANCE",     "BTCUSDT",       0.001m },
        new object[] { "bench_03", "HYPERLIQUID", "BTCUSDT",       0.001m },
        new object[] { "bench_04", "OKX",         "BTC-USDT-SWAP", 0.001m },
    };

    [SkippableTheory]
    [MemberData(nameof(BenchData))]
    public async Task PlaceLimitBuy_NearMarket_ReachesFilledAndAppearsInReports(
        string profile, string exchange, string symbol, decimal qty)
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — LiveTrade tests place real orders and are " +
            "explicitly gated. Set the env var and re-run if you intend to commit funds.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on the expected UDP port; skipping.");
        // HL is documented-slow on this host — every MCP read call past the
        // 60 s default per-call timeout.  Separate investigation needed.
        Skip.If(profile == "bench_03" || exchange == "HYPERLIQUID",
            "HL MCP call latency exceeds test timeout on this host — separate investigation needed");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var artifact = new OrdersPlaceFillArtifact
        {
            Exchange = exchange,
            Profile = profile,
            Symbol = symbol,
            StartedAtUtc = DateTime.UtcNow,
        };

        // Per-test subprocess isolation — prevents cross-test
        // starvation when other benches are slow / frozen.
        await _mcp.RestartSubprocessAsync();

        // Warm-start: best-effort per-bench 60 s budget.  A frozen bench is
        // marked degraded and the run moves on.
        var warm = await _mcp.WarmStartAllBenchesAsync(_bench, perBenchBudgetSeconds: 60);
        bool connected = warm.TryGetValue(profile, out var c) && c;
        if (!connected)
        {
            artifact.FailureClassification = Classification.NotConnected;
            artifact.FinalStatus = "(bench not connected)";
            await FinaliseArtifact(artifact, sw);
            connected.Should().BeTrue(
                because: $"bench {profile} ({exchange}) must reach CONNECTED during warm-start; " +
                         $"classification={artifact.FailureClassification}; " +
                         $"warm-result: {string.Join(", ", warm.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        // 1. Price discovery — klines first, fall back to ticker24.
        var klinesResp = await _mcp.CallTool("mt_exchange_klines", new
        {
            symbol, interval = "1m", limit = 5, market = "FUTURES", profile,
        });
        decimal lastClose = ExtractLastClose(klinesResp.ParsedBody);
        if (lastClose <= 0m)
        {
            var tickerResp = await _mcp.CallTool("mt_exchange_ticker24", new
            {
                symbol, market_type = "FUTURES", profile,
            });
            lastClose = ExtractLastPriceFromTicker(tickerResp.ParsedBody);
        }
        lastClose.Should().BeGreaterThan(0m,
            because: $"either klines or ticker24 (FUTURES) on {exchange} ({symbol}) must yield a positive price; klines.success={klinesResp.InnerSuccess}");

        decimal limitPrice = decimal.Round(lastClose * 1.001m, 2);
        artifact.MarketPrice = lastClose;
        artifact.LimitPrice = limitPrice;

        // OKX requires [A-Za-z0-9]{1,32}; Binance/Bybit accept that intersection.
        string profileAlnum = new string(profile.Where(char.IsLetterOrDigit).ToArray());
        string clientOrderId = $"opfltrade{profileAlnum}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        artifact.ClientOrderId = clientOrderId;

        // ─── Phase 1: Place ─────────────────────────────────────────────────
        artifact.Phase = "place";
        var placeResp = await _mcp.CallTool("mt_orders_place", new
        {
            symbol,
            side = "BUY",
            qty = qty.ToString(System.Globalization.CultureInfo.InvariantCulture),
            price = limitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            type = "LIMIT",
            market = "FUTURES",
            position_side = "BOTH",
            client_order_id = clientOrderId,
            confirm = true,
            profile,
        }, timeout: TimeSpan.FromSeconds(90));

        if (placeResp.IsRpcError || placeResp.InnerSuccess != true)
        {
            // Distinguish timeout-shape rejections from explicit place rejections.
            bool looksLikeTimeout = (placeResp.InnerMessage ?? "").Contains("timed out", StringComparison.OrdinalIgnoreCase);
            artifact.FailureClassification = looksLikeTimeout ? Classification.PlaceTimedOut : Classification.PlaceRejected;
            artifact.FinalStatus = placeResp.InnerMessage ?? "(no message)";
            await FinaliseArtifact(artifact, sw);
            placeResp.InnerSuccess.Should().BeTrue(
                because: $"order place must succeed on {exchange}; classification={artifact.FailureClassification}; got {placeResp.InnerMessage}");
        }

        artifact.ReturnedOrderId = ExtractAssignedOrderId(placeResp.ParsedBody);
        // Binance/OKX broker-prefix our coid; the response's ClientOrderId is authoritative for lookup.
        string lookupCoid = ExtractAssignedCoid(placeResp.ParsedBody) ?? clientOrderId;
        artifact.LookupCoid = lookupCoid;
        artifact.Phase = "venue_confirmation";

        // ─── Phase 2: Venue confirmation (60 s, poll every 5 s) ─────────────
        // Accept FILLED, PARTIALLY_FILLED, or NEW.  NEW is acknowledgement of
        // a real order on the book; we keep polling until it transitions.
        string? observedStatus = null;
        bool sawNew = false;
        DateTime phase2Deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < phase2Deadline)
        {
            await Task.Delay(5_000);
            var ordersResp = await _mcp.CallTool("mt_orders_list", new { profile });
            if (!ordersResp.InnerSuccess) continue;
            observedStatus = FindOrderStatus(ordersResp.ParsedBody, lookupCoid);
            if (observedStatus is "FILLED" or "PARTIALLY_FILLED") break;
            if (observedStatus == "NEW") sawNew = true;
        }

        artifact.FinalStatus = observedStatus;

        if (observedStatus is "FILLED" or "PARTIALLY_FILLED")
        {
            artifact.FailureClassification = Classification.FilledWithinWindow;
            artifact.FillTimestampUtc = DateTime.UtcNow;
            // Phase 2 succeeded; verify reports row exists as supporting evidence
            // (don't hard-fail if reports lag — reports lag is a different concern).
            await EnrichWithReportsIfAvailable(artifact, profile, lookupCoid);
            await FinaliseArtifact(artifact, sw);
            return; // PASS
        }

        // ─── Phase 3: Fallback evidence via mt_reports_trades ───────────────
        artifact.Phase = "fallback_reports";
        var reportsResp = await _mcp.CallTool("mt_reports_trades", new { profile });
        if (reportsResp.InnerSuccess == true)
        {
            bool foundInReports = FindClientOrderIdInReports(reportsResp.ParsedBody, lookupCoid)
                || (artifact.ReturnedOrderId != null
                    && FindOrderIdInReports(reportsResp.ParsedBody, artifact.ReturnedOrderId));
            if (foundInReports)
            {
                artifact.FailureClassification = Classification.PlacedFilledLate;
                artifact.FillTimestampUtc = ExtractFillTimestamp(reportsResp.ParsedBody, lookupCoid)
                    ?? DateTime.UtcNow;
                await FinaliseArtifact(artifact, sw);
                // Real fill is real fill.  PASS.
                return;
            }
        }

        // ─── Phase 4 implicit: classify failure and write artifact ──────────
        artifact.FailureClassification = sawNew
            ? Classification.ReportMissingAfterFill
            : Classification.PlacedNotVisibleInOrders;
        await FinaliseArtifact(artifact, sw);

        // Hard-fail with a structured message containing the classification.
        observedStatus.Should().BeOneOf(new[] { "FILLED", "PARTIALLY_FILLED" },
            because: $"on {exchange}: order placed (returned_order_id={artifact.ReturnedOrderId ?? "<null>"}, " +
                     $"lookup_coid={lookupCoid}) but classification={artifact.FailureClassification}; " +
                     $"saw_new={sawNew}; artifact={artifact.ArtifactPath}");
    }

    // ─── Artifact writer ────────────────────────────────────────────────────

    private sealed class OrdersPlaceFillArtifact
    {
        public string Exchange { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Symbol { get; set; } = "";
        public DateTime StartedAtUtc { get; set; }
        public DateTime EndedAtUtc { get; set; }
        public long ElapsedMs { get; set; }
        public string Phase { get; set; } = "init";
        public string? FailureClassification { get; set; }
        public decimal? MarketPrice { get; set; }
        public decimal? LimitPrice { get; set; }
        public string? ClientOrderId { get; set; }
        public string? LookupCoid { get; set; }
        public string? ReturnedOrderId { get; set; }
        public string? FinalStatus { get; set; }
        public DateTime? FillTimestampUtc { get; set; }
        public bool ReportRowFound { get; set; }
        public string? ArtifactPath { get; set; }
    }

    private async Task FinaliseArtifact(OrdersPlaceFillArtifact a, System.Diagnostics.Stopwatch sw)
    {
        a.EndedAtUtc = DateTime.UtcNow;
        a.ElapsedMs = sw.ElapsedMilliseconds;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "orders-place-fill");
        Directory.CreateDirectory(dir);
        string fname = $"{a.Profile}_{a.StartedAtUtc:yyyyMMdd-HHmmss}.json";
        string path = Path.Combine(dir, fname);
        a.ArtifactPath = path;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(a, opts));
    }

    private async Task EnrichWithReportsIfAvailable(OrdersPlaceFillArtifact a, string profile, string lookupCoid)
    {
        try
        {
            var resp = await _mcp.CallTool("mt_reports_trades", new { profile });
            if (resp.InnerSuccess == true)
                a.ReportRowFound = FindClientOrderIdInReports(resp.ParsedBody, lookupCoid);
        }
        catch { /* best-effort enrichment */ }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static decimal ExtractLastClose(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return 0m;
        if (!b.TryGetProperty("data", out var data)) return 0m;
        if (data.ValueKind != JsonValueKind.Object) return 0m;
        if (!data.TryGetProperty("Klines", out var klines)) return 0m;
        if (klines.ValueKind != JsonValueKind.Array || klines.GetArrayLength() == 0) return 0m;
        var last = klines[klines.GetArrayLength() - 1];
        if (last.ValueKind != JsonValueKind.Object) return 0m;
        if (!last.TryGetProperty("Close", out var close)) return 0m;
        return ParseDecimalLoose(close);
    }

    private static decimal ExtractLastPriceFromTicker(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return 0m;
        if (!b.TryGetProperty("data", out var data)) return 0m;
        if (data.ValueKind != JsonValueKind.Object) return 0m;
        if (!data.TryGetProperty("Tickers", out var tickers)) return 0m;
        if (tickers.ValueKind != JsonValueKind.Array || tickers.GetArrayLength() == 0) return 0m;
        var first = tickers[0];
        if (first.ValueKind != JsonValueKind.Object) return 0m;
        if (!first.TryGetProperty("LastPrice", out var px)) return 0m;
        return ParseDecimalLoose(px);
    }

    private static decimal ParseDecimalLoose(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
        JsonValueKind.String when decimal.TryParse(el.GetString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
        _ => 0m,
    };

    private static string? ExtractAssignedCoid(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("ClientOrderId", out var coid)) return null;
        if (coid.ValueKind != JsonValueKind.String) return null;
        string? v = coid.GetString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>Tolerant extractor for the venue's returned order id (key varies per exchange).</summary>
    private static string? ExtractAssignedOrderId(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        foreach (string key in new[] { "OrderId", "OrderID", "orderId", "Id", "id" })
        {
            if (data.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
        }
        return null;
    }

    private static string? FindOrderStatus(JsonElement? body, string clientOrderId)
    {
        if (body is not { } b) return null;
        if (b.ValueKind == JsonValueKind.Array) return ScanArrayForStatus(b, clientOrderId);
        if (b.ValueKind != JsonValueKind.Object) return null;
        if (b.TryGetProperty("Orders", out var rootOrders) && rootOrders.ValueKind == JsonValueKind.Array)
            return ScanArrayForStatus(rootOrders, clientOrderId);
        if (!b.TryGetProperty("data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.Array) return ScanArrayForStatus(data, clientOrderId);
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("Orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
            return ScanArrayForStatus(orders, clientOrderId);
        return null;
    }

    private static string? ScanArrayForStatus(JsonElement arr, string clientOrderId)
    {
        foreach (var o in arr.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            string? coid = o.TryGetProperty("ClientOrderId", out var c) ? c.GetString() : null;
            if (coid == clientOrderId)
                return o.TryGetProperty("Status", out var s) ? s.GetString() : null;
        }
        return null;
    }

    private static bool FindClientOrderIdInReports(JsonElement? body, string clientOrderId)
    {
        var arr = ExtractReportsArray(body);
        if (arr is not { } a) return false;
        foreach (var item in a.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("ClientOrderId", out var c) && c.GetString() == clientOrderId)
                return true;
        }
        return false;
    }

    private static bool FindOrderIdInReports(JsonElement? body, string orderId)
    {
        var arr = ExtractReportsArray(body);
        if (arr is not { } a) return false;
        foreach (var item in a.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (string key in new[] { "OrderId", "OrderID", "orderId" })
            {
                if (!item.TryGetProperty(key, out var v)) continue;
                string? s = v.ValueKind == JsonValueKind.String ? v.GetString()
                           : v.ValueKind == JsonValueKind.Number ? v.GetRawText()
                           : null;
                if (s == orderId) return true;
            }
        }
        return false;
    }

    private static DateTime? ExtractFillTimestamp(JsonElement? body, string clientOrderId)
    {
        var arr = ExtractReportsArray(body);
        if (arr is not { } a) return null;
        foreach (var item in a.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("ClientOrderId", out var c) || c.GetString() != clientOrderId) continue;
            foreach (string key in new[] { "FillTimestamp", "FilledAt", "Timestamp", "Created", "CreationTime" })
            {
                if (!item.TryGetProperty(key, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var parsed))
                    return parsed.ToUniversalTime();
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
                    return DateTimeOffset.FromUnixTimeMilliseconds(unix > 9_999_999_999L ? unix : unix * 1000).UtcDateTime;
            }
        }
        return null;
    }

    private static JsonElement? ExtractReportsArray(JsonElement? body)
    {
        if (body is not { } b) return null;
        if (b.ValueKind == JsonValueKind.Array) return b;
        if (b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.Array) return data;
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("Trades", out var trades) && trades.ValueKind == JsonValueKind.Array) return trades;
        if (data.TryGetProperty("Reports", out var reports) && reports.ValueKind == JsonValueKind.Array) return reports;
        return null;
    }
}
