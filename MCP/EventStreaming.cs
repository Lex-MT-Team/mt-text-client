using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.MCP;

// MT-005: Event Streaming — algo state changes, order fills, errors via SSE

/// <summary>Immutable event record pushed to subscribers.</summary>
public sealed class NexusEvent
{
    public long   Seq  { get; init; }
    public string Type { get; init; } = "";
    public string Core { get; init; } = "";
    public object? Data { get; init; }
    public string Ts   { get; init; } = DateTime.UtcNow.ToString("o");
}

/// <summary>
/// Thread-safe ring buffer that buffers NexusEvents and pushes them to
/// connected SSE clients.
/// </summary>
public sealed class EventBroadcaster
{
    private const int MaxBuffer = 500;

    private long _seq = 0;
    private readonly ConcurrentQueue<NexusEvent> _buffer = new();
    private readonly List<SseClient> _clients = new();
    private readonly object _clientLock = new();

    // ── NATS bridge (MT-020) ──────────────────────────────────────────────────
    // Optional NATS UDP publish alongside SSE.
    // Enabled by setting MT_NATS_UDP_HOST env var (e.g. "127.0.0.1:4223").
    // Uses a fire-and-forget UDP datagram — zero blocking, sub-millisecond.
    // Format: "mt.events.{type}\t{json}" (tab-delimited subject + payload).
    // A NATS JetStream bridge process can subscribe and re-publish to NATS subjects.
    private static readonly System.Net.Sockets.UdpClient? s_natsUdp;
    private static readonly System.Net.IPEndPoint?        s_natsEndpoint;

    static EventBroadcaster()
    {
        string? udpTarget = System.Environment.GetEnvironmentVariable("MT_NATS_UDP_HOST");
        if (!string.IsNullOrEmpty(udpTarget))
        {
            try
            {
                string[] parts = udpTarget.Split(':');
                int port = parts.Length == 2 && int.TryParse(parts[1], out int p) ? p : 4223;
                s_natsEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(parts[0]), port);
                s_natsUdp = new System.Net.Sockets.UdpClient();
            }
            catch { /* NATS bridge optional — ignore config errors */ }
        }
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Enqueue a new event, notify all SSE clients, and optionally bridge to NATS via UDP.</summary>
    public void Publish(string type, string core, object? data = null)
    {
        long seq = Interlocked.Increment(ref _seq);
        var evt = new NexusEvent { Seq = seq, Type = type, Core = core, Data = data };

        _buffer.Enqueue(evt);

        // Trim: keep last MaxBuffer events
        while (_buffer.Count > MaxBuffer)
            _buffer.TryDequeue(out _);

        NotifyClients(evt);

        // MT-020: bridge to NATS via UDP (fire-and-forget, no blocking)
        if (s_natsUdp != null && s_natsEndpoint != null)
        {
            try
            {
                string subject = $"mt.events.{type}";
                string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    seq = evt.Seq, type = evt.Type, core = evt.Core, data = evt.Data, ts = evt.Ts
                });
                string msg = $"{subject}\t{payload}";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(msg);
                s_natsUdp.SendAsync(bytes, bytes.Length, s_natsEndpoint); // async, no await — fire-and-forget
            }
            catch { /* never throw from event publish path */ }
        }
    }

    /// <summary>Return all buffered events with Seq &gt; sinceSeq.</summary>
    public IReadOnlyList<NexusEvent> GetSince(long sinceSeq)
    {
        var result = new List<NexusEvent>();
        foreach (var e in _buffer)
            if (e.Seq > sinceSeq)
                result.Add(e);
        return result;
    }

    /// <summary>Return the last N events.</summary>
    public IReadOnlyList<NexusEvent> GetLast(int n)
    {
        var all = new List<NexusEvent>(_buffer);
        int start = Math.Max(0, all.Count - n);
        return all.GetRange(start, all.Count - start);
    }

    /// <summary>Current sequence counter (last published Seq).</summary>
    public long CurrentSeq => Interlocked.Read(ref _seq);

    // ── SSE client management ─────────────────────────────────────────────

    public void AddClient(SseClient client)
    {
        lock (_clientLock) _clients.Add(client);
    }

    public void RemoveClient(SseClient client)
    {
        lock (_clientLock) _clients.Remove(client);
    }

    private void NotifyClients(NexusEvent evt)
    {
        List<SseClient> snapshot;
        lock (_clientLock) snapshot = new List<SseClient>(_clients);

        foreach (var client in snapshot)
            client.TrySend(evt);
    }
}

/// <summary>Wraps a single SSE HTTP connection.</summary>
public sealed class SseClient
{
    private readonly HttpListenerResponse _response;
    private readonly EventBroadcaster _broadcaster;
    private bool _dead = false;

    public SseClient(HttpListenerResponse response, EventBroadcaster broadcaster)
    {
        _response = response;
        _broadcaster = broadcaster;

        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.SendChunked = true;
    }

    public void TrySend(NexusEvent evt)
    {
        if (_dead) return;
        try
        {
            string json = JsonConvert.SerializeObject(evt);
            string frame = $"data: {json}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(frame);
            _response.OutputStream.Write(bytes, 0, bytes.Length);
            _response.OutputStream.Flush();
        }
        catch (IOException)
        {
            _dead = true;
            _broadcaster.RemoveClient(this);
        }
    }

    public bool IsDead => _dead;
}

/// <summary>

// MT-006: Prometheus-format metrics collector
/// <summary>Thread-safe counter store for mt-text-client Prometheus metrics.</summary>
public sealed class MetricsCollector
{
    private long _toolCallsTotal;
    private long _toolErrorsTotal;
    private long _eventsTotal;
    private long _connectionsActive;

    private readonly ConcurrentDictionary<string, long> _perToolCalls  = new();
    private readonly ConcurrentDictionary<string, long> _perToolErrors = new();
    private readonly ConcurrentDictionary<string, long> _perEventType  = new();

    // MT-022: per-tool latency histograms (rolling window of last 1000 samples)
    private const int LatencySampleCap = 1000;
    private readonly ConcurrentDictionary<string, List<long>> _perToolLatency = new();

    public void RecordCall(string tool)
    {
        Interlocked.Increment(ref _toolCallsTotal);
        _perToolCalls.AddOrUpdate(tool, 1L, (_, v) => v + 1);
    }

    public void RecordError(string tool)
    {
        Interlocked.Increment(ref _toolErrorsTotal);
        _perToolErrors.AddOrUpdate(tool, 1L, (_, v) => v + 1);
    }

    public void RecordEvent(string type)
    {
        Interlocked.Increment(ref _eventsTotal);
        _perEventType.AddOrUpdate(type, 1L, (_, v) => v + 1);
    }

    public void SetConnectionsActive(int count) =>
        Interlocked.Exchange(ref _connectionsActive, count);

    /// <summary>MT-022: Record observed latency for a tool call (milliseconds).</summary>
    public void RecordLatency(string tool, long ms)
    {
        var samples = _perToolLatency.GetOrAdd(tool, _ => new List<long>());
        lock (samples)
        {
            samples.Add(ms);
            // Keep rolling window — evict oldest half when over cap
            if (samples.Count > LatencySampleCap)
                samples.RemoveRange(0, LatencySampleCap / 2);
        }
    }

    /// <summary>MT-022: Return P50/P95/P99 percentile latencies (ms) for a tool. Returns null if no data.</summary>
    public (long p50, long p95, long p99)? GetLatencyPercentiles(string tool)
    {
        if (!_perToolLatency.TryGetValue(tool, out var samples)) return null;
        long[] sorted;
        lock (samples)
        {
            if (samples.Count == 0) return null;
            sorted = [.. samples];
        }
        Array.Sort(sorted);
        return (Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99));
    }

    private static long Percentile(long[] sorted, int pct)
    {
        if (sorted.Length == 0) return 0;
        double idx = (pct / 100.0) * (sorted.Length - 1);
        int lo = (int)idx;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        double frac = idx - lo;
        return (long)(sorted[lo] * (1 - frac) + sorted[hi] * frac);
    }

    public string ToPrometheusText()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# HELP mt_tool_calls_total Total MCP tool calls");
        sb.AppendLine("# TYPE mt_tool_calls_total counter");
        foreach (var (tool, cnt) in _perToolCalls)
            sb.AppendLine($"mt_tool_calls_total{{tool=\"{tool}\"}} {cnt}");

        sb.AppendLine("# HELP mt_tool_errors_total Total MCP tool errors");
        sb.AppendLine("# TYPE mt_tool_errors_total counter");
        foreach (var (tool, cnt) in _perToolErrors)
            sb.AppendLine($"mt_tool_errors_total{{tool=\"{tool}\"}} {cnt}");

        sb.AppendLine("# HELP mt_events_published_total Total SSE events published");
        sb.AppendLine("# TYPE mt_events_published_total counter");
        foreach (var (t, cnt) in _perEventType)
            sb.AppendLine($"mt_events_published_total{{type=\"{t}\"}} {cnt}");

        sb.AppendLine("# HELP mt_connections_active Active MTCore connections");
        sb.AppendLine("# TYPE mt_connections_active gauge");
        sb.AppendLine($"mt_connections_active {_connectionsActive}");

        // MT-022: latency histograms
        sb.AppendLine("# HELP mt_tool_latency_p50_ms Tool call P50 latency in milliseconds");
        sb.AppendLine("# TYPE mt_tool_latency_p50_ms gauge");
        sb.AppendLine("# HELP mt_tool_latency_p95_ms Tool call P95 latency in milliseconds");
        sb.AppendLine("# TYPE mt_tool_latency_p95_ms gauge");
        sb.AppendLine("# HELP mt_tool_latency_p99_ms Tool call P99 latency in milliseconds");
        sb.AppendLine("# TYPE mt_tool_latency_p99_ms gauge");
        foreach (var tool in _perToolLatency.Keys)
        {
            var p = GetLatencyPercentiles(tool);
            if (p == null) continue;
            sb.AppendLine($"mt_tool_latency_p50_ms{{tool=\"{tool}\"}} {p.Value.p50}");
            sb.AppendLine($"mt_tool_latency_p95_ms{{tool=\"{tool}\"}} {p.Value.p95}");
            sb.AppendLine($"mt_tool_latency_p99_ms{{tool=\"{tool}\"}} {p.Value.p99}");
        }

        return sb.ToString();
    }

    public JObject ToJson()
    {
        var obj = new JObject
        {
            ["tool_calls_total"]   = Interlocked.Read(ref _toolCallsTotal),
            ["tool_errors_total"]  = Interlocked.Read(ref _toolErrorsTotal),
            ["events_total"]       = Interlocked.Read(ref _eventsTotal),
            ["connections_active"] = Interlocked.Read(ref _connectionsActive),
        };

        // MT-022: include per-tool latency percentiles
        var latency = new JObject();
        foreach (var tool in _perToolLatency.Keys)
        {
            var p = GetLatencyPercentiles(tool);
            if (p == null) continue;
            latency[tool] = new JObject
            {
                ["p50_ms"] = p.Value.p50,
                ["p95_ms"] = p.Value.p95,
                ["p99_ms"] = p.Value.p99,
            };
        }
        if (latency.Count > 0) obj["latency_percentiles"] = latency;

        return obj;
    }
}

/// Lightweight HTTP SSE server (System.Net.HttpListener).
/// Listens on <c>http://+:{port}/</c> and serves:
/// <list type="bullet">
/// <item>GET /events        — SSE stream (all future events)</item>
/// <item>GET /events?since=N — SSE stream from sequence N</item>
/// <item>GET /events/poll   — JSON snapshot of last N buffered events</item>
/// </list>
/// </summary>
public sealed class SseEventServer
{
    private readonly EventBroadcaster _broadcaster;
    private readonly int _port;
    private readonly MetricsCollector? _metrics;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public SseEventServer(EventBroadcaster broadcaster, MetricsCollector? metrics = null, int port = 8587)
    {
        _broadcaster = broadcaster;
        _metrics = metrics;
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");

        try
        {
            _listener.Start();
            Console.Error.WriteLine($"[EVENTS] SSE server listening on http://+:{_port}/");
            Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EVENTS] Failed to start SSE server on :{_port} — {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // Handle each request on a thread-pool thread
            _ = Task.Run(() => HandleRequest(ctx), ct);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";

        try
        {
            if (path == "/events/poll")
            {
                HandlePoll(ctx);
            }
            else if (path == "/events")
            {
                HandleSse(ctx);
            }
            else if (path == "/events/status")
            {
                HandleStatus(ctx);
            }
            else if (path == "/metrics")
            {
                HandleMetrics(ctx);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EVENTS] Request handler error: {ex.Message}");
            try { ctx.Response.Close(); } catch { }
        }
    }

    private void HandlePoll(HttpListenerContext ctx)
    {
        int n = 50;
        string? nParam = ctx.Request.QueryString["n"];
        if (nParam != null) int.TryParse(nParam, out n);

        string? sinceParam = ctx.Request.QueryString["since"];
        IReadOnlyList<NexusEvent> events;
        if (sinceParam != null && long.TryParse(sinceParam, out long sinceSeq))
            events = _broadcaster.GetSince(sinceSeq);
        else
            events = _broadcaster.GetLast(n);

        string json = JsonConvert.SerializeObject(new { events, current_seq = _broadcaster.CurrentSeq });
        byte[] body = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    private void HandleSse(HttpListenerContext ctx)
    {
        long sinceSeq = 0;
        string? sinceParam = ctx.Request.QueryString["since"];
        if (sinceParam != null) long.TryParse(sinceParam, out sinceSeq);

        var client = new SseClient(ctx.Response, _broadcaster);
        _broadcaster.AddClient(client);

        // Replay buffered events since requested seq
        foreach (var evt in _broadcaster.GetSince(sinceSeq))
            client.TrySend(evt);

        // Keep connection alive until client disconnects or server stops
        while (!client.IsDead)
            Thread.Sleep(500);

        _broadcaster.RemoveClient(client);
        try { ctx.Response.Close(); } catch { }
    }

    private void HandleStatus(HttpListenerContext ctx)
    {
        string json = JsonConvert.SerializeObject(new
        {
            current_seq = _broadcaster.CurrentSeq,
            port = _port,
            status = "ok"
        });
        byte[] body = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    private void HandleMetrics(HttpListenerContext ctx)
    {
        string body = _metrics?.ToPrometheusText() ?? "# metrics collector not attached\n";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }
}
