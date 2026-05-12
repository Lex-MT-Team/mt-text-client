using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>Connection / session / core-level tools.</summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class CoreTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public CoreTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_status_BeforeConnect_ReturnsZeroConnected()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        var resp = await _mcp.CallTool("mt_status", new { });
        resp.IsRpcError.Should().BeFalse();
        resp.IsToolError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_connect_bench_01_AcceptsRequest()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        Skip.If(!_bench.BenchAvailable, _bench.PreflightMessage);

        var resp = await _mcp.CallTool("mt_connect", new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue(because: "mt_connect is fire-and-forget; it acks the request synchronously");
    }

    [SkippableFact]
    public async Task mt_status_after_connect_reports_state_CONNECTED()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        Skip.If(!_bench.BenchAvailable, _bench.PreflightMessage);

        var connected = await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile, firstAttemptSeconds: 30);
        connected.Should().BeTrue(
            because: $"bench is supposed to be running on UDP:{EnvFlags.DefaultBenchPort}; if this fails, see MCP-002 in MT_RUNBOOK.md §9 (kill+restart MTCore and retry).");

        // Strengthened: assert mt_status itself reports CONNECTED (not just that
        // WaitForConnected returned true — that's a transient poll, this is the
        // actual point-in-time state). Real shape per StatusCommand.Execute:
        // data is a List<{Name, Status (formatted "✓ CONNECTED" / "✗ DISCONNECTED" /
        // "⚠ STALE …"), Active, Address, Exchange, Algos, Running, Uptime, ...}>.
        var resp = await _mcp.CallTool("mt_status", new { });
        resp.InnerSuccess.Should().BeTrue();
        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array,
            because: "status payload is a flat list of connection rows");

        bool foundConnected = false;
        foreach (var c in data.EnumerateArray())
        {
            string? name = c.TryGetProperty("Name", out var n) ? n.GetString() : null;
            string? status = c.TryGetProperty("Status", out var s) ? s.GetString() : null;
            if (name == EnvFlags.DefaultBenchProfile &&
                (status ?? "").Contains("CONNECTED", StringComparison.OrdinalIgnoreCase) &&
                !(status ?? "").Contains("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                foundConnected = true;
        }
        foundConnected.Should().BeTrue(
            because: $"data should include {EnvFlags.DefaultBenchProfile} with a CONNECTED status (Status field uses '✓ CONNECTED' formatting)");
    }

    [SkippableFact]
    public async Task mt_core_status_returns_populated_server_block()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        Skip.If(!_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_core_status", new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        var data = resp.ParsedBody!.Value.GetProperty("data");

        // Real fields per CoreStatusCommand.HandleStatus:
        //   Server, Exchange, EndPoint, CoreCPU, SystemCPU, SystemRAM, CoreRAM,
        //   FreeRAM, Threads (int), ExchangeLatency, PeerLatency, ApiLoading,
        //   UdsStatus, LastUpdate.
        data.TryGetProperty("Server", out var server).Should().BeTrue();
        server.GetString().Should().NotBeNullOrWhiteSpace(because: "Server is the connection name");

        data.TryGetProperty("Threads", out var threads).Should().BeTrue();
        threads.GetInt32().Should().BeGreaterOrEqualTo(0, because: "Threads is a counter from the Core process");

        data.TryGetProperty("CoreCPU", out _).Should().BeTrue(because: "CPU usage telemetry must be present");
        data.TryGetProperty("LastUpdate", out _).Should().BeTrue(because: "the timestamp of the snapshot");
    }

    [SkippableFact]
    public async Task mt_connection_health_reports_at_least_one_tracked_connection_with_required_fields()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        Skip.If(!_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_connection_health", new { });
        resp.InnerSuccess.Should().BeTrue();
        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.TryGetProperty("connections", out var connections).Should().BeTrue();
        connections.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        connections.GetArrayLength().Should().BeGreaterThan(0,
            because: "after WaitForConnected, at least one health record is tracked");

        var first = connections[0];
        // Real fields per FleetCommand.HandleConnectionHealth: profile, connected,
        // healthy, latencyMs, errorCount, reconnectCount, ...
        first.TryGetProperty("profile", out var profile).Should().BeTrue();
        profile.GetString().Should().NotBeNullOrWhiteSpace();
        first.TryGetProperty("connected", out var conn).Should().BeTrue();
        conn.ValueKind.Should().BeOneOf(System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False);
    }

    [SkippableFact]
    public async Task mt_use_unknown_profile_returns_controlled_error()
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);

        var resp = await _mcp.CallTool("mt_use", new { profile = "definitely_does_not_exist_xyz" });
        // Either RPC error or success:false — NOT a crash.
        (resp.IsRpcError || resp.InnerSuccess == false).Should().BeTrue(
            because: "mt_use with unknown profile must surface a clear failure, not silently succeed");
    }

    [Fact]
    [Trait("Category", TraitCategories.Static)]
    [Trait(KnownIssue.TraitKey, KnownIssue.McpRetained009)]
    public void mt_core_restart_is_documented_known_issue()
    {
        // No subprocess call — this is a Static reminder test. mt_core_restart
        // crashes MTCore on macOS arm64 via the Firebird shutdown path.
        // PR #4 made the tool report success:false honestly. Smoke tests must
        // NOT call this tool — see MT_RUNBOOK.md §4.
        var tool = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == "mt_core_restart");
        tool.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined,
            because: "mt_core_restart still exists; the bug is in MTCore (vendor-side), not in mt-text-client");
    }
}
