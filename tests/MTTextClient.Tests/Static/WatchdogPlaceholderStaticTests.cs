using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Watchdog placeholder regression harness.
///
/// The two <c>mt_watchdog_*</c> tools are declared in the registry as
/// schema-only placeholders — there are NO handlers and NO bench coverage
/// on this build.  These tests pin the placeholder contract so future work
/// can find them by grepping for the <c>status: placeholder</c> marker,
/// and so the tools cannot silently disappear from the catalog or shed
/// the marker (which would imply real implementation without the doc /
/// LiveTrade pairing required to ship them).
///
/// See <c>docs/watchdog-integration.md</c> for the type inventory.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class WatchdogPlaceholderStaticTests
{
    private readonly McpFixture _mcp;
    public WatchdogPlaceholderStaticTests(McpFixture mcp) => _mcp = mcp;

    private static readonly string[] PlaceholderToolNames = new[]
    {
        "mt_watchdog_status",
        "mt_watchdog_token_update",
    };

    [Fact]
    public void Both_Watchdog_Placeholder_Tools_Are_Registered()
    {
        foreach (var name in PlaceholderToolNames)
        {
            var tool = _mcp.Tools.FirstOrDefault(
                t => t.GetProperty("name").GetString() == name);
            tool.ValueKind.Should().NotBe(JsonValueKind.Undefined,
                because: $"{name} is a documented placeholder; removing it requires either " +
                         "(a) shipping the real implementation in the same change, or " +
                         "(b) updating docs/watchdog-integration.md to retire the workstream");
        }
    }

    [Theory]
    [InlineData("mt_watchdog_status")]
    [InlineData("mt_watchdog_token_update")]
    public void Placeholder_Description_Contains_Status_Marker(string toolName)
    {
        var tool = _mcp.Tools.FirstOrDefault(
            t => t.GetProperty("name").GetString() == toolName);
        tool.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        string desc = tool.GetProperty("description").GetString() ?? "";
        desc.Should().Contain("status: placeholder",
            because: $"{toolName} must carry the placeholder marker so future engineers can grep " +
                     "for unfinished workstreams.  When the real implementation lands, the marker " +
                     "is removed in the same commit that ships the handler + LiveTrade test.");
        desc.Should().Contain("docs/watchdog-integration.md",
            because: $"{toolName} must point at the discovery doc so the type inventory + " +
                     "WatchdogConnection.cs requirements are one click away from the tool surface");
    }
}
