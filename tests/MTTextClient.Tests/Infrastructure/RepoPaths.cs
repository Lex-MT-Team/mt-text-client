using System.IO;

namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// Locates files relative to the mt-text-client repo root regardless of the
/// directory the test runner is invoked from.
/// </summary>
public static class RepoPaths
{
    private static string? _root;

    /// <summary>The repo root: the first ancestor directory containing MTTextClient.csproj.</summary>
    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MTTextClient.csproj")))
                {
                    _root = dir.FullName;
                    return _root;
                }
                dir = dir.Parent;
            }
            throw new IOException("Could not locate repo root (no MTTextClient.csproj ancestor).");
        }
    }

    /// <summary>Path to the built MCP binary.</summary>
    public static string McpBinary =>
        Path.Combine(Root, "bin", "Release", "net8.0", "MTTextClient");

    /// <summary>Path to the built MCP DLL (used for `dotnet bin/Release/net8.0/MTTextClient.dll --mcp`).</summary>
    public static string McpDll =>
        Path.Combine(Root, "bin", "Release", "net8.0", "MTTextClient.dll");

    /// <summary>Path to the locked baseline fixture.</summary>
    public static string ToolsMinimumFixture =>
        Path.Combine(AppContext.BaseDirectory, "_expected", "tools.minimum.json");

    /// <summary>Source MTShared.dll under lib/ (always AMD64 in vendor build).</summary>
    public static string MTSharedSource =>
        Path.Combine(Root, "lib", "MTShared.dll");

    /// <summary>Built MTShared.dll under bin/.../net8.0/ (must be ARM64-patched on macOS arm64).</summary>
    public static string MTSharedBuilt =>
        Path.Combine(Root, "bin", "Release", "net8.0", "MTShared.dll");
}
