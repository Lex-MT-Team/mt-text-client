using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 2.1 — Smoke coverage for the new <c>mt_orders_update_tpsl</c>
/// tool.  Real fill + TP/SL attach is exercised in
/// <see cref="LiveTrade.Stage2UpdateTpslLiveTradeTests"/> (operator-invoked).
///
/// What these probes prove:
///   • ConfirmGate fires when confirm is omitted (Stage 0.4 behaviour).
///   • Schema validation rejects calls that omit the required take_profit / stop_loss
///     pair via the underlying CLI guard.
///   • The dispatcher routes to OrdersCommand's `update-tpsl` subcommand
///     (no "Unknown tool" / "Unknown subcommand" outputs).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage2UpdateOrderTpslTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage2UpdateOrderTpslTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_orders_update_tpsl_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_orders_update_tpsl", new
        {
            symbol = "BTCUSDT",
            side = "BUY",
            take_profit_percent = "1",
            stop_loss_percent = "1",
            profile = EnvFlags.DefaultBenchProfile,
            // confirm omitted intentionally
        });

        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate (Stage 0.4) emits -32602 when a confirm-required tool is called without confirm");
    }

    [SkippableFact]
    public async Task mt_orders_update_tpsl_without_any_tp_or_sl_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // Pass confirm=true but neither tp nor sl ⇒ the CLI handler should
        // reject with "at least one of --tp / --sl must be > 0".
        var resp = await _mcp.CallTool("mt_orders_update_tpsl", new
        {
            symbol = "BTCUSDT",
            side = "BUY",
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeFalse(
            because: "the CLI rejects update-tpsl when both --tp and --sl are <= 0 (nothing to update)");
        resp.InnerMessage.Should().NotBeNull();
        resp.InnerMessage!.Should().Contain("--tp",
            because: "the error message guides the caller to set at least one of --tp / --sl");
    }

    [SkippableFact]
    public async Task mt_orders_update_tpsl_with_no_matching_position_returns_clean_diagnostic()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // Confirm=true + valid TP/SL on a symbol with no open position → MTCore
        // either rejects at the wire ("no such position") or accepts with a
        // notification code.  Either is fine — we only own the dispatcher /
        // schema-gate behaviour here; the response must be a structured
        // success/fail, never an "Unknown tool" / "Unknown subcommand".
        var resp = await _mcp.CallTool("mt_orders_update_tpsl", new
        {
            symbol = "BTCUSDT",
            side = "BUY",
            market = "FUTURES",
            take_profit_percent = "1.0",
            stop_loss_percent = "1.0",
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeFalse(
            because: "the dispatcher must route the call to OrdersCommand.update-tpsl handler");
        resp.InnerMessage?.Should().NotContain("Unknown tool");
        resp.InnerMessage?.Should().NotContain("Unknown subcommand");
    }
}
