using FluentAssertions;
using MTTextClient.Commands;
using MTTextClient.Output;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// OutputCommand moved out of Program.cs into Commands/OutputCommand.cs so the
/// process entry point no longer embeds an unrelated class below Main (editing
/// Program.cs could otherwise break McpServer's compile). This pins that the
/// command is intact in the Commands namespace; the build proves both Program
/// and McpServer still reference it.
/// </summary>
public sealed class OutputCommandUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void OutputCommand_is_an_output_named_icommand()
    {
        var cmd = new OutputCommand(new OutputManager());
        cmd.Should().BeAssignableTo<ICommand>();
        cmd.Name.Should().Be("output");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutputCommand_sets_json_and_table_modes()
    {
        var output = new OutputManager();
        var cmd = new OutputCommand(output);

        cmd.Execute(new[] { "json" }).Success.Should().BeTrue();
        output.Mode.Should().Be(OutputMode.Json);

        cmd.Execute(new[] { "table" }).Success.Should().BeTrue();
        output.Mode.Should().Be(OutputMode.Table);

        cmd.Execute(new[] { "bogus" }).Success.Should().BeFalse("unknown mode is rejected");
    }
}
