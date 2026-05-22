using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke probes for the 4 notifications-config tools, via the
/// MCP subprocess.  No MTCore connection required (these are internal
/// reflection-only tools), so we don't gate on bench availability.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class NotificationsConfigSmokeTests
{
    private readonly McpFixture _mcp;
    public NotificationsConfigSmokeTests(McpFixture mcp) => _mcp = mcp;

    [SkippableFact]
    public async Task mt_notifications_config_groups_returns_typed_envelope()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_notifications_config_groups", new { });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.GetProperty("count").GetInt32().Should().BeGreaterThan(0,
            because: "MTShared declares at least TRADE, SYSTEM, EXCHANGE, etc.");
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("name").GetString().Should().NotBeNullOrEmpty();
            item.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    [SkippableFact]
    public async Task mt_notifications_config_targets_returns_three_channels()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_notifications_config_targets", new { });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        // CLIENT_NOTIFICATIONS, CLIENT_LOG, TELEGRAM on this build.
        data.GetProperty("count").GetInt32().Should().Be(3);
    }

    [SkippableFact]
    public async Task mt_notifications_config_descriptors_includes_well_known_entries()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_notifications_config_descriptors", new { });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.GetProperty("count").GetInt32().Should().BeGreaterThan(15,
            because: "MTShared declares many notification descriptors on this build");

        bool foundOrderFilled = false, foundMarginInsufficient = false;
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            string? f = item.GetProperty("enum_field").GetString();
            if (f == "ORDER_FILLED") foundOrderFilled = true;
            if (f == "MARGIN_INSUFFICIENT") foundMarginInsufficient = true;
        }
        foundOrderFilled.Should().BeTrue(
            because: "ORDER_FILLED is a fundamental TRADE-group notification");
        foundMarginInsufficient.Should().BeTrue(
            because: "MARGIN_INSUFFICIENT must always be enumerated");
    }

    [SkippableFact]
    public async Task mt_notifications_config_capabilities_reports_mutation_gap()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_notifications_config_capabilities", new { });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.GetProperty("mutation_supported").GetBoolean().Should().BeFalse(
            because: "NotificationSettingsEditor is not yet wired through CoreConnection");
        data.GetProperty("mutation_notice").GetString()
            .Should().Contain("notifications_config_mutation_not_wired",
                because: "the gap must be surfaced honestly to callers");
        data.GetProperty("groups").GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        data.GetProperty("descriptors").GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        data.GetProperty("targets").GetProperty("count").GetInt32().Should().BeGreaterThan(0);
    }
}
