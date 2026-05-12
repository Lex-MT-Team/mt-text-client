using System;
using System.Collections.Generic;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// Stage 4.1 — minimal cross-exchange symbol-format suggester.
///
/// MoonTrader / MTCore presents per-exchange symbol-format quirks: BINANCE +
/// BYBIT both use <c>BTCUSDT</c>, OKX uses <c>BTC-USDT-SWAP</c> for perp
/// futures and <c>BTC-USDT</c> for spot, HYPERLIQUID uses <c>BTC-PERP</c>.
/// When pasting an algorithm across exchanges these have to match the
/// destination's expectations or MTCore rejects.
///
/// This map covers the major base/quote pairs we see in dev (BTC, ETH, SOL,
/// XRP × USDT/USDC).  It is intentionally narrow — the goal is a useful
/// "suggested_symbol" in the structured error, not authoritative routing.
/// Callers must surface the suggestion to the operator, never auto-apply it.
/// </summary>
public static class ExchangeSymbolMap
{
    /// <summary>
    /// Given a symbol observed on <paramref name="sourceExchange"/>, suggest the
    /// equivalent symbol format for <paramref name="destinationExchange"/>.
    /// Returns null if no suggestion is available (caller should surface
    /// "symbol_mismatch: no automatic mapping; specify override_symbol explicitly").
    /// </summary>
    public static string? Suggest(string sourceSymbol, ExchangeType sourceExchange,
        ExchangeType destinationExchange, MarketType marketType)
    {
        if (string.IsNullOrWhiteSpace(sourceSymbol)) return null;
        if (sourceExchange == destinationExchange) return sourceSymbol;

        // Step 1: normalise source → (base, quote) pair.
        var (baseAsset, quote) = Decompose(sourceSymbol, sourceExchange);
        if (baseAsset == null || quote == null) return null;

        // Step 2: format for destination exchange.
        return Format(baseAsset, quote, destinationExchange, marketType);
    }

    /// <summary>Returns true when the source/destination exchange pair are known
    /// to share the same symbol format (no mapping needed).</summary>
    public static bool SameFormat(ExchangeType a, ExchangeType b)
    {
        // BINANCE / BYBIT both use bare concatenated BASE+QUOTE.
        if ((a == ExchangeType.BINANCE && b == ExchangeType.BYBIT) ||
            (a == ExchangeType.BYBIT && b == ExchangeType.BINANCE) ||
            a == b)
            return true;
        return false;
    }

    private static readonly HashSet<string> KnownQuotes = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "USD", "BUSD", "FDUSD", "DAI", "TUSD"
    };

    private static (string? baseAsset, string? quote) Decompose(string raw, ExchangeType source)
    {
        string s = raw.Trim().ToUpperInvariant();
        switch (source)
        {
            case ExchangeType.BINANCE:
            case ExchangeType.BYBIT:
                // Concatenated: BTCUSDT, ETHUSDC, etc.  Find a known quote at the tail.
                foreach (string q in KnownQuotes)
                {
                    if (s.EndsWith(q, StringComparison.OrdinalIgnoreCase) && s.Length > q.Length)
                        return (s[..^q.Length], q);
                }
                return (null, null);
            case ExchangeType.OKX:
                // Dashed: BTC-USDT-SWAP (perp), BTC-USDT (spot).
                string[] parts = s.Split('-');
                if (parts.Length >= 2) return (parts[0], parts[1]);
                return (null, null);
            case ExchangeType.HYPERLIQUID:
                // BTC-PERP — base only, USD implied
                string[] parts2 = s.Split('-');
                if (parts2.Length >= 1) return (parts2[0], "USD");
                return (null, null);
            default:
                return (null, null);
        }
    }

    private static string? Format(string baseAsset, string quote, ExchangeType dst, MarketType marketType)
    {
        switch (dst)
        {
            case ExchangeType.BINANCE:
            case ExchangeType.BYBIT:
                return baseAsset + quote;
            case ExchangeType.OKX:
                return marketType == MarketType.FUTURES || marketType == MarketType.DELIVERY
                    ? $"{baseAsset}-{quote}-SWAP"
                    : $"{baseAsset}-{quote}";
            case ExchangeType.HYPERLIQUID:
                return $"{baseAsset}-PERP";
            default:
                return null;
        }
    }
}
