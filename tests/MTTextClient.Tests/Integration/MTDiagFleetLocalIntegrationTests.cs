extern alias companion;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Xunit.Abstractions;
using NativeCompanionServer = companion::MTTextClient.Core.Diagnostics.Companion.NativeCompanionServer;

namespace MTTextClient.Tests.Integration;

public sealed class MTDiagFleetLocalIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MTDiagFleetLocalIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Tracer_NinthHealthRequest_RemainsResponsive()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var collector = new DiagnosticFleetCollector(new MtDiagClient());

        foreach (ServerProfile[] batch in new[]
        {
            fixture.Profiles.Take(8).ToArray(),
            fixture.Profiles.Skip(8).Take(1).ToArray()
        })
        {
            DiagnosticFleetResult result = await collector.CollectAsync(
                new DiagnosticFleetRequest(batch.Select(item => item.Name).ToArray(), DiagnosticRequest.Health(), null, 8, TimeSpan.FromSeconds(3), false), fixture.Profiles, CancellationToken.None);
            result.Rows.Should().OnlyContain(row => row.Result != null && row.Result.Kind == DiagnosticResultKind.Success);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Tracer_ActualSeventySevenListeners_AreBoundedAndAttributed()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            var collector = new DiagnosticFleetCollector(new MtDiagClient());
            DiagnosticFleetResult result = await collector.CollectAsync(
                new DiagnosticFleetRequest(fixture.Profiles.Select(item => item.Name).ToArray(), DiagnosticRequest.Health(), null, DiagnosticFleetRequest.DefaultConcurrency, DiagnosticFleetRequest.DefaultDeadline, false), fixture.Profiles, CancellationToken.None);
            IReadOnlyList<DiagnosticFleetRow> rows = result.Rows;
            WriteFleetEvidence("health-77", elapsed, result);

            rows.Should().HaveCount(77);
            elapsed.Elapsed.Should().BeLessThan(DiagnosticFleetRequest.DefaultDeadline);
            rows.All(row => row.Result is not null && row.Result.Kind == DiagnosticResultKind.Success && row.Attempts.Count == 1).Should().BeTrue();
            foreach (DiagnosticFleetRow row in rows)
            {
                row.Result!.Profile.Should().Be(row.RequestedProfile);
                row.Result.Source.Should().Be("companion");
            }
        }
        finally
        {
            await fixture.StopAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ActualSeventySevenListeners_LogsKeepEachProfileSourceMarkerIsolated()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            var collector = new DiagnosticFleetCollector(new MtDiagClient());
            DiagnosticFleetResult result = await collector.CollectAsync(
                new DiagnosticFleetRequest(fixture.Profiles.Select(item => item.Name).ToArray(), DiagnosticRequest.Logs(1, null), null, DiagnosticFleetRequest.DefaultConcurrency, DiagnosticFleetRequest.DefaultDeadline, false), fixture.Profiles, CancellationToken.None);
            WriteFleetEvidence("logs-77", elapsed, result);

            result.Rows.Should().HaveCount(77);
            elapsed.Elapsed.Should().BeLessThan(DiagnosticFleetRequest.DefaultDeadline);
            result.Coverage.Validated.Should().Be(77);
            result.Coverage.SourceAvailable.Should().Be(77);
            result.Coverage.SourceUnavailable.Should().Be(0);
            result.Coverage.Attempted.Should().Be(77);
            foreach (DiagnosticFleetRow row in result.Rows)
            {
                row.Result.Should().NotBeNull();
                row.Result!.Kind.Should().Be(DiagnosticResultKind.Success);
                row.Result.Profile.Should().Be(row.RequestedProfile);
                row.Result.Source.Should().Be("companion");
                row.Attempts.Should().ContainSingle();
                string evidence = ((JObject)row.Result.Data!)["lines"]!.ToString();
                string marker = row.RequestedProfile + " marker";
                evidence.Should().Contain(marker);
                fixture.Profiles.Where(profile => profile.Name != row.RequestedProfile)
                    .All(profile => !evidence.Contains(profile.Name + " marker", StringComparison.Ordinal))
                    .Should().BeTrue("each listener may expose only its own configured log evidence");
            }
        }
        finally
        {
            await fixture.StopAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HistoryFleet77NativeListenersHaveIndependentCursorsAndExactMarkers()
    {
        using var fixture = new Fixture(); fixture.WriteHistory(); await fixture.StartAsync();
        try
        {
            var client = new MtDiagClient(); var collector = new DiagnosticFleetCollector(client);
            var queries = fixture.Profiles.ToDictionary(p => p.Name, p => new DiagnosticHistoryQuery("logs", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 1, Guid.NewGuid().ToString("N")));
            var cursors = new HashSet<string>(); int found = 0;
            for (int batch = 0; batch < 3; batch++)
            {
                var result = await collector.CollectAsync(new DiagnosticFleetRequest(queries.Keys.ToArray(), DiagnosticRequest.HistoryRequest(queries.Values.First()), null, 8, TimeSpan.FromSeconds(15), false) { HistoryQueries = queries }, fixture.Profiles, CancellationToken.None);
                result.Coverage.Validated.Should().Be(77, "batch {0}; errors {1}", batch, string.Join(",", result.Rows.Select(r => r.Result?.Error ?? r.Result?.Code ?? r.Code)));
                foreach (var row in result.Rows)
                {
                    JObject page = (JObject)row.Result!.Data!;
                    if (batch == 0) cursors.Add(page["cursor"]!.Value<string>()!).Should().BeTrue();
                    foreach (JToken record in page["records"]!) { record["text"]!.Value<string>().Should().Contain(row.RequestedProfile + " marker"); found++; }
                    if (batch < 2) queries[row.RequestedProfile] = queries[row.RequestedProfile] with { Cursor = page["cursor"]!.Value<string>(), Sequence = page["next_sequence"]!.Value<long>() };
                    else page["complete"]!.Value<bool>().Should().BeTrue();
                }
            }
            found.Should().Be(154);
        }
        finally { await fixture.StopAsync(); }
    }

    private void WriteFleetEvidence(string scenario, Stopwatch elapsed, DiagnosticFleetResult result)
    {
        _output.WriteLine(
            "scenario={0}; elapsed_ms={1}; requested={2}; completed={3}; validated={4}; source_available={5}; source_unavailable={6}; recovered={7}; attempts={8}",
            scenario,
            elapsed.ElapsedMilliseconds,
            result.Coverage.Requested,
            result.Coverage.Completed,
            result.Coverage.Validated,
            result.Coverage.SourceAvailable,
            result.Coverage.SourceUnavailable,
            result.Coverage.Recovered,
            result.Coverage.Attempted);
    }

    private sealed class Fixture : IDisposable
    {
        private const int MaximumExceptionTypeCharacters = 128;
        private const string SystemExceptionPrefix = "System.";
        private const string StartupFailureMessage = "Companion startup failed.";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mtdiag-fleet-" + Guid.NewGuid().ToString("N"));
        private readonly string _config;
        private readonly object _stderrGate = new();
        private readonly StringBuilder _stderrExceptionCandidate = new();
        private Process? _process;
        private Task? _standardOutputDrain;
        private Task? _standardErrorDrain;
        private int _systemPrefixOffset;
        private int _startupFailureOffset;
        private bool _capturingExceptionType;
        private bool _startupFailureObserved;
        private string? _stderrExceptionType;

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Profiles = new List<ServerProfile>();
            var configurations = new JArray();
            var reservedPorts = new HashSet<int>();
            for (int index = 0; index < 77; index++)
            {
                string name = "fleet-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                int port = ReservePort(reservedPorts);
                string token = "synthetic-token-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                string auth = Path.Combine("auth", name, "core.auth");
                string authPath = Path.Combine(_root, auth);
                Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
                File.WriteAllText(authPath, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(token)) + Environment.NewLine);
                OwnerOnly(authPath);
                string log = Path.Combine("logs", name + ".log");
                string logPath = Path.Combine(_root, log);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, name + " marker");
                configurations.Add(new JObject { ["name"] = name, ["exchange"] = "BINANCE", ["corePort"] = 40000 + index, ["diagnosticPort"] = port, ["coreAuthPath"] = auth, ["logRoots"] = new JArray("logs"), ["logGlobs"] = new JArray(name + ".log"), ["guardianSnapshotPath"] = "guardian.snapshot", ["guardianEventPath"] = "guardian.events" });
                Profiles.Add(new ServerProfile { Name = name, Address = "127.0.0.1", Port = 40000 + index, ClientToken = token, Exchange = ExchangeType.BINANCE, Tags = new Dictionary<string, string> { ["mtdiag_port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            }
            File.WriteAllText(Path.Combine(_root, "guardian.snapshot"), "snapshot");
            File.WriteAllText(Path.Combine(_root, "guardian.events"), "event");
            _config = Path.Combine(_root, "companion.json");
            File.WriteAllText(_config, new JObject { ["profiles"] = configurations }.ToString());
            OwnerOnly(_config);
        }

        internal void WriteHistory()
        {
            foreach (var profile in Profiles) File.WriteAllLines(Path.Combine(_root, "logs", profile.Name + ".log"), Enumerable.Range(0, 2).Select(i => "2026-01-01T08:00:00Z " + profile.Name + " marker-" + i));
        }

        internal List<ServerProfile> Profiles { get; }

        internal string? StderrExceptionType
        {
            get
            {
                lock (_stderrGate)
                {
                    return _stderrExceptionType;
                }
            }
        }

        internal string? StderrFailureCategory
        {
            get
            {
                lock (_stderrGate)
                {
                    return _stderrExceptionType ?? (_startupFailureObserved ? "companion-startup-failed" : null);
                }
            }
        }

        internal async Task StartAsync()
        {
            _process = Process.Start(new ProcessStartInfo("dotnet", $"\"{typeof(NativeCompanionServer).Assembly.Location}\" --config \"{_config}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            _process.Should().NotBeNull();
            _standardOutputDrain = DrainAsync(_process!.StandardOutput);
            _standardErrorDrain = DrainAsync(_process.StandardError, CaptureStderrExceptionType);
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (_process!.HasExited)
                {
                    await AwaitDrainsAsync();
                    throw new InvalidOperationException("Synthetic fleet companion exited before readiness: exit=" + _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", diagnostic=" + (StderrFailureCategory ?? "none"));
                }
                ServerProfile first = Profiles[0];
                int port = int.Parse(first.Tags["mtdiag_port"], System.Globalization.CultureInfo.InvariantCulture);
                DiagnosticResult result = await new MtDiagClient().ExecuteAsync(first, new DiagnosticRoute(first.Address, port, "tag"), DiagnosticRequest.Capabilities(), new DiagnosticIdentityEvidence(first.Name, null, Profiles.Select(item => item.Name).ToArray()), TimeSpan.FromSeconds(1), CancellationToken.None);
                if (result.Kind == DiagnosticResultKind.Success) return;
                await Task.Delay(50);
            }
            throw new TimeoutException("Synthetic fleet companion did not become ready.");
        }

        public void Dispose()
        {
            if (_process is not null && !_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); }
            AwaitDrainsAsync().GetAwaiter().GetResult();
            _process?.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        internal async Task StopAsync()
        {
            if (_process is null || _process.HasExited) return;
            using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (OperatingSystem.IsWindows())
            {
                _process.Kill(entireProcessTree: true);
            }
            else
            {
                using Process signal = Process.Start(new ProcessStartInfo("kill", "-TERM " + _process.Id) { UseShellExecute = false })!;
                await signal.WaitForExitAsync(shutdownDeadline.Token);
            }
            await _process.WaitForExitAsync(shutdownDeadline.Token);
            if (!OperatingSystem.IsWindows()) _process.ExitCode.Should().Be(0);
        }

        private static async Task DrainAsync(StreamReader stream, Action<char>? onCharacter = null)
        {
            var buffer = new char[1024];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0) return;
                if (onCharacter is not null)
                {
                    for (int index = 0; index < read; index++) onCharacter(buffer[index]);
                }
            }
        }

        private async Task AwaitDrainsAsync()
        {
            if (_standardOutputDrain is not null && _standardErrorDrain is not null) await Task.WhenAll(_standardOutputDrain, _standardErrorDrain).ConfigureAwait(false);
        }

        private void CaptureStderrExceptionType(char character)
        {
            lock (_stderrGate)
            {
                if (!_startupFailureObserved)
                {
                    _startupFailureOffset = character == StartupFailureMessage[_startupFailureOffset]
                        ? _startupFailureOffset + 1
                        : character == StartupFailureMessage[0] ? 1 : 0;
                    if (_startupFailureOffset == StartupFailureMessage.Length)
                    {
                        _startupFailureObserved = true;
                        _startupFailureOffset = 0;
                    }
                }

                if (_capturingExceptionType)
                {
                    if (char.IsLetterOrDigit(character) || character == '.')
                    {
                        if (_stderrExceptionCandidate.Length < MaximumExceptionTypeCharacters) _stderrExceptionCandidate.Append(character);
                        return;
                    }
                    CompleteExceptionCandidate();
                }

                _systemPrefixOffset = character == SystemExceptionPrefix[_systemPrefixOffset]
                    ? _systemPrefixOffset + 1
                    : character == SystemExceptionPrefix[0] ? 1 : 0;
                if (_systemPrefixOffset == SystemExceptionPrefix.Length)
                {
                    _systemPrefixOffset = 0;
                    _capturingExceptionType = true;
                    _stderrExceptionCandidate.Clear();
                    _stderrExceptionCandidate.Append(SystemExceptionPrefix);
                }
            }
        }

        private void CompleteExceptionCandidate()
        {
            _capturingExceptionType = false;
            string candidate = _stderrExceptionCandidate.ToString();
            if (candidate.EndsWith("Exception", StringComparison.Ordinal)) _stderrExceptionType ??= candidate;
            _stderrExceptionCandidate.Clear();
        }

        private static int ReservePort(ISet<int> reservedPorts)
        {
            while (true)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                if ((port is < 40000 or > 40076) && reservedPorts.Add(port)) return port;
            }
        }

        private static void OwnerOnly(string path)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

}
