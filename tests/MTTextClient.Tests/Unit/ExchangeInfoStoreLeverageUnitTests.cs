using FluentAssertions;
using System.Reflection;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class ExchangeInfoStoreLeverageUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Leverage_info_update_is_cached_independently_of_positions()
    {
        var store = new ExchangeInfoStore();

        store.ProcessData(NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA, new LeverageInfoUpdateData
        {
            maxLeverages = new Dictionary<MarketDataKey, int>
            {
                [MarketKey(MarketType.FUTURES, "btcusdt")] = 125,
            },
            leverages = new Dictionary<LeverageDataKey, int>
            {
                [LeverageKey(MarketType.FUTURES, "btcusdt", LeverageType.CROSS)] = 10,
                [LeverageKey(MarketType.FUTURES, "btcusdt", LeverageType.ISOLATED_LONG)] = 7,
            },
            riskLimits = new Dictionary<LeverageDataKey, double>
            {
                [LeverageKey(MarketType.FUTURES, "btcusdt", LeverageType.CROSS)] = 100_000,
            },
        });

        LeverageInfoSnapshot info = store.GetLeverageInfo("BTCUSDT", MarketType.FUTURES);

        info.HasWireData.Should().BeTrue();
        info.MaxLeverage.Should().Be(125);
        info.Leverages[LeverageType.CROSS].Should().Be(10);
        info.Leverages[LeverageType.ISOLATED_LONG].Should().Be(7);
        info.RiskLimits[LeverageType.CROSS].Should().Be(100_000);
        info.LastUpdatedUtc.Should().NotBeNull();
        store.LastLeverageInfoUpdate.Should().NotBe(default);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Risk_limit_update_merges_with_existing_configured_leverage()
    {
        var store = new ExchangeInfoStore();
        var key = LeverageKey(MarketType.FUTURES, "ethusdt", LeverageType.CROSS);

        store.ProcessData(NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA, new LeverageInfoUpdateData
        {
            leverages = new Dictionary<LeverageDataKey, int> { [key] = 20 },
        });
        store.ProcessData(NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA, new LeverageInfoUpdateData
        {
            riskLimits = new Dictionary<LeverageDataKey, double> { [key] = 50_000 },
        });

        LeverageInfoSnapshot info = store.GetLeverageInfo("ethusdt", MarketType.FUTURES);

        info.Leverages[LeverageType.CROSS].Should().Be(20);
        info.RiskLimits[LeverageType.CROSS].Should().Be(50_000);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public void Trade_pairs_with_the_same_symbol_survive_both_market_arrival_orders(bool spotFirst)
    {
        var store = new ExchangeInfoStore();
        MarketType first = spotFirst ? MarketType.SPOT : MarketType.FUTURES;
        MarketType second = spotFirst ? MarketType.FUTURES : MarketType.SPOT;
        AddPair(store, first);
        AddPair(store, second);

        store.TradePairCount.Should().Be(2);
        store.GetTradePairs().Should().HaveCount(2);
        store.SearchTradePairs("ETH").Should().HaveCount(2);
        store.GetCountsByMarketType()[MarketType.SPOT].Should().Be(1);
        store.GetCountsByMarketType()[MarketType.FUTURES].Should().Be(1);
        store.GetTradePairs(MarketType.SPOT).Single().TickerPrice.Should().Be(100);
        store.GetTradePairs(MarketType.FUTURES).Single().TickerPrice.Should().Be(200);
        store.GetTradePair("ETHUSDT")!.MarketType.Should().Be(MarketType.FUTURES);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public void Prices_for_an_unloaded_market_do_not_change_another_market(bool usePairList)
    {
        var store = new ExchangeInfoStore();
        AddPair(store, MarketType.SPOT);

        UpdatePairPrice(store, MarketType.FUTURES, 999, usePairList);

        store.GetTradePair("ETHUSDT")!.TickerPrice.Should().Be(100);
        store.TradePairCount.Should().Be(1);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public void Price_updates_are_isolated_by_market_and_case_insensitive_symbol(bool usePairList)
    {
        var store = new ExchangeInfoStore();
        AddPair(store, MarketType.SPOT);
        AddPair(store, MarketType.FUTURES);

        UpdatePairPrice(store, MarketType.SPOT, 101, usePairList);

        store.GetTradePairs(MarketType.SPOT).Single().TickerPrice.Should().Be(101);
        store.GetTradePairs(MarketType.FUTURES).Single().TickerPrice.Should().Be(200);
        store.GetTradePairs(MarketType.SPOT).Single().MinQty.Should().Be(1);
        store.GetTradePairs(MarketType.FUTURES).Single().MinQty.Should().Be(0.001);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Explicit_pair_lookup_does_not_fall_back_to_another_market()
    {
        var store = new ExchangeInfoStore();
        AddPair(store, MarketType.SPOT);
        AddPair(store, MarketType.FUTURES);
        TradePairSnapshot? spot = store.GetTradePair("eThUsDt", MarketType.SPOT);
        TradePairSnapshot? futures = store.GetTradePair("ETHUSDT", MarketType.FUTURES);

        spot!.TickerPrice.Should().Be(100);
        futures!.TickerPrice.Should().Be(200);
        store.GetTradePair("ethusdt", MarketType.DELIVERY).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Refreshing_one_market_preserves_the_other_markets_rules()
    {
        var store = new ExchangeInfoStore();
        AddPair(store, MarketType.SPOT);
        AddPair(store, MarketType.FUTURES);
        AddPair(store, MarketType.FUTURES, "ETHUSDT", 0.005);

        store.TradePairCount.Should().Be(2);
        store.GetTradePairs(MarketType.SPOT).Single().MinQty.Should().Be(1);
        store.GetTradePairs(MarketType.FUTURES).Single().MinQty.Should().Be(0.005);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Symbol_lookup_retains_pairs_from_unnamed_market_values()
    {
        var store = new ExchangeInfoStore();
        AddPair(store, (MarketType)123);

        TradePairSnapshot? pair = store.GetTradePair("ETHUSDT");

        pair.Should().NotBeNull();
        pair!.MarketType.Should().Be((MarketType)123);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public void Symbol_lookup_without_futures_uses_stable_market_order(bool spotFirst)
    {
        var store = new ExchangeInfoStore();
        AddPair(store, spotFirst ? MarketType.SPOT : MarketType.DELIVERY);
        AddPair(store, spotFirst ? MarketType.DELIVERY : MarketType.SPOT);

        store.GetTradePair("ETHUSDT")!.MarketType.Should().Be(MarketType.SPOT);
        store.TradePairCount.Should().Be(2);
    }

    private static void AddPair(ExchangeInfoStore store, MarketType market, string symbol = "ethusdt", double? minQty = null)
    {
        var list = new TradePairListData { marketType = market };
        list.TradePairs[symbol] = new TradePairData
        {
            symbol = symbol, marketType = market, baseAsset = "eth", quoteAsset = "usdt",
            minQty = minQty ?? (market == MarketType.SPOT ? 1 : 0.001),
            TickerPrice = market == MarketType.SPOT ? 100 : 200,
        };
        store.ProcessData(NetworkMessageType.TRADE_PAIR_LIST_RESULT, list);
    }

    private static void UpdatePairPrice(ExchangeInfoStore store, MarketType market, double price, bool usePairList)
    {
        NetworkData update;
        if (usePairList)
        {
            var list = new TradePairListData { marketType = market };
            list.TradePairs["ETHUSDT"] = new TradePairData
            {
                symbol = "ETHUSDT", marketType = market, TickerPrice = price,
            };
            update = list;
        }
        else
        {
            var prices = new TradePairPriceListData();
            prices.prices.Add(new TradePairPriceUpdateData
            {
                symbol = "ETHUSDT", marketType = market, price = price,
            });
            update = prices;
        }
        store.ProcessData(NetworkMessageType.TRADE_PAIR_PRICE_LIST_RESULT, update);
    }

    private static MarketDataKey MarketKey(MarketType marketType, string symbol)
    {
        ConstructorInfo? ctor = typeof(MarketDataKey).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(byte), typeof(string) },
            modifiers: null);
        ctor.Should().NotBeNull();
        return (MarketDataKey)ctor!.Invoke(new object[] { (byte)marketType, symbol });
    }

    private static LeverageDataKey LeverageKey(MarketType marketType, string symbol, LeverageType leverageType)
    {
        ConstructorInfo? ctor = typeof(LeverageDataKey).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(MarketType), typeof(string), typeof(LeverageType) },
            modifiers: null);
        ctor.Should().NotBeNull();
        return (LeverageDataKey)ctor!.Invoke(new object[] { marketType, symbol, leverageType });
    }
}
