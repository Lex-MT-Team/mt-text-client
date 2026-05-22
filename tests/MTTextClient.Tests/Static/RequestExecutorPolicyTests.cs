using System.IO;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// RequestExecutor policy regression harness.
///
/// Asserts that every fallback-cluster site has adopted
/// <see cref="MTTextClient.Core.RequestExecutor"/>'s ExecuteWithFallback
/// overload and that the lineage marker is preserved.  See
/// <c>docs/request-executor-policy.md</c> for the full policy.
/// </summary>
[Trait("Category", TraitCategories.Static)]
public sealed class RequestExecutorPolicyTests
{
    [Fact]
    public void RequestExecutor_Has_Command_Layer_Overload()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Core", "RequestExecutor.cs"));
        src.Should().Contain("ExecuteWithFallback<T>(Func<T?> getter",
            because: "the command-layer overload is the seam this policy depends on");
    }

    [Theory]
    [InlineData("Commands/ReportsCommand.cs", "RequestReportComments")]
    [InlineData("Commands/ReportsCommand.cs", "RequestReportDates")]
    [InlineData("Commands/ExchangeCommand.cs", "RequestTicker24")]
    public void FallbackSite_Uses_RequestExecutor_ExecuteWithFallback(string relPath, string getterName)
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), relPath));
        src.Should().Contain("_executor.ExecuteWithFallback",
            because: $"{relPath} must call _executor.ExecuteWithFallback at the fallback sites (policy)");
        src.Should().Contain(getterName,
            because: $"{relPath} must continue to reference {getterName} via the _executor wrapper");
    }

    [Theory]
    [InlineData("Commands/ReportsCommand.cs")]
    [InlineData("Commands/ExchangeCommand.cs")]
    public void FallbackSite_Declares_Static_RequestExecutor_Field(string relPath)
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), relPath));
        src.Should().Contain("private static readonly RequestExecutor _executor = new()",
            because: $"{relPath} must declare a static RequestExecutor instance per the policy doc");
    }

    [Fact]
    public void Policy_Doc_Exists_And_Names_Every_Migrated_Site()
    {
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "request-executor-policy.md"));
        doc.Should().Contain("ReportsCommand.GetReportComments");
        doc.Should().Contain("ReportsCommand.GetReportDates");
        doc.Should().Contain("ExchangeCommand.Ticker24");
        doc.Should().Contain("RequestExecutorPolicyTests",
            because: "the doc must point at this regression harness so the loop is documented in-tree");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MTTextClient.csproj")))
            dir = dir.Parent;
        if (dir == null)
            throw new IOException("Could not locate repo root (no MTTextClient.csproj ancestor).");
        return dir.FullName;
    }
}
