using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// In-process unit tests for <see cref="ConfirmGate"/>. The gate is
/// registry-driven, deterministic, and does not require an MCP
/// subprocess or a real MTCore. We assert:
///   • <see cref="ConfirmGate.IsConfirmRequired"/> returns true for every
///     tool whose schema declares <c>confirm</c> in <c>inputSchema.required</c>;
///   • <see cref="ConfirmGate.RejectIfMissing"/> rejects the call when
///     <c>confirm</c> is absent, false, or non-boolean;
///   • the gate is permissive for non-destructive tools.
/// </summary>
[Trait("Category", TraitCategories.Unit)]
public sealed class ConfirmGateUnitTests
{
    // Tools whose schema declares confirm in inputSchema.required.
    [Theory]
    [InlineData("mt_algos_delete")]
    [InlineData("mt_algos_delete_group")]
    [InlineData("mt_algos_start_all")]
    [InlineData("mt_algos_stop_all")]
    [InlineData("mt_blacklist_add")]
    [InlineData("mt_blacklist_remove")]
    [InlineData("mt_fleet_disconnect")]
    [InlineData("mt_orders_reset_tpsl")]
    [InlineData("mt_profile_settings_update")]
    [InlineData("mt_settings_set")]
    // TPSL bulk + panic.
    [InlineData("mt_tpsl_cancel_many")]
    [InlineData("mt_tpsl_split_many")]
    [InlineData("mt_tpsl_panic")]
    [InlineData("mt_tpsl_panic_many")]
    // Active order TP/SL/TS update.
    [InlineData("mt_orders_update_tpsl")]
    // Autostops balance-filter CRUD.
    [InlineData("mt_autostops_add")]
    [InlineData("mt_autostops_edit")]
    [InlineData("mt_autostops_start")]
    [InlineData("mt_autostops_stop")]
    [InlineData("mt_autostops_delete")]
    // Paste/import mutate the destination profile's algo store.
    [InlineData("mt_algos_paste_from_clipboard")]
    [InlineData("mt_algos_import_json")]
    // Bulk field-level edit.
    [InlineData("mt_algos_bulk_edit")]
    // Algorithm creation via clone-from-source.
    [InlineData("mt_algos_create")]
    // profile_settings delete (list is read-only).
    [InlineData("mt_profile_settings_delete")]
    // Vault profile delete (get is read-only).
    [InlineData("mt_vault_delete_profile")]
    // Alerts delete + set-running (save is non-destructive create-or-update).
    [InlineData("mt_alerts_delete")]
    [InlineData("mt_alerts_set_running")]
    // Watchdog placeholder — destructive token rotation.
    [InlineData("mt_watchdog_token_update")]
    // Fleet margin-type campaign.
    [InlineData("mt_fleet_set_margin_type")]
    // Profile-level whitelist CRUD.
    [InlineData("mt_whitelist_add")]
    [InlineData("mt_whitelist_remove")]
    [InlineData("mt_whitelist_bulk_add")]
    [InlineData("mt_whitelist_bulk_remove")]
    // Local profiles.json / folders.json CRUD.
    [InlineData("mt_profiles_add")]
    [InlineData("mt_profiles_edit")]
    [InlineData("mt_profiles_delete")]
    [InlineData("mt_profiles_move")]
    [InlineData("mt_profiles_import_csv")]
    [InlineData("mt_folders_add")]
    [InlineData("mt_folders_edit")]
    [InlineData("mt_folders_delete")]
    public void ConfirmRequired_True_ForDestructiveTools(string toolName)
    {
        ConfirmGate.IsConfirmRequired(toolName).Should().BeTrue(
            because: $"{toolName}'s registry schema declares confirm in inputSchema.required");
    }

    [Theory]
    [InlineData("mt_status")]
    [InlineData("mt_account_balance")]
    [InlineData("mt_algos_list")]
    [InlineData("mt_exchange_pairs")]
    public void ConfirmRequired_False_ForReadOnlyTools(string toolName)
    {
        ConfirmGate.IsConfirmRequired(toolName).Should().BeFalse(
            because: $"{toolName} is a read-only tool; the gate must not fire");
    }

    [Fact]
    public void ConfirmRequired_False_ForUnknownTool()
    {
        ConfirmGate.IsConfirmRequired("mt_definitely_not_a_real_tool_xxxx").Should().BeFalse(
            because: "an unknown tool name has no registry entry, so the gate cannot demand confirm");
    }

    [Fact]
    public void RejectIfMissing_ReturnsError_WhenConfirmAbsent()
    {
        var args = JObject.Parse("""{"key":"Core.LOG_LEVEL","value":"INFO"}""");
        ConfirmGate.RejectIfMissing("mt_blacklist_add", args)
            .Should().NotBeNull(because: "no confirm field present");
    }

    [Fact]
    public void RejectIfMissing_ReturnsError_WhenConfirmFalse()
    {
        var args = JObject.Parse("""{"key":"Core.LOG_LEVEL","value":"INFO","confirm":false}""");
        ConfirmGate.RejectIfMissing("mt_blacklist_add", args)
            .Should().NotBeNull(because: "confirm=false should still be rejected");
    }

    [Fact]
    public void RejectIfMissing_ReturnsNull_WhenConfirmTrue()
    {
        var args = JObject.Parse("""{"key":"Core.LOG_LEVEL","value":"INFO","confirm":true}""");
        ConfirmGate.RejectIfMissing("mt_blacklist_add", args)
            .Should().BeNull(because: "confirm=true allows the call through");
    }

    [Fact]
    public void RejectIfMissing_ReturnsNull_ForNonDestructiveTool()
    {
        var args = JObject.Parse("""{}""");
        ConfirmGate.RejectIfMissing("mt_status", args)
            .Should().BeNull(because: "mt_status doesn't require confirm");
    }

    [Fact]
    public void RejectIfMissing_ErrorMessage_NamesTheToolAndCitesGate()
    {
        var args = JObject.Parse("""{}""");
        string? msg = ConfirmGate.RejectIfMissing("mt_blacklist_add", args);
        msg.Should().NotBeNull();
        msg!.Should().Contain("mt_blacklist_add", because: "the error identifies the tool that was gated");
        msg.Should().Contain("confirm=true", because: "the error tells the caller what to add");
    }
}
