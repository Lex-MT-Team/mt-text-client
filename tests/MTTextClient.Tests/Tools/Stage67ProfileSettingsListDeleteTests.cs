using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 6.7 — Smoke coverage for mt_profile_settings_list + _delete.
///
/// Discovery findings (recorded for posterity):
///   • MTShared exposes <c>SendGetCurrentProfileSettingsRequest</c>,
///     <c>SendGetProfileSettingsRequest(profileName)</c>, and
///     <c>SendUpdateProfileSettingsRequest(profileName, updated, deleted)</c>.
///   • There is NO list-named-profiles RPC.  The string 'RemoveProfile'
///     appears in the binary but is not a Send*Request wire method.
///   • UpdateProfileSettings already accepts a <c>deleted: HashSet&lt;string&gt;</c>
///     parameter — the previous <c>mt_profile_settings_update</c> schema only
///     exposed <c>updates_json</c>, so deletes had no MCP entry point until
///     this sub-stage.
///   • MCP-010-ext schema fix referenced in the plan is HISTORIC — already
///     applied (mt_profile_settings_update has <c>confirm</c> in
///     <c>inputSchema.required</c>, verified by ConfirmGateStaticTests).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage67ProfileSettingsListDeleteTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage67ProfileSettingsListDeleteTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_profile_settings_list_returns_sorted_keys()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile });
        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue();

        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.GetProperty("KeyCount").GetInt32().Should().BeGreaterThan(0,
            because: "a connected bench's profile settings store must carry at least one key");
        var keys = data.GetProperty("Keys");
        keys.ValueKind.Should().Be(JsonValueKind.Array);
        keys.GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task mt_profile_settings_list_grep_filter_narrows_results()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Probe-finding: bench_02 carries 19 profile-settings keys, several of
        // which are BlackList.* (FirstInitialization, MarketTypes, Quotes,
        // Symbols).  Use BlackList as the substring — it's the most stable
        // grep target across bench builds.
        var full = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile });
        int fullCount = full.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();

        var filtered = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile, grep = "BlackList" });
        int filteredCount = filtered.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();
        filteredCount.Should().BeLessThan(fullCount, because: "grep=BlackList should be a proper subset of all keys");
        filteredCount.Should().BeGreaterThan(0, because: "BlackList.* keys are seeded on every bench");
    }

    [SkippableFact]
    public async Task mt_profile_settings_delete_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_profile_settings_delete", new
        {
            keys = "NonExistent.Sentinel.Key",
            profile = Profile,
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate must reject a confirm-required tool when confirm is omitted");
    }

    [SkippableFact]
    public async Task mt_profile_settings_delete_all_nonexistent_keys_returns_structured_not_found()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_profile_settings_delete", new
        {
            keys = "Sentinel.A,Sentinel.B,Sentinel.C",
            confirm = true,
            profile = Profile,
        });
        resp.InnerSuccess.Should().BeFalse(
            because: "all-keys-absent must surface as a top-level structured failure");
        resp.InnerMessage.Should().NotBeNull();
        resp.InnerMessage!.Should().Contain("not_found");
    }

    [SkippableFact]
    public async Task mt_profile_settings_add_list_delete_round_trip_restores_baseline()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Sentinel that won't collide with real settings.
        string sentinelKey = $"Stage67.SmokeSentinel.{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // 1) ADD via the existing update tool.
        var addResp = await _mcp.CallTool("mt_profile_settings_update", new
        {
            profile_name = Profile,
            updates_json = $"{{\"{sentinelKey}\":\"smoke\"}}",
            confirm = true,
            profile = Profile,
        });
        addResp.InnerSuccess.Should().BeTrue(because: "add via update: " + addResp.InnerMessage);

        // 2) LIST grep — the sentinel must surface.
        var listResp = await _mcp.CallTool("mt_profile_settings_list", new
        {
            profile = Profile,
            grep = "Stage67.SmokeSentinel",
        });
        listResp.InnerSuccess.Should().BeTrue();
        int hits = listResp.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();
        hits.Should().BeGreaterThan(0, because: "sentinel must be visible via grep after add");

        // 3) DELETE the sentinel.
        var delResp = await _mcp.CallTool("mt_profile_settings_delete", new
        {
            keys = sentinelKey,
            confirm = true,
            profile = Profile,
        });
        delResp.IsRpcError.Should().BeFalse();
        delResp.InnerSuccess.Should().BeTrue(because: "delete: " + delResp.InnerMessage);
        var delData = delResp.ParsedBody!.Value.GetProperty("data");
        delData.GetProperty("Deleted").GetArrayLength().Should().Be(1,
            because: "exactly one key should have been deleted");

        // 4) LIST grep — sentinel gone.
        var post = await _mcp.CallTool("mt_profile_settings_list", new
        {
            profile = Profile,
            grep = "Stage67.SmokeSentinel",
        });
        int postHits = post.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();
        postHits.Should().BeLessThan(hits, because: "delete must remove the sentinel");
    }
}
