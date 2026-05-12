using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// Campaign tier L — reports persistence (5 tools):
///   mt_reports_store, mt_reports_load, mt_reports_delete,
///   mt_reports_export, mt_reports_fleet_export.
///
/// These tools work on the LOCAL stored-report registry (~/.mt-reports/
/// store, plus CSV export paths).  They depend on a recent
/// mt_reports_query result; we drive one against bench_02 first to seed
/// the in-memory registry, then exercise the persistence surface.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Campaign_L_ReportsPersistenceTests
{
    private const string Letter = "L";
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;

    public Campaign_L_ReportsPersistenceTests(McpFixture mcp, BenchFixture bench)
    { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task ExerciseReportsPersistence()
    {
        Skip.IfNot(EnvFlags.LiveTrades, "MTC_LIVE_TRADES=1 not set.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"{Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        bool connected = await _mcp.WaitForConnected(Profile, 60);
        if (!connected)
        {
            await Task.Delay(2000);
            connected = await _mcp.WaitForConnected(Profile, 60);
        }
        if (!connected)
        {
            CampaignEvidence.RecordBlocker(Letter, "mt_reports_store",
                "bench_02 did not reach CONNECTED in 2×60s; campaign skipped.");
            return;
        }

        // Warm the reports cache by running a wide query.
        await _mcp.CallTool("mt_reports_query",
            new { period = "30d", max_rows = 200, profile = Profile },
            TimeSpan.FromSeconds(90));

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string storedName = $"campaignL_{ts}";

        // store
        await CampaignEvidence.Probe(_mcp, Letter, "mt_reports_store",
            new { name = storedName, period = "30d", profile = Profile },
            profile: Profile, timeout: TimeSpan.FromSeconds(90));
        // load
        await CampaignEvidence.Probe(_mcp, Letter, "mt_reports_load",
            new { name = storedName });
        // delete
        await CampaignEvidence.Probe(_mcp, Letter, "mt_reports_delete",
            new { name = storedName });

        // export (single profile) and fleet_export (all connected)
        string exportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"campaignL_export_{ts}.csv");
        await CampaignEvidence.Probe(_mcp, Letter, "mt_reports_export",
            new { period = "30d", path = exportPath, profile = Profile },
            profile: Profile, timeout: TimeSpan.FromSeconds(90));

        string fleetExportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"campaignL_fleet_export_{ts}.csv");
        await CampaignEvidence.Probe(_mcp, Letter, "mt_reports_fleet_export",
            new { period = "30d", path = fleetExportPath },
            timeout: TimeSpan.FromSeconds(120));
    }
}
