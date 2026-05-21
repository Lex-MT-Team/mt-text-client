using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Local CRUD over <c>~/.config/mt-textclient/profiles.json</c>.
/// Operates entirely on the on-disk profile registry (the list of bench
/// connections this client knows about) — never sends a wire request to
/// MTCore.  Useful for fleet setup automation.
///
/// Subcommands:
///   profiles list
///   profiles add &lt;name&gt; &lt;address&gt; &lt;port&gt; &lt;token&gt; &lt;exchange&gt; [folder] --confirm
///   profiles edit &lt;name&gt; [--address=A] [--port=P] [--token=T] [--exchange=E] [--folder=F] [--rename=N] --confirm
///   profiles delete &lt;name&gt; --confirm
///   profiles move &lt;name&gt; &lt;folder&gt; --confirm
///   profiles import-csv &lt;path&gt; --confirm
/// </summary>
public sealed class ProfilesCommand : ICommand
{
    public string Name => "profiles";
    public string Description => "Local profiles.json CRUD (no wire calls)";
    public string Usage => "profiles <list|add|edit|delete|move|import-csv> [args] --confirm";

    public ProfilesCommand() { }

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
            "edit" => HandleEdit(rest, confirmFlag),
            "delete" or "rm" => HandleDelete(rest, confirmFlag),
            "move" or "mv" => HandleMove(rest, confirmFlag),
            "import-csv" => HandleImportCsv(rest, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: '{sub}'.")
        };
    }

    // ── list ────────────────────────────────────────────────────────────────

    private CommandResult HandleList()
    {
        var profiles = ProfileManager.LoadProfiles();
        var sb = new StringBuilder();
        sb.AppendLine($"profiles.json: {profiles.Count} profile(s).");
        foreach (var p in profiles.OrderBy(x => x.Folder).ThenBy(x => x.Name))
        {
            string folder = string.IsNullOrEmpty(p.Folder) ? "(root)" : p.Folder;
            sb.AppendLine($"  [{folder}] {p.Name} → {p.Address}:{p.Port} ({p.Exchange})");
        }
        return CommandResult.Ok(sb.ToString(), new
        {
            Count = profiles.Count,
            Profiles = profiles.Select(p => new
            {
                p.Name, p.Address, p.Port, Exchange = p.Exchange.ToString(),
                Folder = p.Folder ?? "",
                Tags = p.Tags ?? new Dictionary<string, string>(),
            }).ToList(),
        });
    }

    // ── add ─────────────────────────────────────────────────────────────────

    private CommandResult HandleAdd(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("profiles add requires --confirm.");
        if (rest.Count < 5)
            return CommandResult.Fail("Usage: profiles add <name> <address> <port> <token> <exchange> [folder] --confirm");

        string name = rest[0];
        string address = rest[1];
        if (!int.TryParse(rest[2], out int port) || port < 1 || port > 65535)
            return CommandResult.Fail($"Invalid port '{rest[2]}'. Use 1-65535.");
        string token = rest[3];
        if (!Enum.TryParse<ExchangeType>(rest[4], ignoreCase: true, out var exchange) ||
            !Enum.IsDefined(typeof(ExchangeType), exchange) || exchange == ExchangeType.UNKNOWN)
            return CommandResult.Fail($"Invalid exchange '{rest[4]}'. Allowed: BINANCE, OKX, BYBIT, HYPERLIQUID.");
        string folder = rest.Count >= 6 ? rest[5] : "";

        var profiles = ProfileManager.LoadProfiles();
        if (profiles.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"duplicate_name: profile '{name}' already exists.");

        profiles.Add(new ServerProfile
        {
            Name = name, Address = address, Port = port,
            ClientToken = token, Exchange = exchange, Folder = folder,
        });
        ProfileManager.SaveProfiles(profiles);

        return CommandResult.Ok(
            $"Added profile '{name}' ({exchange} @ {address}:{port})" +
            (string.IsNullOrEmpty(folder) ? "" : $" in folder '{folder}'") + ".",
            new { Action = "add", Name = name, Folder = folder, ProfileCount = profiles.Count });
    }

    // ── edit ────────────────────────────────────────────────────────────────

    private CommandResult HandleEdit(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("profiles edit requires --confirm.");
        if (rest.Count < 1)
            return CommandResult.Fail("Usage: profiles edit <name> [--address=A] [--port=P] [--token=T] [--exchange=E] [--folder=F] [--rename=N] --confirm");

        string name = rest[0];
        var profiles = ProfileManager.LoadProfiles();
        var target = profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target == null) return CommandResult.Fail($"not_found: profile '{name}' not in profiles.json.");

        var changes = new List<string>();
        for (int i = 1; i < rest.Count; i++)
        {
            string a = rest[i];
            if (!a.StartsWith("--")) continue;
            int eq = a.IndexOf('=');
            if (eq < 0) continue;
            string key = a[2..eq];
            string val = a[(eq + 1)..];
            switch (key.ToLowerInvariant())
            {
                case "address": target.Address = val; changes.Add($"address={val}"); break;
                case "port":
                    if (int.TryParse(val, out int p)) { target.Port = p; changes.Add($"port={p}"); }
                    break;
                case "token": target.ClientToken = val; changes.Add("token=<***>"); break;
                case "exchange":
                    if (Enum.TryParse<ExchangeType>(val, ignoreCase: true, out var ex)) { target.Exchange = ex; changes.Add($"exchange={ex}"); }
                    break;
                case "folder": target.Folder = val; changes.Add($"folder={val}"); break;
                case "rename":
                    if (profiles.Any(x => !ReferenceEquals(x, target) && x.Name.Equals(val, StringComparison.OrdinalIgnoreCase)))
                        return CommandResult.Fail($"duplicate_name: rename target '{val}' already exists.");
                    target.Name = val; changes.Add($"rename={val}");
                    break;
            }
        }
        if (changes.Count == 0)
            return CommandResult.Ok($"profiles edit '{name}': no_change (no fields supplied).",
                new { Action = "edit", Name = name, NoChange = true });

        ProfileManager.SaveProfiles(profiles);
        return CommandResult.Ok(
            $"Edited profile '{name}': {string.Join(", ", changes)}.",
            new { Action = "edit", Name = target.Name, Changes = changes });
    }

    // ── delete ──────────────────────────────────────────────────────────────

    private CommandResult HandleDelete(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("profiles delete requires --confirm.");
        if (rest.Count < 1) return CommandResult.Fail("Usage: profiles delete <name> --confirm");
        string name = rest[0];
        var profiles = ProfileManager.LoadProfiles();
        int before = profiles.Count;
        int removed = profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return CommandResult.Fail($"not_found: profile '{name}' not in profiles.json.");
        ProfileManager.SaveProfiles(profiles);
        return CommandResult.Ok(
            $"Deleted profile '{name}' ({removed} row(s)); {before - removed} remaining.",
            new { Action = "delete", Name = name, Removed = removed, Remaining = before - removed });
    }

    // ── move (to folder) ────────────────────────────────────────────────────

    private CommandResult HandleMove(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("profiles move requires --confirm.");
        if (rest.Count < 2) return CommandResult.Fail("Usage: profiles move <name> <folder> --confirm");
        string name = rest[0];
        string folder = rest[1];
        // Validate folder exists in the known set.
        var knownFolders = FolderStore.LoadFolders();
        if (folder != "" && !knownFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return CommandResult.Fail($"folder_not_found: '{folder}' is not a known folder. Use 'folders add {folder} --confirm' first.");

        var profiles = ProfileManager.LoadProfiles();
        var target = profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target == null) return CommandResult.Fail($"not_found: profile '{name}' not in profiles.json.");
        string oldFolder = target.Folder ?? "";
        if (oldFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok($"profiles move '{name}' → '{folder}': no_change (already there).",
                new { Action = "move", Name = name, From = oldFolder, To = folder, NoChange = true });
        target.Folder = folder;
        ProfileManager.SaveProfiles(profiles);
        return CommandResult.Ok(
            $"Moved profile '{name}' from '{oldFolder}' to '{folder}'.",
            new { Action = "move", Name = name, From = oldFolder, To = folder });
    }

    // ── import-csv ──────────────────────────────────────────────────────────

    private CommandResult HandleImportCsv(List<string> rest, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("profiles import-csv requires --confirm.");
        if (rest.Count < 1) return CommandResult.Fail("Usage: profiles import-csv <path> --confirm");
        string path = rest[0];
        if (!File.Exists(path))
            return CommandResult.Fail($"file_not_found: {path}");

        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return CommandResult.Fail("csv_empty: file has no lines.");

        string[] headers = SplitCsvLine(lines[0]);
        var headerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++) headerIdx[headers[i].Trim()] = i;
        // Required columns: name, address, port, token, exchange.  Optional: folder.
        var required = new[] { "name", "address", "port", "token", "exchange" };
        var missing = required.Where(r => !headerIdx.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            return CommandResult.Fail($"csv_missing_columns: required headers missing: {string.Join(",", missing)}.");

        var profiles = ProfileManager.LoadProfiles();
        var existingNames = new HashSet<string>(profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var importedRows = new List<object>();
        int added = 0, skipped = 0, failed = 0;
        for (int li = 1; li < lines.Length; li++)
        {
            string line = lines[li];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] cells = SplitCsvLine(line);
            string Get(string col) => headerIdx.TryGetValue(col, out int idx) && idx < cells.Length ? cells[idx].Trim() : "";

            string name = Get("name");
            string address = Get("address");
            string portStr = Get("port");
            string token = Get("token");
            string exchangeStr = Get("exchange");
            string folder = Get("folder");

            if (string.IsNullOrEmpty(name))
            { importedRows.Add(new { LineNumber = li + 1, Skipped = true, Reason = "empty_name" }); skipped++; continue; }
            if (existingNames.Contains(name))
            { importedRows.Add(new { LineNumber = li + 1, Name = name, Skipped = true, Reason = "duplicate_name" }); skipped++; continue; }
            if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
            { importedRows.Add(new { LineNumber = li + 1, Name = name, Failed = true, Reason = $"invalid_port: '{portStr}'" }); failed++; continue; }
            if (!Enum.TryParse<ExchangeType>(exchangeStr, ignoreCase: true, out var exchange) ||
                !Enum.IsDefined(typeof(ExchangeType), exchange) || exchange == ExchangeType.UNKNOWN)
            { importedRows.Add(new { LineNumber = li + 1, Name = name, Failed = true, Reason = $"invalid_exchange: '{exchangeStr}'" }); failed++; continue; }

            profiles.Add(new ServerProfile
            {
                Name = name, Address = address, Port = port,
                ClientToken = token, Exchange = exchange, Folder = folder,
            });
            existingNames.Add(name);
            importedRows.Add(new { LineNumber = li + 1, Name = name, Added = true });
            added++;
        }

        if (added > 0) ProfileManager.SaveProfiles(profiles);
        return CommandResult.Ok(
            $"import-csv {path}: {added} added, {skipped} skipped (duplicates/empty), {failed} failed (parse errors).",
            new { Action = "import-csv", Added = added, Skipped = skipped, Failed = failed, Rows = importedRows });
    }

    // CSV line splitter — minimal: comma, no quote handling beyond strip.
    private static string[] SplitCsvLine(string line)
    {
        var parts = line.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string s = parts[i].Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\"")) s = s[1..^1];
            parts[i] = s;
        }
        return parts;
    }
}
