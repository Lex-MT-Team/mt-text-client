using System.Linq;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core;

/// <summary>
/// Stage 0.4 — central confirm-gate audit fixture.
///
/// Per OV-5: the confirm gate is an audit aid + minor injection-defense layer.
/// Pre-Stage-0.4 there were TWO confirm gates inside <see cref="MCP.McpServer"/>:
///
///   1. <c>RequiresMcpConfirm(toolName)</c> — a hard-coded list of three bulk
///      tools (mt_algos_start_all, mt_algos_stop_all, mt_fleet_disconnect)
///      that emitted a custom JSON error string when called without confirm.
///   2. <c>ValidateRequiredArguments</c> — the schema-driven required-fields
///      check that catches every other confirm-required tool by virtue of
///      declaring <c>confirm</c> in <c>inputSchema.required</c>. Returned a
///      generic "-32602 Missing required argument: confirm" message.
///
/// Both branches did the same job in different ways. <see cref="ConfirmGate"/>
/// is the single, registry-driven replacement:
///   • Reads from <see cref="ToolRegistry.AllTools"/> — the tools/list spine
///     installed in Stage 0.3.
///   • Treats any tool whose <c>inputSchema.required</c> array contains
///     <c>confirm</c> as "destructive / requires confirm=true".
///   • Returns a single, named, structured rejection — same shape across
///     every confirm-required tool, no per-tool branching.
///
/// The semantic merger:
///   • Catalog source of truth = registry (eliminates the hard-coded list).
///   • Rejection wording = uniform across tools.
///   • Detection point = a single call early in HandleToolCall, before
///     dispatch.
/// </summary>
public static class ConfirmGate
{
    /// <summary>
    /// True when the named tool's registry entry declares <c>confirm</c> in
    /// <c>inputSchema.required</c>. Stage 0.1's <c>ConfirmGateStaticTests</c>
    /// curates the destructive-tool list; Stage 0.4 reads the same source
    /// of truth (the registry) so the two stay in lockstep without a
    /// separate curated list.
    /// </summary>
    public static bool IsConfirmRequired(string toolName)
    {
        var tool = ToolRegistry.AllTools()
            .FirstOrDefault(t => t["name"]?.Value<string>() == toolName);
        if (tool == null) return false;
        if (!(tool["inputSchema"] is JObject schema)) return false;
        if (!(schema["required"] is JArray required)) return false;
        return required.Any(r => r.Value<string>() == "confirm");
    }

    /// <summary>
    /// Inspect the call and return a rejection message if the tool requires
    /// <c>confirm=true</c> but the caller did not supply it. Returns
    /// <c>null</c> when the call is allowed to proceed.
    ///
    /// Callers (currently <see cref="MCP.McpServer.HandleToolCall"/>) treat a
    /// non-null result as an immediate rejection — the call never reaches
    /// the underlying CLI dispatcher or MTCore.
    /// </summary>
    public static string? RejectIfMissing(string toolName, JObject arguments)
    {
        if (!IsConfirmRequired(toolName)) return null;

        bool confirmed = arguments["confirm"]?.Value<bool>() == true;
        if (confirmed) return null;

        return $"{toolName} requires confirm=true. " +
               "This is a destructive or bulk operation; pass confirm=true to execute. " +
               "(ConfirmGate / OV-5 audit fixture)";
    }
}
