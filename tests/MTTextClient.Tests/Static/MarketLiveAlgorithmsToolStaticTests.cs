using System.Linq;
using FluentAssertions;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Static declaration checks for the mt_market_live_algorithms tool (PR #46):
/// it is registered with the expected argument surface and, being a read-only
/// query, is not confirm-gated. Catalog/snapshot/README parity for the tool is
/// covered by the existing DispatcherSnapshotTests / ReadmeParityTests /
/// ToolCatalogStaticTests.
/// </summary>
public sealed class MarketLiveAlgorithmsToolStaticTests
{
    private static JObject? Tool() =>
        ToolRegistry.AllTools().FirstOrDefault(t => t["name"]?.Value<string>() == "mt_market_live_algorithms");

    [Fact]
    [Trait("Category", "Static")]
    public void Tool_is_declared_with_expected_arguments()
    {
        JObject? tool = Tool();
        tool.Should().NotBeNull("mt_market_live_algorithms must be declared in ToolRegistry");

        var props = tool!["inputSchema"]?["properties"] as JObject;
        props.Should().NotBeNull();
        props!.Properties().Select(p => p.Name)
            .Should().Contain(new[] { "market", "symbol", "algo_ids", "profile" });
    }

    [Fact]
    [Trait("Category", "Static")]
    public void Tool_is_read_only_and_not_confirm_gated()
    {
        JObject? tool = Tool();
        tool.Should().NotBeNull();

        var required = tool!["inputSchema"]?["required"] as JArray;
        var requiredNames = required?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>();
        requiredNames.Should().NotContain("confirm",
            because: "a read-only market/algorithm query must not be confirm-gated");
    }
}
