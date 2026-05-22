using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MTTextClient.Tests.Infrastructure;

namespace MTTextClient.Tests.LiveTrade.Campaign;

/// <summary>
/// 2026-05-12 Live-Validation Campaign — shared per-tool evidence recorder.
/// Each campaign test calls one or more MCP tools via <see cref="Probe"/>,
/// which:
///   1. Issues the tool call;
///   2. Classifies the response (validated_real_response | tool_error |
///      rpc_error | timeout | unexpected_exception);
///   3. Appends a JSON-line record to a per-campaign JSONL artifact under
///      ~/mt-test-artifacts/campaign-2026-05-12/{campaign_letter}.jsonl;
///   4. Returns the response so the caller can drive follow-up flow.
///
/// The artifact is auditable evidence.  Tests deliberately do NOT assert
/// pass/fail on a single tool — campaign success means evidence captured.
/// Real failures (timeouts, rpc errors) are recorded honestly so the
/// evidence can be re-verified.
/// </summary>
public static class CampaignEvidence
{
    public static class Classification
    {
        public const string Validated = "validated_real_response";
        public const string ToolError = "tool_error";
        public const string RpcError = "rpc_error";
        public const string Timeout = "timeout";
        public const string UnexpectedException = "unexpected_exception";
        public const string SkippedBlocker = "skipped_blocker";
    }

    public static string ArtifactsRoot
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "mt-test-artifacts", "campaign-2026-05-12");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static async Task<McpResponse?> Probe(
        McpFixture mcp,
        string campaignLetter,
        string tool,
        object? args,
        string? profile = null,
        TimeSpan? timeout = null,
        string? note = null)
    {
        var sw = Stopwatch.StartNew();
        McpResponse? resp = null;
        string classification;
        string? errorText = null;
        try
        {
            resp = await mcp.CallTool(tool, args, timeout).ConfigureAwait(false);
            classification = ClassifyResponse(resp);
        }
        catch (TimeoutException ex)
        {
            classification = Classification.Timeout;
            errorText = ex.Message;
        }
        catch (Exception ex)
        {
            classification = Classification.UnexpectedException;
            errorText = ex.GetType().Name + ": " + ex.Message;
        }
        sw.Stop();

        WriteRecord(campaignLetter, new EvidenceRecord
        {
            ts_utc = DateTime.UtcNow.ToString("o"),
            tool = tool,
            profile = profile,
            args_summary = SafeShortJson(args),
            elapsed_ms = (int)sw.ElapsedMilliseconds,
            classification = classification,
            tool_error = resp?.IsToolError == true,
            rpc_error = resp?.IsRpcError == true,
            inner_success = resp != null && !resp.IsRpcError && !resp.IsToolError && resp.InnerSuccess,
            inner_message = resp?.InnerMessage,
            response_summary = SafeResponseSummary(resp),
            error_text = errorText,
            note = note,
        });

        return resp;
    }

    public static void RecordBlocker(
        string campaignLetter,
        string tool,
        string reason,
        string? profile = null)
    {
        WriteRecord(campaignLetter, new EvidenceRecord
        {
            ts_utc = DateTime.UtcNow.ToString("o"),
            tool = tool,
            profile = profile,
            args_summary = null,
            elapsed_ms = 0,
            classification = Classification.SkippedBlocker,
            tool_error = false,
            rpc_error = false,
            inner_success = false,
            inner_message = null,
            response_summary = null,
            error_text = null,
            note = reason,
        });
    }

    private static string ClassifyResponse(McpResponse resp)
    {
        if (resp.IsRpcError) return Classification.RpcError;
        if (resp.IsToolError) return Classification.ToolError;
        return Classification.Validated;
    }

    private static string? SafeShortJson(object? args)
    {
        if (args == null) return null;
        try
        {
            var text = JsonSerializer.Serialize(args);
            if (text.Length > 400) text = text.Substring(0, 400) + "…";
            return text;
        }
        catch { return null; }
    }

    private static string? SafeResponseSummary(McpResponse? resp)
    {
        if (resp == null) return null;
        try
        {
            if (resp.IsRpcError) return resp.Envelope.GetRawText();
            string? raw = resp.Text;
            if (raw == null) return null;
            if (raw.Length > 800) raw = raw.Substring(0, 800) + "…";
            return raw;
        }
        catch { return null; }
    }

    private static readonly object _writeLock = new();
    private static void WriteRecord(string letter, EvidenceRecord rec)
    {
        var path = Path.Combine(ArtifactsRoot, $"{letter}.jsonl");
        var line = JsonSerializer.Serialize(rec);
        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class EvidenceRecord
    {
        public string ts_utc { get; set; } = "";
        public string tool { get; set; } = "";
        public string? profile { get; set; }
        public string? args_summary { get; set; }
        public int elapsed_ms { get; set; }
        public string classification { get; set; } = "";
        public bool tool_error { get; set; }
        public bool rpc_error { get; set; }
        public bool inner_success { get; set; }
        public string? inner_message { get; set; }
        public string? response_summary { get; set; }
        public string? error_text { get; set; }
        public string? note { get; set; }
    }
}
