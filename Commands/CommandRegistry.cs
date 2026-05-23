using System;
using System.Collections.Generic;
using System.Text;
namespace MTTextClient.Commands;

/// <summary>
/// Registry and dispatcher for all commands.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICommand> _orderedCommands = new();

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

        string[] parts = ParseCommandLine(input);
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

        try
        {
            return command.Execute(args);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Command error: {ex.Message}");
        }
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
}
