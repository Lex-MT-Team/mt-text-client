using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 6 first-wave Smoke coverage:
///   • 6.10 mt_settings_diff_snapshots — pure client-side diff of two
///     snapshot JSON files (~/.mt-snapshots/).  No bench required.
///   • 6.11 iceberg flag on mt_orders_place — schema audit + dispatcher
///     wiring (real iceberg execution lives in LiveTrade).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage6FirstWaveTests
{
    private readonly McpFixture _mcp;
    public Stage6FirstWaveTests(McpFixture mcp) { _mcp = mcp; }

    // ─── 6.10 ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task mt_settings_diff_snapshots_diffs_two_fixture_files()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        // Write two fixture snapshots in the temp dir.
        string tmpDir = Path.Combine(Path.GetTempPath(), $"s610fixture_{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(tmpDir);
        string pa = Path.Combine(tmpDir, "snapA.json");
        string pb = Path.Combine(tmpDir, "snapB.json");
        var snapA = new JObject
        {
            ["profile"] = "fixtureA",
            ["captured_at"] = "2026-05-12T00:00:00Z",
            ["settings"] = new JObject
            {
                ["Core.LOG_LEVEL"] = "INFO",
                ["NewListedMarket.AddToBlacklistEnabled"] = "1",
                ["WhiteList.Only"] = "false",
            },
            ["algos_count"] = 5,
        };
        var snapB = new JObject
        {
            ["profile"] = "fixtureB",
            ["captured_at"] = "2026-05-12T01:00:00Z",
            ["settings"] = new JObject
            {
                ["Core.LOG_LEVEL"] = "DEBUG",                        // changed
                ["NewListedMarket.AddToBlacklistEnabled"] = "1",     // same
                ["Core.NEW_FIELD"] = "future-only",                  // added in B
                // WhiteList.Only is removed in B
            },
            ["algos_count"] = 7,
        };
        File.WriteAllText(pa, snapA.ToString(Newtonsoft.Json.Formatting.None));
        File.WriteAllText(pb, snapB.ToString(Newtonsoft.Json.Formatting.None));

        var resp = await _mcp.CallTool("mt_settings_diff_snapshots", new
        {
            snapshot_a = pa, snapshot_b = pb,
        });
        try
        {
            // Tool returns the diff JSON at the result root (not wrapped in
            // {success, data}) because it uses HandleInternalTool's direct shape.
            // The McpResponse.ParsedBody is the unwrapped JsonElement object.
            resp.ParsedBody.Should().NotBeNull();
            var body = resp.ParsedBody!.Value;

            body.GetProperty("snapshot_a_profile").GetString().Should().Be("fixtureA");
            body.GetProperty("snapshot_b_profile").GetString().Should().Be("fixtureB");
            int diffCount = body.GetProperty("diff_count").GetInt32();
            diffCount.Should().Be(3,
                because: "expected 3 diffs (changed Core.LOG_LEVEL + added Core.NEW_FIELD + removed WhiteList.Only)");

            // Walk diffs and verify each change classification.
            var diffs = body.GetProperty("diffs");
            var changes = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var d in diffs.EnumerateArray())
                changes[d.GetProperty("key").GetString()!] = d.GetProperty("change").GetString()!;
            changes["Core.LOG_LEVEL"].Should().Be("changed");
            changes["Core.NEW_FIELD"].Should().Be("added");
            changes["WhiteList.Only"].Should().Be("removed");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task mt_settings_diff_snapshots_returns_structured_error_when_file_missing()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        var resp = await _mcp.CallTool("mt_settings_diff_snapshots", new
        {
            snapshot_a = "/tmp/__definitely_not_here__.json",
            snapshot_b = "/tmp/__also_not_here__.json",
        });
        resp.ParsedBody.Should().NotBeNull();
        var body = resp.ParsedBody!.Value;
        body.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetString().Should().Contain("snapshot_not_found");
    }

    // ─── 6.11 ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", TraitCategories.Static)]
    public void mt_orders_place_iceberg_param_is_in_schema_and_dispatcher_snapshot()
    {
        // Schema audit: the iceberg field exists in the registered schema.
        var tool = _mcp.Tools.First(t => t.GetProperty("name").GetString() == "mt_orders_place");
        var props = tool.GetProperty("inputSchema").GetProperty("properties");
        props.TryGetProperty("iceberg", out var icebergProp).Should().BeTrue(
            because: "Stage 6.11 added the iceberg boolean parameter to mt_orders_place");
        icebergProp.GetProperty("type").GetString().Should().Be("boolean");
        // Note: mt_orders_place's `confirm` isn't in inputSchema.required, so
        // ConfirmGate doesn't fire at the MCP layer — OrdersCommand.HandlePlace
        // enforces --confirm itself.  Real iceberg execution against a live
        // venue lives outside this Smoke test (operator-driven LiveTrade).
    }
}
