using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Local CRUD over the set of known folder names
/// (<c>~/.config/mt-textclient/folders.json</c>).  Folders are a pure
/// client-side concept used for display / dispatch grouping; never sent to
/// MTCore.  A profile's <c>Folder</c> field references one of these names.
///
/// Subcommands:
///   folders list
///   folders add &lt;name&gt; --confirm
///   folders edit &lt;old&gt; &lt;new&gt; --confirm   (renames in folders.json AND every profile that references it)
///   folders delete &lt;name&gt; --confirm
/// </summary>
public sealed class FoldersCommand : ICommand
{
    public string Name => "folders";
    public string Description => "Local folders.json CRUD";
    public string Usage => "folders <list|add|edit|delete> [args] --confirm";

    public FoldersCommand() { }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0) return CommandResult.Fail(Usage);

        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string a in args)
        {
            if (a.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-y", StringComparison.OrdinalIgnoreCase)) confirmFlag = true;
            else cleanArgs.Add(a);
        }
        if (cleanArgs.Count == 0) return CommandResult.Fail(Usage);

        string sub = cleanArgs[0].ToLowerInvariant();
        var rest = cleanArgs.GetRange(1, cleanArgs.Count - 1);

        return sub switch
        {
            "list" or "ls" => HandleList(),
            "add" => HandleAdd(rest, confirmFlag),
            "edit" or "rename" => HandleEdit(rest, confirmFlag),
            "delete" or "rm" => HandleDelete(rest, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: '{sub}'.")
        };
    }

    private CommandResult HandleList()
    {
        var known = FolderStore.LoadFolders();
        var profiles = ProfileManager.LoadProfiles();
        var counts = profiles
            .GroupBy(p => p.Folder ?? "")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = known.Select(name => new
        {
            Name = name,
            ProfileCount = counts.TryGetValue(name, out int c) ? c : 0,
        }).ToList();
        // Also surface ad-hoc folders referenced by profiles but not in folders.json.
        var orphans = counts.Keys
            .Where(k => !string.IsNullOrEmpty(k) && !known.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Select(k => new { Name = k, ProfileCount = counts[k], Orphan = true })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"folders.json: {known.Count} known folder(s), {orphans.Count} orphan(s).");
        foreach (var f in rows) sb.AppendLine($"  {f.Name} ({f.ProfileCount} profile(s))");
        foreach (var o in orphans) sb.AppendLine($"  {o.Name} ({o.ProfileCount}) ⚠ ORPHAN");
        return CommandResult.Ok(sb.ToString(), new
        {
            Folders = rows,
            Orphans = orphans,
            UnfolderedProfileCount = counts.TryGetValue("", out int rootCount) ? rootCount : 0,
        });
    }

    private CommandResult HandleAdd(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("folders add requires --confirm.");
        if (rest.Count < 1) return CommandResult.Fail("Usage: folders add <name> --confirm");
        string name = rest[0];
        if (string.IsNullOrWhiteSpace(name)) return CommandResult.Fail("folder name cannot be empty.");
        var known = FolderStore.LoadFolders();
        if (known.Contains(name, StringComparer.OrdinalIgnoreCase))
            return CommandResult.Ok($"folders add '{name}': no_change (already known).",
                new { Action = "add", Name = name, NoChange = true });
        known.Add(name);
        FolderStore.SaveFolders(known);
        return CommandResult.Ok($"Added folder '{name}'. Total known folders: {known.Count}.",
            new { Action = "add", Name = name, FolderCount = known.Count });
    }

    private CommandResult HandleEdit(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("folders edit requires --confirm.");
        if (rest.Count < 2) return CommandResult.Fail("Usage: folders edit <old> <new> --confirm");
        string oldName = rest[0];
        string newName = rest[1];
        var known = FolderStore.LoadFolders();
        int idx = known.FindIndex(f => f.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return CommandResult.Fail($"not_found: folder '{oldName}' not in folders.json.");
        if (known.Any(f => f.Equals(newName, StringComparison.OrdinalIgnoreCase) && !f.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"duplicate_name: '{newName}' already exists.");
        known[idx] = newName;
        FolderStore.SaveFolders(known);

        // Cascade rename across profiles.json.
        var profiles = ProfileManager.LoadProfiles();
        int touched = 0;
        foreach (var p in profiles)
        {
            if (string.Equals(p.Folder, oldName, StringComparison.OrdinalIgnoreCase))
            { p.Folder = newName; touched++; }
        }
        if (touched > 0) ProfileManager.SaveProfiles(profiles);

        return CommandResult.Ok(
            $"Renamed folder '{oldName}' → '{newName}'. Cascaded to {touched} profile(s).",
            new { Action = "edit", From = oldName, To = newName, ProfilesUpdated = touched });
    }

    private CommandResult HandleDelete(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("folders delete requires --confirm.");
        if (rest.Count < 1) return CommandResult.Fail("Usage: folders delete <name> --confirm");
        string name = rest[0];
        var known = FolderStore.LoadFolders();
        int removed = known.RemoveAll(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return CommandResult.Fail($"not_found: folder '{name}' not in folders.json.");

        // Check for profiles referencing this folder (warn but do NOT silently
        // delete; the caller must re-bind them via 'profiles move').
        var profiles = ProfileManager.LoadProfiles();
        int orphanCount = profiles.Count(p => string.Equals(p.Folder, name, StringComparison.OrdinalIgnoreCase));
        FolderStore.SaveFolders(known);

        string msg = $"Deleted folder '{name}'.";
        if (orphanCount > 0)
            msg += $" ⚠ WARNING: {orphanCount} profile(s) still reference '{name}' " +
                   $"(now showing as ORPHAN in folders list — use 'profiles move' to re-bind).";
        return CommandResult.Ok(msg, new
        {
            Action = "delete", Name = name,
            OrphanedProfileCount = orphanCount,
            FolderCount = known.Count,
        });
    }
}
