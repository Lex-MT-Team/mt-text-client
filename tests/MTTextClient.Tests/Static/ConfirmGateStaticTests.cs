using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Asserts that every tool documented as destructive declares <c>confirm</c>
/// in its <c>inputSchema.required</c>.
///
/// Per OV-5: the confirm gate is an audit aid + minor injection-defense layer,
/// not a security boundary. But declaring <c>confirm</c> as required at the
/// schema level is what makes it actually visible to automation-driven operators —
/// without that, an unsuspecting agent can omit it and either get a server-side
/// rejection (when the underlying command rejects without --confirm) or, worse,
/// bypass the gate (when the command tolerates absence). MCP-010 / MCP-010-ext
/// are concrete instances of the latter class of bug.
///
/// The list of confirm-required tools is curated below from the test matrix
/// and the existing PRs. Iterations that add new destructive tools must add
/// them to this list in the same PR.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class ConfirmGateStaticTests
{
    private readonly McpFixture _mcp;
    public ConfirmGateStaticTests(McpFixture mcp) => _mcp = mcp;

    /// <summary>
    /// Tools that MUST declare 'confirm' in inputSchema.required. Curated from
    /// the test matrix, the merged PR catalogue (#2, #6), and runtime evidence
    /// from prior sessions.
    /// </summary>
    public static readonly string[] ConfirmRequiredTools = new[]
    {
        // Algorithm lifecycle (PR #2 + #6)
        "mt_algos_delete",
        "mt_algos_delete_group",

        // Order management (PR #2)
        "mt_orders_reset_tpsl",

        // Blacklist add/remove (PR #3 typed-storage fix)
        "mt_blacklist_add",
        "mt_blacklist_remove",

        // Bulk algo lifecycle (PR #6)
        "mt_algos_start_all",
        "mt_algos_stop_all",
        "mt_fleet_disconnect",

        // Settings mutation (fix/known-defects-batch-1: MCP-010-set)
        "mt_settings_set",

        // Profile settings mutation (fix/known-defects-batch-1: MCP-010-ext)
        "mt_profile_settings_update",

        // Stage 1.1 + 1.2 — TPSL bulk + panic operations.
        // All four are destructive (cancel / split / market-close).
        "mt_tpsl_cancel_many",
        "mt_tpsl_split_many",
        "mt_tpsl_panic",
        "mt_tpsl_panic_many",

        // Stage 2.1 — Active Order TP/SL/TS Update (mutates open position state).
        "mt_orders_update_tpsl",

        // Stage 3.1 — AutoStops balance-filter CRUD (mutates risk-management config).
        "mt_autostops_add",
        "mt_autostops_edit",
        "mt_autostops_start",
        "mt_autostops_stop",
        "mt_autostops_delete",

        // Stage 4.1 — algorithm paste/import (mutates destination profile's algo store).
        // copy-to-clipboard is read-only on the source so it is NOT confirm-gated.
        "mt_algos_paste_from_clipboard",
        "mt_algos_import_json",

        // Stage 4.2 — bulk field-level edit across many algos.
        "mt_algos_bulk_edit",

        // Post-Stage-5 — algorithm creation via clone-from-source.
        "mt_algos_create",

        // Stage 6.7 — profile_settings delete (list is read-only, not gated).
        "mt_profile_settings_delete",

        // Stage 6.6 — vault profile delete (get is read-only, store/list pre-existing).
        "mt_vault_delete_profile",

        // Stage 6.3 — alerts CRUD (save is non-destructive create-or-update, list pre-existing).
        "mt_alerts_delete",
        "mt_alerts_set_running",

        // Watchdog placeholder (out of scope for current epic) — destructive
        // even as a placeholder because rotating the watchdog auth token
        // would sever active monitoring sessions when wired.  status:
        // placeholder is recorded in the registry description.
        "mt_watchdog_token_update",

        // Stage 5.1 — fleet margin-type campaign with mandatory dry_run.
        "mt_fleet_set_margin_type",

        // Stage 5.2 — profile-level whitelist CRUD (add/remove/bulk).  list is read-only.
        "mt_whitelist_add",
        "mt_whitelist_remove",
        "mt_whitelist_bulk_add",
        "mt_whitelist_bulk_remove",

        // Stage 5.3 — local profiles.json / folders.json CRUD.  list tools are read-only.
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
            because: $"{toolName} is destructive; OV-5 mandates 'confirm' in inputSchema.required.");
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
            // [Theory] body. This branch only runs when the operator has
            // re-introduced one or more gaps to the list.
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
        // List empty → every formerly-known gap has been fixed. PR1
        // (fix/known-defects-batch-1 — MCP-010-set / MCP-010-ext) emptied
        // the list by adding mt_settings_set + mt_profile_settings_update
        // to ConfirmRequiredTools.
        ConfirmKnownGaps.Should().BeEmpty(
            because: "no known schema-gate gaps remain after PR1 ports");
    }

    public static IEnumerable<object[]> ConfirmRequiredToolsData() =>
        ConfirmRequiredTools.Select(t => new object[] { t });
}
