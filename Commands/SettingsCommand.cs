using System;
using System.Collections.Generic;
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

        string profileName = subArgs.Length > 0 ? subArgs[0] : conn.Name;
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
        if (conn == null)
        {
            return error!;
        }

        if (!confirmed)
        {
            return CommandResult.Fail("Profile settings update requires --confirm flag.");
        }

        if (subArgs.Length < 2)
        {
            return CommandResult.Fail("Usage: settings profile-update <key> <value> --confirm");
        }

        string key = subArgs[0];
        string value = string.Join(" ", subArgs, 1, subArgs.Length - 1);
        Dictionary<string, string> updated = new Dictionary<string, string> { { key, value } };
        HashSet<string> deleted = new HashSet<string>();

        string profileName = conn.Name;
        string? result = conn.UpdateProfileSettings(profileName, updated, deleted);
        if (string.IsNullOrEmpty(result))
        {
            return CommandResult.Fail("No response from profile settings update.");
        }

        return CommandResult.Ok(result);
    }

}
