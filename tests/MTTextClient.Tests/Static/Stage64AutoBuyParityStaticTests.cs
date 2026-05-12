using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Stage 6.4 — AutoBuy parity audit (Static).
///
/// Reflects MTShared.dll to enumerate <c>AutoBuyRequestData.RequestActionType</c>
/// and asserts that the MCP tool surface (`mt_autobuy_*`) covers every
/// non-sentinel vendor action.  Companion doc:
/// <c>docs/autobuy-parity.md</c>.
///
/// This is a regression-detection harness: if MTShared adds a new
/// enum value, (1) fails until a tool is added; if a new tool appears
/// in the registry, (2) fails until the audit is updated.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class Stage64AutoBuyParityStaticTests
{
    private readonly McpFixture _mcp;
    public Stage64AutoBuyParityStaticTests(McpFixture mcp) => _mcp = mcp;

    /// <summary>
    /// Curated mapping from MTShared enum value → MCP tool that wires it.
    /// The reflection test asserts that this dictionary covers every
    /// non-UNKNOWN value of <c>RequestActionType</c>.  Keep in lockstep
    /// with <c>docs/autobuy-parity.md</c>.
    /// </summary>
    private static readonly Dictionary<string, string> VendorActionToMcpTool = new()
    {
        ["SUBSCRIBE"]           = "mt_autobuy_subscribe",
        ["SAVE"]                = "mt_autobuy_save",
        ["DELETE"]              = "mt_autobuy_delete",
        ["START"]               = "mt_autobuy_start",
        ["STOP"]                = "mt_autobuy_stop",
        ["REFRESH_ASSET_PAIRS"] = "mt_autobuy_refresh_pairs",
    };

    /// <summary>
    /// AutoBuy MCP tools that have no direct MTShared RequestActionType
    /// counterpart but are intentionally part of the surface.  The
    /// 'list' tool reads the local AutoBuyStore (no wire RPC); the
    /// 'unsubscribe' tool calls <c>SendAutoBuyUnsubscribe</c> (a
    /// standalone RPC, not an action enum value).
    /// </summary>
    private static readonly HashSet<string> ClientExtraTools = new()
    {
        "mt_autobuy_list",
        "mt_autobuy_unsubscribe",
    };

    [Fact]
    public void Every_Vendor_RequestActionType_Has_Corresponding_McpTool()
    {
        var asm = Assembly.LoadFrom(RepoPaths.MTSharedBuilt);
        var requestData = asm.GetType("MTShared.Network.AutoBuyRequestData", throwOnError: true)!;
        var actionEnum = requestData.GetNestedTypes(BindingFlags.Public)
            .FirstOrDefault(t => t.Name == "RequestActionType");
        actionEnum.Should().NotBeNull(
            because: "MTShared.Network.AutoBuyRequestData.RequestActionType must exist on this build");

        var actionNames = Enum.GetNames(actionEnum!);
        actionNames.Should().Contain("UNKNOWN", because: "UNKNOWN is the sentinel value");

        var nonSentinel = actionNames.Where(n => n != "UNKNOWN").ToArray();
        foreach (var name in nonSentinel)
        {
            VendorActionToMcpTool.Should().ContainKey(name,
                because: $"Stage 6.4 audit: vendor action '{name}' must be mapped to an MCP tool. " +
                         "If MTShared has just added this enum value, update " +
                         "Stage64AutoBuyParityStaticTests.VendorActionToMcpTool and " +
                         "docs/autobuy-parity.md in the same change.");
        }
    }

    [Fact]
    public void Every_Mt_AutoBuy_Tool_Is_Either_Mapped_Or_Listed_As_ClientExtra()
    {
        var allowed = new HashSet<string>(VendorActionToMcpTool.Values);
        allowed.UnionWith(ClientExtraTools);

        var registryAutoBuyTools = _mcp.Tools
            .Select(t => t.GetProperty("name").GetString()!)
            .Where(n => n.StartsWith("mt_autobuy_"))
            .ToArray();

        registryAutoBuyTools.Should().NotBeEmpty(
            because: "the registry must continue to expose the mt_autobuy_* family");

        foreach (var toolName in registryAutoBuyTools)
        {
            allowed.Should().Contain(toolName,
                because: $"Stage 6.4 audit: tool '{toolName}' is in the registry but not mapped " +
                         "to a vendor action and not declared in ClientExtraTools. " +
                         "Either map it to an MTShared.AutoBuyRequestData.RequestActionType value " +
                         "or add it to ClientExtraTools and update docs/autobuy-parity.md.");
        }
    }

    [Fact]
    public void Curated_Mapping_Targets_Resolve_To_Real_Registry_Tools()
    {
        var registryNames = new HashSet<string>(
            _mcp.Tools.Select(t => t.GetProperty("name").GetString()!));

        foreach (var (action, toolName) in VendorActionToMcpTool)
            registryNames.Should().Contain(toolName,
                because: $"the audit maps vendor action '{action}' to '{toolName}', " +
                         "but that tool is not in the registry. Either add the tool or update the audit.");

        foreach (var extra in ClientExtraTools)
            registryNames.Should().Contain(extra,
                because: $"client-extra tool '{extra}' must remain registered (audit invariant).");
    }
}
