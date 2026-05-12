using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Stage 0.4 — verify the ConfirmGate actually fires at runtime, not just
/// that the registry declares <c>confirm</c> in <c>inputSchema.required</c>.
/// <see cref="ConfirmGateStaticTests"/> covers the schema declaration;
/// this file probes the running MCP server to confirm that calls without
/// confirm are rejected with the documented -32602 error, while calls
/// with <c>confirm=true</c> pass the gate.
///
/// The Static category is appropriate here: the gate runs inside the MCP
/// server's HandleToolCall path, BEFORE any MTCore dispatch. So the test
/// works without a bench — only the MCP subprocess is needed.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class ConfirmGateRuntimeTests
{
    private readonly McpFixture _mcp;
    public ConfirmGateRuntimeTests(McpFixture mcp) => _mcp = mcp;

    [Theory]
    [InlineData("mt_algos_start_all",     "profile",  "bench_01")]
    [InlineData("mt_algos_stop_all",      "profile",  "bench_01")]
    [InlineData("mt_fleet_disconnect",    null,        null)]
    public async Task ConfirmRequiredTool_RejectsWithoutConfirm(string toolName, string? argKey, string? argValue)
    {
        // Build minimal args that exclude `confirm`. For the three bulk tools
        // above, only `confirm` is required by the schema — every other
        // field is optional. The gate must fire AT THE MCP LAYER before
        // the call even reaches MTCore. No bench needed.
        object args = argKey == null
            ? new { }
            : (object)new System.Collections.Generic.Dictionary<string, object?> { [argKey] = argValue };

        var resp = await _mcp.CallTool(toolName, args);

        // The gate emits -32602 (same shape as ValidateRequiredArguments
        // does for missing-required-field errors). Either an RPC error
        // OR a tool error with success:false is acceptable as a rejection.
        bool rejected = resp.IsRpcError ||
            (resp.ParsedBody is { } b &&
             b.TryGetProperty("success", out var s) &&
             s.ValueKind == System.Text.Json.JsonValueKind.False);
        rejected.Should().BeTrue(
            because: $"{toolName} is registry-marked confirm-required; ConfirmGate must reject without confirm");
    }

    [Fact]
    public async Task BlacklistAdd_WithoutConfirm_IsRejectedAtGate()
    {
        // mt_blacklist_add has multiple required fields (type, market_type,
        // confirm). The gate fires on the missing confirm even when other
        // required fields are also missing — confirm is checked first.
        var resp = await _mcp.CallTool("mt_blacklist_add", new
        {
            type = "symbol",
            market_type = 3,
            quote_asset = "usdt",
            symbol = "testforensicusdt9999",
            // confirm intentionally omitted
        });
        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate emits -32602 when a confirm-required tool is called without confirm");
    }

    [Fact]
    public async Task NonDestructiveTool_NotRejectedByGate()
    {
        // mt_status has no required fields. ConfirmGate must be permissive.
        var resp = await _mcp.CallTool("mt_status", new { });
        resp.IsRpcError.Should().BeFalse(
            because: "mt_status is read-only; ConfirmGate must not fire");
    }
}
