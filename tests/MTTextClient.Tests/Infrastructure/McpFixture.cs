using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// Spawns one mt-text-client MCP server subprocess for a whole test collection,
/// performs the JSON-RPC <c>initialize</c> + <c>notifications/initialized</c>
/// handshake, and exposes <see cref="CallTool"/> /
/// <see cref="ListTools"/> for tests.
///
/// Lifecycle: <see cref="InitializeAsync"/> on first use, <see cref="DisposeAsync"/>
/// on collection teardown. xUnit handles both via <see cref="IAsyncLifetime"/>.
///
/// This fixture does NOT spawn a mock MTCore.  If a Smoke test needs a
/// connected core, ensure a real MTCore is running on the configured bench
/// UDP port (or the testing-environment workflow does); Smoke tests that
/// require the bench gate themselves with <see cref="EnvFlags.TestingEnv"/>.
///
/// The first <c>mt_connect</c> after a fresh MTCore start sometimes
/// silently stalls.  Tests that require a connection use
/// <see cref="WaitForConnected"/> which retries via reconnect-once rather
/// than asserting first-attempt success.
/// </summary>
public sealed class McpFixture : IAsyncLifetime
{
    private Process? _proc;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private int _nextId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>Catalog returned by tools/list at fixture startup.</summary>
    public IReadOnlyList<JsonElement> Tools { get; private set; } = Array.Empty<JsonElement>();

    /// <summary>Names of every tool returned by tools/list.</summary>
    public IReadOnlyList<string> ToolNames { get; private set; } = Array.Empty<string>();

    /// <summary>Default per-call timeout. Override per call as needed.</summary>
    /// <remarks>
    /// Bumped 30 s → 60 s on 2026-05-11 after the LiveTrade harness showed that
    /// MCP read calls (mt_exchange_ticker24, mt_orders_list) routinely take
    /// longer than 30 s on slow benches/exchanges under load.  Per-call
    /// overrides via <see cref="CallTool"/>'s <c>timeout</c> param remain the
    /// way to extend further (e.g. LiveTrade place leg uses 90 s).
    /// </remarks>
    public TimeSpan DefaultCallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => SpawnAsync();

    public Task DisposeAsync() => TeardownAsync();

    /// <summary>
    /// Tear the current MTTextClient --mcp subprocess down and start a fresh one,
    /// re-running the initialize / tools-list handshake.  Used by tests whose prior
    /// in-flight call may have wedged the shared subprocess pipe — notably
    /// LiveTrade place calls whose MTCore-side RTT exceeds the per-call deadline,
    /// leaving the subprocess processing a now-abandoned request and starving
    /// later parameterised cases.
    /// </summary>
    public async Task RestartSubprocessAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        _proc = null;
        _stdin = null;
        _cts = null;
        _readerTask = null;
        _pending.Clear();
        _nextId = 1;
        await SpawnAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Issue mt_connect + poll mt_status for every observed bench profile
    /// best-effort: each bench gets up to <paramref name="perBenchBudgetSeconds"/>
    /// (default 60 s) to reach CONNECTED, then is marked degraded (false in the
    /// returned snapshot) and the warm-start moves on.  This avoids one slow /
    /// frozen bench blocking the others — the typical MTCore-freeze failure
    /// mode where one bench stops responding after its first place.
    ///
    /// Only benches whose UDP port is currently bound get a connect — silent
    /// no-op for the rest.  Safe to call multiple times.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, bool>> WarmStartAllBenchesAsync(
        BenchFixture bench,
        int perBenchBudgetSeconds = 60)
    {
        // Collect ports that look bound; if none, there's nothing to warm.
        var profiles = new List<string>();
        foreach (var b in EnvFlags.AllBenches)
            if (bench.IsBenchAvailable(b.Profile)) profiles.Add(b.Profile);

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (profiles.Count == 0) return result;

        // Kick off mt_connect for each profile up front, then poll mt_status
        // in a single loop until every profile has a verdict (CONNECTED → true,
        // budget-elapsed → false).  Polls are concurrent across benches so the
        // wall-clock budget per bench is effectively <= perBenchBudgetSeconds.
        foreach (var p in profiles)
            await CallTool("mt_connect", new { profile = p }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(perBenchBudgetSeconds);
        while (DateTime.UtcNow < deadline && result.Count < profiles.Count)
        {
            var status = await CallTool("mt_status", new { }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var body = status.ParsedBody;
            if (body is { } b && b.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string? name = entry.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    if (name is null || !profiles.Contains(name)) continue;
                    if (result.ContainsKey(name)) continue;
                    string s = entry.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";
                    if (s.Contains("CONNECTED", StringComparison.OrdinalIgnoreCase) &&
                        !s.Contains("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                    {
                        result[name] = true;
                    }
                }
            }
            if (result.Count < profiles.Count)
                await Task.Delay(1000).ConfigureAwait(false);
        }
        // Mark each not-yet-CONNECTED profile false (degraded) in the returned
        // snapshot.  Callers see exactly which benches missed the budget; their
        // own assertion fails cleanly for the affected case without blocking
        // other parameterised cases.
        foreach (var p in profiles)
            if (!result.ContainsKey(p)) result[p] = false;
        return result;
    }

    private async Task SpawnAsync()
    {
        EnsurePatched();

        var psi = new ProcessStartInfo
        {
            FileName = RepoPaths.McpBinary,
            Arguments = "--mcp",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_NOLOGO"] = "1";

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MTTextClient --mcp");
        _stdin = _proc.StandardInput;

        _cts = new CancellationTokenSource();
        _readerTask = Task.Run(() => StdoutReader(_proc.StandardOutput, _cts.Token));

        // Drain stderr so we don't deadlock if the child writes too much.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                }
            }
            catch { /* fixture teardown */ }
        });

        // Initialize handshake.
        var initResp = await SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "mt-text-client-tests", version = "0" },
        }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        if (!initResp.TryGetProperty("protocolVersion", out _))
        {
            throw new InvalidOperationException(
                $"initialize did not return a protocolVersion. Got: {initResp.GetRawText()}");
        }

        // notifications/initialized (no id, no response)
        await SendNotificationAsync("notifications/initialized", new { }).ConfigureAwait(false);

        // tools/list once at startup; expose for [Theory] catalog tests.
        var listResp = await SendRequestAsync("tools/list", new { }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (listResp.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var snap = new List<JsonElement>(tools.GetArrayLength());
            var names = new List<string>(tools.GetArrayLength());
            foreach (var t in tools.EnumerateArray())
            {
                // Clone() so the JsonDocument the response was parsed from can be GC'd.
                snap.Add(t.Clone());
                names.Add(t.GetProperty("name").GetString() ?? "");
            }
            Tools = snap;
            ToolNames = names;
        }
        else
        {
            throw new InvalidOperationException("tools/list did not return a tools array.");
        }
    }

    private async Task TeardownAsync()
    {
        try { _cts?.Cancel(); } catch { }
        // Fail any in-flight TCSes so awaiters wake up rather than wait forever.
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException("MCP subprocess restarted while request was pending."));
        }
        try { _stdin?.Dispose(); } catch { }
        if (_proc is not null && !_proc.HasExited)
        {
            try
            {
                _proc.Kill(entireProcessTree: true);
                await Task.WhenAny(Task.Run(() => _proc.WaitForExit()), Task.Delay(3000)).ConfigureAwait(false);
            }
            catch { }
        }
        if (_readerTask is not null)
        {
            try { await Task.WhenAny(_readerTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Call an MCP tool and return the parsed response envelope.
    /// The returned JsonElement is the JSON-RPC <c>result</c> field (or
    /// <c>error</c> if the call failed).
    /// </summary>
    public Task<McpResponse> CallTool(string name, object? args = null, TimeSpan? timeout = null)
    {
        return CallToolCore(name, args, timeout ?? DefaultCallTimeout);
    }

    /// <summary>List the tools advertised at startup (cached snapshot).</summary>
    public IReadOnlyList<JsonElement> ListTools() => Tools;

    /// <summary>
    /// Connect to <paramref name="profile"/> and poll mt_status until the connection
    /// is reported as connected. Returns true on success, false on timeout.
    ///
    /// If first connect doesn't complete in
    /// <paramref name="firstAttemptSeconds"/>, the caller may want to restart MTCore
    /// and retry. This helper does NOT restart MTCore; that's an out-of-band
    /// action.
    /// </summary>
    public async Task<bool> WaitForConnected(string profile, int firstAttemptSeconds = 30)
    {
        await CallTool("mt_connect", new { profile }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(firstAttemptSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var status = await CallTool("mt_status", new { }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var body = status.ParsedBody;
            if (body is { } b && b.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.TryGetProperty("Name", out var n) &&
                        n.GetString() == profile &&
                        entry.TryGetProperty("Status", out var st))
                    {
                        var s = st.GetString() ?? "";
                        if (s.Contains("CONNECTED", StringComparison.OrdinalIgnoreCase) &&
                            !s.Contains("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<McpResponse> CallToolCore(string name, object? args, TimeSpan timeout)
    {
        var rpcResult = await SendRequestAsync("tools/call",
            new { name, arguments = args ?? new { } }, timeout).ConfigureAwait(false);
        return McpResponse.FromResult(rpcResult);
    }

    private async Task<JsonElement> SendRequestAsync(string method, object @params, TimeSpan timeout)
    {
        if (_stdin is null) throw new InvalidOperationException("McpFixture not initialized.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        await _stdin.WriteLineAsync(msg).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
                t.TrySetException(new TimeoutException(
                    $"MCP call timed out after {timeout.TotalSeconds:F1}s: method={method}"));
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendNotificationAsync(string method, object @params)
    {
        if (_stdin is null) throw new InvalidOperationException("McpFixture not initialized.");
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
        await _stdin.WriteLineAsync(msg).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);
    }

    private async Task StdoutReader(StreamReader stdout, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync().ConfigureAwait(false);
                if (line is null) return;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        // Notification or malformed — ignore.
                        continue;
                    }
                    var id = idEl.GetInt32();
                    if (!_pending.TryRemove(id, out var tcs)) continue;

                    if (root.TryGetProperty("error", out var err))
                    {
                        // Surface the error envelope so tests can assert on it.
                        tcs.TrySetResult(err.Clone());
                    }
                    else if (root.TryGetProperty("result", out var res))
                    {
                        tcs.TrySetResult(res.Clone());
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidOperationException(
                            $"JSON-RPC reply missing both result and error: {line}"));
                    }
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(new InvalidOperationException("MCP stdout reader died", ex));
        }
    }

    /// <summary>
    /// Self-heal the macOS arm64 PE patch on the built MTShared.dll so smoke
    /// tests can spawn the binary even if <c>scripts/patch_mtshared_arm64.py</c>
    /// hasn't been run since the last <c>dotnet build</c>.
    /// </summary>
    private static void EnsurePatched()
    {
        // Only auto-patch on macOS arm64 hosts. Linux x64 / Windows x64 leave it alone.
        bool isMacArm64 = OperatingSystem.IsMacOS() &&
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64;
        if (!isMacArm64) return;

        var path = RepoPaths.MTSharedBuilt;
        if (!File.Exists(path)) return;

        // Read PE header offset at 0x3C, then Machine field at PE+4.
        // Open ReadWrite only on the patching host: on Windows a sibling
        // fixture's spawned MTTextClient.exe holds the DLL loaded, and an
        // exclusive write handle is denied, which failed every test in the
        // collection.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        Span<byte> u32 = stackalloc byte[4];
        fs.Position = 0x3C;
        if (fs.Read(u32) != 4) return;
        int peOff = BitConverter.ToInt32(u32);

        Span<byte> u16 = stackalloc byte[2];
        fs.Position = peOff + 4;
        if (fs.Read(u16) != 2) return;
        ushort machine = BitConverter.ToUInt16(u16);

        const ushort AMD64 = 0x8664;
        const ushort ARM64 = 0xAA64;

        if (machine == AMD64)
        {
            fs.Position = peOff + 4;
            BitConverter.TryWriteBytes(u16, ARM64);
            fs.Write(u16);
        }
    }
}

/// <summary>Wrapper over an MCP <c>tools/call</c> response.</summary>
public sealed class McpResponse
{
    /// <summary>The raw <c>result</c> (or <c>error</c>) JSON envelope.</summary>
    public JsonElement Envelope { get; }

    /// <summary>True if the JSON-RPC reply was an error envelope.</summary>
    public bool IsRpcError { get; }

    /// <summary>True if the tool returned <c>isError: true</c>.</summary>
    public bool IsToolError { get; }

    /// <summary>The text content of the tool's reply (often a JSON string itself).</summary>
    public string? Text { get; }

    /// <summary>The parsed inner JSON body (when <see cref="Text"/> is JSON), otherwise null.</summary>
    public JsonElement? ParsedBody { get; }

    private McpResponse(JsonElement env, bool isRpcError, bool isToolError, string? text, JsonElement? parsedBody)
    {
        Envelope = env;
        IsRpcError = isRpcError;
        IsToolError = isToolError;
        Text = text;
        ParsedBody = parsedBody;
    }

    public static McpResponse FromResult(JsonElement env)
    {
        // RPC error envelope: { code, message, ... }
        if (env.TryGetProperty("code", out _) && env.TryGetProperty("message", out _))
            return new McpResponse(env, isRpcError: true, isToolError: false, text: null, parsedBody: null);

        bool isToolErr = env.TryGetProperty("isError", out var iee) &&
                         iee.ValueKind == JsonValueKind.True;
        string? text = null;
        if (env.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in content.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var typ) && typ.GetString() == "text" &&
                    c.TryGetProperty("text", out var tx))
                {
                    text = tx.GetString();
                    break;
                }
            }
        }
        JsonElement? parsed = null;
        if (text is not null)
        {
            try { parsed = JsonDocument.Parse(text).RootElement.Clone(); }
            catch { /* non-JSON text body, leave parsed null */ }
        }
        return new McpResponse(env, isRpcError: false, isToolError: isToolErr, text: text, parsedBody: parsed);
    }

    /// <summary>Convenience: <c>true</c> when the tool returned <c>success: true</c> in its inner JSON body.</summary>
    public bool InnerSuccess =>
        !IsRpcError && !IsToolError &&
        ParsedBody is { } b &&
        b.TryGetProperty("success", out var s) &&
        s.ValueKind == JsonValueKind.True;

    /// <summary>
    /// True when the call did not error AT ALL (no RPC error, no isError, has a
    /// parsed body). Useful for tools that return raw payloads instead of the
    /// <c>{success, message, data}</c> envelope (e.g. <c>mt_events_status</c>,
    /// <c>mt_metrics_get</c>, <c>mt_rate_status</c>).
    /// </summary>
    public bool NoError =>
        !IsRpcError && !IsToolError && ParsedBody is not null;

    /// <summary>Inner <c>message</c> field when present.</summary>
    public string? InnerMessage =>
        ParsedBody is { } b && b.TryGetProperty("message", out var m) ? m.GetString() : null;
}
