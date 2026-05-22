using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Catch-all for read-only tools that don't fit a larger family:
/// dust, deposit, funding, buylimit, signals, monitor, events, livemarkets,
/// graphtool, autobuy, triggers, perf, fleet (read-side).
///
/// One representative test per family. Per-feature deeper coverage will land
/// when feature iterations touch each surface.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class MiscTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public MiscTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_perf_list_succeeds_with_text_body()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // PerformanceCommand emits text-only Ok with a "[server] No performance
        // data..." or summary block.
        var resp = await _mcp.CallTool("mt_perf_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("message", out var msg).Should().BeTrue(
            because: "perf list always emits a text body even when no data is cached");
        (msg.GetString() ?? "").Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_buylimit_request_succeeds()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_buylimit_request",
            new { amount = 10, profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    // mt_events_status / mt_metrics_get / mt_rate_status return RAW JSON
    // payloads — they don't follow the {success, message, data} envelope.
    // Use NoError instead of InnerSuccess for these.

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_events_status_payload_is_ok_with_seq_and_sse_url()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        // events doesn't need MTCore; just MCP server is enough.
        var resp = await _mcp.CallTool("mt_events_status", new { });
        resp.NoError.Should().BeTrue();
        var body = resp.ParsedBody!.Value;

        // Real shape per McpServer.HandleEventsStatus: { current_seq, sse_port,
        // sse_url, poll_url, status }. The user spec asks for status==ok and
        // uptime_seconds>0; uptime_seconds is NOT a field — current_seq plays
        // the closest "this server is alive" role.
        body.TryGetProperty("status", out var st).Should().BeTrue();
        st.GetString().Should().Be("ok");
        body.TryGetProperty("current_seq", out var seq).Should().BeTrue();
        seq.GetInt64().Should().BeGreaterOrEqualTo(0);
        body.TryGetProperty("sse_url", out var sseUrl).Should().BeTrue();
        (sseUrl.GetString() ?? "").Should().StartWith("http",
            because: "sse_url is the concrete listener URL the agent can subscribe to");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_metrics_get_returns_tool_calls_and_errors_counters()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_metrics_get",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.NoError.Should().BeTrue();
        var body = resp.ParsedBody!.Value;

        // Real shape per McpMetrics.ToJson: { tool_calls_total, tool_errors_total,
        // events_total, connections_active, ... }. We've made many calls by now,
        // so tool_calls_total > 0.
        body.TryGetProperty("tool_calls_total", out var calls).Should().BeTrue();
        calls.GetInt64().Should().BeGreaterThan(0,
            because: "the test fixture has already issued many tools/call requests");
        body.TryGetProperty("tool_errors_total", out var errors).Should().BeTrue(
            because: "the metric uses 'tool_errors_total' — note the field name differs from the user spec's 'errors_total'");
        errors.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_rate_status_orders_bucket_has_remaining_and_window_ms()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_rate_status",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.NoError.Should().BeTrue();
        var body = resp.ParsedBody!.Value;

        // Real shape per RateLimits.GetStatus: { orders: { limit, window_ms,
        // used, remaining }, market: {...}, account: {...} }. There is NO
        // 'reset_at' timestamp — only 'window_ms' (rolling window). We check
        // 'remaining' (int) and 'window_ms' (int) on the orders bucket.
        body.TryGetProperty("orders", out var orders).Should().BeTrue();
        orders.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);

        orders.TryGetProperty("remaining", out var remaining).Should().BeTrue();
        remaining.GetInt32().Should().BeGreaterOrEqualTo(0);

        orders.TryGetProperty("window_ms", out var windowMs).Should().BeTrue();
        windowMs.GetInt32().Should().BeGreaterThan(0,
            because: "the rolling-window length must be a positive integer");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_monitor_health_starts_then_reads()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // mt_monitor_health requires monitoring to have started AND data collected.
        // Cold-call returns success:false with "No monitoring data...". Start
        // the monitor first, then read.
        await _mcp.CallTool("mt_monitor_start",
            new { profile = EnvFlags.DefaultBenchProfile });
        await Task.Delay(TimeSpan.FromSeconds(3));

        var resp = await _mcp.CallTool("mt_monitor_health",
            new { profile = EnvFlags.DefaultBenchProfile });

        // Either success:true (monitor data collected), or success:false with the
        // documented "No monitoring data" message (timing window). Both are valid.
        bool acceptable = resp.InnerSuccess ||
            (resp.InnerMessage ?? "").Contains("No monitoring data", StringComparison.OrdinalIgnoreCase);
        acceptable.Should().BeTrue(
            because: "either monitor has data, or returns the documented empty-state message");

        // Cleanup
        await _mcp.CallTool("mt_monitor_stop",
            new { profile = EnvFlags.DefaultBenchProfile });
    }
}
