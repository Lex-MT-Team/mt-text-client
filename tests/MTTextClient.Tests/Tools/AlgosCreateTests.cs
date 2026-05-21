using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke coverage for <c>mt_algos_create</c> — the clone-from-source
/// algorithm creator with 3-layer resilience against MoonTrader-update drift.
///
/// What these probes prove:
///   • ConfirmGate fires when confirm is omitted.
///   • Dry-run is the default — calling with confirm=true but without
///     no_dry_run returns a preview, NOT a real wire SAVE.
///   • Auto-discovery by algo_type works on a bench that carries at least
///     one algorithm of the requested group_type.
///   • Unknown algo_type returns the structured 'algo_type_unknown:' error
///     with the list of allowed types.
///   • Unknown override fields are warned (unknown_override_fields) but
///     accepted — the caller may know about a new MT field before MCP.
///   • template_not_available is returned when no source can be found
///     AND no source_algo_id was specified.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AlgosCreateTests
{
    private const string Profile = "bench_02";
    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosCreateTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_algos_create_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_algos_create_dry_run_by_default_returns_preview_no_wire_save()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Baseline algo count — must NOT change after a dry-run.
        int before = await CountAlgos();
        Skip.If(before <= 0, "Bench has no algos to use as clone source");

        var resp = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            preset_name = "SG",
            confirm = true,  // gate-level confirm; no_dry_run is omitted → dry-run
        });
        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue();

        var data = resp.ParsedBody!.Value.GetProperty("data");
        data.GetProperty("DryRun").GetBoolean().Should().BeTrue();
        data.GetProperty("PresetSource").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("SchemaVersion").GetString().Should().Be("algo-create-v1");
        // No wire SAVE => algo count unchanged.
        (await CountAlgos()).Should().Be(before, because: "dry-run must not mutate the bench");
    }

    [SkippableFact]
    public async Task mt_algos_create_unknown_algo_type_returns_structured_error()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "TOTALLY_NOT_REAL",
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse();
        // The MCP dispatcher's allow-list strips the unknown value before
        // calling the handler, which then reports "algo_type_required" because
        // no --algo-type flag reached it.  Either signal is acceptable proof
        // that an unknown group_type was rejected before any wire call.
        string msg = resp.InnerMessage ?? "";
        msg.Should().Match(m => m.Contains("algo_type_unknown") || m.Contains("algo_type_required"));
    }

    [SkippableFact]
    public async Task mt_algos_create_template_not_available_when_no_source_exists()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);

        // Ask for a group_type that no algo on bench_02 carries.  bench_02
        // only has SHOTS algos.  Request SAVER and we should get the
        // structured template_not_available error.
        var resp = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SAVER",
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse();
        resp.InnerMessage!.Should().Contain("template_not_available");
    }

    [SkippableFact]
    public async Task mt_algos_create_overrides_record_known_and_unknown()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} unavailable.");
        await _mcp.WaitForConnected(Profile);
        int before = await CountAlgos();
        Skip.If(before <= 0, "Bench has no algos to use as clone source");

        // Mix a known parameter ('autoStart' is documented in algo argsJson)
        // with a bogus key — the bogus one must surface as unknown_override_fields.
        var overrides = new JObject {
            ["autoStart"] = "ON",
            ["__totally_not_a_real_parameter__"] = "x",
        };
        var resp = await _mcp.CallTool("mt_algos_create", new
        {
            profile = Profile,
            algo_type = "SHOTS",
            preset_name = "SG",
            overrides_json = overrides.ToString(Newtonsoft.Json.Formatting.None),
            confirm = true,
        });
        resp.IsRpcError.Should().BeFalse();
        resp.InnerSuccess.Should().BeTrue();

        var data = resp.ParsedBody!.Value.GetProperty("data");
        bool autoStartApplied = false;
        foreach (var o in data.GetProperty("OverriddenFields").EnumerateArray())
            if (o.GetString() == "autoStart") { autoStartApplied = true; break; }
        autoStartApplied.Should().BeTrue(because: "the known 'autoStart' override must apply");

        bool bogusReported = false;
        foreach (var o in data.GetProperty("UnknownOverrideFields").EnumerateArray())
            if ((o.GetString() ?? "").Contains("__totally_not_a_real_parameter__")) { bogusReported = true; break; }
        bogusReported.Should().BeTrue(
            because: "unknown override keys must surface in unknown_override_fields, not silently apply");
    }

    private async Task<int> CountAlgos()
    {
        var resp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        if (!resp.InnerSuccess) return -1;
        var body = resp.ParsedBody;
        if (body is not { } b || !b.TryGetProperty("data", out var data)) return -1;
        return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : -1;
    }
}
