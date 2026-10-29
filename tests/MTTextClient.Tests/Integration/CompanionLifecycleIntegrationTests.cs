extern alias companion;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using CompanionConfiguration = companion::MTTextClient.Core.Diagnostics.Companion.CompanionConfiguration;
using NativeCompanionServer = companion::MTTextClient.Core.Diagnostics.Companion.NativeCompanionServer;

namespace MTTextClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class CompanionLifecycleIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public CompanionLifecycleIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(256, 0)] // 0400
    [InlineData(384, 0)] // 0600
    [InlineData(416, 78)] // 0640
    [InlineData(420, 78)] // 0644
    [InlineData(436, 78)] // 0664
    public async Task ConfigurationCheck_ValidatesAuthWithoutBindingOrChangingFiles(int mode, int expectedExit)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new CompanionFixture(profileCount: 1);
        string auth = Path.Combine(Path.GetDirectoryName(fixture.ConfigurationPath)!, "auth", "profile-1", "core.auth");
        File.SetUnixFileMode(auth, (UnixFileMode)mode);
        byte[] original = File.ReadAllBytes(auth);
        // Keeping the port occupied proves the check neither binds nor starts a listener.
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Any, fixture.DiagnosticPort));
        using Process process = StartConfigurationCheck(fixture.ConfigurationPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.Should().Be(expectedExit);
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            if (expectedExit == 0)
            {
                output.Should().Contain("No listeners started");
            }
            else
            {
                error.Should().Contain("0400 or 0600");
            }
            (output + error).Should().NotContain(fixture.Token).And.NotContain(fixture.ConfigurationPath);
            File.GetUnixFileMode(auth).Should().Be((UnixFileMode)mode);
            File.ReadAllBytes(auth).Should().Equal(original);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task ConfigurationCheck_RejectsMalformedAuthContentBeforeOpeningListeners()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new MalformedAuthFixture();
        using Process process = StartConfigurationCheck(Path.Combine(fixture.Root, "companion.json"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.Should().Be(78);
            (await process.StandardError.ReadToEndAsync()).Should().Contain("Configuration or auth rejected").And.NotContain(fixture.Root);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static Process StartConfigurationCheck(string path)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { typeof(NativeCompanionServer).Assembly.Location, "--check-config", path }
        }) ?? throw new InvalidOperationException("Synthetic configuration check did not start.");
    }

    [Fact]
    public async Task CompanionProcess_RejectsFifoAuthFileBeforeItCanBlockStartup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new FifoAuthFixture();
        using Process process = fixture.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.Should().Be(78);
            (await process.StandardError.ReadToEndAsync()).Should().Contain("Companion startup failed.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task CompanionProcess_ReportsOwnerOnlyPermissionCategoryForGroupReadableAuthFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new GroupReadableAuthFixture();
        using Process process = fixture.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.Should().Be(78);
        string stderr = await process.StandardError.ReadToEndAsync();
        stderr.Should().Contain("Companion startup failed.");
        stderr.Should().Contain("owner-only permissions");
        stderr.Should().Contain("0400 or 0600");
    }

    [Fact]
    public async Task CompanionProcess_ReportsMalformedOwnerOnlyAuthAsConfigurationRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new MalformedAuthFixture();
        using Process process = fixture.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.Should().Be(78);
        string stderr = await process.StandardError.ReadToEndAsync();
        stderr.Should().Contain("Configuration or auth rejected");
        stderr.Should().NotContain(fixture.Root);
        stderr.Should().NotContain("Companion auth is invalid");
    }

    [Fact]
    public async Task CompanionProcess_RejectsFifoEvidenceSourcesWithoutBlockingOrReturningPayload()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new FifoEvidenceFixture();
        await fixture.StartAsync();
        bool stopped = false;
        try
        {
            DiagnosticResult logs = await fixture.ExecuteAsync(DiagnosticRequest.Logs(1, null));
            DiagnosticResult snapshot = await fixture.ExecuteAsync(DiagnosticRequest.GuardianStatus());
            DiagnosticResult events = await fixture.ExecuteAsync(DiagnosticRequest.GuardianEvents(1));

            logs.Kind.Should().Be(DiagnosticResultKind.Success);
            ((JObject)logs.Data!)["lines"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
            ((JObject)logs.Data!)["files"]!.ToString().Should().Contain("blocked");
            snapshot.Kind.Should().Be(DiagnosticResultKind.Success);
            ((JObject)snapshot.Data!)["snapshot"]!["status"]!.Value<string>().Should().Be("blocked");
            events.Kind.Should().Be(DiagnosticResultKind.Success);
            ((JObject)events.Data!)["eventHistory"]!["status"]!.Value<string>().Should().Be("blocked");
            (await fixture.StopGracefullyAsync()).Should().BeTrue();
            stopped = true;
        }
        finally
        {
            if (!stopped)
            {
                await fixture.StopAsync();
            }
        }
    }

    [Fact]
    public void TryAcquire_RollsBackGlobalCapacityWhenOneListenerIsSaturated()
    {
        using var fixture = new CompanionFixture(profileCount: 2);
        using NativeCompanionServer server = NativeCompanionServer.Create(fixture.Load());
        NativeCompanionServer.Host saturated = server.Hosts[0];
        NativeCompanionServer.Host independent = server.Hosts[1];

        for (int index = 0; index < 4; index++)
        {
            server.TryAcquire(saturated).Should().BeTrue();
        }

        for (int index = 0; index < 32; index++)
        {
            server.TryAcquire(saturated).Should().BeFalse();
        }

        server.TryAcquire(independent).Should().BeTrue();

        server.Release(independent);
        for (int index = 0; index < 4; index++)
        {
            server.Release(saturated);
        }
    }

    [Fact]
    public async Task RunAsync_CancelsAndDrainsActiveSourceWorkBeforeReturning()
    {
        using var fixture = new CompanionFixture(profileCount: 1);
        using var sourceStarted = new ManualResetEventSlim();
        using var allowSourceCompletion = new ManualResetEventSlim();
        using var server = NativeCompanionServer.Create(fixture.Load(), (_, operation, _, _, cancellationToken) =>
        {
            if (operation == "capabilities")
            {
                return new JObject { ["capabilities"] = new JArray("capabilities", "logs") };
            }

            operation.Should().Be("logs");
            sourceStarted.Set();
            cancellationToken.WaitHandle.WaitOne();
            allowSourceCompletion.Wait();
            return new JObject { ["lines"] = new JArray("synthetic") };
        });
        using var shutdown = new CancellationTokenSource();
        using var requestCancellation = new CancellationTokenSource();
        Task run = server.RunAsync(shutdown.Token);
        Task<DiagnosticResult> request = ExecuteLogsAsync(fixture, requestCancellation.Token);

        try
        {
            (await Task.Run(() => sourceStarted.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();

            shutdown.Cancel();
            await Task.Delay(100);

            run.IsCompleted.Should().BeFalse("shutdown must drain admitted source work before returning");

            allowSourceCompletion.Set();
            await AwaitRunAsync(run);
            server.Hosts[0].QueuedResponseCount.Should().Be(0, "a completed source must not enqueue a response after shutdown begins");
            requestCancellation.Cancel();
            (await request).Kind.Should().Be(DiagnosticResultKind.Cancelled);
        }
        finally
        {
            allowSourceCompletion.Set();
            shutdown.Cancel();
            await AwaitRunAsync(run);
            requestCancellation.Cancel();
            await AwaitRunAsync(request);
        }
    }

    [Fact]
    public async Task RunAsync_IdleSampleSeparatesWarmupFromSustainedResourceUse()
    {
        using var fixture = new CompanionFixture(profileCount: 1);
        using var server = NativeCompanionServer.Create(fixture.Load());
        using var shutdown = new CancellationTokenSource();
        Task run = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            IdleSample warmup = await MeasureIdleWindowAsync();
            IdleSample sustained = await MeasureIdleWindowAsync();

            _output.WriteLine($"warmup: wall_ms={warmup.WallMilliseconds} cpu_ms={warmup.CpuMilliseconds:F1} allocated_bytes={warmup.AllocatedBytes} gen0={warmup.Gen0Collections} gen1={warmup.Gen1Collections} gen2={warmup.Gen2Collections}");
            _output.WriteLine($"sustained: wall_ms={sustained.WallMilliseconds} cpu_ms={sustained.CpuMilliseconds:F1} allocated_bytes={sustained.AllocatedBytes} gen0={sustained.Gen0Collections} gen1={sustained.Gen1Collections} gen2={sustained.Gen2Collections}");

            run.IsCompleted.Should().BeFalse("the synthetic listener must remain idle and available during both samples");
        }
        finally
        {
            shutdown.Cancel();
            await AwaitRunAsync(run);
        }
    }

    private static async Task<IdleSample> MeasureIdleWindowAsync()
    {
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        Stopwatch stopwatch = Stopwatch.StartNew();

        await Task.Delay(750);

        stopwatch.Stop();
        process.Refresh();
        return new IdleSample(
            (long)stopwatch.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before);
    }

    private static async Task AwaitRunAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct IdleSample(long WallMilliseconds, double CpuMilliseconds, long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections);

    private static Task<DiagnosticResult> ExecuteLogsAsync(CompanionFixture fixture, CancellationToken cancellationToken)
    {
        var profile = new ServerProfile
        {
            Name = "profile-1",
            Address = "127.0.0.1",
            Port = 4242,
            ClientToken = fixture.Token,
            Exchange = ExchangeType.BINANCE
        };
        return new MtDiagClient().ExecuteAsync(
            profile,
            new DiagnosticRoute("127.0.0.1", fixture.DiagnosticPort, "explicit"),
            DiagnosticRequest.Logs(1, null),
            new DiagnosticIdentityEvidence(profile.Name, null, new[] { profile.Name }),
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    private sealed class CompanionFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _configPath;

        internal CompanionFixture(int profileCount)
        {
            _root = Path.Combine(Path.GetTempPath(), "mtdiag-lifecycle-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _configPath = Path.Combine(_root, "companion.json");
            var profiles = new JArray();
            for (int index = 1; index <= profileCount; index++)
            {
                string name = "profile-" + index;
                string authDirectory = Path.Combine("auth", name);
                Directory.CreateDirectory(Path.Combine(_root, authDirectory));
                string authPath = Path.Combine(authDirectory, "core.auth");
                string logDirectory = Path.Combine("logs", name);
                Directory.CreateDirectory(Path.Combine(_root, logDirectory));
                File.WriteAllText(Path.Combine(_root, authPath), "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Token)) + Environment.NewLine);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Path.Combine(_root, authPath), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                profiles.Add(new JObject
                {
                    ["name"] = name,
                    ["exchange"] = "BINANCE",
                    ["corePort"] = 4241 + index,
                    ["diagnosticPort"] = index == 1 ? DiagnosticPort : ReserveUdpPortForFixture(),
                    ["coreAuthPath"] = authPath,
                    ["logRoots"] = new JArray(logDirectory),
                    ["logGlobs"] = new JArray("*.log")
                });
            }
            File.WriteAllText(_configPath, new JObject { ["profiles"] = profiles }.ToString());
        }

        internal string Token => "synthetic-lifecycle-token";
        internal string ConfigurationPath => _configPath;
        internal int DiagnosticPort { get; } = ReserveUdpPortForFixture();

        internal CompanionConfiguration Load() => CompanionConfiguration.Load(_configPath);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        internal static int ReserveUdpPortForFixture()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }

    private sealed class FifoAuthFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _configPath;

        internal FifoAuthFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "mtdiag-fifo-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "logs"));
            _configPath = Path.Combine(_root, "companion.json");
            string authPath = Path.Combine(_root, "core.auth");
            CreateFifo(authPath);
            var profile = new JObject
            {
                ["name"] = "fifo",
                ["exchange"] = "BINANCE",
                ["corePort"] = 4242,
                ["diagnosticPort"] = CompanionFixture.ReserveUdpPortForFixture(),
                ["coreAuthPath"] = "core.auth",
                ["logRoots"] = new JArray("logs"),
                ["logGlobs"] = new JArray("*.log")
            };
            File.WriteAllText(_configPath, new JObject { ["profiles"] = new JArray(profile) }.ToString());
        }

        internal Process Start()
        {
            string companionPath = typeof(NativeCompanionServer).Assembly.Location;
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { companionPath, "--config", _configPath }
            });
            return process ?? throw new InvalidOperationException("Synthetic companion process did not start.");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        internal static void CreateFifo(string path)
        {
            if (mkfifo(path, Convert.ToUInt32("600", 8)) != 0)
            {
                throw new InvalidOperationException("Synthetic FIFO fixture could not be created.");
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string pathname, uint mode);
    }

    private sealed class MalformedAuthFixture : IDisposable
    {
        private readonly string _configPath;

        internal MalformedAuthFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mtdiag-malformed-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "logs"));
            _configPath = Path.Combine(Root, "companion.json");
            string authPath = Path.Combine(Root, "core.auth");
            File.WriteAllText(authPath, "CLIENT:not-valid-base64!" + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var profile = new JObject
            {
                ["name"] = "malformed-auth",
                ["exchange"] = "BINANCE",
                ["corePort"] = 4242,
                ["diagnosticPort"] = CompanionFixture.ReserveUdpPortForFixture(),
                ["coreAuthPath"] = "core.auth",
                ["logRoots"] = new JArray("logs"),
                ["logGlobs"] = new JArray("*.log")
            };
            File.WriteAllText(_configPath, new JObject { ["profiles"] = new JArray(profile) }.ToString());
        }

        internal string Root { get; }

        internal Process Start()
        {
            string companionPath = typeof(NativeCompanionServer).Assembly.Location;
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { companionPath, "--config", _configPath }
            });
            return process ?? throw new InvalidOperationException("Synthetic companion process did not start.");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class GroupReadableAuthFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _configPath;

        internal GroupReadableAuthFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "mtdiag-group-readable-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "logs"));
            _configPath = Path.Combine(_root, "companion.json");
            string authPath = Path.Combine(_root, "core.auth");
            File.WriteAllText(authPath, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-group-readable-token")) + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
            var profile = new JObject
            {
                ["name"] = "group-readable",
                ["exchange"] = "BINANCE",
                ["corePort"] = 4242,
                ["diagnosticPort"] = CompanionFixture.ReserveUdpPortForFixture(),
                ["coreAuthPath"] = "core.auth",
                ["logRoots"] = new JArray("logs"),
                ["logGlobs"] = new JArray("*.log")
            };
            File.WriteAllText(_configPath, new JObject { ["profiles"] = new JArray(profile) }.ToString());
        }

        internal Process Start()
        {
            string companionPath = typeof(NativeCompanionServer).Assembly.Location;
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { companionPath, "--config", _configPath }
            });
            return process ?? throw new InvalidOperationException("Synthetic companion process did not start.");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FifoEvidenceFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _configPath;
        private readonly int _diagnosticPort;
        private Process? _process;

        internal FifoEvidenceFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "mtdiag-fifo-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            string logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logs);
            FifoAuthFixture.CreateFifo(Path.Combine(logs, "blocked.log"));
            FifoAuthFixture.CreateFifo(Path.Combine(_root, "guardian.snapshot"));
            FifoAuthFixture.CreateFifo(Path.Combine(_root, "guardian.events"));
            File.WriteAllText(Path.Combine(_root, "core.auth"), "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Token)) + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(_root, "core.auth"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _diagnosticPort = CompanionFixture.ReserveUdpPortForFixture();
            _configPath = Path.Combine(_root, "companion.json");
            var profile = new JObject
            {
                ["name"] = "fifo",
                ["exchange"] = "BINANCE",
                ["corePort"] = 4242,
                ["diagnosticPort"] = _diagnosticPort,
                ["coreAuthPath"] = "core.auth",
                ["logRoots"] = new JArray("logs"),
                ["logGlobs"] = new JArray("*.log"),
                ["guardianSnapshotPath"] = "guardian.snapshot",
                ["guardianEventPath"] = "guardian.events"
            };
            File.WriteAllText(_configPath, new JObject { ["profiles"] = new JArray(profile) }.ToString());
        }

        private const string Token = "synthetic-fifo-token";

        internal async Task StartAsync()
        {
            string companionPath = typeof(NativeCompanionServer).Assembly.Location;
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { companionPath, "--config", _configPath }
            }) ?? throw new InvalidOperationException("Synthetic companion process did not start.");

            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (_process.HasExited)
                {
                    string error = await _process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException("Synthetic companion exited before accepting loopback requests: " + error);
                }
                if ((await ExecuteAsync(DiagnosticRequest.Capabilities())).Kind == DiagnosticResultKind.Success)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("Synthetic companion did not accept a loopback request.");
        }

        internal Task<DiagnosticResult> ExecuteAsync(DiagnosticRequest request)
        {
            var profile = new ServerProfile
            {
                Name = "fifo",
                Address = "127.0.0.1",
                Port = 4242,
                ClientToken = Token,
                Exchange = ExchangeType.BINANCE
            };
            return new MtDiagClient().ExecuteAsync(
                profile,
                new DiagnosticRoute("127.0.0.1", _diagnosticPort, "explicit"),
                request,
                new DiagnosticIdentityEvidence(profile.Name, null, new[] { profile.Name }),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        }

        internal async Task StopAsync()
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        internal async Task<bool> StopGracefullyAsync()
        {
            if (_process is null || _process.HasExited)
            {
                return _process?.ExitCode == 0;
            }
            if (kill(_process.Id, 15) != 0)
            {
                return false;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
                return _process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Dispose()
        {
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

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int processId, int signal);
    }
}
