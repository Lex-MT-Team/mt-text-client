using MTTextClient.Output;

namespace MTTextClient.Commands;

/// <summary>
/// Switch output mode between table and JSON.
/// </summary>
public sealed class OutputCommand : ICommand
{
    private readonly OutputManager _output;

    public OutputCommand(OutputManager output)
    {
        _output = output;
    }

    public string Name => "output";
    public string Description => "Switch output format (table/json)";
    public string Usage => "output table | output json";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Ok($"Current output mode: {_output.Mode}");
        }

        return args[0].ToLowerInvariant() switch
        {
            "table" => SetMode(OutputMode.Table),
            "json" => SetMode(OutputMode.Json),
            _ => CommandResult.Fail($"Unknown mode: {args[0]}. Use 'table' or 'json'.")
        };
    }

    private CommandResult SetMode(OutputMode mode)
    {
        _output.Mode = mode;
        return CommandResult.Ok($"Output mode set to: {mode}");
    }
}
