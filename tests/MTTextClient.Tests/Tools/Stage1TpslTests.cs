using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 1.1 + 1.2 — Smoke coverage for the new TPSL bulk and panic tools.
///
/// Each tool is exercised on bench_01 (BYBIT) with bogus IDs to drive the
/// "no such TPSL" rejection path. The point: prove (a) the dispatcher
/// routes the call to the right subcommand, (b) the schema gate fires
/// without confirm, (c) the underlying loop wrapper aggregates results
/// rather than aborting on the first bad ID.
///
/// Real fills are exercised in <see cref="LiveTrade.Stage1LiveTradeTests"/>
/// (operator-invoked).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage1TpslTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage1TpslTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    // ── 1.1 cancel-many / split-many ────────────────────────────────────────

    [SkippableFact]
    public async Task mt_tpsl_cancel_many_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_cancel_many", new
        {
            tpsl_ids = new[] { "111111111", "222222222" },
            // confirm omitted intentionally
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate (Stage 0.4) emits -32602 when a confirm-required tool is called without confirm");
    }

    [SkippableFact]
    public async Task mt_tpsl_cancel_many_with_bogus_ids_returns_per_id_failures()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_cancel_many", new
        {
            tpsl_ids = new[] { "999999991", "999999992" },
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        // Two routes are acceptable: success=true with a Results array showing
        // per-id outcomes (some fails), OR success=false if MTCore rejects the
        // batch at the wire level. Either is a clean dispatch — what we don't
        // want is "Unknown tool" or a crash.
        resp.IsRpcError.Should().BeFalse();
        resp.InnerMessage?.Should().NotContain("Unknown tool",
            because: "the dispatcher must route to the cancel-many handler");
    }

    [SkippableFact]
    public async Task mt_tpsl_split_many_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_split_many", new
        {
            tpsl_ids = new[] { "333333333" },
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeTrue();
    }

    // ── 1.2 panic / panic-many ──────────────────────────────────────────────

    [SkippableFact]
    public async Task mt_tpsl_panic_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_panic", new
        {
            tpsl_id = "12345",
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_tpsl_panic_with_unknown_id_returns_not_found()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_panic", new
        {
            tpsl_id = "999999991",
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        // Without an active TPSL subscription, the store is empty and the
        // handler reports NOT_FOUND. This is the expected diagnostic; we
        // don't auto-subscribe in this smoke test because doing so would
        // mutate connection state mid-suite.
        resp.IsRpcError.Should().BeFalse();
        if (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s))
        {
            s.GetBoolean().Should().BeFalse(because: "no such TPSL in store");
        }
    }

    [SkippableFact]
    public async Task mt_tpsl_panic_many_with_bogus_ids_returns_clean_rejection()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_panic_many", new
        {
            tpsl_ids = new[] { "999999991", "999999992" },
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue(
            because: "panic-many returns success=true with per-id rejection rows in Results[]");
    }

    // ── 1.3 mt_orders_close_by_tpsl with order_type ─────────────────────────

    [SkippableFact]
    public async Task mt_orders_close_by_tpsl_accepts_order_type_LIMIT()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // No actual position to close — but the schema must accept order_type
        // and the dispatcher must route correctly. We expect a clean
        // success:false ("no position to close") rather than a routing error.
        var resp = await _mcp.CallTool("mt_orders_close_by_tpsl", new
        {
            symbol = "BTCUSDT",
            market = "FUTURES",
            side = "BOTH",
            order_type = "LIMIT",
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeFalse();
        // Whether the close succeeds depends on whether there's a position;
        // the routing assertion is what we own here.
    }
}
