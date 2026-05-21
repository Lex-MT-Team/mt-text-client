using System.Linq;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core;

/// <summary>
/// Central confirm-gate audit fixture. An audit aid plus a minor injection-defense
/// layer at the JSON-RPC boundary.
///
/// Reads from <see cref="ToolRegistry.AllTools"/> and treats any tool whose
/// <c>inputSchema.required</c> array contains <c>confirm</c> as destructive
/// / requires <c>confirm=true</c>. Returns a single, uniform rejection shape
/// across every confirm-required tool, with no per-tool branching. Invoked
/// once early in <c>HandleToolCall</c>, before dispatch.
/// </summary>
public static class ConfirmGate
{
    /// <summary>
    /// True when the named tool's registry entry declares <c>confirm</c> in
    /// <c>inputSchema.required</c>. <c>ConfirmGateStaticTests</c> curates the
    /// destructive-tool list and reads from the same registry, so the two
    /// stay in lockstep without a separate curated list.
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
               "(ConfirmGate audit fixture)";
    }
}
