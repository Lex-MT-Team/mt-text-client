namespace MTTextClient.Commands;

/// <summary>
/// Result of executing a command. Every command returns this.
/// Designed for MCP-readiness: structured data + human message.
/// </summary>
public sealed class CommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public object? Data { get; init; }

    public static CommandResult Ok(string message, object? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static CommandResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>
/// Interface for all commands. Each command has a name, description, and execute method.
/// </summary>
public interface ICommand
{
    /// <summary>Command name (e.g. "connect", "algos").</summary>
    string Name { get; }

    /// <summary>Short description for help text.</summary>
    string Description { get; }

    /// <summary>Usage syntax (e.g. "connect <profile>").</summary>
    string Usage { get; }

    /// <summary>Execute the command with parsed arguments.</summary>
    CommandResult Execute(string[] args);
}
