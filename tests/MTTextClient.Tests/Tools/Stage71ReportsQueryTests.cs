using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 7.1 — Smoke probes for mt_reports_query / _csv_inline / _cancel / _status.
/// Verifies the structured envelope shape and the request-id observability
/// surface, independent of how many rows happen to exist on the bench at
/// probe time.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage71ReportsQueryTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage71ReportsQueryTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_reports_query_returns_structured_envelope_with_request_id()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_reports_query", new
        {
            period = "7d", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(45));
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.GetProperty("request_id").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("row_count").GetInt32().Should().BeGreaterOrEqualTo(0);
        data.GetProperty("range").GetString().Should().Be("7d");
        data.TryGetProperty("rows", out _).Should().BeTrue();
        data.TryGetProperty("summary", out var summary).Should().BeTrue();
        summary.TryGetProperty("total", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_reports_csv_inline_returns_csv_string_with_header()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_reports_csv_inline", new
        {
            period = "7d", market = "FUTURES", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(45));
        resp.IsRpcError.Should().BeFalse();
        string csv = resp.ParsedBody!.Value.GetProperty("csv").GetString()!;
        csv.Should().StartWith("id,reportOpenTime,reportTime,marketType,symbol",
            because: "CSV must declare a documented header on row 1");
    }

    [SkippableFact]
    public async Task mt_reports_status_returns_completed_for_recent_query()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var q = await _mcp.CallTool("mt_reports_query", new
        {
            period = "24h", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(45));
        string requestId = q.ParsedBody!.Value.GetProperty("request_id").GetString()!;

        var st = await _mcp.CallTool("mt_reports_status", new { request_id = requestId });
        st.IsRpcError.Should().BeFalse();
        st.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("completed");
        st.ParsedBody!.Value.GetProperty("request_id").GetString().Should().Be(requestId);
        st.ParsedBody!.Value.GetProperty("latency_ms").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [SkippableFact]
    public async Task mt_reports_status_returns_not_found_for_unknown_request_id()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_reports_status", new
        {
            request_id = "deadbeef-not-a-real-request-id-stage71",
        });
        resp.IsRpcError.Should().BeFalse();
        resp.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("not_found");
    }

    [SkippableFact]
    public async Task mt_reports_cancel_records_intent_for_recent_query()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var q = await _mcp.CallTool("mt_reports_query", new
        {
            period = "24h", profile = Profile,
        }, timeout: System.TimeSpan.FromSeconds(45));
        string requestId = q.ParsedBody!.Value.GetProperty("request_id").GetString()!;

        var c = await _mcp.CallTool("mt_reports_cancel", new { request_id = requestId });
        c.IsRpcError.Should().BeFalse();
        // The query already completed before the cancel arrived (synchronous wire).
        c.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("already_completed");
        c.ParsedBody!.Value.GetProperty("wire_is_synchronous").GetBoolean().Should().BeTrue();
    }
}
