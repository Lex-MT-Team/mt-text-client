using System.Collections.Generic;
using FluentAssertions;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Pins TradingPerformanceStore against the MTCore 0.7.25589 wire shape:
/// a snapshot/delta list of TradingPerformanceMetricData, each carrying one
/// metrics tuple per timeframe, plus deletedKeys. The live cores publish only
/// when a metric actually changes, so this is the deterministic check that the
/// parse — including the timeframe indexing — is right.
/// </summary>
public sealed class TradingPerformanceStoreUnitTests
{
    private static TradingPerformanceMetricData Metric(
        string symbol, long algorithmId, double h1Total, float h1ProfitFactor)
    {
        var metric = new TradingPerformanceMetricData(
            TradingPerformanceKey.GetKey((byte)MarketType.FUTURES, symbol, algorithmId))
        {
            startTime = 1_700_000_000_000,
            comment = "bench",
        };
        metric.GetMetrics(TradingPerformanceTimeFrame.H1) = new TradingPerformanceMetrics
        {
            total = h1Total,
            priceDelta = 0.25f,
            profitFactor = h1ProfitFactor,
            profitTotal = 12.5,
            lossTotal = -4.5,
        };
        return metric;
    }

    /// <summary>Round-trip through the vendor serializer: what the core actually puts
    /// on the wire is what the store has to read.</summary>
    private static TradingPerformanceListData RoundTrip(TradingPerformanceListData data)
    {
        var wire = new TradingPerformanceListData();
        ((NetworkData)wire).Deserialize(((NetworkData)data).Serialize());
        return wire;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Snapshot_populates_per_timeframe_metrics()
    {
        var store = new TradingPerformanceStore();
        store.HasData.Should().BeFalse();

        var snapshot = new TradingPerformanceListData
        {
            isSnapshot = true,
            metricChanges = new List<TradingPerformanceMetricData>
            {
                Metric("btcusdt", 42, h1Total: 8.0, h1ProfitFactor: 2.75f),
            },
        };

        store.ProcessData(RoundTrip(snapshot));

        store.HasData.Should().BeTrue();
        store.Count.Should().Be(1);

        TradingPerformanceSnapshot entry = store.GetAll()[0];
        entry.Symbol.Should().Be("btcusdt");
        entry.AlgorithmId.Should().Be(42);
        entry.MarketType.Should().Be(MarketType.FUTURES);
        entry.StartTime.Should().Be(1_700_000_000_000);
        entry.Comment.Should().Be("bench");

        entry.Metrics.Should().ContainKey(TradingPerformanceTimeFrame.H1);
        TradingPerformanceMetricsSnapshot h1 = entry.Metrics[TradingPerformanceTimeFrame.H1];
        h1.Total.Should().Be(8.0);
        h1.ProfitFactor.Should().Be(2.75f);
        h1.ProfitTotal.Should().Be(12.5);
        h1.LossTotal.Should().Be(-4.5);
        h1.IsEmpty.Should().BeFalse();

        // Untouched timeframes come back as the vendor default, not as missing keys.
        entry.Metrics[TradingPerformanceTimeFrame.M5].IsEmpty.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Delta_upserts_by_key_and_deletedKeys_removes()
    {
        var store = new TradingPerformanceStore();

        store.ProcessData(RoundTrip(new TradingPerformanceListData
        {
            isSnapshot = true,
            metricChanges = new List<TradingPerformanceMetricData>
            {
                Metric("btcusdt", 42, h1Total: 8.0, h1ProfitFactor: 2.75f),
                Metric("ethusdt", 43, h1Total: -1.0, h1ProfitFactor: 0.5f),
            },
        }));
        store.Count.Should().Be(2);

        // A delta revises one key and drops the other; the untouched one survives.
        store.ProcessData(RoundTrip(new TradingPerformanceListData
        {
            isSnapshot = false,
            metricChanges = new List<TradingPerformanceMetricData>
            {
                Metric("btcusdt", 42, h1Total: 11.0, h1ProfitFactor: 3.0f),
            },
            deletedKeys = new List<TradingPerformanceKey>
            {
                TradingPerformanceKey.GetKey((byte)MarketType.FUTURES, "ethusdt", 43),
            },
        }));

        store.Count.Should().Be(1);
        TradingPerformanceSnapshot entry = store.GetAll()[0];
        entry.Symbol.Should().Be("btcusdt");
        entry.Metrics[TradingPerformanceTimeFrame.H1].Total.Should().Be(11.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Snapshot_replaces_previous_contents()
    {
        var store = new TradingPerformanceStore();

        store.ProcessData(RoundTrip(new TradingPerformanceListData
        {
            isSnapshot = true,
            metricChanges = new List<TradingPerformanceMetricData>
            {
                Metric("btcusdt", 42, h1Total: 8.0, h1ProfitFactor: 2.75f),
            },
        }));

        store.ProcessData(RoundTrip(new TradingPerformanceListData
        {
            isSnapshot = true,
            metricChanges = new List<TradingPerformanceMetricData>
            {
                Metric("solusdt", 44, h1Total: 3.0, h1ProfitFactor: 1.5f),
            },
        }));

        store.Count.Should().Be(1);
        store.GetAll()[0].Symbol.Should().Be("solusdt");
    }
}
