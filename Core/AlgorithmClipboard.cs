using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Core;

/// <summary>
/// File-backed clipboard for cross-profile / cross-exchange algorithm transfer.
/// The clipboard lives at <c>~/mt-clipboard/algo-clipboard.json</c> so it
/// survives MCP-subprocess restarts and is readable from a separate session.
///
/// Single-slot semantics: copy-to-clipboard overwrites; paste reads.
///
/// <para><b>Schema version</b>: bumped when the exported JSON shape changes
/// in a way that breaks import on older builds. Old payloads are rejected
/// with a structured <c>schema_version_mismatch</c> error — never silently
/// applied — so an MTCore update that ships a new algorithm config layout
/// can not corrupt the destination by paste.</para>
/// </summary>
public static class AlgorithmClipboard
{
    /// <summary>Version of the export-JSON shape. Bump on any field rename / addition.</summary>
    public const string CurrentSchemaVersion = "v1";

    public static readonly string ClipboardDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mt-clipboard");

    public static readonly string ClipboardFile = Path.Combine(ClipboardDir, "algo-clipboard.json");

    /// <summary>
    /// Write <paramref name="exportJson"/> to the clipboard file (overwrites).
    /// </summary>
    public static void Save(string exportJson)
    {
        Directory.CreateDirectory(ClipboardDir);
        File.WriteAllText(ClipboardFile, exportJson);
    }

    /// <summary>
    /// Returns the raw clipboard JSON text, or null if the clipboard is empty.
    /// </summary>
    public static string? Load()
    {
        if (!File.Exists(ClipboardFile)) return null;
        string s = File.ReadAllText(ClipboardFile);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Deletes the clipboard file. No-op if absent.</summary>
    public static void Clear()
    {
        if (File.Exists(ClipboardFile)) File.Delete(ClipboardFile);
    }

    /// <summary>
    /// Validate that <paramref name="payload"/> declares the current
    /// schema version.  Returns null on success; structured error message
    /// otherwise (always starts with "schema_version_mismatch" so callers
    /// can pattern-match).
    /// </summary>
    public static string? ValidateSchema(JObject payload)
    {
        string? v = payload[SchemaVersionField]?.Value<string>();
        if (v == null)
            return $"schema_version_mismatch: payload missing {SchemaVersionField} (expected {CurrentSchemaVersion}).";
        if (!string.Equals(v, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
            return $"schema_version_mismatch: payload schema_version={v}, current={CurrentSchemaVersion}. " +
                "Update the source mt-text-client build or re-export the algorithm with the current schema.";
        return null;
    }

    public const string SchemaVersionField = "schema_version";
    public const string ExportedFromExchangeField = "exported_from_exchange";
    public const string ExportedFromProfileField = "exported_from_profile";
    public const string AlgorithmField = "algorithm";
}
