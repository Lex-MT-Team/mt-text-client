using System.IO;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Xunit;
using SnapshotGen = MTTextClient.Tools.DispatcherSnapshotGenerator.Program;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Stage 0.3 / OV-2 — the dispatcher snapshot is the spine of "no silent
/// CLI drift". The committed file at
/// <c>tests/MTTextClient.Tests/_expected/commandlines.snapshot.json</c> is
/// produced by <c>tools/DispatcherSnapshotGenerator</c>. This test re-runs
/// the same rendering in-process and asserts byte-equality. Any change to
/// any tool's dispatcher CLI string — schema rename, arg reorder, new
/// required field, swapped Build*Command — surfaces here as a one-character
/// diff before it can ship.
///
/// To regenerate after an intentional change:
///   <c>dotnet run --project tools/DispatcherSnapshotGenerator -c Release</c>
/// and commit the resulting <c>commandlines.snapshot.json</c>.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class DispatcherSnapshotTests
{
    [Fact]
    public void CommittedSnapshot_IsByteEqualToCurrentDispatcherOutput()
    {
        string path = ResolveSnapshotPath();
        File.Exists(path).Should().BeTrue(
            because: $"the snapshot baseline must be committed at {path}");

        string committed = File.ReadAllText(path);
        string rendered = SnapshotGen.Render(ToolRegistry.AllTools());

        rendered.Should().Be(committed,
            because: "the dispatcher CLI strings for every tool must match the " +
                     "committed snapshot. If this fails after an intentional " +
                     "schema or dispatcher change, regenerate via: " +
                     "`dotnet run --project tools/DispatcherSnapshotGenerator -c Release` " +
                     "and commit the updated commandlines.snapshot.json.");
    }

    [Fact]
    public void CommittedSnapshot_ContainsEveryRegistryTool()
    {
        string path = ResolveSnapshotPath();
        string committed = File.ReadAllText(path);
        int toolCount = System.Linq.Enumerable.Count(ToolRegistry.AllTools());

        // Cheap surface check: the file should have one line per tool plus the
        // surrounding object braces. With pretty-print there is exactly one
        // entry per line in the body.
        // We don't parse JSON here on purpose — the byte-equality test above
        // is the strict assertion; this one is a sanity floor.
        int lineCount = committed.Split('\n').Length;
        lineCount.Should().BeGreaterOrEqualTo(toolCount,
            because: $"snapshot file should have at least {toolCount} lines (one per tool, plus braces)");
    }

    private static string ResolveSnapshotPath()
    {
        // RepoPaths.Root walks up to find MTTextClient.csproj.
        return Path.Combine(RepoPaths.Root, SnapshotGen.SnapshotRepoPath);
    }
}
