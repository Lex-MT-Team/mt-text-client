using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier H — TPSL mutator surface on bench_02 (7 tools):
///   mt_tpsl_cancel, mt_tpsl_cancel_many, mt_tpsl_split,
///   mt_tpsl_split_many, mt_tpsl_join, mt_tpsl_panic, mt_tpsl_panic_many.
///
/// Strategy: subscribe to TPSL, list, exercise verbs against the first
/// (and second, if available) TPSL id.  If no TPSL exists the verbs surface
/// a structured "not_found" — that is still real-wire evidence.  Bulk
/// variants exercise the loop wrapper.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_H_TpslMutatorsTests
{
    private const string Letter = "H";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_H_TpslMutatorsTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseTpslMutators()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        await _mcp.CallTool("mt_tpsl_subscribe", new { profile = Profile });
        await Task.Delay(2000);
        var ids = await DiscoverTpslIdsAsync();

        if (ids.Count == 0)
        {
            const string reason =
                "No TPSL positions present on bench_02; tpsl_id-dependent mutators are exercised " +
                "against a synthetic id to surface the real structured 'not_found' response — that " +
                "still counts as real-wire evidence.";
            // Probe with synthetic id so each tool's wire path runs end-to-end.
            await Probe("mt_tpsl_cancel",
                new { id = "0", confirm = true, profile = Profile }, reason);
            await Probe("mt_tpsl_cancel_many",
                new { tpsl_ids = new[] { "0", "0" }, confirm = true, profile = Profile }, reason);
            await Probe("mt_tpsl_split",
                new { tpsl_id = "0", confirm = true, profile = Profile }, reason);
            await Probe("mt_tpsl_split_many",
                new { tpsl_ids = new[] { "0", "0" }, confirm = true, profile = Profile }, reason);
            await Probe("mt_tpsl_join",
                new { tpsl_ids = new[] { "0", "0" }, confirm = true, profile = Profile }, reason);
            // panic / panic_many are destructive — probed with synthetic id only.
            await Probe("mt_tpsl_panic",
                new { tpsl_id = "0", confirm = true, profile = Profile }, reason);
            await Probe("mt_tpsl_panic_many",
                new { tpsl_ids = new[] { "0", "0" }, confirm = true, profile = Profile }, reason);
            return;
        }

        string first = ids[0];
        // ── split first, then join, then cancel ──
        await Probe("mt_tpsl_split",
            new { tpsl_id = first, confirm = true, profile = Profile });
        await Task.Delay(2000);

        // Re-discover ids after the split.
        var idsAfterSplit = await DiscoverTpslIdsAsync();
        if (idsAfterSplit.Count >= 2)
        {
            await Probe("mt_tpsl_split_many",
                new { tpsl_ids = idsAfterSplit.GetRange(0, Math.Min(2, idsAfterSplit.Count)).ToArray(),
                      confirm = true, profile = Profile });
            await Probe("mt_tpsl_join",
                new { tpsl_ids = idsAfterSplit.GetRange(0, Math.Min(2, idsAfterSplit.Count)).ToArray(),
                      confirm = true, profile = Profile });
        }

        // ── panic on a synthetic id (we don't want to MARKET-close real positions
        //    on bench_02 unless retention-policy demanded it) — touches the wire ──
        await Probe("mt_tpsl_panic",
            new { tpsl_id = "0", confirm = true, profile = Profile },
            "synthetic id — exercises the wire without market-closing a real position");
        await Probe("mt_tpsl_panic_many",
            new { tpsl_ids = new[] { "0", "0" }, confirm = true, profile = Profile },
            "synthetic ids — exercises the wire without market-closing real positions");

        // ── cancel + cancel_many on the original real id (drops the TPSL, but the
        //    underlying position remains; the bench data-retention policy is fine
        //    with this because the FILLED trade is still in Firebird) ──
        await Probe("mt_tpsl_cancel",
            new { id = first, confirm = true, profile = Profile });
        if (ids.Count >= 2)
        {
            await Probe("mt_tpsl_cancel_many",
                new { tpsl_ids = ids.GetRange(1, ids.Count - 1).ToArray(),
                      confirm = true, profile = Profile });
        }
        else
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_tpsl_cancel_many",
                "Only one real TPSL id available; cancel_many real-id loop not exercised this run.");
        }
    }

    private Task<McpResponse?> Probe(string tool, object args, string? note = null)
        => CampaignEvidence.Probe(_mcp, Letter, tool, args, profile: Profile, note: note);

    private async Task<List<string>> DiscoverTpslIdsAsync()
    {
        var resp = await _mcp.CallTool("mt_tpsl_list", new { profile = Profile });
        var ids = new List<string>();
        if (resp.ParsedBody is { } body &&
            body.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                if (row.TryGetProperty("id", out var id))
                {
                    var s = id.ValueKind == JsonValueKind.Number
                        ? id.GetInt64().ToString()
                        : id.GetString();
                    if (!string.IsNullOrEmpty(s)) ids.Add(s!);
                }
            }
        }
        return ids;
    }
}
