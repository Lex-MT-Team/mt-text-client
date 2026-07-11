using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MTTextClient.Core;
using MTTextClient.MCP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Tools.DispatcherSnapshotGenerator;

/// <summary>
/// Renders the deterministic CLI-string that
/// <see cref="McpServer.MapToolToCommand"/> emits for every registry tool
/// with a canonical argument set. The output is committed as
/// <c>tests/MTTextClient.Tests/_expected/commandlines.snapshot.json</c>;
/// the Static <see cref="MTTextClient.Tests.Static.DispatcherSnapshotTests"/>
/// re-runs this rendering in-process and asserts byte-equality against the
/// committed file. Any change to the dispatcher CLI string for any tool
/// breaks CI immediately — the registry-drift safety net.
///
/// CLI:
///   dotnet run --project tools/DispatcherSnapshotGenerator -- [--check]
///
/// Without --check: writes the snapshot file. With --check: writes to stdout
/// only and compares against the committed file, exit 1 on drift.
/// </summary>
public static class Program
{
    /// <summary>Path the committed snapshot lives at, relative to repo root.</summary>
    public const string SnapshotRepoPath = "tests/MTTextClient.Tests/_expected/commandlines.snapshot.json";

    public static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        string repoRoot = FindRepoRoot();
        string snapshotPath = Path.Combine(repoRoot, SnapshotRepoPath);

        string rendered = Render(ToolRegistry.AllTools());

        if (check)
        {
            if (!File.Exists(snapshotPath))
            {
                Console.Error.WriteLine($"[DispatcherSnapshotGenerator] Snapshot missing at {snapshotPath}. Run without --check to create it.");
                return 1;
            }
            string committed = File.ReadAllText(snapshotPath);
            if (committed != rendered)
            {
                Console.Error.WriteLine($"[DispatcherSnapshotGenerator] Snapshot drift detected at {snapshotPath}. Re-run without --check to regenerate.");
                return 1;
            }
            Console.WriteLine("[DispatcherSnapshotGenerator] Snapshot is in sync with the dispatcher.");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, rendered);
        int count = ToolRegistry.AllTools().Count();
        Console.WriteLine($"[DispatcherSnapshotGenerator] Wrote {snapshotPath} ({count} tools).");
        return 0;
    }

    /// <summary>
    /// Walk the registry, build a canonical argument set for each tool, call
    /// <see cref="McpServer.MapToolToCommand"/>, and emit a deterministic
    /// JSON document: <c>{ "tool_name": "cli string", ... }</c>.
    ///
    /// Canonical values (deterministic by design — value depends only on
    /// type + property name, never on environment):
    ///   • string  → <c>&lt;property_name&gt;</c> (angle-bracketed for readability)
    ///   • boolean → <c>true</c>
    ///   • integer → <c>1</c>
    ///   • number  → <c>1</c>
    ///   • array   → <c>["&lt;item&gt;"]</c>
    ///
    /// Every property in the tool's <c>inputSchema.properties</c> is filled in
    /// (not just required ones) — that way the snapshot exercises the full
    /// breadth of the dispatcher template.
    /// </summary>
    public static string Render(IEnumerable<JObject> tools)
    {
        var pairs = new List<(string Name, string Cli)>();
        foreach (var t in tools)
        {
            string name = t["name"]?.Value<string>() ?? "";
            if (string.IsNullOrEmpty(name)) continue;

            // Internal / event-stream tools skip MapToolToCommand entirely —
            // they're handled by HandleInternalTool / HandleEventTool. We
            // record an explicit marker so the snapshot still includes one
            // line per tool (deterministic count assertion).
            if (IsInternallyDispatched(name))
            {
                pairs.Add((name, "<internal-handler>"));
                continue;
            }

            var args = BuildCanonicalArgs(t);
            string? cli = McpServer.MapToolToCommand(name, args);
            pairs.Add((name, cli ?? "<unmapped>"));
        }

        // Stable ordering: registry enumeration order. We DON'T alpha-sort —
        // the registry order is itself an assertion target.
        var obj = new JObject();
        foreach (var (n, c) in pairs)
            obj[n] = c;

        // Pretty-print with a trailing newline so the file plays nicely with
        // editors and git diffs.
        return JsonConvert.SerializeObject(obj, Formatting.Indented) + "\n";
    }

    private static bool IsInternallyDispatched(string toolName) =>
        toolName.StartsWith("mt_events_", StringComparison.Ordinal) ||
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
        toolName == "mt_market_live_algorithms" ||
        toolName == "mt_algos_group_by_name";

    private static JObject BuildCanonicalArgs(JObject tool)
    {
        var result = new JObject();
        if (!(tool["inputSchema"] is JObject schema)) return result;
        if (!(schema["properties"] is JObject props)) return result;

        foreach (var prop in props.Properties())
        {
            string key = prop.Name;
            string? type = (prop.Value as JObject)?["type"]?.Value<string>();
            JToken value = type switch
            {
                "boolean" => new JValue(true),
                "integer" => new JValue(1),
                "number"  => new JValue(1),
                "array"   => new JArray("<item>"),
                _         => new JValue($"<{key}>")
            };
            result[key] = value;
        }
        return result;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MTTextClient.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new IOException("Could not locate repo root (no MTTextClient.csproj ancestor).");
    }
}
