using Xunit;

namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// xUnit collection that owns one <see cref="McpFixture"/> for the lifetime of
/// the test session. Static and Smoke tests both use this collection so we
/// only spawn one MCP subprocess for the whole run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class McpCollection : ICollectionFixture<McpFixture>
{
    public const string Name = "Mcp";
}

/// <summary>
/// xUnit collection that owns one <see cref="BenchFixture"/> per test session.
/// Tests that need the bench reachable use <c>[Collection(BenchCollection.Name)]</c>
/// AND check <see cref="EnvFlags.TestingEnv"/> before asserting (otherwise CI
/// without a bench would fail).
///
/// We compose by including both fixtures in the same collection so Smoke tests
/// don't have to declare two collections (xUnit doesn't support multi-collection
/// classes).
/// </summary>
[CollectionDefinition(Name)]
public sealed class BenchCollection : ICollectionFixture<McpFixture>, ICollectionFixture<BenchFixture>
{
    public const string Name = "Bench";
}
