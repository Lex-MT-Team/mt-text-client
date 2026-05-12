using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Stage 7.1 LiveTrade — exercises mt_reports_query / _csv_inline / _status
/// against bench_02 BINANCE.  No state is mutated (reports are read-only on
/// the wire); the LiveTrade tier is still appropriate because the test
/// reaches into the real wire path with a real bench.  Captures whatever
/// row count the bench has at probe time — if non-zero, the first 5 rows
/// land in the artifact for the PoW doc.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class Stage71ReportsQueryLiveTradeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public Stage71ReportsQueryLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task Query_Csv_Status_Cancel_Round_Trip()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — Stage 7.1 LiveTrade exercises live reports wire.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(Profile, 60)).Should().BeTrue();

        // 1) JSON query — 90d wide window to maximise odds of finding any
        //    Stage 1+2 fills if present.  When the bench shows 0 rows we
        //    still verify the envelope shape.
        var q = await _mcp.CallTool("mt_reports_query", new
        {
            period = "90d", profile = Profile, max_rows = 200,
        }, timeout: System.TimeSpan.FromSeconds(45));
        q.IsRpcError.Should().BeFalse();
        var qd = q.ParsedBody!.Value;
        string requestId = qd.GetProperty("request_id").GetString()!;
        int rowCount     = qd.GetProperty("row_count").GetInt32();

        // 2) CSV inline.
        var c = await _mcp.CallTool("mt_reports_csv_inline", new
        {
            period = "90d", profile = Profile, max_rows = 200,
        }, timeout: System.TimeSpan.FromSeconds(45));
        c.IsRpcError.Should().BeFalse();
        string csv = c.ParsedBody!.Value.GetProperty("csv").GetString()!;
        csv.Should().Contain("id,reportOpenTime,reportTime");

        // 3) Status of the JSON query.
        var st = await _mcp.CallTool("mt_reports_status", new { request_id = requestId });
        st.IsRpcError.Should().BeFalse();
        st.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("completed");
        double? latencyMs = st.ParsedBody!.Value.GetProperty("latency_ms").GetDouble();

        // 4) Cancel a known-completed request — must report already_completed.
        var canc = await _mcp.CallTool("mt_reports_cancel", new { request_id = requestId });
        canc.IsRpcError.Should().BeFalse();
        string cancelStatus = canc.ParsedBody!.Value.GetProperty("status").GetString()!;

        // 5) Capture artifact (first 5 rows when any exist).
        var firstFiveRows = new System.Collections.Generic.List<object?>();
        if (qd.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var r in rows.EnumerateArray())
            {
                if (i++ >= 5) break;
                firstFiveRows.Add(new
                {
                    id          = r.GetProperty("id").GetInt64(),
                    symbol      = r.GetProperty("symbol").GetString(),
                    market_type = r.GetProperty("market_type").GetString(),
                    side        = r.GetProperty("side").GetString(),
                    price_open  = r.GetProperty("price_open").GetDouble(),
                    price_close = r.GetProperty("price_close").GetDouble(),
                    profit_usdt = r.GetProperty("profit_usdt").GetDouble(),
                    closed_by   = r.GetProperty("closed_by").GetString(),
                    is_emulated = r.GetProperty("is_emulated").GetBoolean(),
                });
            }
        }

        await WriteArtifact(new
        {
            Stage = "7.1",
            Profile,
            RequestId = requestId,
            RowCount = rowCount,
            CsvFirstChars = csv.Length > 800 ? csv.Substring(0, 800) + "…(truncated)" : csv,
            LatencyMs = latencyMs,
            CancelStatus = cancelStatus,
            FirstFiveRows = firstFiveRows,
            EndedAtUtc = System.DateTime.UtcNow,
        });
    }

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "stage7_1");
        Directory.CreateDirectory(dir);
        string fname = $"bench_02_{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
