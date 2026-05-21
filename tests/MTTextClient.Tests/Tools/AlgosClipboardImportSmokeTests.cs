using System.IO;
using System.Text;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Smoke coverage for the clipboard-based paste / import tools.
/// Full cross-exchange paste lifecycle is exercised in
/// <see cref="LiveTrade.AlgosClipboardPasteLiveTradeTests"/>.
///
/// What these probes prove:
///   • ConfirmGate fires on paste / import-json when confirm is omitted.
///   • The dispatcher routes the new tool names cleanly (no "Unknown tool").
///   • Schema-version mismatch is rejected with a structured error rather than
///     silently applied — the bumped-version fixture below asserts this.
///   • copy-to-clipboard against a live bench writes a parseable JSON file at
///     ~/mt-clipboard/algo-clipboard.json with the current schema_version.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class AlgosClipboardImportSmokeTests
{
    // Target bench_02 explicitly (only consistently-alive bench in practice).
    private const string Profile = "bench_02";

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosClipboardImportSmokeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task mt_algos_paste_from_clipboard_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        var resp = await _mcp.CallTool("mt_algos_paste_from_clipboard", new
        {
            destination_profile = Profile,
            // confirm omitted intentionally
        });
        resp.IsRpcError.Should().BeTrue(
            because: "ConfirmGate emits -32602 when a confirm-required tool is called without confirm");
    }

    [SkippableFact]
    public async Task mt_algos_import_json_without_confirm_is_rejected()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        string payloadJson = BuildSamplePayload(AlgorithmClipboard.CurrentSchemaVersion);
        var resp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload = payloadJson,
            destination_profile = Profile,
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_algos_import_json_rejects_bumped_schema_version()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        // Forge a payload claiming a future schema version.  The importer must
        // reject this before any wire call (no destination mutation).
        string bumpedPayload = BuildSamplePayload("v999-future");
        var resp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload = bumpedPayload,
            destination_profile = Profile,
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse(
            because: "the importer must refuse payloads that claim a schema_version it does not recognise");
        resp.InnerMessage.Should().NotBeNull();
        resp.InnerMessage!.Should().Contain("schema_version_mismatch",
            because: "the structured error keyword tells callers what failed");
    }

    [SkippableFact]
    public async Task mt_algos_import_json_rejects_payload_missing_schema_version()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        // Forge a payload with NO schema_version field — still must be refused.
        var p = new JObject
        {
            // schema_version intentionally omitted
            ["exported_from_exchange"] = "BINANCE",
            ["exported_from_profile"] = "fake",
            ["algorithm"] = new JObject { ["name"] = "test", ["symbol"] = "BTCUSDT", ["market"] = "FUTURES" },
        };
        var resp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload = p.ToString(Formatting.None),
            destination_profile = Profile,
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse();
        resp.InnerMessage!.Should().Contain("schema_version_mismatch");
    }

    [SkippableFact]
    public async Task copy_to_clipboard_round_trip_writes_parseable_json()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        // Find a real algorithm to copy.  bench_02 carries 2 default SG algos.
        var listResp = await _mcp.CallTool("mt_algos_list", new { profile = Profile });
        listResp.InnerSuccess.Should().BeTrue();
        long? firstId = ExtractFirstAlgoId(listResp.ParsedBody);
        Skip.If(firstId == null, "No algorithms on bench_02 to copy");

        var copyResp = await _mcp.CallTool("mt_algos_copy_to_clipboard", new
        {
            id = firstId.Value.ToString(),
            profile = Profile,
        });
        copyResp.InnerSuccess.Should().BeTrue(because: "copy_to_clipboard: " + copyResp.InnerMessage);

        // Re-read the clipboard file directly from disk and verify shape.
        File.Exists(AlgorithmClipboard.ClipboardFile).Should().BeTrue();
        string raw = File.ReadAllText(AlgorithmClipboard.ClipboardFile);
        var parsed = JObject.Parse(raw);
        parsed[AlgorithmClipboard.SchemaVersionField]?.Value<string>()
            .Should().Be(AlgorithmClipboard.CurrentSchemaVersion);
        parsed[AlgorithmClipboard.ExportedFromExchangeField]?.Value<string>()
            .Should().NotBeNullOrEmpty();
        parsed[AlgorithmClipboard.AlgorithmField].Should().NotBeNull();
        (parsed[AlgorithmClipboard.AlgorithmField] as JObject)!["symbol"]?.Value<string>()
            .Should().NotBeNull();
    }

    [SkippableFact]
    public async Task paste_with_cross_exchange_symbol_mismatch_returns_structured_error()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(Profile), $"Bench {Profile} not observed on UDP port; skipping.");
        await _mcp.WaitForConnected(Profile);

        // Forge a payload pretending to come from OKX.  Pasting onto bench_02
        // BINANCE without --override-symbol must yield a symbol_mismatch with
        // structured fields, not a silent paste.
        var p = new JObject
        {
            [AlgorithmClipboard.SchemaVersionField] = AlgorithmClipboard.CurrentSchemaVersion,
            [AlgorithmClipboard.ExportedFromExchangeField] = "OKX",
            [AlgorithmClipboard.ExportedFromProfileField] = "fake_okx",
            ["exported_at"] = "2026-05-11T00:00:00Z",
            [AlgorithmClipboard.AlgorithmField] = new JObject
            {
                ["name"] = "test_xexchange_paste",
                ["signature"] = "SG",
                ["description"] = "",
                ["symbol"] = "BTC-USDT-SWAP",
                ["market"] = "FUTURES",
                ["marketTypeInt"] = 3,
                ["groupType"] = "SHOTS",
                ["groupTypeInt"] = 0,
                ["isTradingAlgo"] = true,
                ["version"] = 1,
                ["argsJson"] = "{}",
            },
        };
        var resp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload = p.ToString(Formatting.None),
            destination_profile = Profile,
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse(
            because: "cross-exchange paste without override_symbol must surface a structured symbol_mismatch error");
        resp.InnerMessage!.Should().Contain("symbol_mismatch");
        resp.InnerMessage.Should().Contain("source_symbol");
        resp.InnerMessage.Should().Contain("source_exchange");
        resp.InnerMessage.Should().Contain("destination_exchange");
        resp.InnerMessage.Should().Contain("suggested_symbol");
        // Sanity: ExchangeSymbolMap should suggest BTCUSDT (BINANCE) for BTC-USDT-SWAP (OKX).
        resp.InnerMessage.Should().Contain("BTCUSDT");
    }

    private static string BuildSamplePayload(string schemaVersion)
    {
        var p = new JObject
        {
            [AlgorithmClipboard.SchemaVersionField] = schemaVersion,
            [AlgorithmClipboard.ExportedFromExchangeField] = "BINANCE",
            [AlgorithmClipboard.ExportedFromProfileField] = "fake_source",
            ["exported_at"] = "2026-05-11T00:00:00Z",
            [AlgorithmClipboard.AlgorithmField] = new JObject
            {
                ["name"] = "algos_clipboard_sample",
                ["signature"] = "SG",
                ["description"] = "",
                ["symbol"] = "BTCUSDT",
                ["market"] = "FUTURES",
                ["marketTypeInt"] = 3,
                ["groupType"] = "SHOTS",
                ["groupTypeInt"] = 0,
                ["isTradingAlgo"] = true,
                ["version"] = 1,
                ["argsJson"] = "{}",
            },
        };
        return p.ToString(Formatting.None);
    }

    private static long? ExtractFirstAlgoId(System.Text.Json.JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        if (data.GetArrayLength() == 0) return null;
        var first = data[0];
        if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!first.TryGetProperty("id", out var id)) return null;
        return id.ValueKind == System.Text.Json.JsonValueKind.Number ? id.GetInt64() : null;
    }
}
