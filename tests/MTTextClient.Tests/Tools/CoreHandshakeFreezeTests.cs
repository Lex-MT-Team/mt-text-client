using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Detect the MTCore freeze pattern that Stage 1's LiveTrade campaign
/// exposed (DEFECT-11 / DEFECT-MTCORE-FREEZE on the Stage 0 defect
/// register).  Symptom: the MTCore process stays alive and its UDP port
/// stays bound, but its LiteNetLib receive loop stops pumping, so
/// inbound peer-connect packets sit in the OS receive queue forever.
/// The bench port "looks up" via <c>lsof</c>, but no client can ever
/// reach the CONNECTED state.
///
/// This test does a real end-to-end handshake: a fresh
/// <see cref="McpFixture.RestartSubprocessAsync"/> followed by an
/// <see cref="McpFixture.WaitForConnected"/> that times out only if
/// MTCore's accept loop is genuinely not running.  Earlier Smoke tests
/// (e.g. <c>CoreTests.mt_connect_bench_01_AcceptsRequest</c>) only
/// asserted the client-side ack of <c>mt_connect</c>; they didn't catch
/// a frozen receive loop because the client-side send always succeeds.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class CoreHandshakeFreezeTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public CoreHandshakeFreezeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    /// <summary>
    /// Bench profiles whose receive-loop liveness should be asserted.
    /// Each row gets its own parameterised case so a single freeze fails
    /// loudly per bench rather than wedging the whole suite.
    /// </summary>
    public static IEnumerable<object[]> BenchProfiles() => new[]
    {
        new object[] { "bench_01", 4242 },
        new object[] { "bench_02", 4243 },
        new object[] { "bench_03", 4244 },
        new object[] { "bench_04", 4245 },
    };

    [SkippableTheory]
    [MemberData(nameof(BenchProfiles))]
    public async Task ReceiveLoop_Live_ConnectAndStatusRoundTripWithinSixtySeconds(string profile, int port)
    {
        Skip.If(!EnvFlags.TestingEnv, _bench.PreflightMessage);
        Skip.If(!_bench.IsBenchAvailable(profile),
            $"Bench {profile} not observed on UDP:{port}; skipping (port-not-bound is a different failure than receive-loop-frozen).");

        // A fresh subprocess is the only way to be sure the prior test's
        // state can't mask a freeze (e.g. an already-cached CONNECTED state).
        await _mcp.RestartSubprocessAsync();

        bool connected = await _mcp.WaitForConnected(profile, firstAttemptSeconds: 60);
        connected.Should().BeTrue(
            because: $"bench {profile} is bound to UDP:{port} but its MTCore receive loop did not produce a CONNECTED state within 60 s — " +
                     "this is the DEFECT-11 / MTCORE-FREEZE signature.  Restart the bench " +
                     "(`kill -9 $(pgrep -f \"--profile <profile>\"); start_all_cores.sh`) and re-run.");
    }
}
