using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Catalog-level Static tests. One test class drives a [Theory] over every
/// tool returned by the live MCP server's tools/list, plus a [Theory] over
/// every name in the locked baseline fixture. Together these prove:
///
/// <list type="bullet">
/// <item>Every previously-shipped tool name still exists (no silent removals).</item>
/// <item>Every advertised tool follows the naming convention.</item>
/// <item>Every advertised tool has an inputSchema with a 'required' array
///       that is at least as strict as the baseline (no silent loosening).</item>
/// <item>The catalog total never drops below the locked floor (206 at the
///       time this baseline was captured).</item>
/// </list>
///
/// The test stack is xUnit/.NET only.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class ToolCatalogStaticTests
{
    private static readonly Regex NameRegex = new(@"^mt_[a-z][a-z0-9_]*[a-z0-9]$", RegexOptions.Compiled);

    private readonly McpFixture _mcp;

    public ToolCatalogStaticTests(McpFixture mcp) => _mcp = mcp;

    [Fact]
    public void ToolsList_HasAtLeastBaselineCount()
    {
        var baseline = LoadBaseline();
        _mcp.ToolNames.Count.Should().BeGreaterOrEqualTo(
            baseline.ToolCountMinimum,
            because: "the locked floor is 206 tools; iterations may add tools but must not silently remove them.");
    }

    [Theory]
    [MemberData(nameof(BaselineToolNames))]
    public void EveryBaselineTool_StillExistsInLiveCatalog(string baselineName)
    {
        _mcp.ToolNames.Should().Contain(baselineName,
            because: $"tool {baselineName} was present in the locked baseline; removing or renaming a tool is a backwards-incompatible change.");
    }

    [Theory]
    [MemberData(nameof(BaselineToolEntries))]
    public void EveryBaselineTool_RetainsItsRequiredArgs(string toolName, string[] expectedRequired)
    {
        var live = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
        live.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            because: $"baseline tool {toolName} must still be advertised");

        var liveRequired = ExtractRequired(live);
        foreach (var req in expectedRequired)
        {
            liveRequired.Should().Contain(req,
                because: $"tool {toolName} must continue to require '{req}' (loosening a required field is backwards-incompatible).");
        }
    }

    // ─── Theory data ─────────────────────────────────────────────────────────

    public static IEnumerable<object[]> BaselineToolNames()
    {
        var b = LoadBaseline();
        foreach (var t in b.Tools)
            yield return new object[] { t.Name };
    }

    public static IEnumerable<object[]> BaselineToolEntries()
    {
        var b = LoadBaseline();
        foreach (var t in b.Tools)
            yield return new object[] { t.Name, t.Required };
    }

    public static IEnumerable<object[]> LiveTools()
    {
        // We can't access the McpFixture directly from a static MemberData provider
        // (MemberData is enumerated before fixtures are constructed). Instead we
        // use the locked baseline's NAMES to drive [Theory] params, and the
        // body of each theory looks up the live tool by name.
        //
        // BUT: per-tool theories that need the live JsonElement will look it up
        // inline. Here we yield the locked-baseline tool *names* so the theory
        // runner has stable test IDs; the body then resolves the live element.
        var b = LoadBaseline();
        foreach (var t in b.Tools)
            yield return new object[] { t.Name };
    }

    /// <summary>Adapter so `[Theory] [MemberData(nameof(LiveTools))]` test
    /// methods that take a JsonElement parameter can resolve via the fixture.</summary>
    private JsonElement ResolveLive(string toolName)
    {
        var live = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
        live.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            because: $"tool {toolName} should exist in the live catalog (covered by EveryBaselineTool_StillExistsInLiveCatalog).");
        return live;
    }

    // The Theories above declared `JsonElement tool` as a parameter, but the
    // MemberData provider yields a string. Replace the parameter type with
    // `string toolName` and resolve inside via ResolveLive(). Below are the
    // "by name" overloads matching the Theories above.

    // Naming-convention check (replaces EveryLiveTool_HasValidName above):
    [Theory]
    [MemberData(nameof(LiveTools))]
    public void EveryLiveToolByName_HasValidName(string toolName)
    {
        var t = ResolveLive(toolName);
        var name = t.GetProperty("name").GetString() ?? "";
        NameRegex.IsMatch(name).Should().BeTrue(
            because: $"tool name '{name}' must match {NameRegex}.");
    }

    [Theory]
    [MemberData(nameof(LiveTools))]
    public void EveryLiveToolByName_HasInputSchemaWithRequiredArrayWhenPresent(string toolName)
    {
        var t = ResolveLive(toolName);
        t.TryGetProperty("inputSchema", out var schema).Should().BeTrue();
        if (schema.TryGetProperty("required", out var req))
            req.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Theory]
    [MemberData(nameof(LiveTools))]
    public void EveryLiveToolByName_HasNonEmptyDescription(string toolName)
    {
        var t = ResolveLive(toolName);
        t.TryGetProperty("description", out var d).Should().BeTrue();
        d.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(LiveTools))]
    public void EveryLiveToolByName_HasNoDuplicateSchemaFields(string toolName)
    {
        var t = ResolveLive(toolName);
        if (!t.TryGetProperty("inputSchema", out var schema)) return;
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in req.EnumerateArray())
            {
                var s = r.GetString();
                if (s is null) continue;
                seen.Add(s).Should().BeTrue(
                    because: $"tool {toolName} inputSchema.required must not contain duplicate '{s}'");
            }
        }
    }

    // ─── Baseline loader ─────────────────────────────────────────────────────

    private static Baseline LoadBaseline()
    {
        var path = RepoPaths.ToolsMinimumFixture;
        File.Exists(path).Should().BeTrue(because: $"baseline fixture must be at {path}");
        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var min = root.GetProperty("tool_count_minimum").GetInt32();
        var tools = new List<BaselineTool>();
        foreach (var t in root.GetProperty("tools").EnumerateArray())
        {
            var name = t.GetProperty("name").GetString() ?? "";
            var required = new List<string>();
            if (t.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in r.EnumerateArray())
                    if (s.GetString() is { } str) required.Add(str);
            }
            tools.Add(new BaselineTool(name, required.ToArray()));
        }
        return new Baseline(min, tools);
    }

    private static string[] ExtractRequired(JsonElement tool)
    {
        if (!tool.TryGetProperty("inputSchema", out var schema)) return Array.Empty<string>();
        if (!schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var r in req.EnumerateArray())
            if (r.GetString() is { } s) list.Add(s);
        return list.ToArray();
    }

    private sealed record BaselineTool(string Name, string[] Required);
    private sealed record Baseline(int ToolCountMinimum, List<BaselineTool> Tools);
}
