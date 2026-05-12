using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Stage 0.3 / OV-2: every tool's <c>inputSchema.required</c> array must contain
/// no duplicate entries. This catches the <c>mt_settings_set</c>-class bug:
/// adding <c>confirm</c> to the required array twice (once by the migration,
/// once by a hand-written entry) silently passes the regex name check but
/// breaks JSON-Schema consumers.
///
/// Source asserted against: live <c>tools/list</c> from <see cref="McpFixture.Tools"/>.
/// This is identical to what the registry emits, so any duplicate from
/// <see cref="MTTextClient.Core.ToolRegistry"/> is caught here.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class NoDuplicateSchemaFieldsTests
{
    private readonly McpFixture _mcp;
    public NoDuplicateSchemaFieldsTests(McpFixture mcp) => _mcp = mcp;

    [Theory]
    [MemberData(nameof(AllToolNames))]
    public void ToolRequiredArray_HasNoDuplicateEntries(string toolName)
    {
        var tool = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
        tool.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            because: $"{toolName} must be in the live catalog");

        if (!tool.TryGetProperty("inputSchema", out var schema)) return;
        if (!schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array) return;

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var dupes = new System.Collections.Generic.List<string>();
        foreach (var r in req.EnumerateArray())
        {
            var s = r.GetString();
            if (s is null) continue;
            if (!seen.Add(s)) dupes.Add(s);
        }

        dupes.Should().BeEmpty(
            because: $"{toolName}.inputSchema.required must not contain duplicate entries; " +
                     $"found duplicates: [{string.Join(", ", dupes)}]");
    }

    [Theory]
    [MemberData(nameof(AllToolNames))]
    public void ToolPropertiesObject_HasNoDuplicateKeys(string toolName)
    {
        // System.Text.Json deduplicates by default during parse, so this test
        // is structurally a no-op against the parsed live catalog. Its real
        // purpose is to document the invariant — any future change that
        // emits raw text catalog (e.g. a snapshot test) can run the same
        // assertion against the unparsed bytes.
        var tool = _mcp.Tools.FirstOrDefault(t => t.GetProperty("name").GetString() == toolName);
        tool.ValueKind.Should().NotBe(JsonValueKind.Undefined);

        if (!tool.TryGetProperty("inputSchema", out var schema)) return;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return;

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var p in props.EnumerateObject())
            seen.Add(p.Name).Should().BeTrue(
                because: $"{toolName}.inputSchema.properties must not declare duplicate key '{p.Name}'");
    }

    public static IEnumerable<object[]> AllToolNames()
    {
        // Drive from the locked baseline rather than the live fixture: theory
        // data is enumerated before fixtures are constructed. The "live tool
        // exists" check happens inside each theory body.
        var baseline = LoadBaselineNames();
        foreach (var name in baseline)
            yield return new object[] { name };
    }

    private static IEnumerable<string> LoadBaselineNames()
    {
        var path = RepoPaths.ToolsMinimumFixture;
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
            yield return tool.GetProperty("name").GetString()!;
    }
}
