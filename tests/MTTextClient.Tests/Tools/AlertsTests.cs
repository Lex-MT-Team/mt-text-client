using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Alert read + subscribe pair tools. Alert CRUD (create / edit / delete)
/// is missing from mt-text-client today; lands in Stage 6.3 of the unified
/// plan (gap block M).
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class AlertsTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlertsTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_alerts_list_returns_count_or_text()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_alerts_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Real shape per AlertsCommand: data is { count: int } when subscribed
        // with cached entries, else text-only Ok ("No alerts on … Subscribe first").
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            data.TryGetProperty("count", out var cnt).Should().BeTrue();
            cnt.GetInt32().Should().BeGreaterOrEqualTo(0);
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_alerts_history_returns_count_or_text()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_alerts_history",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            data.TryGetProperty("count", out _).Should().BeTrue();
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_alerts_subscribe_unsubscribe_roundtrip_both_succeed()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var sub = await _mcp.CallTool("mt_alerts_subscribe",
            new { profile = EnvFlags.DefaultBenchProfile });
        sub.InnerSuccess.Should().BeTrue();
        sub.ParsedBody!.Value.TryGetProperty("success", out var subS).Should().BeTrue();
        subS.GetBoolean().Should().BeTrue();

        var unsub = await _mcp.CallTool("mt_alerts_unsubscribe",
            new { profile = EnvFlags.DefaultBenchProfile });
        unsub.InnerSuccess.Should().BeTrue();
        unsub.ParsedBody!.Value.TryGetProperty("success", out var unsubS).Should().BeTrue();
        unsubS.GetBoolean().Should().BeTrue();
    }
}
