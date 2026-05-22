using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Profile-settings CRUD LiveTrade — full add + list + delete round-trip on
/// bench_02 BINANCE.  Writes a structured JSON artifact recording the
/// sentinel key, initial baseline count, post-add count, post-delete count.
/// No cleanup beyond the test's own delete (the only mutation introduced is
/// the sentinel, and it's removed before the test ends).
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class ProfileSettingsCrudLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public ProfileSettingsCrudLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task Add_List_Delete_RestoresBaseline()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade mutates profile settings.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // Baseline.
        var listBefore = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile });
        listBefore.InnerSuccess.Should().BeTrue();
        int baseCount = listBefore.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();

        // 1) Add a sentinel.
        string sentinel = $"ProfileSettings.LiveTradeSentinel.{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var addResp = await _mcp.CallTool("mt_profile_settings_update", new
        {
            profile_name = Profile,
            updates_json = $"{{\"{sentinel}\":\"livetrade-roundtrip\"}}",
            confirm = true,
            profile = Profile,
        });
        addResp.InnerSuccess.Should().BeTrue(because: "add via existing update: " + addResp.InnerMessage);

        // 2) List shows it.
        var listAfterAdd = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile });
        int afterAddCount = listAfterAdd.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();
        afterAddCount.Should().Be(baseCount + 1,
            because: $"add must grow the key count by 1 (before={baseCount} after={afterAddCount})");

        // Verify the sentinel is among the listed keys.
        bool sentinelInList = false;
        foreach (var k in listAfterAdd.ParsedBody.Value.GetProperty("data").GetProperty("Keys").EnumerateArray())
            if (k.GetString() == sentinel) { sentinelInList = true; break; }
        sentinelInList.Should().BeTrue(because: "the sentinel must appear in the listed keys");

        // 3) Delete via new tool.
        var delResp = await _mcp.CallTool("mt_profile_settings_delete", new
        {
            keys = sentinel,
            confirm = true,
            profile = Profile,
        });
        delResp.IsRpcError.Should().BeFalse();
        delResp.InnerSuccess.Should().BeTrue(because: "delete: " + delResp.InnerMessage);

        // 4) Baseline restored.
        var listFinal = await _mcp.CallTool("mt_profile_settings_list", new { profile = Profile });
        int finalCount = listFinal.ParsedBody!.Value.GetProperty("data").GetProperty("KeyCount").GetInt32();
        finalCount.Should().Be(baseCount,
            because: $"delete must restore the baseline count (before={baseCount} final={finalCount})");

        // 5) Artifact.
        await WriteArtifact(new
        {
            Scenario = "ProfileSettingsCrud",
            Profile,
            Sentinel = sentinel,
            BaselineKeyCount = baseCount,
            AfterAddKeyCount = afterAddCount,
            FinalKeyCount = finalCount,
            CrudPath = "update(add) → list grep → delete → list verify baseline",
            EndedAtUtc = System.DateTime.UtcNow,
        });
    }

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "profile-settings-crud");
        Directory.CreateDirectory(dir);
        string fname = $"bench_02_{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
