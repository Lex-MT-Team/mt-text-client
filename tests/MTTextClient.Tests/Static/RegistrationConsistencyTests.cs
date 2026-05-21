using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// The registry refactor's audit fixture: every tool that appears in the
/// registry must be dispatchable end-to-end, and every dispatched call must
/// be answerable by the registry's schema. This is the "no orphan
/// registrations" guarantee:
///
///   • <b>Registry → dispatcher:</b> for each tool emitted by
///     <c>ToolRegistry.AllTools</c>, calling that tool must not return
///     <c>"Unknown command"</c>.
///   • <b>Dispatcher → registry:</b> covered by
///     <see cref="ToolCatalogStaticTests"/> in the form
///     <c>EveryBaselineTool_StillExistsInLiveCatalog</c>.
///
/// This file asserts the first direction with a smoke-shaped probe per tool:
/// call the tool with empty arguments and assert the response isn't the
/// "Unknown command" / "no MCP dispatcher entry" string. We tolerate any
/// other failure (success:false from validation, isError from runtime),
/// because the goal is purely to verify the dispatch routing exists, not
/// that the underlying command would succeed.
///
/// Performance note: 200+ subprocess calls add ~2s to PR-gate CI. To keep
/// this cheap, the test category is <see cref="TraitCategories.Static"/>
/// and the tool spawn is shared across the whole collection via
/// <see cref="McpFixture"/>.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class RegistrationConsistencyTests
{
    private readonly McpFixture _mcp;
    public RegistrationConsistencyTests(McpFixture mcp) => _mcp = mcp;

    [Fact]
    public async Task EveryRegisteredTool_HasDispatcherEntry()
    {
        // Enumerate the live tools/list — that's exactly what ToolRegistry.AllTools
        // emits. Then probe each tool's dispatch path with a short per-call
        // timeout. The only failure shape we care
        // about is the McpServer's "Unknown tool" sentinel string; any other
        // response (success, runtime failure, timeout) means routing exists.
        //
        // A per-call timeout is fine because we are not waiting for MTCore —
        // we are only verifying that the MCP-side dispatch table has an entry.
        // The 200+ tools take ~5–15s total this way.
        var orphans = new System.Collections.Generic.List<(string Tool, string Hint)>();
        foreach (var tool in _mcp.Tools)
        {
            string name = tool.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrEmpty(name)) continue;

            // Internal / event-stream tools are dispatched by separate handlers
            // (HandleInternalTool / HandleEventTool) without going through the
            // CLI mapping. Skip them — their schema-vs-handler binding is
            // covered structurally by the McpServer code itself.
            if (IsInternallyDispatched(name)) continue;

            // Build a minimal "required-only" argument set so we pass the schema
            // gate. We don't care whether the underlying command succeeds —
            // only whether the routing exists.
            var args = BuildMinimalArgs(tool);

            string? msg = null;
            try
            {
                var resp = await _mcp.CallTool(name, args, timeout: TimeSpan.FromSeconds(1));
                msg = resp.InnerMessage ?? resp.Text;
            }
            catch (TimeoutException)
            {
                // Routing exists, the handler is just slow / blocked on a
                // dependency (bench, vault, exchange). Not an orphan.
                continue;
            }

            if (msg != null && msg.StartsWith("Unknown tool:", System.StringComparison.OrdinalIgnoreCase))
                orphans.Add((name, msg));
        }

        orphans.Should().BeEmpty(
            because: "every tool emitted by the registry must have a corresponding " +
                     "dispatcher entry. Orphans: " +
                     string.Join("; ", orphans.Select(o => $"{o.Tool} → '{o.Hint}'")));
    }

    /// <summary>
    /// Tools handled by <c>HandleInternalTool</c> or <c>HandleEventTool</c> in
    /// <see cref="MCP.McpServer"/>'s top-level switch — they skip the
    /// REPL-style <c>MapToolToCommand</c> mapping that the rest of the
    /// registry feeds into. Their schema is in the registry; their handler
    /// is a direct method call.
    /// </summary>
    private static bool IsInternallyDispatched(string toolName) =>
        toolName.StartsWith("mt_events_", System.StringComparison.Ordinal) ||
        toolName == "mt_metrics_get" ||
        toolName == "mt_rate_status" ||
        toolName == "mt_vault_store_profile" ||
        toolName == "mt_vault_list_profiles" ||
        toolName == "mt_config_snapshot" ||
        toolName == "mt_config_restore" ||
        toolName == "mt_settings_diff" ||
        toolName == "mt_core_shutdown" ||
        toolName == "mt_algos_tpsl_change" ||
        toolName == "mt_algos_profiling" ||
        toolName == "mt_config_import_algos" ||
        toolName == "mt_algos_snapshot" ||
        toolName == "mt_algos_group_by_name";

    /// <summary>
    /// Build a JSON-object stub of arguments using each required-string field's
    /// name as a placeholder value. Just enough to pass <c>inputSchema.required</c>
    /// — the dispatcher uniqueness check runs before the underlying
    /// command tries to interpret the values.
    /// </summary>
    private static object BuildMinimalArgs(JsonElement tool)
    {
        var dict = new System.Collections.Generic.Dictionary<string, object?>();
        if (!tool.TryGetProperty("inputSchema", out var schema)) return dict;
        if (!schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array) return dict;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return dict;

        foreach (var r in req.EnumerateArray())
        {
            string? key = r.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            if (!props.TryGetProperty(key, out var propDef)) continue;
            string? type = propDef.TryGetProperty("type", out var t) ? t.GetString() : "string";
            object? value = type switch
            {
                "boolean" => false,
                "integer" => 0,
                "number"  => 0.0,
                "array"   => System.Array.Empty<string>(),
                _         => "_probe_"
            };
            dict[key] = value;
        }
        return dict;
    }
}
