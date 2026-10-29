extern alias companion;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;
using NativeCompanionServer = companion::MTTextClient.Core.Diagnostics.Companion.NativeCompanionServer;

namespace MTTextClient.Tests.Integration;

/// <summary>
/// Exercises the independently started companion through the production native
/// client using only disposable loopback fixtures.
/// </summary>
public sealed class NativeCompanionIntegrationTests
{
    [Fact]
    public async Task SingleProfile_SpawnedCompanion_AcceptsCapabilitiesAndAllReadOnlyOperations()
    {
        using var fixture = new CompanionProcessFixture();
        await fixture.StartAsync();

        DiagnosticResult capabilities = await fixture.ExecuteAsync(DiagnosticRequest.Capabilities());

        capabilities.Kind.Should().Be(DiagnosticResultKind.Success);
        capabilities.Data.Should().BeOfType<JObject>();
        ((JArray)((JObject)capabilities.Data!)["capabilities"]!).Values<string>()
            .Should().BeEquivalentTo(new[] { "capabilities", "health", "logs", "guardian-status", "guardian-events" });

        (await fixture.ExecuteAsync(DiagnosticRequest.Health())).Kind.Should().Be(DiagnosticResultKind.Success);
        (await fixture.ExecuteAsync(DiagnosticRequest.Logs(1, "visible marker"))).Kind.Should().Be(DiagnosticResultKind.Success);
        (await fixture.ExecuteAsync(DiagnosticRequest.GuardianStatus())).Kind.Should().Be(DiagnosticResultKind.Success);
        (await fixture.ExecuteAsync(DiagnosticRequest.GuardianEvents(1))).Kind.Should().Be(DiagnosticResultKind.Success);
    }

    [Fact]
    public async Task Evidence_TwoSpawnedListeners_RejectWrongSeedAndDoNotCrossProfileSources()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var fixture = new CompanionProcessFixture(includeSecondProfile: true);
            await fixture.StartAsync();

            Task<DiagnosticResult> first = fixture.ExecuteAsync("synthetic", "synthetic-loopback-token", DiagnosticRequest.Logs(1, null));
            Task<DiagnosticResult> second = fixture.ExecuteAsync("synthetic-second", "second-loopback-token", DiagnosticRequest.Logs(1, null));
            DiagnosticResult[] results = await Task.WhenAll(first, second);
            DiagnosticResult wrongSeed = await fixture.ExecuteAsync("synthetic", "wrong-synthetic-token", DiagnosticRequest.Capabilities());

            wrongSeed.Kind.Should().NotBe(DiagnosticResultKind.Success);
            wrongSeed.Code.Should().Be("native-connection-rejected");
            wrongSeed.Data.Should().BeNull();
            wrongSeed.Metadata.Should().BeNull();
            results[0].Kind.Should().Be(DiagnosticResultKind.Success);
            results[1].Kind.Should().Be(DiagnosticResultKind.Success);
            results[0].Data!.ToString().Should().Contain("synthetic marker").And.NotContain("second marker");
            results[1].Data!.ToString().Should().Contain("second marker").And.NotContain("synthetic marker");
        }
    }

    [Fact]
    public async Task AuthenticatedRequest_WithUnboundProfile_ReturnsPayloadFreeRefusal()
    {
        using var fixture = new CompanionProcessFixture();
        await fixture.StartAsync();

        DiagnosticResult result = await fixture.ExecuteWithRequestProfileAsync("unbound-synthetic-profile", DiagnosticRequest.Capabilities(), TimeSpan.FromSeconds(1));

        result.Code.Should().Be("server-error");
        result.Kind.Should().Be(DiagnosticResultKind.ServerError);
        result.Error.Should().Be("profile-unbound");
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task SilentUdpEndpoint_RemainsAnAmbiguousTimeoutWithNoEvidence()
    {
        using var silentEndpoint = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silentEndpoint.Client.LocalEndPoint!).Port;
        var profile = new ServerProfile { Name = "synthetic-silent", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-silent-token", Exchange = ExchangeType.BINANCE };

        DiagnosticResult result = await new MtDiagClient().ExecuteAsync(profile,
            new DiagnosticRoute(profile.Address, port, "explicit"), DiagnosticRequest.Capabilities(),
            new DiagnosticIdentityEvidence(profile.Name, null, new[] { profile.Name }), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        result.Kind.Should().Be(DiagnosticResultKind.Timeout);
        result.Code.Should().Be("deadline-exceeded");
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task Shutdown_SigtermExitsAndReleasesTheExplicitPort()
    {
        using var fixture = new CompanionProcessFixture();
        await fixture.StartAsync();

        await fixture.StopAsync();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, fixture.DiagnosticPort));
    }

    [Theory]
    [InlineData(101, 0)]
    [InlineData(500, 0)]
    [InlineData(500, 200)]
    public async Task GuardianEvents_AdvertisedRangeRoundTripsWithNewestEvidence(int count, int padding)
    {
        using var fixture = new CompanionProcessFixture();
        fixture.WriteGuardianEvents(600, padding);
        await fixture.StartAsync();

        DiagnosticResult result = await fixture.ExecuteAsync(DiagnosticRequest.GuardianEvents(count));

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        JArray entries = (JArray)result.Data!["eventHistory"]!["entries"]!;
        entries.Count.Should().BeInRange(1, count);
        if (padding == 0) entries.Should().HaveCount(count);
        entries.Last!.Value<string>().Should().Contain("event-599").And.Contain("WS.State").And.Contain("12:34:56");
        result.Data!["eventHistory"]!["truncated"]!.Value<bool>().Should().BeTrue();
        if (padding > 0) result.Data!["responseTruncated"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task HistoryPages_ReuseNativeAuthAndReplayAfterConnectionReplacement()
    {
        using var fixture = new CompanionProcessFixture();
        fixture.WriteHistory();
        await fixture.StartAsync();
        var query = new DiagnosticHistoryQuery("logs", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 500, Guid.NewGuid().ToString("N"));
        int count = 0;
        while (true)
        {
            DiagnosticResult result = await fixture.ExecuteAsync(DiagnosticRequest.HistoryRequest(query));
            result.Kind.Should().Be(DiagnosticResultKind.Success, "history error: {0}", result.Error);
            JObject page = (JObject)result.Data!;
            if (count == 0) fixture.ReplaceClient();
            DiagnosticResult replay = await fixture.ExecuteAsync(DiagnosticRequest.HistoryRequest(query));
            replay.Data!.ToString().Should().Be(page.ToString());
            count += ((JArray)page["records"]!).Count;
            if (!page["has_more"]!.Value<bool>()) break;
            query = query with { Cursor = page["cursor"]!.Value<string>(), Sequence = page["next_sequence"]!.Value<long>() };
        }
        count.Should().Be(2000);
    }

    [Fact]
    public async Task HistorySessions_SerializeDistinctQueriesBindCredentialsAndDispose()
    {
        using var fixture = new CompanionProcessFixture(); fixture.WriteHistory(); await fixture.StartAsync();
        var query = new DiagnosticHistoryQuery("logs", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 10, Guid.NewGuid().ToString("N"), UnknownTime: "report-only");
        DiagnosticResult[] first = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.ExecuteAsync(DiagnosticRequest.HistoryRequest(query with { QueryId = Guid.NewGuid().ToString("N") }))));
        first.Should().OnlyContain(r => r.Kind == DiagnosticResultKind.Success);
        var sessions = (System.Collections.IDictionary)typeof(MtDiagClient).GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Client)!;
        sessions.Count.Should().Be(1, "four queries must share one authenticated connection");
        object original = sessions.Values.Cast<object>().Single();
        object connection = original.GetType().GetField("Session")!.GetValue(original)!;
        var againQuery = query with { QueryId = first[0].Data!["query_id"]!.Value<string>()!, Cursor = first[0].Data!["cursor"]!.Value<string>(), Sequence = first[0].Data!["sequence"]!.Value<long>() };
        (await fixture.ExecuteAsync(DiagnosticRequest.HistoryRequest(againQuery))).Kind.Should().Be(DiagnosticResultKind.Success);
        original.GetType().GetField("Session")!.GetValue(original).Should().BeSameAs(connection, "page replay reuses the authenticated native session");
        (await fixture.ExecuteAsync("synthetic", "wrong-synthetic-token", DiagnosticRequest.HistoryRequest(query))).Kind.Should().NotBe(DiagnosticResultKind.Success);
        sessions.Count.Should().Be(1, "a changed credential must neither reuse nor poison the valid session");
        sessions.Values.Cast<object>().Single().Should().BeSameAs(original);
        fixture.Client.Dispose(); sessions.Count.Should().Be(0);
        (await fixture.ExecuteAsync(DiagnosticRequest.HistoryRequest(query))).Kind.Should().Be(DiagnosticResultKind.Cancelled);
    }

    private sealed class CompanionProcessFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _configPath;
        private readonly Dictionary<string, (int Port, int CorePort, string Token)> _profiles = new(StringComparer.Ordinal)
        {
            ["synthetic"] = (0, 4242, "synthetic-loopback-token")
        };
        private Process? _process;
        internal MtDiagClient Client = new();
        internal void ReplaceClient() { Client.Dispose(); Client = new MtDiagClient(); }

        internal CompanionProcessFixture(bool includeSecondProfile = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "mtdiag-companion-integration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "logs"));
            _profiles["synthetic"] = (ReserveUdpPort(), 4242, "synthetic-loopback-token");
            if (includeSecondProfile)
            {
                _profiles["synthetic-second"] = (ReserveUdpPort(), 4243, "second-loopback-token");
            }
            _configPath = Path.Combine(_root, "companion.json");
            var profiles = new JArray();
            foreach ((string name, (int port, int corePort, string token) profile) in _profiles)
            {
                string authDirectory = Path.Combine(_root, "auth", name);
                Directory.CreateDirectory(authDirectory);
                string authPath = Path.Combine("auth", name, "core.auth");
                File.WriteAllText(Path.Combine(_root, authPath), "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.token)) + Environment.NewLine);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Path.Combine(_root, authPath), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                string logDirectory = Path.Combine(_root, "logs", name);
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(Path.Combine(logDirectory, "service.log"), "token=hidden" + Environment.NewLine + name + " marker");
                profiles.Add(new JObject
                {
                    ["name"] = name,
                    ["exchange"] = "BINANCE",
                    ["corePort"] = profile.corePort,
                    ["diagnosticPort"] = profile.port,
                    ["coreAuthPath"] = authPath,
                    ["logRoots"] = new JArray(Path.Combine("logs", name)),
                    ["logGlobs"] = new JArray("*.log"),
                    ["guardianSnapshotPath"] = "guardian.snapshot",
                    ["guardianEventPath"] = "guardian.events"
                });
            }
            File.WriteAllText(Path.Combine(_root, "guardian.snapshot"), "snapshot");
            File.WriteAllText(Path.Combine(_root, "guardian.events"), "2026-10-26T00:00:00Z event");
            File.WriteAllText(_configPath, new JObject { ["profiles"] = profiles }.ToString());
        }

        internal async Task StartAsync()
        {
            string companionPath = typeof(NativeCompanionServer).Assembly.Location;
            _process = Process.Start(new ProcessStartInfo("dotnet", $"\"{companionPath}\" --config \"{_configPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            _process.Should().NotBeNull();

            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (_process!.HasExited)
                {
                    string error = await _process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException("Synthetic companion exited before accepting loopback requests: " + error);
                }
                DiagnosticResult result = await ExecuteAsync(DiagnosticRequest.Capabilities());
                if (result.Kind == DiagnosticResultKind.Success)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("Synthetic companion did not accept a loopback request.");
        }

        internal void WriteHistory() => File.WriteAllLines(Path.Combine(_root, "logs", "synthetic", "service.log"), Enumerable.Range(0, 2000).Select(i => $"2026-01-01T08:00:00Z record-{i}"));

        internal void WriteGuardianEvents(int count, int padding)
        {
            File.WriteAllText(Path.Combine(_root, "guardian.events"), string.Join('\n', Enumerable.Range(0, count).Select(index => $"12:34:56 WS.State event-{index:D3} {new string('x', padding)}")));
        }

        internal int DiagnosticPort => _profiles["synthetic"].Port;

        internal Task<DiagnosticResult> ExecuteAsync(DiagnosticRequest request)
        {
            return ExecuteAsync("synthetic", _profiles["synthetic"].Token, request);
        }

        internal Task<DiagnosticResult> ExecuteAsync(string name, string token, DiagnosticRequest request)
        {
            (int port, int corePort, _) = _profiles[name];
            var profile = new ServerProfile
            {
                Name = name,
                Address = "127.0.0.1",
                Port = corePort,
                ClientToken = token,
                Exchange = ExchangeType.BINANCE
            };
            return Client.ExecuteAsync(
                profile,
                new DiagnosticRoute("127.0.0.1", port, "explicit"),
                request,
                new DiagnosticIdentityEvidence(name, null, _profiles.Keys),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
        }

        internal Task<DiagnosticResult> ExecuteWithRequestProfileAsync(string requestProfile, DiagnosticRequest request, TimeSpan timeout)
        {
            (int port, int corePort, string token) configured = _profiles["synthetic"];
            var profile = new ServerProfile
            {
                Name = requestProfile,
                Address = "127.0.0.1",
                Port = configured.corePort,
                ClientToken = configured.token,
                Exchange = ExchangeType.BINANCE
            };
            return Client.ExecuteAsync(
                profile,
                new DiagnosticRoute("127.0.0.1", configured.port, "explicit"),
                request,
                new DiagnosticIdentityEvidence(requestProfile, null, new[] { "synthetic" }),
                timeout,
                CancellationToken.None);
        }

        public void Dispose()
        {
            Client.Dispose();
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            _process?.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        internal async Task StopAsync()
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                _process.Kill(entireProcessTree: true);
            }
            else
            {
                using Process signal = Process.Start(new ProcessStartInfo("kill", "-TERM " + _process.Id)
                {
                    UseShellExecute = false
                })!;
                await signal.WaitForExitAsync();
            }
            await _process.WaitForExitAsync();
            _process.ExitCode.Should().Be(0);
        }

        private static int ReserveUdpPort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }
}
