using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Order-management tools. Read paths run as Smoke. Catalog-presence checks
/// for the trade-affecting tools (place / cancel) run as Static — they assert
/// the tool name is in the registry but do NOT call MTCore and do NOT execute
/// trades, so they should not be counted as real LiveTrade evidence.
/// Real trade-execution coverage lives in <see cref="LiveTrade.OrdersPlaceFillLiveTradeTests"/>.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class OrdersTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public OrdersTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_orders_list_returns_envelope_orders_array_when_present()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_orders_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Empty bench → text-only Ok (no data field). Non-empty → data is
        // { Server, TotalOrders, ShowAll, Orders[] }. Both shapes are valid.
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            data.TryGetProperty("Orders", out var orders).Should().BeTrue();
            orders.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_orders_positions_returns_envelope_positions_array_when_present()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_orders_positions",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Empty bench → text-only Ok. Non-empty → data is the positions list.
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data))
        {
            data.ValueKind.Should().BeOneOf(
                System.Text.Json.JsonValueKind.Array,
                System.Text.Json.JsonValueKind.Object,
                System.Text.Json.JsonValueKind.Null);
        }
    }

    // Catalog-presence checks for trade-affecting tools.  These do NOT call
    // MTCore — they only assert the tool name is in the registry.  Tagged
    // Static so the LiveTrade pass count reflects only real wire-touching
    // tests.  Real place / cancel execution coverage lives in
    // LiveTrade.OrdersPlaceFillLiveTradeTests.

    [Fact]
    [Trait("Category", TraitCategories.Static)]
    public void mt_orders_place_is_in_catalog()
    {
        _mcp.ToolNames.Should().Contain("mt_orders_place");
    }

    [Fact]
    [Trait("Category", TraitCategories.Static)]
    public void mt_orders_cancel_is_in_catalog()
    {
        _mcp.ToolNames.Should().Contain("mt_orders_cancel");
    }
}
