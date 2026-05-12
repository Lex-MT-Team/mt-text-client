using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AccountTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AccountTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    private async Task EnsureConnectedAsync()
    {
        var ok = await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);
        Skip.IfNot(ok, "bench did not become CONNECTED — see MCP-002 in MT_RUNBOOK.md §9");
    }

    [SkippableFact]
    public async Task mt_account_balance_returns_USDT_entry_with_positive_total()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_balance",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per AccountCommands.HandleBalance: List<{Asset, Total (string),
        // Available (string), Locked (string), EstUSDT (string), Market,
        // Transferable, Dust}>. Total/Available/Locked are FormatNumber strings
        // like "935.00000000" — decimal-parseable.
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterThan(0,
            because: "bench_01 has balances seeded (~$935 USDT)");

        bool foundUsdtPositive = false;
        foreach (var entry in data.EnumerateArray())
        {
            entry.TryGetProperty("Asset", out var asset).Should().BeTrue(because: "every balance row has Asset");
            entry.TryGetProperty("Total", out var total).Should().BeTrue();
            entry.TryGetProperty("Available", out _).Should().BeTrue();

            // Total is a formatted string — decimal.Parse with InvariantCulture handles it.
            string? assetStr = asset.GetString();
            string? totalStr = total.GetString();
            decimal.TryParse(totalStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out decimal totalNum)
                .Should().BeTrue(because: $"Total field '{totalStr}' must be decimal-parseable");

            if (string.Equals(assetStr, "USDT", StringComparison.OrdinalIgnoreCase) && totalNum > 0)
                foundUsdtPositive = true;
        }
        foundUsdtPositive.Should().BeTrue(
            because: "bench_01 (Tour_CORP_001) is seeded with ~$935 USDT on the futures wallet");
    }

    [SkippableFact]
    public async Task mt_account_orders_succeeds_and_returns_envelope()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_orders",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        // Empty bench → no Orders array (handler returns text-only Ok).
        // Non-empty → data: { Server, TotalOrders, ShowAll, Orders[] }.
        // Both shapes are valid; we only assert success here.
    }

    [SkippableFact]
    public async Task mt_account_positions_succeeds_and_returns_envelope()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_positions",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        // Empty bench → text-only Ok, no data. Non-empty → list of PositionSnapshot.
    }

    [SkippableFact]
    public async Task mt_account_executions_succeeds_and_returns_envelope()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_executions",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_account_info_returns_exchange_identity_block()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_info",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real fields per AccountCommands.HandleInfo: { Server, Exchange,
        // MarketType, CanTrade, PositionMode, MultiAssetMode, EventTime,
        // LastUpdate }.
        // EXCEPT: when AccountInfoSnapshot is null (early in the connection),
        // the handler returns success:true with text only and NO data field.
        // Tolerate both.
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            data.TryGetProperty("Server", out _).Should().BeTrue();
            data.TryGetProperty("Exchange", out var exchange).Should().BeTrue();
            exchange.GetString().Should().NotBeNullOrWhiteSpace();
            data.TryGetProperty("MarketType", out _).Should().BeTrue(
                because: "MarketType is part of the account-identity block");
        }
    }

    [SkippableFact]
    public async Task mt_account_summary_returns_numeric_balance_and_algo_counts()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await EnsureConnectedAsync();

        var resp = await _mcp.CallTool("mt_account_summary",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);

        // Real fields per AccountCommands.HandleSummary: { Server, Exchange,
        // Status, Uptime, CanTrade, TotalBalanceUSDT (string "$N.NN"),
        // ActiveBalances (int), OpenPositions (int), UnrealizedPnl (string),
        // ActiveOrders (int), EmulatedOrders (int), AlgoOrders (int),
        // Algorithms (int), RunningAlgos (int), TradePairs (int), ... }.
        data.TryGetProperty("Status", out var status).Should().BeTrue();
        status.GetString().Should().Be("CONNECTED");

        data.TryGetProperty("Algorithms", out var algos).Should().BeTrue(
            because: "Algorithms is the per-server algorithm count — a numeric field");
        algos.GetInt32().Should().BeGreaterOrEqualTo(0);

        data.TryGetProperty("TotalBalanceUSDT", out _).Should().BeTrue(
            because: "TotalBalanceUSDT is the headline balance figure (formatted string)");
    }
}
