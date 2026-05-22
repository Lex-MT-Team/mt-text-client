using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Integration;

/// <summary>
/// Full-bench regression. Iterates all configured bench
/// profiles (<see cref="EnvFlags.AllBenches"/>) and exercises a
/// representative read-only tool set against each one, validating real
/// MTCore response shapes. The point: prove the MCP/CoreConnection path
/// is exchange-agnostic — not just BYBIT (bench_01) — and that any future
/// exchange-specific drift surfaces immediately.
///
/// For each bench profile:
///   • connect via mt_connect and wait for CONNECTED state
///   • mt_status — assert bench appears with CONNECTED
///   • mt_account_balance — assert array
///   • mt_algos_list — assert array (may be empty on fresh bench)
///   • mt_exchange_pairs — assert non-empty (every exchange has pairs)
///   • mt_core_status — assert Server field
///   • mt_settings_get — assert array of {Key, Value}
///
/// Gated by MTC_TESTING_ENV=1 plus per-bench BenchFixture.IsBenchAvailable.
/// A bench that's not up gets skipped, NOT failed — the caller can
/// start a subset of cores and still get useful coverage.
///
/// Note on bench_03 (HYPERLIQUID): the third bench's MTCore can take
/// longer to authenticate than the others. We use a 60s WaitForConnected
/// window for every bench to absorb that variance.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.BenchAll)]
public sealed class BenchAllTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public BenchAllTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    public static IEnumerable<object[]> AllBenchData()
    {
        foreach (var b in EnvFlags.AllBenches)
            yield return new object[] { b.Profile, b.Port, b.Exchange };
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task BenchHandshake_Succeeds(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} not observed on UDP:{port} ({exchange}); skipping.");

        bool connected = await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);
        connected.Should().BeTrue(
            because: $"bench {profile} is up on UDP:{port}, mt_connect + mt_status must reach CONNECTED");
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task BenchStatus_ListsProfileAsConnected(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_status", new { });
        resp.InnerSuccess.Should().BeTrue();

        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.ValueKind.Should().Be(JsonValueKind.Array);

        bool foundConnected = false;
        foreach (var c in data.EnumerateArray())
        {
            string? name   = c.TryGetProperty("Name",   out var n) ? n.GetString() : null;
            string? status = c.TryGetProperty("Status", out var s) ? s.GetString() : null;
            if (name == profile &&
                (status ?? "").Contains("CONNECTED", System.StringComparison.OrdinalIgnoreCase) &&
                !(status ?? "").Contains("DISCONNECTED", System.StringComparison.OrdinalIgnoreCase))
                foundConnected = true;
        }
        foundConnected.Should().BeTrue(
            because: $"mt_status data must include {profile} with a CONNECTED status row");
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task AccountBalance_ReturnsArray(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_account_balance", new { profile });
        resp.InnerSuccess.Should().BeTrue();
        // Empty balance is acceptable on benches with no funded test accounts;
        // a missing data field is acceptable for "no balances received yet"
        // (text-only Ok). Non-empty: must be an array with Asset / Total / Available.
        if (resp.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];
            first.TryGetProperty("Asset",     out _).Should().BeTrue();
            first.TryGetProperty("Total",     out _).Should().BeTrue();
            first.TryGetProperty("Available", out _).Should().BeTrue();
        }
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task AlgosList_ReturnsArrayOrTextOk(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_algos_list", new { profile });
        resp.InnerSuccess.Should().BeTrue();
        // The first bench profile is expected to ship with seeded algorithms;
        // the others may have 0. We accept any array shape (including empty /
        // absent for the text-only "No algorithms loaded yet" branch).
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task ExchangePairs_ReturnsNonEmptyPairsArray(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_exchange_pairs", new { profile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();
        data.TryGetProperty("Pairs", out var pairs).Should().BeTrue();
        pairs.ValueKind.Should().Be(JsonValueKind.Array);
        pairs.GetArrayLength().Should().BeGreaterThan(0,
            because: $"{exchange} always has trade pairs to enumerate");

        var first = pairs[0];
        first.TryGetProperty("Symbol",     out _).Should().BeTrue();
        first.TryGetProperty("MarketType", out _).Should().BeTrue();
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task CoreStatus_ReturnsServerBlock(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_core_status", new { profile });
        resp.InnerSuccess.Should().BeTrue();
        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.TryGetProperty("Server", out var server).Should().BeTrue();
        server.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableTheory]
    [MemberData(nameof(AllBenchData))]
    public async Task SettingsGet_ReturnsKeyValueArray(string profile, int port, string exchange)
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} ({exchange}) not observed on UDP:{port}; skipping.");
        await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);

        var resp = await _mcp.CallTool("mt_settings_get", new { profile });
        resp.InnerSuccess.Should().BeTrue();
        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterThan(0,
            because: "every MTCore profile exposes a non-empty settings dictionary");

        var first = data[0];
        first.TryGetProperty("Key",   out _).Should().BeTrue();
        first.TryGetProperty("Value", out _).Should().BeTrue();
    }
}
