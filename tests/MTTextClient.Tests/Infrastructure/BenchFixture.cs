using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using Xunit;

namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// Smoke and BenchAll tests run against REAL MTCore processes. This fixture
/// is a lightweight pre-flight check (it does not own MTCore's lifecycle):
/// when <see cref="EnvFlags.TestingEnv"/> is true it probes every bench port
/// in <see cref="EnvFlags.AllBenches"/> and records which are reachable.
/// Tests can then conditionally skip per-bench.
///
/// MTCore is started out-of-band (or the testing-environment CI workflow
/// does it). When <see cref="EnvFlags.TestingEnv"/> is false (PR-gate CI
/// default), this fixture no-ops; Smoke / BenchAll tests check
/// <see cref="EnvFlags.TestingEnv"/> themselves and skip.
///
/// The fixture probes all configured benches in parallel and exposes
/// <see cref="PortBound"/> + per-bench availability via
/// <see cref="IsBenchAvailable"/>. The handshake check itself
/// (mt_connect → wait for CONNECTED state) lives in
/// <see cref="McpFixture.WaitForConnected"/> — see the per-test class
/// helpers. Doing both pieces in McpFixture is the right place because
/// they share the MCP subprocess, whereas this fixture is purely the
/// pre-flight gate.
/// </summary>
public sealed class BenchFixture : IAsyncLifetime
{
    private readonly Dictionary<string, bool> _byProfile =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if MTCore is observed listening on the default bench port
    /// (<see cref="EnvFlags.DefaultBenchPort"/>). Always false when
    /// <see cref="EnvFlags.TestingEnv"/> is false. Preserved for the
    /// existing Smoke-test gate; BenchAll tests should use
    /// <see cref="IsBenchAvailable"/> for per-profile checks instead.
    /// </summary>
    public bool BenchAvailable { get; private set; }

    /// <summary>Diagnostic message captured during InitializeAsync.</summary>
    public string? PreflightMessage { get; private set; }

    /// <summary>
    /// True if the named bench profile has MTCore observed on
    /// its expected UDP port (per <see cref="EnvFlags.AllBenches"/>). Used
    /// by BenchAll tests to skip benches that aren't up.
    /// </summary>
    public bool IsBenchAvailable(string profile) =>
        _byProfile.TryGetValue(profile, out var b) && b;

    /// <summary>
    /// Snapshot of every bench's preflight result. Useful in test output
    /// or diagnostics when figuring out why BenchAll tests skip.
    /// </summary>
    public IReadOnlyDictionary<string, bool> PortBound => _byProfile;

    public Task InitializeAsync()
    {
        if (!EnvFlags.TestingEnv)
        {
            PreflightMessage = $"{EnvFlags.TestingEnvVar} not set; bench preflight skipped.";
            BenchAvailable = false;
            foreach (var b in EnvFlags.AllBenches) _byProfile[b.Profile] = false;
            return Task.CompletedTask;
        }

        // Probe each bench port. Allow up to 30s per fixture init to absorb
        // cold-start delay; we poll all four in parallel and finish as soon
        // as each one resolves (bound or window-expired).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var pending = new Dictionary<string, (int Port, string Exchange)>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var b in EnvFlags.AllBenches)
        {
            pending[b.Profile] = (b.Port, b.Exchange);
            _byProfile[b.Profile] = false;
        }

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            var resolved = new System.Collections.Generic.List<string>();
            foreach (var (profile, info) in pending)
            {
                if (IsUdpPortBound(info.Port))
                {
                    _byProfile[profile] = true;
                    resolved.Add(profile);
                }
            }
            foreach (var r in resolved) pending.Remove(r);
            if (pending.Count > 0) Thread.Sleep(500);
        }

        BenchAvailable = _byProfile.TryGetValue(EnvFlags.DefaultBenchProfile, out var defAvail) && defAvail;
        int upCount = 0;
        foreach (var v in _byProfile.Values) if (v) upCount++;
        PreflightMessage = upCount == EnvFlags.AllBenches.Length
            ? $"All {EnvFlags.AllBenches.Length} bench MTCores observed."
            : $"{upCount}/{EnvFlags.AllBenches.Length} bench MTCores observed. " +
              "Missing: " + string.Join(", ",
                  System.Linq.Enumerable.Where(_byProfile, kv => !kv.Value)
                    .Select(kv => kv.Key)) +
              ". Start the bench MTCores on their configured UDP ports.";
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// UDP is connectionless, so the "is the port bound?" check is to
    /// attempt to bind the same port — if bind fails with AddressInUse,
    /// something else is already there.  This probe is iterated over every
    /// configured bench port.
    /// </summary>
    private static bool IsUdpPortBound(int port)
    {
        try
        {
            using var udp = new UdpClient(port);
            udp.Close();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
