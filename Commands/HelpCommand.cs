using System.Collections.Generic;
namespace MTTextClient.Commands;

/// <summary>
/// Display help for all commands or a specific command.
/// </summary>
public sealed class HelpCommand : ICommand
{
    private readonly CommandRegistry _registry;

    public HelpCommand(CommandRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "help";
    public string Description => "Show available commands";
    public string Usage => "help [command]";

    public CommandResult Execute(string[] args)
    {
        if (args.Length > 0)
        {
            ICommand? cmd = _registry.Find(args[0]);
            if (cmd == null)
            {
                return CommandResult.Fail($"Unknown command: '{args[0]}'");
            }

            return CommandResult.Ok(
                $"{cmd.Name} — {cmd.Description}\nUsage: {cmd.Usage}",
                new { cmd.Name, cmd.Description, cmd.Usage });
        }

        var commands = new List<object>();
        for (int i = 0; i < _registry.All.Count; i++)
        {
            ICommand? c = _registry.All[i];
            commands.Add(new
            {
                c.Name,
                c.Description,
                c.Usage
            });
        }
        // Add built-in exit command (handled by REPL, not registered as ICommand)
        commands.Add(new { Name = "exit", Description = "Exit the application", Usage = "exit" });

        var lines = new List<string>();
        for (int i = 0; i < _registry.All.Count; i++)
        {
            ICommand? c = _registry.All[i];
            lines.Add($"  {c.Name,-15} {c.Description}");
        }
        lines.Add($"  {"exit",-15} Exit the application");

        string? msg = "Available commands:\n" + string.Join("\n", lines) + "\n\nType 'help <command>' for details.";

        return CommandResult.Ok(msg, commands);
    }
}
