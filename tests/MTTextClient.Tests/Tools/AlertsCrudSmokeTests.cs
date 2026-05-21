using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke probes for mt_alerts_save / mt_alerts_delete /
/// mt_alerts_set_running.  The destructive tools (delete, set_running)
/// must be rejected without confirm; save is non-destructive (create
/// or update) and runs without confirm.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AlertsCrudSmokeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlertsCrudSmokeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_alerts_delete_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_alerts_delete", new
        {
            apply_to_all = true, profile = Profile,
            // confirm intentionally omitted
        });
        resp.IsRpcError.Should().BeTrue(
            because: "destructive alerts tool without confirm must be rejected");
        resp.Envelope.GetProperty("message").GetString()!.Should().Contain("confirm");
    }

    [SkippableFact]
    public async Task mt_alerts_set_running_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_alerts_set_running", new
        {
            running = false, apply_to_all = true, profile = Profile,
        });
        resp.IsRpcError.Should().BeTrue();
        resp.Envelope.GetProperty("message").GetString()!.Should().Contain("confirm");
    }

    [SkippableFact]
    public async Task mt_alerts_save_validates_market_type_and_condition_type()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Invalid market_type — handler rejects before wire.
        var bad = await _mcp.CallTool("mt_alerts_save", new
        {
            name = "alerts-crud-smoke",
            symbol = "btcusdt",
            market_type = "WAFFLE_MARKET",
            condition_type = "CROSSING",
            ref_price = 1.0,
            profile = Profile,
        });
        bad.IsRpcError.Should().BeFalse();
        var body = bad.ParsedBody!.Value;
        body.GetProperty("error").GetString().Should().Contain("invalid market_type");
    }

    [SkippableFact]
    public async Task mt_alerts_save_with_valid_typed_args_dispatches_to_wire()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Use an out-of-range ref price so the alert is functionally inert
        // (BTC would have to cross $1.00 on the way down to trigger).
        var resp = await _mcp.CallTool("mt_alerts_save", new
        {
            name = $"alerts-crud-smoke-{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            symbol = "btcusdt",
            market_type = "FUTURES",
            condition_type = "CROSSING",
            ref_price = 1.0,
            direction = "DOWN",
            repeat_type = "ONLY_ONCE",
            profile = Profile,
        });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        // 'ok' field is present + 'alert' echoes our typed args.
        data.GetProperty("ok").GetBoolean().Should().BeTrue();
        data.GetProperty("alert").GetProperty("symbol").GetString().Should().Be("btcusdt");
        data.GetProperty("alert").GetProperty("market_type").GetString().Should().Be("FUTURES");
        data.GetProperty("alert").GetProperty("condition_type").GetString().Should().Be("CROSSING");
        data.GetProperty("alert").GetProperty("direction").GetString().Should().Be("DOWN");
    }
}
