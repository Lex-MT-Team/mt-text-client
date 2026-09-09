using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>Validate the actual built assemblies and the offline bootstrap regressions.</summary>
[Trait("Category", TraitCategories.Static)]
public sealed class VendorBuildTests
{
    [Fact]
    public void EveryProjectOutput_UsesThePinnedPairForThisRuntime()
    {
        string os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "lib", "vendor.json")));
        pin.RootElement.GetProperty("core_build").GetString().Should().Be(CoreStatusStore.ExpectedCoreBuild);
        var files = pin.RootElement.GetProperty("platforms").GetProperty($"{os}-{arch}").GetProperty("files");
        string[] projects = { "", "tests/MTTextClient.Tests", "tools/RegistryReadmeGenerator", "tools/DispatcherSnapshotGenerator" };
        foreach (string project in projects)
        {
            string output = Path.Combine(RepoPaths.Root, project, "bin", "Release", "net8.0");
            foreach (var file in files.EnumerateObject())
            {
                byte[] data = File.ReadAllBytes(Path.Combine(output, file.Name));
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant().Should()
                    .Be(file.Value.GetProperty("sha256").GetString(), $"{project}/{file.Name} must use the pinned bytes");
                if (file.Name == "MTShared.dll")
                {
                    int peOffset = BitConverter.ToInt32(data, 0x3C);
                    BitConverter.ToUInt16(data, peOffset + 4).Should()
                        .Be((ushort)(arch == "arm64" ? 0xAA64 : 0x8664));
                }
            }
        }
    }

    [Fact]
    public async Task VendorBootstrap_OfflineArchiveRegressionSuitePasses()
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "python" : "python3",
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(RepoPaths.Root, "tests", "MTTextClient.Tests", "Static", "vendor_fetch_tests.py"));
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        process.ExitCode.Should().Be(0, await stdout + await stderr);
    }
}
