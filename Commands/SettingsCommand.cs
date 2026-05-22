using System;
using System.Collections.Generic;
using System.Linq;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Server profile settings commands — view and modify Core server configuration.
/// Settings are key-value string pairs stored on the Core server.
/// 
/// settings                    — show cached settings summary
/// settings get                — fetch and show all settings from Core
/// settings get <key>          — get a specific setting value
/// settings search <query>     — search settings by key or value
/// settings set <key> <value>  — update a setting (requires --confirm for safety)
/// settings delete <key>       — delete a setting (requires --confirm)
/// settings groups             — show settings grouped by prefix
/// </summary>
public sealed class SettingsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public SettingsCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "settings";
    public string Description => "View and modify server profile settings (supports @profile targeting)";
    public string Usage => "settings [get|get <key>|search <q>|set <key> <value> --confirm|delete <key> --confirm|groups]";

    public CommandResult Execute(string[] args)
    {
        // Parse out @profile suffix and --confirm flag
        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else if (arg.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        if (cleanArgs.Count == 0)
        {
            return ShowCachedSummary(targetProfile);
        }

        string? subCmd = cleanArgs[0].ToLowerInvariant();
        string[]? subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        return subCmd switch
        {
            "get" => subArgs.Length > 0
                ? GetSetting(subArgs, targetProfile)
                : FetchAllSettings(targetProfile),
            "search" => SearchSettings(subArgs, targetProfile),
            "set" => SetSetting(subArgs, targetProfile, confirmFlag),
            "delete" => DeleteSetting(subArgs, targetProfile, confirmFlag),
            "groups" => ShowGrouped(targetProfile),
            "profile-get" => GetProfileSettings(subArgs, targetProfile),
            "profile-update" => UpdateProfileSettings(subArgs, targetProfile, confirmFlag),
            // List keys of a named profile + delete one or more keys.
            "profile-list" => ListProfileSettings(subArgs, targetProfile),
            "profile-delete" => DeleteProfileSettings(subArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. {Usage}")
        };
    }

    private CoreConnection? ResolveConnection(string? targetProfile, out CommandResult? error)
    {
        error = null;
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            error = targetProfile != null
                ? CommandResult.Fail($"No connection '{targetProfile}'. Use 'status' to see connections.")
                : CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
            return null;
        }
        if (!conn.IsConnected)
        {
            error = CommandResult.Fail($"[{conn.Name}] Not connected.");
            return null;
        }
        return conn;
    }

    private CommandResult ShowCachedSummary(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        ProfileSettingsStore store = conn.ProfileSettingsStore;
        if (!store.HasData)
        {
            return CommandResult.Ok($"[{conn.Name}] No settings cached. Use 'settings get' to fetch from Core.");
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Profile '{store.ProfileName}': {store.Count} setting(s), last updated {store.LastUpdate:HH:mm:ss}.",
            new { store.ProfileName, store.Count, LastUpdate = store.LastUpdate.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    private CommandResult FetchAllSettings(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        (bool success, string? fetchError) = conn.RequestProfileSettings();
        if (!success)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to fetch settings: {fetchError}");
        }

        IReadOnlyList<KeyValuePair<string, string>>? all = conn.ProfileSettingsStore.GetAll();
        var data = new List<object>();
        for (int i = 0; i < all.Count; i++)
        {
            data.Add(new { Key = all[i].Key, Value = all[i].Value });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Profile '{conn.ProfileSettingsStore.ProfileName}': {all.Count} setting(s).",
            data);
    }

    private CommandResult GetSetting(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        string? key = args[0];

        // Fetch fresh if not cached
        if (!conn.ProfileSettingsStore.HasData)
        {
            (bool success, string? fetchError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to fetch settings: {fetchError}");
            }
        }

        string? value = conn.ProfileSettingsStore.GetValue(key);
        if (value == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Setting '{key}' not found.");
        }

        return CommandResult.Ok($"[{conn.Name}] {key} = {value}",
            new { Key = key, Value = value });
    }

    private CommandResult SearchSettings(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: settings search <query>");
        }

        if (!conn.ProfileSettingsStore.HasData)
        {
            return CommandResult.Fail($"[{conn.Name}] No settings cached. Use 'settings get' first.");
        }

        string? query = string.Join(" ", args);
        IReadOnlyList<KeyValuePair<string, string>>? results = conn.ProfileSettingsStore.Search(query);

        if (results.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No settings matching '{query}'.");
        }

        var data = new List<object>();
        for (int i = 0; i < results.Count; i++)
        {
            data.Add(new { Key = results[i].Key, Value = results[i].Value });
        }
        return CommandResult.Ok($"[{conn.Name}] {results.Count} setting(s) matching '{query}'.", data);
    }

    private CommandResult SetSetting(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: settings set <key> <value> --confirm");
        }

        string? key = args[0];
        string? value = string.Join(" ", args, 1, args.Length - 1);

        if (!confirmed)
        {
            string? currentValue = conn.ProfileSettingsStore.GetValue(key);
            string? currentDisplay = currentValue != null ? $"'{currentValue}'" : "(not set)";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Update setting '{key}'?\n" +
                $"  Current: {currentDisplay}\n" +
                $"  New:     '{value}'\n" +
                $"  Some settings require a Core restart.\n" +
                $"  Re-run with --confirm flag:\n" +
                $"  settings set {key} {value} --confirm");
        }

        var updated = new Dictionary<string, string> { { key, value } };
        (bool success, bool coreRestartNeeded, string? updateError) = conn.UpdateProfileSettings(updated);

        if (!success)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to update setting: {updateError}");
        }

        string? msg = $"[{conn.Name}] Setting '{key}' updated to '{value}' ✓";
        if (coreRestartNeeded)
        {
            msg += "\n  ⚠ Core restart is needed for this change to take effect.";
        }

        return CommandResult.Ok(msg,
            new { Key = key, Value = value, CoreRestartNeeded = coreRestartNeeded });
    }

    private CommandResult DeleteSetting(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: settings delete <key> --confirm");
        }

        string? key = args[0];

        if (!confirmed)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Delete setting '{key}'?\n" +
                $"  This is IRREVERSIBLE. Re-run with --confirm flag:\n" +
                $"  settings delete {key} --confirm");
        }

        var deleted = new HashSet<string> { key };
        (bool success, bool coreRestartNeeded, string? updateError) = conn.UpdateProfileSettings(
            new Dictionary<string, string>(), deleted);

        if (!success)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to delete setting: {updateError}");
        }

        string? msg = $"[{conn.Name}] Setting '{key}' deleted ✓";
        if (coreRestartNeeded)
        {
            msg += "\n  ⚠ Core restart is needed for this change to take effect.";
        }

        return CommandResult.Ok(msg,
            new { Key = key, Deleted = true, CoreRestartNeeded = coreRestartNeeded });
    }

    private CommandResult ShowGrouped(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.ProfileSettingsStore.HasData)
        {
            (bool success, string? fetchError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to auto-fetch settings: {fetchError}");
            }
        }

        IReadOnlyDictionary<string, List<KeyValuePair<string, string>>>? grouped = conn.ProfileSettingsStore.GetGrouped();
        var data = new List<object>();
        foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> g in grouped)
        {
            var keyParts = new string[g.Value.Count];
            for (int i = 0; i < g.Value.Count; i++)
            {
                keyParts[i] = g.Value[i].Key;
            }
            data.Add(new
            {
                Group = g.Key,
                Count = g.Value.Count,
                Keys = string.Join(", ", keyParts)
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {grouped.Count} setting group(s), {conn.ProfileSettingsStore.Count} total.",
            data);
    }

    
    private CommandResult GetProfileSettings(string[] subArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        string profileName = subArgs.Length > 0 ? subArgs[0] : "";
        string? result = conn.GetProfileSettings(profileName);
        if (string.IsNullOrEmpty(result))
        {
            return CommandResult.Fail("No profile settings returned.");
        }

        // BUG-3 fix: CoreConnection.GetProfileSettings returns a multi-line
        // string that may embed `(success=False)` and an `Error: ...` line
        // when MTCore rejected the request (e.g. "Getting profile settings
        // for non-current profile is not supported yet."). The wrapper
        // previously surfaced this as top-level success=true, masking the
        // upstream failure. Detect the marker and reflect it.
        if (result.IndexOf("(success=False)", StringComparison.Ordinal) >= 0)
        {
            return CommandResult.Fail(result.TrimEnd());
        }

        return CommandResult.Ok(result);
    }

    private CommandResult UpdateProfileSettings(string[] subArgs, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (!confirmed) return CommandResult.Fail("Profile settings update requires --confirm flag.");

        // Real root cause of the historical update bug: the mt_profile_settings_update
        // dispatcher sends `settings profile-update <profile_name> <updates_json>`.
        // The previous implementation mis-parsed subArgs[0] as a setting KEY and
        // subArgs[1] as a setting VALUE — completely ignoring the JSON updates
        // payload.  Nothing actually invoked the typed update path before
        // the Smoke test added later, so the bug was latent.  Correct parse:
        //   subArgs[0] = profile_name (ignored — see below)
        //   subArgs[1] = updates JSON object {"key":"value", ...}
        //
        // MTCore-side: rejects updates whose profileName is not the current
        // profile ("Can't update profile X because it is not current profile.").
        // The explicit-profileName overload passes the supplied name verbatim,
        // so passing the connection alias fails whenever the bench-side current
        // profile name differs from the client-side connection name. The
        // implicit-profileName overload reads the actual current profile name
        // from the local profile-settings store instead.
        if (subArgs.Length < 2)
            return CommandResult.Fail("Usage: settings profile-update <profile_name> <updates_json> --confirm");

        // The dispatcher base64-encodes updates_json so the REPL tokenizer
        // doesn't strip the JSON's double-quotes.  Tolerate raw JSON too
        // (when called directly from REPL).
        string raw = subArgs[1];
        string updatesJson = TryDecodeBase64Utf8(raw) ?? raw;
        Dictionary<string, string> updated;
        try
        {
            var token = Newtonsoft.Json.Linq.JObject.Parse(updatesJson);
            updated = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in token.Properties())
                updated[prop.Name] = prop.Value?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"updates_json parse error: {ex.Message}");
        }
        if (updated.Count == 0)
            return CommandResult.Fail("updates_json is empty — nothing to apply.");

        var (uok, _, uerr) = conn.UpdateProfileSettings(updated, new HashSet<string>());
        if (!uok)
            return CommandResult.Fail($"Profile settings update failed: {uerr}");
        return CommandResult.Ok(
            $"[{conn.Name}] profile-update: applied {updated.Count} key(s).",
            new
            {
                Server = conn.Name,
                ProfileName = conn.ProfileSettingsStore.ProfileName,
                AppliedKeys = updated.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                Count = updated.Count,
            });
    }

    // ── profile_settings list + delete ───────────────────────────

    /// <summary>
    /// List the keys of the connected profile's settings.
    /// Read-only.  MTShared has no list-named-profiles RPC (verified via
    /// reflection: the only profile-settings wire methods are
    /// SendGetCurrentProfileSettingsRequest / SendGetProfileSettingsRequest /
    /// SendUpdateProfileSettingsRequest), so this enumerates the keys of the
    /// CURRENT profile via the existing ProfileSettingsStore.  Optional
    /// substring filter via <c>--grep &lt;needle&gt;</c>.
    /// </summary>
    private CommandResult ListProfileSettings(string[] subArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        // Ensure settings store is warm.
        if (!conn.ProfileSettingsStore.HasData)
        {
            var (ok, reqErr) = conn.RequestProfileSettings();
            if (!ok) return CommandResult.Fail($"[{conn.Name}] Failed to load profile settings: {reqErr}");
        }

        string? grep = null;
        for (int i = 0; i < subArgs.Length; i++)
        {
            if (subArgs[i].Equals("--grep", StringComparison.OrdinalIgnoreCase) && i + 1 < subArgs.Length)
            { grep = subArgs[i + 1]; i++; }
        }

        IReadOnlyList<KeyValuePair<string, string>> all = conn.ProfileSettingsStore.GetAll();
        var keys = (grep != null
                ? all.Where(kv => kv.Key.IndexOf(grep, StringComparison.OrdinalIgnoreCase) >= 0)
                : all)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return CommandResult.Ok(
            $"[{conn.Name}] {keys.Count} profile-setting key(s){(grep != null ? $" (grep=\"{grep}\")" : "")}.",
            new
            {
                Server = conn.Name,
                ProfileName = conn.ProfileSettingsStore.ProfileName,
                KeyCount = keys.Count,
                Grep = grep,
                Keys = keys,
                LastUpdate = conn.ProfileSettingsStore.LastUpdate.ToString("o"),
            });
    }

    /// <summary>
    /// Accept either base64-encoded JSON (from the MCP dispatcher,
    /// which encodes to dodge the REPL tokenizer's double-quote stripping) OR
    /// raw JSON (from direct REPL calls).  Returns null if the input is not
    /// valid base64 of a JSON object — caller treats input as raw.
    /// </summary>
    private static string? TryDecodeBase64Utf8(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith("{")) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(s.Trim());
            string str = System.Text.Encoding.UTF8.GetString(bytes);
            if (str.TrimStart().StartsWith("{")) return str;
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Delete one or more profile-settings keys via the existing
    /// <c>SendUpdateProfileSettingsRequest</c> wire method's <c>deleted</c>
    /// HashSet parameter.  Confirm-gated.  Accepts comma-separated keys.
    /// </summary>
    private CommandResult DeleteProfileSettings(string[] subArgs, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (!confirmed)
            return CommandResult.Fail("settings profile-delete requires --confirm.");
        if (subArgs.Length < 1)
            return CommandResult.Fail("Usage: settings profile-delete <key[,key,...]> --confirm");

        // Allow a single CSV arg OR multiple positional args.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string a in subArgs)
        {
            foreach (string part in a.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) keys.Add(t);
            }
        }
        if (keys.Count == 0) return CommandResult.Fail("No keys supplied for delete.");

        // Warm the store so we can classify per-key.
        if (!conn.ProfileSettingsStore.HasData)
        {
            var (ok, reqErr) = conn.RequestProfileSettings();
            if (!ok) return CommandResult.Fail($"[{conn.Name}] Failed to load profile settings: {reqErr}");
        }
        var present = conn.ProfileSettingsStore.GetAll()
            .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actuallyPresent = keys.Where(k => present.Contains(k)).ToHashSet();
        var notFound = keys.Where(k => !present.Contains(k)).ToList();

        if (actuallyPresent.Count == 0)
            return CommandResult.Fail(
                $"not_found: none of [{string.Join(",", keys)}] are in {conn.Name}'s profile settings.");

        // Must use the implicit-profileName overload so MTCore sees the actual
        // current profile name (not conn.Name).  See the matching fix in
        // UpdateProfileSettings above.
        var (uok, _, uerr) = conn.UpdateProfileSettings(
            new Dictionary<string, string>(), actuallyPresent);
        if (!uok)
            return CommandResult.Fail($"Profile settings delete failed: {uerr}");

        return CommandResult.Ok(
            $"[{conn.Name}] profile-delete: removed {actuallyPresent.Count} key(s)" +
            (notFound.Count > 0 ? $"; not_found: {string.Join(",", notFound)}" : "") +
            ".",
            new
            {
                Server = conn.Name,
                Deleted = actuallyPresent.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                NotFound = notFound.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            });
    }
}
