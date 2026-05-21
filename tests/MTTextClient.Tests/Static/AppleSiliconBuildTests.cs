using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Apple Silicon build hygiene. Verify the post-build MSBuild target in
/// <c>MTTextClient.csproj</c> ran and produced an ARM64-marked
/// <c>lib/MTShared.dll</c>, plus that the patch script is idempotent.
///
/// Vendor ships <c>MTShared.dll</c> with PE Machine field 0x8664 (AMD64).
/// .NET 8 on Apple Silicon refuses to load that. <c>scripts/patch_mtshared_arm64.py</c>
/// flips the field to 0xAA64 (ARM64). The repo ships the file already
/// patched; the post-build target re-applies the patch on every build
/// as a safety net (idempotent on already-patched files).
///
/// On non-macOS hosts every test in this file skips cleanly — the patch
/// is not needed, and the file's Machine field reflects whatever the
/// platform's MTCore distribution shipped.
/// </summary>
[Trait("Category", TraitCategories.Static)]
public sealed class AppleSiliconBuildTests
{
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    /// <summary>Repo-relative path to the patch script.</summary>
    private static string ScriptPath => Path.Combine(RepoPaths.Root, "scripts", "patch_mtshared_arm64.py");

    /// <summary>Repo-relative path to the source MTShared.dll (the vendor copy).</summary>
    private static string LibDllPath => Path.Combine(RepoPaths.Root, "lib", "MTShared.dll");

    [SkippableFact]
    public void PatchScript_ExistsAtCommittedPath()
    {
        // The script's repo path is documented in MTTextClient.csproj's
        // PatchMTSharedArm64 target. A missing script breaks every macOS
        // arm64 build silently (the build still succeeds, but
        // lib/MTShared.dll won't be re-patched on a vendor refresh).
        // Cross-platform: still asserted — the file must be in the repo
        // on every host, even if it never runs there.
        File.Exists(ScriptPath).Should().BeTrue(
            because: $"the post-build target points at {ScriptPath}; " +
                     "without the script the target throws at every build.");
    }

    [SkippableFact]
    public void LibMTShared_HasArm64MachineField_AfterBuild()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            "Apple Silicon patch is macOS-only; on Linux/Windows the file Machine field is left as the vendor ship state.");

        ushort machine = ReadMachineField(LibDllPath);
        machine.Should().Be(MachineArm64,
            because: $"on macOS, the post-build PatchMTSharedArm64 target must leave " +
                     $"lib/MTShared.dll with Machine=0xAA64 (ARM64). Got 0x{machine:X4}. " +
                     "If 0x8664 (AMD64), the post-build target didn't fire or python3 is missing.");
    }

    [SkippableFact]
    public void PatchScript_IsIdempotent()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            "Idempotency check requires python3 + macOS-side build hygiene.");

        // Snapshot the file's bytes before running the script.
        byte[] before = File.ReadAllBytes(LibDllPath);

        // Run the script once. lib/MTShared.dll should already be ARM64
        // (the post-build target ran during the test-project build).
        // Therefore the script's report line must say "already patched",
        // and the file bytes must match before/after exactly.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{ScriptPath}\" \"{LibDllPath}\"",
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        proc.ExitCode.Should().Be(0, because: $"script must succeed; stdout: {stdout}");
        stdout.Should().Contain("already patched",
            because: "the file was already 0xAA64 (post-build target already ran); " +
                     $"second-run output should report idempotent-no-op. Got: {stdout}");

        byte[] after = File.ReadAllBytes(LibDllPath);
        after.Should().Equal(before,
            because: "idempotent: re-running the patch on an already-patched DLL must not modify any bytes");
    }

    [SkippableFact]
    public void PatchScript_DocumentsMachineFieldConstants()
    {
        // Catch the case where someone edits the script and accidentally
        // changes the magic numbers. The script's intent is fixed — flip
        // AMD64 to ARM64 — and that intent is encoded in the two
        // hex constants. We grep the file for them rather than parse
        // python AST: the script is small enough.
        string text = File.ReadAllText(ScriptPath);
        text.Should().Contain("0x8664",
            because: "the source Machine field (AMD64 vendor build) must be referenced");
        text.Should().Contain("0xAA64",
            because: "the target Machine field (ARM64 Apple Silicon) must be referenced");
    }

    /// <summary>
    /// Read the PE Machine field from a managed assembly without loading
    /// it. The offset arithmetic follows the well-known PE/COFF layout:
    /// the DWORD at file offset 0x3C points to the "PE\0\0" signature;
    /// the WORD immediately after the signature is the Machine field.
    /// </summary>
    private static ushort ReadMachineField(string path)
    {
        File.Exists(path).Should().BeTrue(because: $"target DLL must exist at {path}");
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        fs.Seek(0x3C, SeekOrigin.Begin);
        uint peOff = r.ReadUInt32();
        fs.Seek(peOff + 4, SeekOrigin.Begin);
        return r.ReadUInt16();
    }
}
