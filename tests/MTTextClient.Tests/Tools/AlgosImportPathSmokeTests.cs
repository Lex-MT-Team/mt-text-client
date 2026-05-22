using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke probes for the `path` argument on <c>mt_algos_import_json</c> and
/// the <c>mt_import_from_profile</c> survey tool.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AlgosImportPathSmokeTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosImportPathSmokeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_algos_import_json_path_arg_reports_path_not_found_for_missing_file()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Use an unmistakably-absent path; confirm=true to bypass the
        // ConfirmGate (the test exercises the path-resolution branch, not
        // confirm).  The wrapper injects path_not_found into the payload,
        // which AlgosCommand surfaces back as a JSON-parse failure carrying
        // that marker.
        var resp = await _mcp.CallTool("mt_algos_import_json", new
        {
            path = "/tmp/definitely-not-here-algos-import.json",
            destination_profile = Profile,
            confirm = true,
        });
        resp.IsRpcError.Should().BeFalse();
        var msg = resp.InnerMessage ?? "";
        msg.Should().Contain("/tmp/definitely-not-here-algos-import.json",
            because: "the wrapper must echo the failing path so callers can see what was checked");
    }

    [SkippableFact]
    public async Task mt_algos_import_json_path_arg_loads_real_payload_dry_run()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Write a syntactically-OK but semantically-invalid payload so the
        // call exercises the "file → payload → parse" wiring.  confirm=true
        // is required to pass the ConfirmGate; the payload is intentionally
        // malformed so no mutation can happen at the AlgosCommand layer.
        string tmp = Path.Combine(Path.GetTempPath(), $"algos-import-payload-{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
        await File.WriteAllTextAsync(tmp, "{\"_algos_import_probe_marker\":\"file_was_loaded\"}");
        try
        {
            var resp = await _mcp.CallTool("mt_algos_import_json", new
            {
                path = tmp,
                destination_profile = Profile,
                confirm = true,
            });
            resp.IsRpcError.Should().BeFalse();
            string msg = resp.InnerMessage ?? "";
            msg.Should().NotContain("path_not_found",
                because: "the file existed; path_not_found must not fire");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [SkippableFact]
    public async Task mt_import_from_profile_returns_structured_survey_against_self()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Use source==destination so every algo is a "duplicate" by name.
        var resp = await _mcp.CallTool("mt_import_from_profile", new
        {
            source_profile = Profile,
            destination_profile = Profile,
        });
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.GetProperty("source_profile").GetString().Should().Be(Profile);
        data.GetProperty("destination_profile").GetString().Should().Be(Profile);
        data.GetProperty("mutation_supported").GetBoolean().Should().BeFalse(
            because: "the survey is read-only; mutation is a follow-up");
        data.GetProperty("mutation_notice").GetString()
            .Should().Contain("import_from_profile_dry_run_only");
        data.TryGetProperty("entries", out _).Should().BeTrue();
        // Source==destination: every eligible entry MUST be a duplicate.
        int totalEligible = data.GetProperty("eligible_for_import").GetInt32();
        int dupCount = data.GetProperty("duplicate_count").GetInt32();
        dupCount.Should().Be(totalEligible,
            because: "when source==destination, every name is a duplicate by definition");
    }

    [SkippableFact]
    public async Task mt_import_from_profile_rejects_missing_required_args()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_import_from_profile", new
        {
            source_profile = Profile,
            // destination_profile intentionally omitted.
        });
        // Internal-tool dispatch bypasses the schema's required[] check; the
        // handler emits a structured 'error' field instead.
        resp.IsRpcError.Should().BeFalse();
        var data = resp.ParsedBody!.Value;
        data.TryGetProperty("error", out var e).Should().BeTrue();
        e.GetString()!.Should().Contain("source_profile and destination_profile are required");
    }
}
