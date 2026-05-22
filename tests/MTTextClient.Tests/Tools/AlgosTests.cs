using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Algorithm management — the largest tool family (30 tools). Smoke tests
/// here cover the read paths and a few non-destructive lifecycle paths.
/// Trade-affecting algo lifecycle (start/stop/save_start) is in <see cref="OrdersTests"/>
/// (LiveTrade category) since it actually places orders.
///
/// <c>mt_algos_list</c> is the canary for dispatcher routing because it is
/// the most-used read.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AlgosTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_algos_list_returns_populated_array_with_id_name_and_running_status()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_algos_list", new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per AlgosCommand.ListAlgos: List<{id (long), name (string),
        // CoreName, signature, Running ("YES"/"no"), isRunning (bool), Processing,
        // Market, symbol, GroupType, Group}>.
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterThan(0,
            because: "the first bench profile is expected to ship with seeded algorithms");

        var first = data[0];
        first.TryGetProperty("id", out var id).Should().BeTrue();
        id.ValueKind.Should().BeOneOf(System.Text.Json.JsonValueKind.Number, System.Text.Json.JsonValueKind.String);

        first.TryGetProperty("name", out var name).Should().BeTrue();
        name.GetString().Should().NotBeNullOrWhiteSpace();

        first.TryGetProperty("isRunning", out var running).Should().BeTrue(
            because: "isRunning is the boolean status used to drive the displayed Running column");
        running.ValueKind.Should().BeOneOf(System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False);
    }

    [SkippableFact]
    public async Task mt_algos_groups_returns_array_each_with_id_and_name()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_algos_groups", new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per AlgosCommand.ListGroups: List<{id, name, GroupType,
        // AlgoCount, Running, Stopped}>. Empty array is acceptable on a fresh
        // profile, but if non-empty each item must have id+name.
        if (data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];
            first.TryGetProperty("id", out _).Should().BeTrue();
            first.TryGetProperty("name", out var name).Should().BeTrue();
            name.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [SkippableFact]
    public async Task mt_algos_search_returns_filtered_array_with_id_and_name()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_algos_search",
            new { query = "shot", profile = EnvFlags.DefaultBenchProfile });
        resp.IsRpcError.Should().BeFalse();
        resp.IsToolError.Should().BeFalse();

        // success may be true with no data field (when no matches), or with an
        // array of algo summaries. If data is present, each item has id+name.
        if (resp.ParsedBody is { } b &&
            b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Array &&
            data.GetArrayLength() > 0)
        {
            var first = data[0];
            first.TryGetProperty("id", out _).Should().BeTrue();
            first.TryGetProperty("name", out var name).Should().BeTrue();
            name.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [SkippableFact]
    public async Task mt_algos_list_all_works_without_profile_arg()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_algos_list_all", new { });
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_algos_snapshot_returns_snapshot_object()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // mt_algos_snapshot returns a raw {snapshot[], captured_at, server_count}
        // payload (not the success/message envelope).
        var resp = await _mcp.CallTool("mt_algos_snapshot",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.NoError.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("snapshot", out _).Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("captured_at", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_algos_get_with_unknown_id_returns_not_found()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_algos_get",
            new { id = "not_a_real_id_99999", profile = EnvFlags.DefaultBenchProfile });
        // Should fail cleanly with success:false; not crash and not RPC error.
        resp.IsRpcError.Should().BeFalse();
        if (resp.ParsedBody is { } body && body.TryGetProperty("success", out var s))
            s.GetBoolean().Should().BeFalse();
    }
}
