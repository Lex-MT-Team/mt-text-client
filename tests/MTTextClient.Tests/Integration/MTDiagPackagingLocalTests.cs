using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Integration;

/// <summary>
/// Exercises self-contained diagnostic candidates using only disposable local output.
/// </summary>
public sealed class MTDiagPackagingLocalTests
{
    [Fact]
    public async Task MacOs_PublishedCandidates_ProvideRuntimeFreeHelpAndSyntheticDiagnostics()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string repositoryRoot = FindRepositoryRoot();
        string output = Path.Combine(Path.GetTempPath(), "mtdiag-packaging-local-" + Guid.NewGuid().ToString("N"));
        Process? companion = null;
        SyntheticFixture? fixture = null;
        try
        {
            string companionOutput = Path.Combine(output, "companion");
            string clientOutput = Path.Combine(output, "client");
            await PublishAsync(repositoryRoot, "Core/Diagnostics/Companion/MTDiag.Companion.csproj", companionOutput);
            await PublishAsync(repositoryRoot, "MTTextClient.csproj", clientOutput);

            string companionExecutable = Path.Combine(companionOutput, "MTDiag.Companion");
            string clientExecutable = Path.Combine(clientOutput, "MTTextClient");
            File.Exists(companionExecutable).Should().BeTrue();
            File.Exists(clientExecutable).Should().BeTrue();
            CandidateContainsPrivateResidue(await File.ReadAllBytesAsync(companionExecutable), allowMacRuntimeProvenance: true).Should().BeFalse();
            CandidateContainsPrivateResidue(await File.ReadAllBytesAsync(clientExecutable), allowMacRuntimeProvenance: true).Should().BeFalse();

            ChildProcessEnvironment helpEnvironment = ChildProcessEnvironment.Create(companionOutput, "companion-help");
            ProcessResult help = await RunAsync(companionExecutable, "--help", companionOutput, helpEnvironment);
            help.ExitCode.Should().Be(0);
            help.StandardOutput.Should().Contain("Usage: MTDiag.Companion --config <file>");
            Directory.Exists(helpEnvironment.BundleExtractionDirectory).Should().BeTrue();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(helpEnvironment.BundleExtractionDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            fixture = new SyntheticFixture(output);
            ChildProcessEnvironment companionEnvironment = ChildProcessEnvironment.Create(companionOutput, "companion");
            companion = Start(companionExecutable, "--config \"" + fixture.ConfigurationPath + "\"", companionOutput, companionEnvironment);
            Stopwatch startup = Stopwatch.StartNew();
            await WaitForDiagnosticAsync(clientExecutable, clientOutput, fixture, "capabilities", startup);
            startup.Stop();

            long idleRss = companion.WorkingSet64;
            TimeSpan idleCpu = companion.TotalProcessorTime;
            await ExecuteClientAsync(clientExecutable, clientOutput, fixture, "health");
            companion.Refresh();
            long activeRss = companion.WorkingSet64;
            TimeSpan activeCpu = companion.TotalProcessorTime;

            new FileInfo(companionExecutable).Length.Should().BeGreaterThan(0);
            new FileInfo(clientExecutable).Length.Should().BeGreaterThan(0);
            startup.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            idleRss.Should().BeGreaterThanOrEqualTo(0);
            activeRss.Should().BeGreaterThanOrEqualTo(0);
            idleCpu.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            activeCpu.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }
        finally
        {
            if (companion is not null)
            {
                await StopAsync(companion);
                companion.Dispose();
            }

            fixture?.Dispose();
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public async Task Linux_CrossPublishedCompanion_IsSingleFileAndNotExecuted(string runtimeIdentifier)
    {
        string repositoryRoot = FindRepositoryRoot();
        string output = Path.Combine(Path.GetTempPath(), "mtdiag-linux-publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            string companionOutput = Path.Combine(output, "companion");
            string clientOutput = Path.Combine(output, "client");
            await PublishAsync(repositoryRoot, "Core/Diagnostics/Companion/MTDiag.Companion.csproj", companionOutput, runtimeIdentifier);
            await PublishAsync(repositoryRoot, "MTTextClient.csproj", clientOutput, runtimeIdentifier);
            string[] candidates =
            {
                Path.Combine(companionOutput, "MTDiag.Companion"),
                Path.Combine(clientOutput, "MTTextClient")
            };
            foreach (string executable in candidates)
            {
                File.Exists(executable).Should().BeTrue();
                ProcessResult inspect = await RunAsync("file", "\"" + executable + "\"", Path.GetDirectoryName(executable)!);
                inspect.ExitCode.Should().Be(0);
                string format = inspect.StandardOutput;
                format.Should().Contain("ELF").And.Contain(runtimeIdentifier.EndsWith("arm64", StringComparison.Ordinal) ? "aarch64" : "x86-64");

                byte[] candidate = await File.ReadAllBytesAsync(executable);
                CandidateContainsPrivateResidue(candidate, allowMacRuntimeProvenance: false).Should().BeFalse();
            }

            Directory.GetFiles(companionOutput).Should().ContainSingle(path => Path.GetFileName(path) == "MTDiag.Companion");
            Directory.GetFiles(clientOutput).Should().Contain(path => Path.GetFileName(path) == "MTTextClient");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MacOs_PublishedCompanion_StopsBeforeFixtureCleanupWhenFailureOccurs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string repositoryRoot = FindRepositoryRoot();
        string output = Path.Combine(Path.GetTempPath(), "mtdiag-packaging-cleanup-" + Guid.NewGuid().ToString("N"));
        Process? companion = null;
        SyntheticFixture? fixture = null;
        bool companionStopped = false;
        try
        {
            string companionOutput = Path.Combine(output, "companion");
            await PublishAsync(repositoryRoot, "Core/Diagnostics/Companion/MTDiag.Companion.csproj", companionOutput);
            fixture = new SyntheticFixture(output);
            companion = Start(
                Path.Combine(companionOutput, "MTDiag.Companion"),
                "--config \"" + fixture.ConfigurationPath + "\"",
                companionOutput,
                ChildProcessEnvironment.Create(companionOutput, "companion-failure"));

            throw new InvalidOperationException("Synthetic post-start assertion failure.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "Synthetic post-start assertion failure.")
        {
        }
        finally
        {
            if (companion is not null)
            {
                await StopAsync(companion);
                companionStopped = companion.HasExited;
                companion.Dispose();
            }

            fixture?.Dispose();
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }

        companionStopped.Should().BeTrue();
        Directory.Exists(output).Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/Users/runner/work/1/s/src/coreclr/vm/example.cpp", true, false)]
    [InlineData("/Users/runner/work/1/s/src/coreclr/vm/example.cpp", false, true)]
    [InlineData("/Users/runner/work/1/s/artifacts/obj/System.Runtime/example.pdb", true, false)]
    [InlineData("/Users/runner/work/1/s/artifacts/obj-extra/example.pdb", true, true)]
    [InlineData("/Users/example/project/example.cs", true, true)]
    [InlineData("/Users/runner/work/1/s/src-extra/example.cs", true, true)]
    [InlineData("/Users/runner/work/1/s/src/example.cpp\0/Users/example/key", true, true)]
    [InlineData("synthetic-packaging-token", true, true)]
    public void CandidateHygiene_OnlyAllowsExactUpstreamRuntimeProvenance(string value, bool allowMacRuntimeProvenance, bool expectedResidue)
    {
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            // An odd byte offset also exercises embedded UTF-16 strings in a binary.
            byte[] candidate = encoding.GetBytes("x" + value);
            byte[] unaligned = new byte[candidate.Length + 1];
            candidate.CopyTo(unaligned, 1);
            CandidateContainsPrivateResidue(unaligned, allowMacRuntimeProvenance).Should().Be(expectedResidue);
        }
    }

    private static bool CandidateContainsPrivateResidue(byte[] candidate, bool allowMacRuntimeProvenance)
    {
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            foreach (string marker in new[] { "internal-project-context", "synthetic-packaging-token" })
            {
                if (candidate.AsSpan().IndexOf(encoding.GetBytes(marker)) >= 0)
                {
                    return true;
                }
            }

            byte[] userPath = encoding.GetBytes("/Users/");
            // Source and symbol provenance embedded in the unchanged .NET 8.0.25 runtime.
            byte[] upstreamPath = encoding.GetBytes("/Users/runner/work/1/s/src/");
            byte[] upstreamSymbols = encoding.GetBytes("/Users/runner/work/1/s/artifacts/obj/");
            ReadOnlySpan<byte> remaining = candidate;
            int index;
            while ((index = remaining.IndexOf(userPath)) >= 0)
            {
                remaining = remaining[index..];
                if (!allowMacRuntimeProvenance || (!remaining.StartsWith(upstreamPath) && !remaining.StartsWith(upstreamSymbols)))
                {
                    return true;
                }

                remaining = remaining[userPath.Length..];
            }
        }

        return false;
    }

    private static async Task PublishAsync(string repositoryRoot, string project, string output, string runtimeIdentifier = "osx-arm64")
    {
        ProcessResult publish = await RunAsync(
            "dotnet",
            "publish \"" + project + "\" -c Release -r " + runtimeIdentifier + " --self-contained true -p:VendorOffline=true -p:DebugType=None -p:DebugSymbols=false -o \"" + output + "\"",
            repositoryRoot);
        publish.ExitCode.Should().Be(0, publish.StandardError);
    }

    private static async Task WaitForDiagnosticAsync(string clientExecutable, string clientOutput, SyntheticFixture fixture, string operation, Stopwatch startup)
    {
        while (startup.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                await ExecuteClientAsync(clientExecutable, clientOutput, fixture, operation);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(50);
            }
        }

        throw new TimeoutException("Published synthetic companion did not accept a diagnostic request.");
    }

    private static async Task ExecuteClientAsync(string clientExecutable, string clientOutput, SyntheticFixture fixture, string operation)
    {
        ProcessResult client = await RunAsync(
            clientExecutable,
            "diag " + operation + " synthetic --diag-port " + fixture.DiagnosticPort,
            clientOutput,
            fixture.CreateClientEnvironment(clientOutput));
        if (client.ExitCode != 0 || !client.StandardOutput.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Published synthetic client request did not succeed: " + client.StandardOutput + client.StandardError);
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            using Process signal = Start("kill", "-TERM " + process.Id, process.StartInfo.WorkingDirectory!);
            await WaitForExitWithinAsync(signal, TimeSpan.FromSeconds(5));
            if (await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5)))
            {
                return;
            }
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static async Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, ChildProcessEnvironment? environment = null)
    {
        using Process process = Start(fileName, arguments, workingDirectory, environment);
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static Process Start(string fileName, string arguments, string workingDirectory, ChildProcessEnvironment? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environment is not null)
        {
            startInfo.Environment["HOME"] = environment.HomeDirectory;
            startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = environment.BundleExtractionDirectory;
        }

        Process? process = Process.Start(startInfo);
        process.Should().NotBeNull();
        return process!;
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MTTextClient.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the packaging test must run from a product build output");
        return directory!.FullName;
    }

    private sealed class SyntheticFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _token;

        internal SyntheticFixture(string parent)
        {
            _root = Path.Combine(parent, "synthetic-fixture");
            CreateOwnerOnlyDirectory(_root);
            CreateOwnerOnlyDirectory(Path.Combine(_root, "logs"));
            CreateOwnerOnlyDirectory(Path.Combine(_root, "auth"));
            DiagnosticPort = ReserveUdpPort();
            _token = "synthetic-packaging-token";
            string authPath = Path.Combine(_root, "auth", "core.auth");
            File.WriteAllText(authPath, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_token)) + Environment.NewLine);
            SetOwnerOnly(authPath);
            File.WriteAllText(Path.Combine(_root, "logs", "synthetic.log"), "synthetic packaging marker");
            File.WriteAllText(Path.Combine(_root, "guardian.snapshot"), "synthetic snapshot");
            File.WriteAllText(Path.Combine(_root, "guardian.events"), "2026-10-26T00:00:00Z synthetic event");
            ConfigurationPath = Path.Combine(_root, "companion.json");
            File.WriteAllText(ConfigurationPath, new JObject
            {
                ["profiles"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "synthetic",
                        ["exchange"] = "BINANCE",
                        ["corePort"] = 4242,
                        ["diagnosticPort"] = DiagnosticPort,
                        ["coreAuthPath"] = "auth/core.auth",
                        ["logRoots"] = new JArray("logs"),
                        ["logGlobs"] = new JArray("*.log"),
                        ["guardianSnapshotPath"] = "guardian.snapshot",
                        ["guardianEventPath"] = "guardian.events"
                    }
                }
            }.ToString());
        }

        internal string ConfigurationPath { get; }

        internal int DiagnosticPort { get; }

        internal ChildProcessEnvironment CreateClientEnvironment(string output)
        {
            ChildProcessEnvironment environment = ChildProcessEnvironment.Create(output, "client");
            string profileDirectory = Path.Combine(environment.HomeDirectory, ".config", "mt-textclient");
            CreateOwnerOnlyDirectory(Path.Combine(environment.HomeDirectory, ".config"));
            CreateOwnerOnlyDirectory(profileDirectory);
            string profilePath = Path.Combine(profileDirectory, "profiles.json");
            File.WriteAllText(profilePath, new JArray
            {
                new JObject
                {
                    ["Name"] = "synthetic",
                    ["Address"] = "127.0.0.1",
                    ["Port"] = 4242,
                    ["ClientToken"] = _token,
                    ["Exchange"] = "BINANCE"
                }
            }.ToString());
            SetOwnerOnly(profilePath);
            return environment;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static int ReserveUdpPort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        private static void SetOwnerOnly(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private sealed class ChildProcessEnvironment
    {
        private ChildProcessEnvironment(string root)
        {
            HomeDirectory = Path.Combine(root, "home");
            BundleExtractionDirectory = Path.Combine(root, "bundle-extract");
            CreateOwnerOnlyDirectory(root);
            CreateOwnerOnlyDirectory(HomeDirectory);
            CreateOwnerOnlyDirectory(BundleExtractionDirectory);
        }

        internal string BundleExtractionDirectory { get; }

        internal string HomeDirectory { get; }

        internal static ChildProcessEnvironment Create(string output, string role)
        {
            string root = Path.Combine(output, "child-environments", role + "-" + Guid.NewGuid().ToString("N"));
            return new ChildProcessEnvironment(root);
        }
    }

    private sealed class ProcessResult
    {
        internal ProcessResult(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        internal int ExitCode { get; }

        internal string StandardError { get; }

        internal string StandardOutput { get; }
    }
}
