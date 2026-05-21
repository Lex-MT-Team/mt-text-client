using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier E — order mutator surface on bench_02 BINANCE (17 tools):
///   cancel, cancel_all, move, move_batch, set_leverage, set_margin_type,
///   set_position_mode, change_margin, transfer, reset_tpsl,
///   set_leverage_buysell, set_multiasset, close, close_all, close_by_tpsl,
///   join, split.
///
/// Strategy:
///   1. Place a far-from-market LIMIT order (won't fill) so cancel/move/
///      cancel_all/split/join/move_batch have a real working order to act on.
///   2. After cancel, exercise the leverage / margin / position-mode tools
///      (they touch venue state but not the order book).
///   3. close/close_all/close_by_tpsl/reset_tpsl operate on positions; we run
///      them anyway — bench_02 has earlier-run fills from prior campaigns,
///      so the venue will either close real positions or surface a structured
///      no-op response.  Either is real-wire evidence.
///   4. mt_orders_panic_sell is NEVER auto-invoked (explicitly gated).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_E_OrderMutatorsTests
{
    private const string Letter = "E";
    private const string Profile = "bench_02";
    private const string Symbol = "BTCUSDT";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_E_OrderMutatorsTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseOrderMutators()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // ── Place LIMIT BUY ~5% below market (won't fill in test window,
        //    but inside the venue's accepted price range) ──
        var lastPrice = await GetLastPriceAsync();
        // Binance FUTURES BTCUSDT rejects prices well outside the ±20% PERCENT_PRICE filter.
        // 5% below is safely inside and unlikely to fill in the next minute or two.
        var farPrice = lastPrice > 0 ? Math.Round(lastPrice * 0.95m, 1) : 30000m;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var coid1 = $"campaignE1{ts}";
        var coid2 = $"campaignE2{ts}";

        var place1 = await CampaignEvidence.Probe(_mcp, Letter, "mt_orders_place", new
        {
            symbol = Symbol, side = "BUY", qty = "0.001",
            price = farPrice.ToString("0.0"), market = "FUTURES",
            client_order_id = coid1, confirm = true, profile = Profile,
        }, profile: Profile, timeout: TimeSpan.FromSeconds(60),
           note: "baseline working order #1 for cancel/move/split/join");
        bool placed1 = place1?.InnerSuccess == true;

        var place2 = await CampaignEvidence.Probe(_mcp, Letter, "mt_orders_place", new
        {
            symbol = Symbol, side = "BUY", qty = "0.001",
            price = farPrice.ToString("0.0"), market = "FUTURES",
            client_order_id = coid2, confirm = true, profile = Profile,
        }, profile: Profile, timeout: TimeSpan.FromSeconds(60),
           note: "baseline working order #2 for cancel_all / move_batch");
        bool placed2 = place2?.InnerSuccess == true;

        // ── mt_orders_move (single) ──
        if (placed1)
        {
            await Probe("mt_orders_move", new
            {
                client_order_id = coid1,
                new_price = Math.Round(farPrice * 0.99m, 1).ToString("0.0"),
                confirm = true, profile = Profile,
            });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_orders_move",
                "Could not place a baseline working order to move; check exchange place response.");
        }

        // ── mt_orders_move_batch ──
        if (placed1 && placed2)
        {
            var batchPayload = "{\"" + coid1 + "\":\"" + Math.Round(farPrice * 0.97m, 1).ToString("0.0") + "\"," +
                               "\"" + coid2 + "\":\"" + Math.Round(farPrice * 0.96m, 1).ToString("0.0") + "\"}";
            await Probe("mt_orders_move_batch", new
            {
                orders_json = batchPayload, market = "FUTURES", profile = Profile,
            });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_orders_move_batch",
                "Insufficient placed orders to drive batch move.");
        }

        // ── mt_orders_split / mt_orders_join ──
        if (placed1)
        {
            await Probe("mt_orders_split", new
            {
                client_order_id = coid1, count = "2", market = "FUTURES", profile = Profile,
            });
            await Task.Delay(1500);
            await Probe("mt_orders_join", new
            {
                client_order_id = coid1, market = "FUTURES", profile = Profile,
            });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_orders_split", "No baseline order.");
            CampaignEvidence.RecordBlocker(Letter, "mt_orders_join", "No baseline order.");
        }

        // ── mt_orders_cancel (single) ──
        if (placed2)
        {
            await Probe("mt_orders_cancel",
                new { client_order_id = coid2, confirm = true, profile = Profile });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_orders_cancel", "No order to cancel.");
        }

        // ── mt_orders_cancel_all (by symbol) ──
        await Probe("mt_orders_cancel_all",
            new { symbol = Symbol, confirm = true, profile = Profile });

        // ── Leverage / margin / mode tools (venue state) ──
        await Probe("mt_orders_set_leverage",
            new { symbol = Symbol, leverage = "5", confirm = true, profile = Profile });
        await Probe("mt_orders_set_margin_type",
            new { symbol = Symbol, margin_type = "CROSS", confirm = true, profile = Profile });
        await Probe("mt_orders_set_position_mode",
            new { symbol = Symbol, mode = "ONE_WAY", confirm = true, profile = Profile });

        // change_margin (works against an existing isolated position; if none,
        // the venue returns a structured error — still real wire evidence).
        await Probe("mt_orders_change_margin", new
        {
            symbol = Symbol, position_side = "BOTH", amount = "1.0",
            action = "add", confirm = true, profile = Profile,
        });

        // transfer SPOT↔FUTURES (small amount; structured rejection if no SPOT balance)
        await Probe("mt_orders_transfer", new
        {
            asset = "USDT", amount = "0.01", from = "SPOT", to = "FUTURES",
            confirm = true, profile = Profile,
        });

        // set_leverage_buysell — Bybit-only split leverage; on Binance the venue
        // returns a structured error.  Either is real-wire evidence.
        await Probe("mt_orders_set_leverage_buysell", new
        {
            asset = Symbol, buy_leverage = "5", sell_leverage = "5",
            market = "FUTURES", confirm = true, profile = Profile,
        });

        // set_multiasset (FUTURES) — toggle multi-asset margin mode
        await Probe("mt_orders_set_multiasset", new
        {
            enabled = "false", market = "FUTURES", confirm = true, profile = Profile,
        });

        // close / close_all / close_by_tpsl — touch existing positions if any
        await Probe("mt_orders_close", new
        {
            symbol = Symbol, percentage = "10", confirm = true, profile = Profile,
        });
        await Probe("mt_orders_close_all", new { confirm = true, profile = Profile });
        await Probe("mt_orders_close_by_tpsl", new
        {
            symbol = Symbol, market = "FUTURES", side = "BOTH",
            order_type = "MARKET", confirm = true, profile = Profile,
        });
        await Probe("mt_orders_reset_tpsl", new
        {
            symbol = Symbol, market = "FUTURES", side = "BOTH",
            confirm = true, profile = Profile,
        });

        // ── Blocker: panic_sell never auto-invoked ──
        CampaignEvidence.RecordBlocker(Letter, "mt_orders_panic_sell",
            "destructive — never auto-invoked by the campaign.");
    }

    private Task<McpResponse?> Probe(string tool, object args)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args, profile: Profile);

    private async Task<decimal> GetLastPriceAsync()
    {
        // Try klines first, then ticker24 fallback.
        try
        {
            var kl = await _mcp.CallTool("mt_exchange_klines",
                new { symbol = Symbol, interval = "1m", limit = "1", market = "FUTURES", profile = Profile },
                TimeSpan.FromSeconds(30));
            if (kl.ParsedBody is { } b && b.TryGetProperty("data", out var d) &&
                d.TryGetProperty("Klines", out var klines) &&
                klines.ValueKind == JsonValueKind.Array && klines.GetArrayLength() > 0)
            {
                var last = klines[klines.GetArrayLength() - 1];
                if (last.TryGetProperty("Close", out var close))
                {
                    if (close.ValueKind == JsonValueKind.Number) return close.GetDecimal();
                    if (close.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(close.GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
                }
            }
        }
        catch { /* fall through */ }
        try
        {
            var resp = await _mcp.CallTool("mt_exchange_ticker24",
                new { symbol = Symbol, market_type = "FUTURES", profile = Profile },
                TimeSpan.FromSeconds(30));
            if (resp.ParsedBody is { } body && body.TryGetProperty("data", out var data) &&
                data.TryGetProperty("Tickers", out var tickers) &&
                tickers.ValueKind == JsonValueKind.Array && tickers.GetArrayLength() > 0 &&
                tickers[0].TryGetProperty("LastPrice", out var lp))
            {
                if (lp.ValueKind == JsonValueKind.Number) return lp.GetDecimal();
                if (lp.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(lp.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            }
        }
        catch { /* fall through */ }
        return 0m;
    }
}
