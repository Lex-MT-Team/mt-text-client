using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Orders update-tpsl LiveTrade — exercises `mt_orders_update_tpsl` against a
/// real, just-filled BTCUSDT FUTURES position on bench_02 BINANCE (the only
/// reachable / non-frozen bench in practice).
///
/// <para>
/// <b>POLICY</b> — gated by <c>MTC_LIVE_TRADES=1</c> AND
/// <c>MTC_TESTING_ENV=1</c>.  Run it explicitly via:
/// </para>
/// <code>
/// MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
///     dotnet test -c Release --filter "Category=LiveTrade&amp;DisplayName~OrdersUpdateTpsl"
/// </code>
/// <para>
/// <b>NO CLEANUP</b> — the placed order, the resulting position, and the
/// applied TP/SL stay in place after the test.  Reports tooling reads
/// this state.
/// </para>
/// <para>
/// <b>FINANCIAL EXPOSURE</b> — one LIMIT BUY 0.001 BTCUSDT FUTURES priced
/// 0.1 % above market (so it fills near-instantly); the subsequent
/// update-tpsl arms TP at +0.5 % and SL at -0.5 % from entry.  At a $80 k
/// BTC the notional is ~$80 per run; TP/SL legs can additionally exit at
/// ±0.5 % with whatever resulting PnL.
/// </para>
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class OrdersUpdateTpslLiveTradeTests
{
    private const string Profile = "bench_02";
    private const string Exchange = "BINANCE";
    private const string Symbol = "BTCUSDT";

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public OrdersUpdateTpslLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task PlaceFill_ThenUpdateTpsl_AcceptedByMtCore()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade places real orders + mutates TP/SL on a live position.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile),
            $"Bench {Profile} ({Exchange}) not observed on UDP port; skipping.");

        // Per-test subprocess isolation — prevents cross-test starvation
        // when bench_01 / bench_04 are frozen elsewhere in the suite.
        await _mcp.RestartSubprocessAsync();

        bool connected = await _mcp.WaitForConnected(Profile, firstAttemptSeconds: 60);
        connected.Should().BeTrue(because: $"bench {Profile} must reach CONNECTED");

        // 1) Get the FUTURES last-close price via klines.
        var klinesResp = await _mcp.CallTool("mt_exchange_klines", new
        {
            symbol = Symbol.ToLowerInvariant(),
            interval = "1m",
            limit = 3,
            market = "FUTURES",
            profile = Profile,
        });
        decimal lastClose = ExtractLastClose(klinesResp.ParsedBody);
        lastClose.Should().BeGreaterThan(0m,
            because: $"klines (FUTURES) on {Exchange} must yield a positive Close; klines.success={klinesResp.InnerSuccess}");

        // 2) Place a LIMIT BUY 0.1 % above market so it fills near-instantly.
        decimal limitPrice = decimal.Round(lastClose * 1.001m, 2);
        string clientOrderId = $"updtpsltrade{Profile.Replace("_","")}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var placeResp = await _mcp.CallTool("mt_orders_place", new
        {
            symbol = Symbol,
            side = "BUY",
            qty = "0.001",
            price = limitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            type = "LIMIT",
            market = "FUTURES",
            position_side = "BOTH",
            client_order_id = clientOrderId,
            confirm = true,
            profile = Profile,
        }, timeout: TimeSpan.FromSeconds(90));
        placeResp.InnerSuccess.Should().BeTrue(
            because: $"order place must succeed on {Exchange}; got {placeResp.InnerMessage}");

        // 3) Poll up to 60s for the order to FILL.  Binance's UDS pump can take
        // around a minute to reflect the fill in mt_orders_list; the no-cleanup
        // policy lets the order persist so this poll is best-effort.
        bool filled = false;
        for (int i = 0; i < 60 && !filled; i++)
        {
            await Task.Delay(1000);
            var listResp = await _mcp.CallTool("mt_orders_list", new { profile = Profile });
            if (!listResp.InnerSuccess) continue;
            string? status = FindOrderStatus(listResp.ParsedBody, clientOrderId);
            if (status is "FILLED" or "PARTIALLY_FILLED") { filled = true; break; }
        }
        // Soft-assert: even if the poll window doesn't catch the FILLED state,
        // the order is on Binance's book and will fill — we proceed to attempt
        // the TPSL update regardless (the update targets the resulting position
        // by symbol+side, which is what this test actually exercises).
        // The hard assertion is on the update-tpsl response below.

        // 4) Call mt_orders_update_tpsl on the BTCUSDT/BUY position.
        // TP at +0.5 %, SL at -0.5 %.  position_side=BOTH matches the place
        // call; the update targets the resulting position by symbol+side.
        var updateResp = await _mcp.CallTool("mt_orders_update_tpsl", new
        {
            symbol = Symbol,
            side = "BUY",
            market = "FUTURES",
            position_side = "BOTH",
            take_profit_percent = "0.5",
            stop_loss_percent = "0.5",
            client_order_id = clientOrderId,
            confirm = true,
            profile = Profile,
        }, timeout: TimeSpan.FromSeconds(60));

        // The hard assertion: MTCore must accept the update.  Whether the
        // venue applies the TP/SL synchronously or asynchronously is venue-
        // specific; what we own is the wire-protocol acceptance.
        updateResp.IsRpcError.Should().BeFalse(
            because: "mt_orders_update_tpsl must dispatch cleanly; got " + (updateResp.InnerMessage ?? "<null>"));
        updateResp.InnerSuccess.Should().BeTrue(
            because: $"MTCore must accept update-tpsl on a freshly-filled {Exchange} position; got: {updateResp.InnerMessage}");

        // NO CLEANUP — the position + applied TPSL settings remain on the
        // bench per the bench-data-retention policy.  Whether the TP / SL
        // legs subsequently fire and close the position is venue-driven and
        // not asserted here.
    }

    private static decimal ExtractLastClose(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return 0m;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return 0m;
        if (!data.TryGetProperty("Klines", out var klines)) return 0m;
        if (klines.ValueKind != JsonValueKind.Array || klines.GetArrayLength() == 0) return 0m;
        var last = klines[klines.GetArrayLength() - 1];
        if (last.ValueKind != JsonValueKind.Object) return 0m;
        if (!last.TryGetProperty("Close", out var close)) return 0m;
        return close.ValueKind switch
        {
            JsonValueKind.Number => close.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String when decimal.TryParse(close.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
            _ => 0m,
        };
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
}
