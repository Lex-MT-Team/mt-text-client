using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Asserts that every tool documented as destructive declares <c>confirm</c>
/// in its <c>inputSchema.required</c>.
///
/// The confirm gate is an audit aid + minor injection-defense layer, not a
/// security boundary. But declaring <c>confirm</c> as required at the schema
/// level is what makes it actually visible to automation-driven callers —
/// without that, an unsuspecting caller can omit it and either get a server-side
/// rejection (when the underlying command rejects without --confirm) or, worse,
/// bypass the gate (when the command tolerates absence).
///
/// The list of confirm-required tools is curated below from the test matrix
/// and the registry.  Iterations that add new destructive tools must add
/// them to this list in the same change.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class ConfirmGateStaticTests
{
    private readonly McpFixture _mcp;
    public ConfirmGateStaticTests(McpFixture mcp) => _mcp = mcp;

    /// <summary>
    /// Tools that MUST declare 'confirm' in inputSchema.required.
    /// </summary>
    public static readonly string[] ConfirmRequiredTools = new[]
    {
        // Algorithm lifecycle
        "mt_algos_delete",
        "mt_algos_delete_group",

        // Order management
        "mt_orders_reset_tpsl",

        // Blacklist add/remove
        "mt_blacklist_add",
        "mt_blacklist_remove",

        // Bulk algo lifecycle
        "mt_algos_start_all",
        "mt_algos_stop_all",
        "mt_fleet_disconnect",

        // Settings mutation
        "mt_settings_set",

        // Profile settings mutation
        "mt_profile_settings_update",

        // TPSL bulk + panic operations.
        // All four are destructive (cancel / split / market-close).
        "mt_tpsl_cancel_many",
        "mt_tpsl_split_many",
        "mt_tpsl_panic",
        "mt_tpsl_panic_many",

        // Active Order TP/SL/TS Update (mutates open position state).
        "mt_orders_update_tpsl",

        // AutoStops balance-filter CRUD (mutates risk-management config).
        "mt_autostops_add",
        "mt_autostops_edit",
        "mt_autostops_start",
        "mt_autostops_stop",
        "mt_autostops_delete",

        // Algorithm paste/import (mutates destination profile's algo store).
        // copy-to-clipboard is read-only on the source so it is NOT confirm-gated.
        "mt_algos_paste_from_clipboard",
        "mt_algos_import_json",

        // Bulk field-level edit across many algos.
        "mt_algos_bulk_edit",

        // Algorithm creation via clone-from-source.
        "mt_algos_create",

        // profile_settings delete (list is read-only, not gated).
        "mt_profile_settings_delete",

        // Vault profile delete (get is read-only, store/list pre-existing).
        "mt_vault_delete_profile",

        // Alerts CRUD (save is non-destructive create-or-update, list pre-existing).
        "mt_alerts_delete",
        "mt_alerts_set_running",

        // Watchdog placeholder — destructive even as a placeholder because
        // rotating the watchdog auth token would sever active monitoring
        // sessions when wired.  status: placeholder is recorded in the
        // registry description.
        "mt_watchdog_token_update",

        // Fleet margin-type campaign with mandatory dry_run.
        "mt_fleet_set_margin_type",

        // Profile-level whitelist CRUD (add/remove/bulk).  list is read-only.
        "mt_whitelist_add",
        "mt_whitelist_remove",
        "mt_whitelist_bulk_add",
        "mt_whitelist_bulk_remove",

        // Local profiles.json / folders.json CRUD.  list tools are read-only.
        "mt_profiles_add",
        "mt_profiles_edit",
        "mt_profiles_delete",
        "mt_profiles_move",
        "mt_profiles_import_csv",
        "mt_folders_add",
        "mt_folders_edit",
        "mt_folders_delete",
    };

    /// <summary>
    /// Tools known to require confirm at the SERVER side but missing it from
    /// the schema. Each is tagged with the bug ID. These tests assert the
    /// CURRENT broken state; when the bug is fixed, the test moves to the
    /// "passing" list above and this list shrinks.
    /// </summary>
    public static readonly (string Tool, string BugId)[] ConfirmKnownGaps =
        Array.Empty<(string, string)>();

    [Theory]
    [MemberData(nameof(ConfirmRequiredToolsData))]
    public void ConfirmRequiredTool_DeclaresConfirmInSchema(string toolName)
    {
        var tool = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
        tool.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            because: $"{toolName} is required to be in the catalog");

        tool.TryGetProperty("inputSchema", out var schema).Should().BeTrue(
            because: $"{toolName} must have an inputSchema");
        schema.TryGetProperty("required", out var req).Should().BeTrue(
            because: $"{toolName} must declare a required array");
        req.ValueKind.Should().Be(JsonValueKind.Array);

        bool hasConfirm = false;
        foreach (var r in req.EnumerateArray())
            if (r.GetString() == "confirm") { hasConfirm = true; break; }

        hasConfirm.Should().BeTrue(
            because: $"{toolName} is destructive; 'confirm' must appear in inputSchema.required.");
    }

    /// <summary>
    /// When <see cref="ConfirmKnownGaps"/> still has rows, each is asserted
    /// to remain broken (a regression-detection harness for in-flight bug
    /// IDs). When the list is empty — every previously-known gap has been
    /// fixed and moved to <see cref="ConfirmRequiredTools"/> — this single
    /// fact records that state explicitly. Keeping the assertion in
    /// fact-form avoids xUnit's "No data found for theory" failure when
    /// the array goes empty.
    /// </summary>
    [Fact]
    public void ConfirmKnownGaps_IsEmpty_AllPreviouslyKnownGapsFixed()
    {
        if (ConfirmKnownGaps.Length > 0)
        {
            // Re-assert each row's broken state, identical to the previous
            // [Theory] body. This branch only runs when one or more gaps
            // have been re-introduced to the list.
            foreach (var (toolName, bugId) in ConfirmKnownGaps)
            {
                var tool = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
                tool.ValueKind.Should().NotBe(JsonValueKind.Undefined);

                tool.TryGetProperty("inputSchema", out var schema).Should().BeTrue();
                schema.TryGetProperty("required", out var req);
                bool hasConfirm = req.ValueKind == JsonValueKind.Array &&
                                  req.EnumerateArray().Any(r => r.GetString() == "confirm");

                hasConfirm.Should().BeFalse(
                    because: $"{toolName} known gap {bugId}: schema does not yet require confirm. " +
                             "When the fix lands, move this tool from ConfirmKnownGaps to ConfirmRequiredTools.");
            }
            return;
        }
        // List empty → every formerly-known gap has been fixed.
        ConfirmKnownGaps.Should().BeEmpty(
            because: "no known schema-gate gaps remain");
    }

    public static IEnumerable<object[]> ConfirmRequiredToolsData() =>
        ConfirmRequiredTools.Select(t => new object[] { t });
}
