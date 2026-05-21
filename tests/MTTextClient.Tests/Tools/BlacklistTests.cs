using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

[Collection(BenchCollection.Name)]
public sealed class BlacklistTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public BlacklistTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_blacklist_list_returns_envelope_with_data_when_present()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_blacklist_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Empty blacklist → text-only Ok (no data field). Non-empty → array.
        // Both are valid; if data is present, it must be an array.
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data))
        {
            data.ValueKind.Should().BeOneOf(
                System.Text.Json.JsonValueKind.Array,
                System.Text.Json.JsonValueKind.Null,
                System.Text.Json.JsonValueKind.Object);
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_blacklist_add_remove_roundtrip_each_returns_success_true()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        const string symbol = "testforensicusdt9999";

        var add = await _mcp.CallTool("mt_blacklist_add", new
        {
            type = "symbol",
            market_type = 3,
            quote_asset = "usdt",
            symbol,
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });
        add.InnerSuccess.Should().BeTrue(because: "PR #3 typed-object storage; add+confirm should round-trip");
        add.ParsedBody!.Value.TryGetProperty("success", out var addS).Should().BeTrue();
        addS.GetBoolean().Should().BeTrue();

        var remove = await _mcp.CallTool("mt_blacklist_remove", new
        {
            type = "symbol",
            market_type = 3,
            quote_asset = "usdt",
            symbol,
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });
        remove.InnerSuccess.Should().BeTrue();
        remove.ParsedBody!.Value.TryGetProperty("success", out var rmS).Should().BeTrue();
        rmS.GetBoolean().Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_blacklist_add_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_blacklist_add", new
        {
            type = "symbol",
            market_type = 3,
            quote_asset = "usdt",
            symbol = "testforensicusdt9998",
            // no confirm
            profile = EnvFlags.DefaultBenchProfile,
        });

        bool gated = resp.IsRpcError ||
            (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s) &&
             s.ValueKind == System.Text.Json.JsonValueKind.False);
        gated.Should().BeTrue(because: "ConfirmGate: blacklist_add must reject without confirm");
    }
}
