using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

[Collection(BenchCollection.Name)]
public sealed class NotificationsTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public NotificationsTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_notifications_list_returns_count_when_subscribed_else_text()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_notifications_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Real handler shape per NotificationsCommand: text-only Ok when not
        // subscribed; data: { count: int } when subscribed with cached entries.
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            data.TryGetProperty("count", out var cnt).Should().BeTrue();
            cnt.GetInt32().Should().BeGreaterOrEqualTo(0);
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_notifications_clear_succeeds()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_notifications_clear",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_notifications_subscribe_unsubscribe_roundtrip()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var sub = await _mcp.CallTool("mt_notifications_subscribe",
            new { profile = EnvFlags.DefaultBenchProfile });
        sub.InnerSuccess.Should().BeTrue();

        var unsub = await _mcp.CallTool("mt_notifications_unsubscribe",
            new { profile = EnvFlags.DefaultBenchProfile });
        unsub.InnerSuccess.Should().BeTrue();
    }
}
