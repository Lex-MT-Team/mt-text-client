using System;
using System.IO;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>Protects public diagnostic packaging material from local or private residue.</summary>
[Trait("Category", TraitCategories.Static)]
public sealed class PublicArtifactHygieneTests
{
    [Fact]
    public void DiagnosticPublicArtifacts_ContainNoPrivateContextOrCredentialCanaries()
    {
        string[] files =
        {
            "Core/Diagnostics/Companion/MTDiag.Companion.csproj",
            "Core/Diagnostics/Companion/Program.cs",
            "Core/Diagnostics/Companion/packaging/mtdiag-companion.service.example",
            "docs/mtdiag-companion.md",
            "README.md",
            "CHANGELOG.md"
        };
        string[] forbidden =
        {
            "internal-project-context",
            "/Users/",
            "private planning",
            "internal-process-marker",
            "synthetic-packaging-token",
            "mtdiag-packaging-local"
        };

        foreach (string relativePath in files)
        {
            string contents = File.ReadAllText(Path.Combine(RepoPaths.Root, relativePath));
            foreach (string marker in forbidden)
            {
                contents.Should().NotContain(marker, relativePath + " must remain safe for public distribution");
            }
        }
    }

    [Fact]
    public void CompanionPublishAndVendorInputs_RemainPinnedAndSingleFileCompatible()
    {
        string project = File.ReadAllText(Path.Combine(RepoPaths.Root, "Core", "Diagnostics", "Companion", "MTDiag.Companion.csproj"));
        project.Should().Contain("<PublishSingleFile>true</PublishSingleFile>");
        project.Should().Contain("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");
        project.Should().Contain("<PublishTrimmed>false</PublishTrimmed>");
        project.Should().Contain("<PublishAot>false</PublishAot>");
        project.Should().Contain("PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\"");

        string vendor = File.ReadAllText(Path.Combine(RepoPaths.Root, "lib", "vendor.json"));
        vendor.Should().Contain("\"osx-arm64\"").And.Contain("\"linux-x64\"").And.Contain("\"linux-arm64\"");
        vendor.Should().Contain("\"core_build\": \"0.7.25589\"");

        string mainProject = File.ReadAllText(Path.Combine(RepoPaths.Root, "MTTextClient.csproj"));
        mainProject.Should().Contain("<Compile Remove=\"Core\\Diagnostics\\Companion\\**\" />");
    }
}
