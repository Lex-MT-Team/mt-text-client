using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Reports family. <c>mt_reports_dates</c> and <c>mt_reports_comments</c> are
/// in the MCP-003 timeout cluster (cold/empty DB + 10s client cap). The other
/// reads (<c>mt_reports_trades</c>, <c>mt_reports_stored</c>) work fine.
///
/// Rich-filter query (<c>mt_reports_query</c>) lands in Stage 7.1.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class ReportsTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public ReportsTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_reports_trades_succeeds()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_reports_trades",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_reports_stored_succeeds()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_reports_stored",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_reports_dates_returns_empty_or_populated_array()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // MCP-003 fix: cold/empty DB now returns success:true with empty data
        // instead of timing out. The 30s default leaves plenty of headroom.
        var resp = await _mcp.CallTool("mt_reports_dates",
            new { profile = EnvFlags.DefaultBenchProfile },
            timeout: TimeSpan.FromSeconds(35));

        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_reports_comments_returns_empty_or_populated_array()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_reports_comments",
            new { profile = EnvFlags.DefaultBenchProfile },
            timeout: TimeSpan.FromSeconds(35));

        resp.InnerSuccess.Should().BeTrue();
    }
}
