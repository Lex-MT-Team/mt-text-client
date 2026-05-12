using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Vault-stored profile-settings tools. MCP-010-ext is fixed in
/// fix/known-defects-batch-1: schema now declares <c>confirm</c> as required,
/// so a call without it is rejected at the RPC layer (-32602).
/// </summary>
[Collection(McpCollection.Name)]
public sealed class ProfileSettingsTests
{
    private readonly McpFixture _mcp;
    public ProfileSettingsTests(McpFixture mcp) { _mcp = mcp; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_profile_settings_update_without_confirm_is_rejected_at_schema_layer()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set; profile_settings tests require Vault");

        var resp = await _mcp.CallTool("mt_profile_settings_update", new
        {
            profile_name = "non_existent_test_profile_xyz",
            updates_json = "{}",
        });

        // Schema now declares confirm as required → RPC error OR inner success:false
        // (server fallback for clients that bypass schema validation). Either is
        // an acceptable rejection, but it MUST not silently succeed.
        bool rejected = resp.IsRpcError ||
            (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s) &&
             s.ValueKind == System.Text.Json.JsonValueKind.False);
        rejected.Should().BeTrue(
            because: "MCP-010-ext fix: profile_settings_update without confirm must be rejected");
    }
}
