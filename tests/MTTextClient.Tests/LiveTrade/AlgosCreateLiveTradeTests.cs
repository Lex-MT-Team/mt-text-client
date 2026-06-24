using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// LiveTrade — real create-via-clone of a SHOTS algorithm on bench_02 BINANCE.
/// Dry-run preview first, then commit, then verify the new row appears in
/// <c>mt_algos_list</c> with the metadata stamp, then delete (the only
/// cleanup we do here — every other create artifact is preserved per the
/// bench-data-retention policy).  Writes a structured JSON artifact to
/// <c>~/mt-test-artifacts/mt_algos_create/&lt;profile&gt;_&lt;ts&gt;.json</c>.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class AlgosCreateLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosCreateLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task DryRun_ThenCommit_ThenVerify_ThenDelete()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — mt_algos_create LiveTrade mutates real algorithm rows.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // Baseline algo IDs.  We diff against this set after the commit to
        // identify the new algo — MTCore re-computes the name server-side from
        // the algorithm's `namingRule` parameter (e.g. for SHOTS:
        // `SG_##MTYPE##_##SIDE##_##DIST##_##BUFF##_tp##TP##_sl##SL##`), so the
        // caller-supplied `new_name` doesn't survive the SAVE round-trip on
        // every algo type.  ID-diff is the reliable identification path.
        var beforeIds = await ListAlgoIds();
        int before = beforeIds.Count;
        Skip.If(before <= 0, "Bench has no algos to use as clone source");

        string uniqueName = $"algocreate_lt_{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var artifact = new
        {
            Stage = "mt_algos_create",
            Profile,
            BaselineAlgoCount = before,
            NewName = uniqueName,
            StartedAtUtc = System.DateTime.UtcNow,
        };

        // 1) DRY RUN — must not mutate.
        var dry = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            preset_name = "SG",
            new_name = uniqueName,
            overrides_json = "{\"autoStart\":\"OFF\"}",
            confirm = true,
            // no_dry_run omitted → DRY RUN
        });
        dry.IsRpcError.Should().BeFalse();
        dry.InnerSuccess.Should().BeTrue(because: "dry-run preview: " + dry.InnerMessage);
        var dryData = dry.ParsedBody!.Value.GetProperty("data");
        dryData.GetProperty("DryRun").GetBoolean().Should().BeTrue();
        dryData.GetProperty("SchemaVersion").GetString().Should().Be("algo-create-v1");
        // No wire SAVE => count unchanged.
        (await CountAlgos()).Should().Be(before, because: "dry-run must not change algo count");

        // 2) COMMIT — no_dry_run + confirm.
        var commit = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            preset_name = "SG",
            new_name = uniqueName,
            overrides_json = "{\"autoStart\":\"OFF\"}",
            no_dry_run = true,
            confirm = true,
        });
        commit.IsRpcError.Should().BeFalse();
        commit.InnerSuccess.Should().BeTrue(because: "commit must succeed: " + commit.InnerMessage);

        // 3) VERIFY — the new algo appears in mt_algos_list.  Identify it by
        // ID-diff (MTCore re-computes the name from `namingRule`, so we can't
        // rely on name equality for SHOTS-family algos).
        await Task.Delay(1500);  // brief settle for the bench to write the row.
        var afterIds = await ListAlgoIds();
        afterIds.Count.Should().Be(before + 1, because: "commit must add exactly one algorithm row");
        var newIds = afterIds.Except(beforeIds).ToList();
        newIds.Count.Should().Be(1, because: $"exactly one new algorithm ID expected; beforeIds={beforeIds.Count} afterIds={afterIds.Count}");
        long newId = newIds[0];

        // 4) CLEAN WIRE ARGS — issue #44.  The new algo's argsJson must NOT
        // carry any synthetic client metadata: MTCore 0.7.24554's argument
        // parser rejects unknown keys (e.g. the old `_mcp_metadata` block),
        // making the algorithm unstartable. The wire arguments must contain only
        // real algorithm parameters.
        var cfgResp = await _mcp.CallTool("mt_algos_config", new
        {
            id = newId.ToString(), profile = Profile,
        });
        cfgResp.InnerSuccess.Should().BeTrue();
        // mt_algos_config flattens Arguments → 'data' array of {Key, Value, ...}.
        foreach (var p in cfgResp.ParsedBody!.Value.GetProperty("data").EnumerateArray())
            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("Key", out var k))
                k.GetString().Should().NotStartWith("_mcp",
                    because: "synthetic client metadata in wire args breaks start on 0.7.24554 (#44)");

        // 5) WRITE ARTIFACT.
        await WriteArtifact(new
        {
            artifact.Stage, artifact.Profile, artifact.BaselineAlgoCount, artifact.NewName,
            artifact.StartedAtUtc,
            EndedAtUtc = System.DateTime.UtcNow,
            NewAlgoId = newId,
            WireArgsClean = true, // no _mcp_metadata in argsJson (issue #44)
            FinalAlgoCount = before + 1,
            CrudPath = "create_clone_from_source(SHOTS+SG) → SAVE → verify (ID-diff) → delete",
            SchemaVersion = "algo-create-v1",
        });

        // 6) CLEANUP — delete this LiveTrade's newly-created row to keep the
        // bench algo store from accreting test rows over time.  All other
        // creations (paste-from-clipboard etc.) follow no-cleanup.  This
        // test deletes its OWN creation by design.
        var del = await _mcp.CallTool("mt_algos_delete", new
        {
            id = newId.ToString(),
            confirm = true,
            profile = Profile,
        });
        del.InnerSuccess.Should().BeTrue(because: "delete cleanup: " + del.InnerMessage);
        (await CountAlgos()).Should().Be(before, because: "delete must restore the baseline count");
    }

    private async Task<int> CountAlgos()
    {
        var resp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (!resp.InnerSuccess) return -1;
        var body = resp.ParsedBody;
        if (body is not { } b || !b.TryGetProperty("data", out var data)) return -1;
        return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : -1;
    }

    private async Task<HashSet<long>> ListAlgoIds()
    {
        var ids = new HashSet<long>();
        var resp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (!resp.InnerSuccess) return ids;
        if (resp.ParsedBody is not { } b || !b.TryGetProperty("data", out var data)) return ids;
        if (data.ValueKind != JsonValueKind.Array) return ids;
        foreach (var a in data.EnumerateArray())
        {
            if (a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                ids.Add(id.GetInt64());
        }
        return ids;
    }

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "mt_algos_create");
        Directory.CreateDirectory(dir);
        string fname = $"bench_02_{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            System.Text.Json.JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
