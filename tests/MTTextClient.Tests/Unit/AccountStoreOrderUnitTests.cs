using FluentAssertions;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class AccountStoreOrderUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Order_list_batches_do_not_clear_existing_active_orders_for_same_market()
    {
        var store = new AccountStore();

        store.ProcessData(NetworkMessageType.UDS_ORDER_LIST_RESULT, OrderList(
            MarketType.FUTURES,
            Order("coid-1", "BTCUSDT", OrderStatus.NEW),
            Order("coid-2", "ETHUSDT", OrderStatus.NEW)));

        store.ProcessData(NetworkMessageType.UDS_ORDER_LIST_RESULT, OrderList(
            MarketType.FUTURES,
            Order("coid-1", "BTCUSDT", OrderStatus.NEW)));

        store.GetOrders(activeOnly: true)
            .Select(o => o.ClientOrderId)
            .Should()
            .BeEquivalentTo(new[] { "coid-1", "coid-2" });
        store.ActiveOrderCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Terminal_order_update_removes_order_from_active_count_without_losing_history()
    {
        var store = new AccountStore();

        store.ProcessData(NetworkMessageType.UDS_ORDER_UPDATE_RESULT,
            Order("coid-1", "BTCUSDT", OrderStatus.NEW));
        store.ProcessData(NetworkMessageType.UDS_ORDER_UPDATE_RESULT,
            Order("coid-1", "BTCUSDT", OrderStatus.FILLED));

        store.GetOrders(activeOnly: true).Should().BeEmpty();
        store.ActiveOrderCount.Should().Be(0);
        store.GetOrders(activeOnly: false).Should().ContainSingle(o =>
            o.ClientOrderId == "coid-1" && o.Status == OrderStatus.FILLED);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("mtc-d0SGnUVYApay", false, false, "SG", "ALGORITHM", true, false)]
    [InlineData("mtc-d0TPnUVYApay", true, false, "TP", "TPSL", true, false)]
    [InlineData("mtc-d000nUVYApay", false, false, "00", "MANUAL", false, true)]
    public void Order_attribution_falls_back_to_moontrader_client_order_id_signature(
        string clientOrderId,
        bool isTakeProfit,
        bool isStopLoss,
        string expectedSignature,
        string expectedSource,
        bool expectedAlgo,
        bool expectedManual)
    {
        var store = new AccountStore();

        store.ProcessData(NetworkMessageType.UDS_ORDER_UPDATE_RESULT,
            Order(clientOrderId, "BTCUSDT", OrderStatus.NEW, isTakeProfit, isStopLoss));

        OrderSnapshot order = store.GetOrders(activeOnly: true).Should().ContainSingle().Subject;
        order.AlgoSignature.Should().Be(expectedSignature);
        order.DerivedAlgoSignature.Should().Be(expectedSignature);
        order.OrderSource.Should().Be(expectedSource);
        order.IsAlgoOrder.Should().Be(expectedAlgo);
        order.IsManualOrder.Should().Be(expectedManual);
    }

    private static OrderListData OrderList(MarketType market, params OrderData[] orders)
    {
        var list = new OrderListData { marketType = market };
        foreach (OrderData order in orders)
        {
            list.AddOrUpdateOrder(order);
        }
        return list;
    }

    private static OrderData Order(
        string clientOrderId,
        string symbol,
        OrderStatus status,
        bool isTakeProfit = false,
        bool isStopLoss = false) =>
        new()
        {
            clientOrderId = clientOrderId,
            orderId = clientOrderId,
            symbol = symbol,
            marketType = MarketType.FUTURES,
            side = OrderSideType.BUY,
            positionSide = PositionSide.BOTH,
            orderType = OrderType.LIMIT,
            status = status,
            price = 100m,
            qty = 1m,
            isTakeProfit = isTakeProfit,
            isStopLoss = isStopLoss,
            creationTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            transactTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
}
