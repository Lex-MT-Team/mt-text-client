using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// TP/SL family. <c>list</c>, <c>subscribe</c>, <c>unsubscribe</c> are
/// non-destructive Smoke. <c>cancel</c> / <c>split</c> / <c>join</c> mutate
/// live TPSL state and are LiveTrade.
///
/// Bulk variants (<c>cancel_list</c>, <c>split_list</c>, <c>panic</c>) and
/// per-attribute <c>update_settings</c> land in Stage 1 of the unified plan.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class TpslTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public TpslTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_tpsl_list_succeeds()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_list",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_tpsl_subscribe_unsubscribe_roundtrip()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        (await _mcp.CallTool("mt_tpsl_subscribe", new { profile = EnvFlags.DefaultBenchProfile }))
            .InnerSuccess.Should().BeTrue();
        (await _mcp.CallTool("mt_tpsl_unsubscribe", new { profile = EnvFlags.DefaultBenchProfile }))
            .InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_tpsl_cancel_with_unknown_id_returns_clean_error()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_tpsl_cancel",
            new { id = "nonexistent_tpsl_99999", confirm = true, profile = EnvFlags.DefaultBenchProfile });

        // Should reject cleanly: success:false with an Invalid TPSL ID message,
        // NOT crash and NOT silently succeed.
        resp.IsRpcError.Should().BeFalse();
        if (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s))
            s.GetBoolean().Should().BeFalse();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_tpsl_join_with_array_of_bogus_ids_returns_clean_error()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // fix/known-defects-batch-1: tpsl_ids is now an array of string in the
        // schema. Two numeric-but-nonexistent IDs should round-trip through the
        // dispatcher (space-joined), pass the parser ("Need at least 2 IDs"),
        // and reach the server which then reports "ID(s) not found".
        var resp = await _mcp.CallTool("mt_tpsl_join", new
        {
            tpsl_ids = new[] { "999999991", "999999992" },
            confirm = true,
            profile = EnvFlags.DefaultBenchProfile,
        });

        resp.IsRpcError.Should().BeFalse(
            because: "tpsl_ids is a valid array of two numeric strings; schema accepts the call");

        if (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s))
        {
            s.GetBoolean().Should().BeFalse(
                because: "the IDs don't exist on the bench, so the server should reject with success:false");

            if (b.TryGetProperty("message", out var m))
            {
                string msg = m.GetString() ?? "";
                msg.Should().NotContain("Need at least 2 TPSL IDs",
                    because: "the array form must produce a parser-acceptable command — " +
                             "the server should reject for missing IDs, not for arg-shape");
            }
        }
    }
}
