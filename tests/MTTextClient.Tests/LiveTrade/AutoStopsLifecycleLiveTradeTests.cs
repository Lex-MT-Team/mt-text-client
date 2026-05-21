using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// AutoStops lifecycle LiveTrade — exercises the full <c>mt_autostops_*</c>
/// CRUD lifecycle against the real bench_02 BINANCE profile (the only
/// consistently-alive bench in practice).
///
/// <para>
/// <b>POLICY</b> — gated by <c>MTC_LIVE_TRADES=1</c> AND
/// <c>MTC_TESTING_ENV=1</c>.  Run it explicitly via:
/// </para>
/// <code>
/// MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
///     dotnet test -c Release --filter "Category=LiveTrade&amp;DisplayName~AutoStopsLifecycle"
/// </code>
/// <para>
/// <b>CRUD CONTRACT</b> (must always pass): add → start → stop → start (the
/// check-and-restart leg) → delete.  Each step asserts an MTCore success and
/// the list-derived state transition.
/// </para>
/// <para>
/// <b>TRIGGER VALIDATION</b> (best-effort): a tight balance filter (min=-0.01)
/// is armed; the test polls <c>mt_autostops_list</c> for the filter's enabled
/// state to flip OFF (the documented MTCore behaviour after a trigger fires:
/// the autostop disables itself).  If no trigger occurs within
/// <see cref="TriggerPollSeconds"/> seconds we record the absence and proceed
/// with the CRUD lifecycle — current account state may not show the necessary
/// unrealised loss to fire a balance autostop.
/// </para>
/// <para>
/// <b>NO CLEANUP OF POSITIONS</b> — every open BTCUSDT FUTURES position from
/// earlier LiveTrade runs remains untouched.  Only the autostop filter we add
/// is deleted at the end of the test; the position book is foundational seed
/// data for reports tooling.
/// </para>
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
// Trigger observation is best-effort. The class-level default declares
// "TriggerProven=false" so the evidence matrix can distinguish CRUD-only
// proof from full-trigger-fire proof. The per-run JSON artifact carries the
// actual observed boolean; this trait declares the CONTRACT (the test will
// never claim trigger was proven if it wasn't observed).
[Trait("TriggerProven", "false")]
public sealed class AutoStopsLifecycleLiveTradeTests
{
    private const string Profile = "bench_02";
    private const string Exchange = "BINANCE";
    private const int TriggerPollSeconds = 90;

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AutoStopsLifecycleLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task Add_Start_Trigger_CheckRestart_Stop_Delete_FullLifecycle()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade mutates autostop settings on bench_02.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile),
            $"Bench {Profile} ({Exchange}) not observed on UDP port; skipping.");

        // Per-test subprocess isolation.
        await _mcp.RestartSubprocessAsync();
        bool connected = await _mcp.WaitForConnected(Profile, firstAttemptSeconds: 60);
        connected.Should().BeTrue(because: $"bench {Profile} must reach CONNECTED");

        // 0) Baseline filter count (the bench may already carry filters from
        //    earlier activity — capture the starting state so the post-test
        //    delete restores it exactly).
        var listBefore = await _mcp.CallTool("mt_autostops_list", new { profile = Profile });
        listBefore.InnerSuccess.Should().BeTrue(
            because: $"baseline mt_autostops_list must succeed; got: {listBefore.InnerMessage}");
        int baselineCount = ReadFilterCount(listBefore.ParsedBody);

        // 1) ADD a balance autostop with a deliberately tight min — if any
        //    open position has unrealised loss below -0.01 USDT the filter
        //    should fire on the next evaluation cycle.
        var addResp = await _mcp.CallTool("mt_autostops_add", new
        {
            max_loss = "-0.01",
            value_max = "1000000",
            filter_type = "GLOBAL_BY_SYMBOL",
            source_type = "VALUE",
            market = "FUTURES",
            timeframe_ms = "3600000",  // 1h window — tight enough to catch recent unrealised PnL
            pause_algo = true,
            confirm = true,
            profile = Profile,
        });
        addResp.IsRpcError.Should().BeFalse(
            because: "the dispatcher must route mt_autostops_add to OrdersCommand; got " + (addResp.InnerMessage ?? "<null>"));
        addResp.InnerSuccess.Should().BeTrue(
            because: $"mt_autostops_add must succeed on a connected bench; got: {addResp.InnerMessage}");
        int newIdx = ReadIndex(addResp.ParsedBody);
        newIdx.Should().BeGreaterOrEqualTo(0);

        // 2) START — flip the filter's isEnabled true (CRUD ACTIVE transition).
        var startResp = await _mcp.CallTool("mt_autostops_start", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile = Profile,
        });
        startResp.IsRpcError.Should().BeFalse();
        startResp.InnerSuccess.Should().BeTrue(because: "start: " + startResp.InnerMessage);

        // 3) Confirm the local list reports the filter enabled.
        var listActive = await _mcp.CallTool("mt_autostops_list", new { profile = Profile });
        listActive.InnerSuccess.Should().BeTrue();
        bool isEnabled = ReadFilterEnabled(listActive.ParsedBody, newIdx);
        isEnabled.Should().BeTrue(because: "after start the filter must report enabled=true in the list response");

        // 4) Best-effort TRIGGER observation — Binance balance autostops fire
        //    when the filter's evaluated value (here: GLOBAL_BY_SYMBOL VALUE
        //    over a 1h window) falls outside [min,max].  When a trigger fires
        //    MTCore disables the filter (isEnabled flips back to false) and
        //    optionally panic-sells matching positions.  We polled
        //    pause_algo=true so positions aren't auto-closed; we only need to
        //    observe the filter-disabled-by-MTCore signal.
        bool observedTrigger = false;
        for (int i = 0; i < TriggerPollSeconds; i++)
        {
            await Task.Delay(1000);
            var pollResp = await _mcp.CallTool("mt_autostops_list", new { profile = Profile });
            if (!pollResp.InnerSuccess) continue;
            if (!ReadFilterEnabled(pollResp.ParsedBody, newIdx))
            {
                observedTrigger = true;
                break;
            }
        }

        if (observedTrigger)
        {
            // 5) CHECK-AND-RESTART — re-arm the filter.  The fact that start
            //    succeeds after a trigger is the proof the algorithm actually
            //    ran (analogous to a fill confirming an order tool worked).
            var reArm = await _mcp.CallTool("mt_autostops_start", new
            {
                index = newIdx.ToString(),
                confirm = true,
                profile = Profile,
            });
            reArm.IsRpcError.Should().BeFalse();
            reArm.InnerSuccess.Should().BeTrue(because: "check-and-restart: " + reArm.InnerMessage);

            // Reports query — confirms MTCore registered the trigger.  Empty
            // arrays are acceptable on cold/empty Firebird; the assertion is on
            // the dispatcher being clean.
            var reportsResp = await _mcp.CallTool("mt_autostops_reports", new
            {
                profile = Profile,
            }, timeout: TimeSpan.FromSeconds(35));
            reportsResp.IsRpcError.Should().BeFalse();
            // We DO NOT hard-assert reports has rows — Firebird population is
            // venue-/timing-dependent.  Soft-check only.
        }

        // 6) STOP the filter — flips isEnabled false unconditionally.
        var stopResp = await _mcp.CallTool("mt_autostops_stop", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile = Profile,
        });
        stopResp.IsRpcError.Should().BeFalse();
        stopResp.InnerSuccess.Should().BeTrue(because: "stop: " + stopResp.InnerMessage);

        // 7) DELETE the filter — removes it from the list, restoring baseline.
        var delResp = await _mcp.CallTool("mt_autostops_delete", new
        {
            index = newIdx.ToString(),
            confirm = true,
            profile = Profile,
        });
        delResp.IsRpcError.Should().BeFalse();
        delResp.InnerSuccess.Should().BeTrue(because: "delete: " + delResp.InnerMessage);

        // 8) Final list — must shrink back to the baseline count.
        var listFinal = await _mcp.CallTool("mt_autostops_list", new { profile = Profile });
        listFinal.InnerSuccess.Should().BeTrue();
        int finalCount = ReadFilterCount(listFinal.ParsedBody);
        finalCount.Should().Be(baselineCount,
            because: "the CRUD lifecycle must leave the autostop list at the same size it found");

        // Two-branch outcome.  Trigger observation is best-effort by design —
        // current account state may not provide unrealised loss big enough to
        // fire a balance autostop within the poll window — but the test must
        // never claim trigger proof when it didn't observe one.
        //
        // Both branches PASS the test (CRUD lifecycle IS the hard contract),
        // but they emit different evidence and write distinct artifact rows.
        await WriteAutoStopsLifecycleArtifact(observedTrigger, baselineCount, finalCount);
        if (observedTrigger)
        {
            // PASS — trigger fired and check-and-restart succeeded.
            // The earlier branch in this test re-armed the filter; that is
            // the cross-check that the algorithm actually ran.
            // Recorded as TriggerProven=true in the artifact.
        }
        else
        {
            // PASS — CRUD lifecycle proven; trigger not observed within window.
            // Recorded as TriggerProven=false in the artifact, matching the
            // [Trait("TriggerProven", "false")] declared at class level.
        }
    }

    private async Task WriteAutoStopsLifecycleArtifact(bool triggerProven, int baselineCount, int finalCount)
    {
        var record = new
        {
            Scenario = "AutoStopsLifecycle",
            Profile = Profile,
            Exchange = Exchange,
            TriggerProven = triggerProven,
            TriggerPollSeconds = TriggerPollSeconds,
            BaselineFilterCount = baselineCount,
            FinalFilterCount = finalCount,
            CrudIdempotent = finalCount == baselineCount,
            Note = triggerProven
                ? "trigger fired — confirmed by autostop self-disable + successful re-arm (check-and-restart)"
                : "trigger not observed within window (best-effort) — CRUD lifecycle proven, trigger deferred",
            EndedAtUtc = DateTime.UtcNow,
        };
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "autostops-lifecycle");
        Directory.CreateDirectory(dir);
        string fname = $"{Profile}_{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ReadFilterCount(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return -1;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return -1;
        if (!data.TryGetProperty("BalanceFilterCount", out var c)) return -1;
        return c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
    }

    private static int ReadIndex(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return -1;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return -1;
        if (!data.TryGetProperty("Index", out var i)) return -1;
        return i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
    }

    private static bool ReadFilterEnabled(JsonElement? body, int idx)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return false;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty("Filters", out var filters) || filters.ValueKind != JsonValueKind.Array) return false;
        if (idx < 0 || idx >= filters.GetArrayLength()) return false;
        var f = filters[idx];
        if (f.ValueKind != JsonValueKind.Object) return false;
        return f.TryGetProperty("Enabled", out var e) && e.ValueKind == JsonValueKind.True;
    }
}
