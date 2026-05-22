using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Pin guard for <c>BouncyCastle.Cryptography</c>.
///
/// The MTCore wire protocol uses AES256 encryption implemented by
/// <c>BouncyCastle.Cryptography</c>. The dependency was historically an
/// implicit transitive — <c>lib/MTShared.dll</c>'s metadata named the
/// assembly but our build never copied it under <c>bin/Release/net8.0/</c>.
/// A future NuGet probe order shift could silently substitute a different
/// patched version, breaking cipher interop with MTCore.  We pin the
/// version explicitly in <c>MTTextClient.csproj</c>; this test asserts
/// the pin is honoured at runtime, not just at restore time:
///
///   1. The assembly is reachable from the test process — i.e. the
///      explicit NuGet PackageReference caused the DLL to be copied
///      into the consumer's bin.
///   2. The loaded version is exactly the one we expect (2.0.0).
///   3. The on-disk DLL agrees with the loaded one (no shadow-copy drift).
///
/// If a future iteration bumps the pin to a patched 2.x to clear the
/// NU1902 advisories the plan §12.1 acknowledges, update
/// <see cref="ExpectedVersion"/> in the same commit.
/// </summary>
[Trait("Category", TraitCategories.Static)]
public sealed class BouncyCastleVersionPinTests
{
    /// <summary>
    /// The exact version pinned by <c>MTTextClient.csproj</c>. Update
    /// in lockstep with the PackageReference.
    /// </summary>
    public const string ExpectedVersion = "2.0.0.0";

    /// <summary>Short form used by the on-disk file lookup.</summary>
    public const string ExpectedVersionShort = "2.0.0";

    [Fact]
    public void BouncyCastleAssembly_IsLoadable_FromTestProcess()
    {
        Assembly? loaded = TryLoad();
        loaded.Should().NotBeNull(
            because: "BouncyCastle.Cryptography is referenced by MTTextClient.csproj " +
                     "and must be copied into the test bin via ProjectReference. " +
                     "If null, the pin is broken — re-run `dotnet restore` and rebuild.");
    }

    [Fact]
    public void BouncyCastleAssembly_VersionMatchesPin()
    {
        Assembly? bc = TryLoad();
        bc.Should().NotBeNull();
        Version? actual = bc!.GetName().Version;
        actual.Should().NotBeNull();
        actual!.ToString().Should().Be(ExpectedVersion,
            because: $"the explicit NuGet pin in MTTextClient.csproj is {ExpectedVersionShort}; " +
                     "any drift means transitive resolution overrode the pin.");
    }

    [Fact]
    public void BouncyCastleDll_OnDisk_AgreesWithLoadedVersion()
    {
        // Sanity check: the DLL file shipped alongside MTTextClient (under
        // bin/Release/net8.0/) carries the same version as the in-process
        // load. Catches the case where a stale dev-build copy is sitting
        // in the test bin while NuGet restored a different version to the
        // main bin.
        string mainBinDll = Path.Combine(RepoPaths.Root, "bin", "Release", "net8.0", "BouncyCastle.Cryptography.dll");
        File.Exists(mainBinDll).Should().BeTrue(
            because: $"the pin must produce {mainBinDll} after build");

        AssemblyName diskName = AssemblyName.GetAssemblyName(mainBinDll);
        diskName.Version!.ToString().Should().Be(ExpectedVersion,
            because: "the file copied into bin/ must match the pin");
    }

    [Fact]
    public void BouncyCastleAssembly_LocationIsInsideNuGetCache_NotShadowCopied()
    {
        // The loaded assembly's Location should resolve into the .NET NuGet
        // cache (~/.nuget/packages/bouncycastle.cryptography/2.0.0/...) or
        // into a sibling bin directory. Either way, it must NOT be loaded
        // from a stray system-wide install — that would mean the pin isn't
        // actually exercised at runtime.
        Assembly? bc = TryLoad();
        bc.Should().NotBeNull();
        string location = bc!.Location;
        location.Should().NotBeNullOrWhiteSpace(
            because: "an assembly with no location is a dynamic / collectible load — not what we want here");
        (location.Contains(".nuget", StringComparison.OrdinalIgnoreCase) ||
         location.Contains("bin/Release/net8.0", StringComparison.OrdinalIgnoreCase) ||
         location.Contains("bin/Debug/net8.0",   StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
            because: $"BouncyCastle.Cryptography should resolve from a NuGet cache or build output " +
                     $"directory, got: {location}");
    }

    /// <summary>
    /// Load (or find already-loaded) <c>BouncyCastle.Cryptography</c>. We
    /// don't reference a BouncyCastle type directly so the test project
    /// doesn't need its own PackageReference — transitive resolution via
    /// the MTTextClient ProjectReference is what we want to validate.
    /// </summary>
    private static Assembly? TryLoad()
    {
        Assembly? found = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "BouncyCastle.Cryptography");
        if (found != null) return found;
        try { return Assembly.Load("BouncyCastle.Cryptography"); }
        catch { return null; }
    }
}
