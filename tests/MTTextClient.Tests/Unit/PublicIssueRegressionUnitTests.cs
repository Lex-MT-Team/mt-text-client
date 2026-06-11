using System.Reflection;
using FluentAssertions;
using MTShared.Algorithms;
using MTShared.Types;
using MTTextClient.Commands;
using Newtonsoft.Json;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class PublicIssueRegressionUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_autostops_parser_accepts_real_mtshared_array_shape()
    {
        string raw = JsonConvert.SerializeObject(new[]
        {
            new AutoStopAlgorithmData
            {
                id = 123,
                info = "risk guard",
                marketType = MarketType.FUTURES,
                minMargin = -5,
                isRunning = true,
                asset = "usdt",
                panicIfTriggered = true,
                timeFrame = AutoStopsTimeFrame.D1,
                symbolFilter = "btcusdt",
                marketTypes = new List<MarketType>(),
            }
        });

        (bool ok, List<AutoStopAlgorithmData> filters, string? error) = InvokeAutoStopsParser(raw);

        ok.Should().BeTrue(error);
        filters.Should().ContainSingle();
        filters[0].minMargin.Should().Be(-5);
        filters[0].symbolFilter.Should().Be("btcusdt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Issue34_autostops_parser_rejects_legacy_wrapper_shape()
    {
        const string raw = """
        {"isEnabled":true,"Values":[{"isEnabled":true,"valueRange":{"min":-5.0}}]}
        """;

        (bool ok, List<AutoStopAlgorithmData> filters, string? error) = InvokeAutoStopsParser(raw);

        ok.Should().BeFalse();
        filters.Should().BeEmpty();
        error.Should().Contain("legacy mt-text-client wrapper");
    }

    private static (bool Ok, List<AutoStopAlgorithmData> Filters, string? Error) InvokeAutoStopsParser(string raw)
    {
        MethodInfo method = typeof(AutoStopsCommand).GetMethod(
            "TryParseBalanceFilters",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = method.Invoke(null, new object?[] { raw })!;
        Type t = result.GetType();
        return (
            (bool)t.GetField("Item1")!.GetValue(result)!,
            (List<AutoStopAlgorithmData>)t.GetField("Item2")!.GetValue(result)!,
            (string?)t.GetField("Item3")!.GetValue(result));
    }
}
