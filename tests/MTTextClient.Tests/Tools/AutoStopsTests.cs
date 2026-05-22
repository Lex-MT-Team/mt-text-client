using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// AutoStops read tools.  Full lifecycle (add / start / stop / edit / delete)
/// coverage is exercised by the autostops-crud Smoke and LiveTrade suites.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class AutoStopsTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AutoStopsTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_autostops_list_returns_text_describing_settings_block()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_autostops_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Real handler returns text-only Ok with a settings/algos summary in the
        // message body (no structured data field). Strengthening: verify the
        // message body documents the expected sections.
        resp.ParsedBody!.Value.TryGetProperty("message", out var msg).Should().BeTrue();
        string text = msg.GetString() ?? "";
        text.Should().Contain("AutoStop",
            because: "the body must announce the AutoStop section so a caller recognises the response");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_autostops_baseline_acknowledges_recalculation_request()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_autostops_baseline",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Fire-and-forget: handler returns text-only Ok with a recalc-ack message.
        resp.ParsedBody!.Value.TryGetProperty("message", out var msg).Should().BeTrue();
        (msg.GetString() ?? "").Should().Contain("baseline",
            because: "the response confirms the baseline recalculation was sent");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_autostops_reports_returns_empty_or_populated_data()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // Cold/empty Firebird returns success:true with an empty Reports
        // array instead of timing out. 35s leaves headroom over the 30s default.
        var resp = await _mcp.CallTool("mt_autostops_reports",
            new { profile = EnvFlags.DefaultBenchProfile },
            timeout: TimeSpan.FromSeconds(35));

        resp.InnerSuccess.Should().BeTrue();
    }
}
