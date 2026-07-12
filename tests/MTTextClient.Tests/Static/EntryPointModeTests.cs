using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Entry-point resilience: the process must expose exactly four modes (REPL,
/// one-shot CLI, --mcp stdio, --daemon unix-socket) and reject a malformed
/// --daemon invocation loudly rather than falling through into command
/// dispatch. These spawn the built binary; no MTCore is required.
/// </summary>
[Trait("Category", "Static")]
public sealed class EntryPointModeTests
{
    private static ProcessStartInfo Psi(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RepoPaths.McpBinary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        return psi;
    }

    [Fact]
    public void Daemon_without_socket_path_exits_2_with_usage()
    {
        using var p = Process.Start(Psi("--daemon"))!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000).Should().BeTrue("a malformed --daemon invocation must exit promptly");
        p.ExitCode.Should().Be(2, "missing socket path is a usage error");
        err.Should().Contain("usage", "usage must be printed to stderr, not silently ignored");
    }

    [Fact]
    public void Daemon_with_empty_socket_path_exits_2()
    {
        using var p = Process.Start(Psi("--daemon", ""))!;
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000).Should().BeTrue();
        p.ExitCode.Should().Be(2, "an empty socket path is not a valid daemon invocation");
    }

    [Fact]
    public void Mcp_mode_answers_initialize_and_tools_list()
    {
        using var p = Process.Start(Psi("--mcp"))!;
        try
        {
            p.StandardInput.WriteLine(Req(1, "initialize",
                "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}"));
            p.StandardInput.WriteLine(Req(2, "tools/list", null));
            p.StandardInput.Flush();

            var resp = ReadResponses(p.StandardOutput, new[] { 1, 2 }, 25);
            resp.Should().ContainKey(1).And.ContainKey(2);
            resp[1]["result"].Should().NotBeNull("initialize must return a result");
            (resp[2]["result"]?["tools"] as JArray)?.Count.Should().BeGreaterThan(0, "tools/list must return tools");
        }
        finally { Kill(p); }
    }

    [Fact]
    public void Daemon_mode_creates_socket_and_answers_a_jsonrpc_request()
    {
        string sock = Path.Combine(Path.GetTempPath(), $"mtd_{Guid.NewGuid():N}.sock");
        using var p = Process.Start(Psi("--daemon", sock))!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(sock) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            File.Exists(sock).Should().BeTrue("the daemon must create the unix socket");

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(sock));
            using var ns = new NetworkStream(client, ownsSocket: true);
            using var w = new StreamWriter(ns) { AutoFlush = true };
            using var r = new StreamReader(ns);

            w.WriteLine(Req(1, "initialize",
                "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}"));
            var resp = ReadResponses(r, new[] { 1 }, 20);
            resp.Should().ContainKey(1);
            resp[1]["result"].Should().NotBeNull("the daemon must answer a JSON-RPC request over the socket");
        }
        finally { Kill(p); try { File.Delete(sock); } catch { /* best effort */ } }
    }

    [Fact]
    public void OneShot_cli_dispatches_output_command_from_the_repl_registry()
    {
        // One-shot CLI mode still works AND OutputCommand is registered in the
        // REPL command registry (it now lives in Commands/OutputCommand.cs).
        using var p = Process.Start(Psi("output", "json"))!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(20_000).Should().BeTrue();
        outp.Should().Contain("Json", "one-shot CLI must dispatch 'output' via the REPL registry");
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string Req(int id, string method, string? paramsJson) =>
        paramsJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

    // Single background pass reads lines until every requested id is seen or it
    // times out — avoids concurrent reads on one stream.
    private static Dictionary<int, JObject> ReadResponses(TextReader r, int[] ids, int timeoutSec)
    {
        var results = new Dictionary<int, JObject>();
        var want = new HashSet<int>(ids);
        var tcs = new TaskCompletionSource<bool>();
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = r.ReadLine()) is not null)
                {
                    if (line.Length == 0) continue;
                    JObject o;
                    try { o = JObject.Parse(line); } catch { continue; }
                    int? oid = o["id"]?.Value<int?>();
                    if (oid.HasValue && want.Contains(oid.Value))
                    {
                        results[oid.Value] = o;
                        if (results.Count == ids.Length) { tcs.TrySetResult(true); return; }
                    }
                }
            }
            catch { /* stream closed */ }
            tcs.TrySetResult(false);
        });
        tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSec));
        return results;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
