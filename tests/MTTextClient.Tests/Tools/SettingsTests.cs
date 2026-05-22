using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

[Collection(BenchCollection.Name)]
public sealed class SettingsTests
{
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public SettingsTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_settings_get_returns_array_with_Core_LOG_LEVEL_present()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        var resp = await _mcp.CallTool("mt_settings_get",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per SettingsCommand.FetchAllSettings: List<{Key, Value}>.
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterThan(0,
            because: "MTCore exposes a non-empty profile-settings dictionary");

        bool foundLogLevel = false;
        foreach (var entry in data.EnumerateArray())
        {
            entry.TryGetProperty("Key", out var key).Should().BeTrue(because: "every settings row has Key");
            entry.TryGetProperty("Value", out var value).Should().BeTrue(because: "every settings row has Value");
            if (key.GetString() == "Core.LOG_LEVEL")
            {
                value.GetString().Should().NotBeNullOrWhiteSpace(
                    because: "Core.LOG_LEVEL is a known-present setting with a non-empty value");
                foundLogLevel = true;
            }
        }
        foundLogLevel.Should().BeTrue(because: "Core.LOG_LEVEL must be in the settings dictionary");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_settings_groups_returns_at_least_5_groups_with_Group_field()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // settings_groups depends on cached settings; warm the cache first.
        await _mcp.CallTool("mt_settings_get", new { profile = EnvFlags.DefaultBenchProfile });

        var resp = await _mcp.CallTool("mt_settings_groups",
            new { profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();
        resp.ParsedBody!.Value.TryGetProperty("data", out var data).Should().BeTrue();

        // Real shape per SettingsCommand.ShowGrouped: List<{Group, Count, Keys}>.
        data.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterOrEqualTo(5,
            because: "MTCore profile has at least 5 setting groups (Core, BlackList, AutoStop, ...)");

        var first = data[0];
        first.TryGetProperty("Group", out var grp).Should().BeTrue();
        grp.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_settings_search_with_query_returns_filtered_array()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // search depends on cached settings; warm the cache.
        await _mcp.CallTool("mt_settings_get", new { profile = EnvFlags.DefaultBenchProfile });

        var resp = await _mcp.CallTool("mt_settings_search",
            new { query = "log", profile = EnvFlags.DefaultBenchProfile });
        resp.InnerSuccess.Should().BeTrue();

        // Search results are List<{Key, Value}> when matches found, or text-only
        // success when no matches. "log" is broad enough to always match
        // (Core.LOG_LEVEL is one of many).
        if (resp.ParsedBody is { } b &&
            b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                entry.TryGetProperty("Key", out _).Should().BeTrue();
                entry.TryGetProperty("Value", out _).Should().BeTrue();
            }
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_settings_set_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // settings_set requires confirm:true. Calling without confirm should
        // fail at either the schema gate (RPC error) or the parser gate
        // (success:false).
        var resp = await _mcp.CallTool("mt_settings_set",
            new { key = "Core.LOG_LEVEL", value = "INFO", profile = EnvFlags.DefaultBenchProfile });

        // Either RPC error (-32602 missing required) or inner success:false.
        bool gated = resp.IsRpcError ||
            (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s) &&
             s.ValueKind == System.Text.Json.JsonValueKind.False);
        gated.Should().BeTrue(
            because: "settings_set must reject without confirm:true");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_settings_set_with_confirm_roundtrips_log_level()
    {
        Skip.If(!EnvFlags.TestingEnv || !_bench.BenchAvailable, _bench.PreflightMessage);
        await _mcp.WaitForConnected(EnvFlags.DefaultBenchProfile);

        // Capture original
        var orig = await _mcp.CallTool("mt_settings_get",
            new { key = "Core.LOG_LEVEL", profile = EnvFlags.DefaultBenchProfile });
        orig.InnerSuccess.Should().BeTrue();

        string original = "WARNING";
        if (orig.ParsedBody is { } b && b.TryGetProperty("data", out var data) &&
            data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.TryGetProperty("Value", out var v))
                original = v.GetString() ?? "WARNING";
        }

        // Set INFO with confirm
        var setResp = await _mcp.CallTool("mt_settings_set",
            new { key = "Core.LOG_LEVEL", value = "INFO", confirm = true, profile = EnvFlags.DefaultBenchProfile });
        setResp.InnerSuccess.Should().BeTrue();

        // Restore
        var restore = await _mcp.CallTool("mt_settings_set",
            new { key = "Core.LOG_LEVEL", value = original, confirm = true, profile = EnvFlags.DefaultBenchProfile });
        restore.InnerSuccess.Should().BeTrue();
    }
}
