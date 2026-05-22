using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Algos clipboard-paste LiveTrade — exercises cross-profile / cross-exchange
/// paste against real benches.  Three legs, each gated on its bench being
/// CONNECTED:
///
///   <list type="bullet">
///   <item>Same-profile paste (bench_02 → bench_02).  Idempotency check —
///   pasting to the same profile must succeed and create a new algorithm row
///   (duplicate detection surfaces as a warning, not a hard failure).</item>
///   <item>Cross-exchange paste with matching symbol format
///   (bench_02 BINANCE → bench_01 BYBIT).  Both exchanges use the bare
///   <c>BTCUSDT</c> convention, so no override should be required.</item>
///   <item>Cross-exchange paste with symbol-format mismatch
///   (bench_02 BINANCE → bench_04 OKX).  Must surface the structured
///   <c>symbol_mismatch</c> error with <c>suggested_symbol=BTC-USDT-SWAP</c>;
///   the caller then retries with <c>override_symbol=BTC-USDT-SWAP</c> and
///   the paste succeeds.</item>
///   </list>
///
/// <para><b>POLICY</b> — gated by <c>MTC_LIVE_TRADES=1</c> AND
/// <c>MTC_TESTING_ENV=1</c>.  Run it explicitly via:</para>
/// <code>
/// MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
///     dotnet test -c Release --filter "Category=LiveTrade&amp;DisplayName~AlgosClipboardPaste"
/// </code>
///
/// <para><b>NO CLEANUP</b> — pasted algorithms remain on each destination
/// profile.  This is deliberate: reports tooling will read the resulting
/// algorithm catalog state.  Same-profile paste creates a duplicate-row
/// pair (one of the documented edge cases approved of in advance).</para>
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class AlgosClipboardPasteLiveTradeTests
{
    private const string SourceProfile = "bench_02";   // BINANCE
    private const string BybitProfile = "bench_01";   // BYBIT (same symbol format)
    private const string OkxProfile = "bench_04";     // OKX (different symbol format)

    private readonly McpFixture _mcp;
    private readonly BenchFixture _bench;
    public AlgosClipboardPasteLiveTradeTests(McpFixture mcp, BenchFixture bench) { _mcp = mcp; _bench = bench; }

    [SkippableFact]
    public async Task SameProfilePaste_bench02_to_bench02()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade pastes real algorithm rows onto destination profiles.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(SourceProfile),
            $"Bench {SourceProfile} not observed on UDP port; skipping.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(SourceProfile, 60)).Should().BeTrue();

        long? sourceId = await ListFirstAlgoIdAsync(SourceProfile);
        Skip.If(sourceId == null, "No algorithm on bench_02 to copy");

        // 1) Snapshot baseline algo count on bench_02.
        int countBefore = await CountAlgosAsync(SourceProfile);

        // 2) copy-to-clipboard from bench_02.
        var copyResp = await _mcp.CallTool("mt_algos_copy_to_clipboard", new
        {
            id = sourceId.Value.ToString(),
            profile = SourceProfile,
        });
        copyResp.InnerSuccess.Should().BeTrue(because: "clipboard copy: " + copyResp.InnerMessage);

        // 3) paste-from-clipboard onto bench_02 itself.
        var pasteResp = await _mcp.CallTool("mt_algos_paste_from_clipboard", new
        {
            destination_profile = SourceProfile,
            confirm = true,
        });
        pasteResp.IsRpcError.Should().BeFalse();
        pasteResp.InnerSuccess.Should().BeTrue(because: "same-profile paste must succeed; got: " + pasteResp.InnerMessage);

        // 4) Verify the algorithm list grew by exactly 1.
        await Task.Delay(1500);
        int countAfter = await CountAlgosAsync(SourceProfile);
        countAfter.Should().Be(countBefore + 1,
            because: $"same-profile paste should add exactly one algorithm row (before={countBefore}, after={countAfter})");
    }

    [SkippableFact]
    public async Task CrossExchangePaste_bench02_BINANCE_to_bench01_BYBIT_sameSymbolFormat()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade pastes real algorithm rows onto destination profiles.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(SourceProfile), $"Bench {SourceProfile} unavailable; skipping.");
        Skip.If(!_bench.IsBenchAvailable(BybitProfile),
            $"Bench {BybitProfile} (BYBIT) unavailable; skipping cross-exchange leg.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(SourceProfile, 60)).Should().BeTrue();
        (await _mcp.WaitForConnected(BybitProfile, 60)).Should().BeTrue(
            because: $"{BybitProfile} BYBIT must reach CONNECTED for cross-exchange paste leg");

        long? sourceId = await ListFirstAlgoIdAsync(SourceProfile);
        Skip.If(sourceId == null, "No algorithm on bench_02 to copy");

        int countBefore = await CountAlgosAsync(BybitProfile);

        var copyResp = await _mcp.CallTool("mt_algos_copy_to_clipboard", new
        {
            id = sourceId.Value.ToString(),
            profile = SourceProfile,
        });
        copyResp.InnerSuccess.Should().BeTrue();

        // Same symbol format (BTCUSDT) on both — no override needed.
        var pasteResp = await _mcp.CallTool("mt_algos_paste_from_clipboard", new
        {
            destination_profile = BybitProfile,
            confirm = true,
        });
        pasteResp.IsRpcError.Should().BeFalse();
        pasteResp.InnerSuccess.Should().BeTrue(
            because: $"BINANCE → BYBIT BTCUSDT paste should succeed without override: {pasteResp.InnerMessage}");

        await Task.Delay(1500);
        int countAfter = await CountAlgosAsync(BybitProfile);
        countAfter.Should().Be(countBefore + 1,
            because: $"BYBIT algorithm list should grow by 1 (before={countBefore}, after={countAfter})");
    }

    [SkippableFact]
    public async Task CrossExchangePaste_bench02_BINANCE_to_bench04_OKX_symbolMismatchThenOverride()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade pastes real algorithm rows onto destination profiles.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        Skip.If(!_bench.IsBenchAvailable(OkxProfile),
            $"Bench {OkxProfile} (OKX) unavailable; skipping cross-exchange leg.");

        await _mcp.RestartSubprocessAsync();
        (await _mcp.WaitForConnected(OkxProfile, 60)).Should().BeTrue(
            because: $"{OkxProfile} OKX must reach CONNECTED for cross-exchange symbol-mismatch leg");

        // Use a forged BTCUSDT BINANCE payload (matches the Smoke fixture) so this
        // test is independent of whatever default algos happen to live on bench_02
        // — the Shots Group templates carry an empty symbol field, which would
        // make the suggested_symbol assertion vacuous.
        string payload = BuildForgedBinancePayload("BTCUSDT");

        // 1) First attempt — NO override.  Must surface structured symbol_mismatch
        //    against the LIVE OKX bench (proves the dispatcher reaches the
        //    destination-aware pre-flight, not just a local schema gate).
        var failResp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload,
            destination_profile = OkxProfile,
            confirm = true,
        });
        failResp.InnerSuccess.Should().BeFalse(
            because: "BINANCE BTCUSDT → OKX paste without override must surface symbol_mismatch");
        failResp.InnerMessage!.Should().Contain("symbol_mismatch");
        failResp.InnerMessage.Should().Contain("source_symbol");
        failResp.InnerMessage.Should().Contain("destination_exchange");
        failResp.InnerMessage.Should().Contain("suggested_symbol");
        failResp.InnerMessage.Should().Contain("BTC-USDT-SWAP",
            because: "ExchangeSymbolMap.Suggest must produce the OKX perp format for the BINANCE source");

        int countBefore = await CountAlgosAsync(OkxProfile);

        // 2) Second attempt — caller applies suggested_symbol override.  Must
        //    succeed against the live OKX bench and grow its algo list by 1.
        var pasteResp = await _mcp.CallTool("mt_algos_import_json", new
        {
            payload,
            destination_profile = OkxProfile,
            override_symbol = "BTC-USDT-SWAP",
            confirm = true,
        });
        pasteResp.IsRpcError.Should().BeFalse();
        pasteResp.InnerSuccess.Should().BeTrue(
            because: $"OKX paste with override_symbol=BTC-USDT-SWAP must succeed: {pasteResp.InnerMessage}");

        await Task.Delay(1500);
        int countAfter = await CountAlgosAsync(OkxProfile);
        countAfter.Should().Be(countBefore + 1,
            because: $"OKX algorithm list should grow by 1 (before={countBefore}, after={countAfter})");
    }

    private static string BuildForgedBinancePayload(string symbol)
    {
        var p = new Newtonsoft.Json.Linq.JObject
        {
            ["schema_version"] = "v1",
            ["exported_from_exchange"] = "BINANCE",
            ["exported_from_profile"] = "bench_02_forged",
            ["exported_at"] = "2026-05-11T22:00:00Z",
            ["algorithm"] = new Newtonsoft.Json.Linq.JObject
            {
                ["name"] = $"clipboard_xexchange_{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                ["signature"] = "SG",
                ["description"] = "",
                ["symbol"] = symbol,
                ["market"] = "FUTURES",
                ["marketTypeInt"] = 3,
                ["groupType"] = "SHOTS",
                ["groupTypeInt"] = 0,
                ["isTradingAlgo"] = true,
                ["version"] = 1,
                ["argsJson"] = "{}",
            },
        };
        return p.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<long?> ListFirstAlgoIdAsync(string profile)
    {
        var resp = await _mcp.CallTool("mt_algos_list", new { profile });
        if (!resp.InnerSuccess) return null;
        return ExtractFirstAlgoId(resp.ParsedBody);
    }

    private async Task<int> CountAlgosAsync(string profile)
    {
        var resp = await _mcp.CallTool("mt_algos_list", new { profile });
        if (!resp.InnerSuccess) return -1;
        var body = resp.ParsedBody;
        if (body is not { } b || !b.TryGetProperty("data", out var data)) return -1;
        return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : -1;
    }

    private static long? ExtractFirstAlgoId(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        if (data.GetArrayLength() == 0) return null;
        var first = data[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("id", out var id)) return null;
        return id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null;
    }
}
