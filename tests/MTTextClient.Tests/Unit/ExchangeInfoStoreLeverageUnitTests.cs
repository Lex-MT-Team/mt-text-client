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
