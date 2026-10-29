using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core.Diagnostics;
namespace MTTextClient.Commands;

/// <summary>
/// Registry and dispatcher for all commands.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICommand> _orderedCommands = new();

    /// <summary>Optional post-dispatch observer that receives sanitized facts only.</summary>
    public Action<DiagnosticDispatchFact>? DispatchObserver { get; set; }

    /// <summary>Optional selected-profile resolver for commands without an explicit override.</summary>
    public Func<string?>? ActiveProfileResolver { get; set; }

    public void Register(ICommand command)
    {
        _commands[command.Name] = command;
        _orderedCommands.Add(command);
    }

    public ICommand? Find(string name)
    {
        _commands.TryGetValue(name, out ICommand? cmd);
        return cmd;
    }

    public IReadOnlyList<ICommand> All => _orderedCommands;

    /// <summary>
    /// Parse a command line and dispatch to the appropriate command.
    /// </summary>
    public CommandResult Dispatch(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CommandResult.Fail("No command entered. Type 'help' for available commands.");
        }

        return DispatchArguments(ParseCommandLine(input));
    }

    /// <summary>Dispatches already tokenized process arguments without reinterpreting spaces or quotes.</summary>
    public CommandResult DispatchArguments(string[] parts)
    {
        if (parts.Length == 0)
        {
            return CommandResult.Fail("No command entered.");
        }

        string? commandName = parts[0];
        string[]? args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        ICommand? command = Find(commandName);
        if (command == null)
        {
            return CommandResult.Fail($"Unknown command: '{commandName}'. Type 'help' for available commands.");
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        CommandResult result;
        try
        {
            result = command.Execute(args);
        }
        catch (Exception ex)
        {
            result = CommandResult.Fail($"Command error: {ex.Message}");
        }
        NotifyObserver(commandName, args, startedAt, DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Simple command line parser that handles quoted strings and embedded
    /// JSON payloads. Outside a JSON block, a bare " is a quote-toggle that
    /// disables space-splitting (so 'algos start "my algo name"' yields one
    /// arg). Inside a {...} or [...] block (depth tracked across the input),
    /// quotes are preserved literally and spaces are non-splitting — that
    /// keeps JSON args like {"k":"v"} intact across the MCP→REPL boundary,
    /// where the McpServer interpolates JSON dataJson values verbatim into
    /// a REPL command line.
    /// </summary>
    private static string[] ParseCommandLine(string input)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        int braceDepth = 0;

        foreach (char c in input)
        {
            if (c == '"')
            {
                if (braceDepth > 0)
                {
                    // Inside a JSON block: preserve the quote character as-is
                    // so the resulting token is still parseable JSON.
                    current.Append(c);
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if ((c == '{' || c == '[') && !inQuotes)
            {
                braceDepth++;
                current.Append(c);
            }
            else if ((c == '}' || c == ']') && !inQuotes && braceDepth > 0)
            {
                braceDepth--;
                current.Append(c);
            }
            else if (c == ' ' && !inQuotes && braceDepth == 0)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.ToArray();
    }

    private void NotifyObserver(string commandName, string[] args, DateTimeOffset startedAt, DateTimeOffset endedAt, CommandResult result)
    {
        try
        {
            if (DispatchObserver is null || !TryBuildDispatchFact(commandName, args, startedAt, endedAt, result.Success, out DiagnosticDispatchFact? fact)) return;
            DispatchObserver(fact!);
        }
        catch
        {
            // A diagnostic observer is optional and never changes the original command result.
        }
    }

    private bool TryBuildDispatchFact(string commandName, string[] args, DateTimeOffset startedAt, DateTimeOffset endedAt, bool success, out DiagnosticDispatchFact? fact)
    {
        fact = null;
        string noun = commandName.ToLowerInvariant();
        if (!TryGetObservedCommand(noun, args, out string? subcommand, out string? explicitProfile)) return false;
        string? profile = explicitProfile ?? ActiveProfileResolver?.Invoke();
        if (!IsSafeProfile(profile)) return false;
        fact = new DiagnosticDispatchFact(noun, subcommand, profile, startedAt, endedAt, success);
        return true;
    }

    private static bool TryGetObservedCommand(string noun, string[] args, out string? subcommand, out string? explicitProfile)
    {
        subcommand = null;
        explicitProfile = null;
        switch (noun)
        {
            case "orders":
                return TryGetProfileOverride(args, out subcommand, out explicitProfile) && subcommand is "place" or "cancel" or "cancel-all" or "close" or "close-all" or "panic-sell" or "update-tpsl";
            case "algos":
                // Copy may operate on a source and destination; do not claim it as one server action.
                return TryGetProfileOverride(args, out subcommand, out explicitProfile) && subcommand is "start" or "stop" or "delete";
            case "disconnect":
                if (args.Length == 0) return true;
                if (args.Length != 1 || args[0].Equals("all", StringComparison.OrdinalIgnoreCase) || !IsSafeProfile(args[0])) return false;
                explicitProfile = args[0];
                return true;
            case "connect":
                if (args.Length != 1 || args[0].Equals("all", StringComparison.OrdinalIgnoreCase) || !IsSafeProfile(args[0])) return false;
                explicitProfile = args[0];
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetProfileOverride(string[] args, out string? subcommand, out string? explicitProfile)
    {
        subcommand = null;
        explicitProfile = null;
        var commandParts = new List<string>();
        foreach (string argument in args)
        {
            if (argument.StartsWith('@'))
            {
                if (!IsSafeProfile(argument[1..]) || explicitProfile is not null) return false;
                explicitProfile = argument[1..];
            }
            else if (!argument.Equals("--confirm", StringComparison.OrdinalIgnoreCase) && !argument.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                commandParts.Add(argument);
            }
        }
        if (commandParts.Count == 0) return false;
        subcommand = commandParts[0].ToLowerInvariant();
        return true;
    }

    private static bool IsSafeProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 128) return false;
        foreach (char character in profile)
        {
            if (!char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.') return false;
        }
        return true;
    }
}
