using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    [Fact]
    public async Task OneShot_diagnostics_emit_complete_json_without_banner_or_table_truncation()
    {
        string home = Path.Combine(Path.GetTempPath(), "diagnostic-cli-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string[] names = Enumerable.Range(0, 77).Select(index => "synthetic-missing-" + index).ToArray();
        ProcessStartInfo start = Psi("diag", "fleet", "collect", "--profiles", string.Join(',', names), "--operation", "health", "--diag-port", "4343");
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        using Process process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            JObject response = JObject.Parse(await output);
            JArray rows = (JArray)response["data"]!["rows"]!;
            response["success"]!.Value<bool>().Should().BeFalse();
            rows.Should().HaveCount(77);
            rows.Select(row => row["requested_profile"]!.Value<string>()).Should().Equal(names);
            rows.Should().OnlyContain(row => row["code"]!.Value<string>() == "not_configured");
            response["data"]!["coverage"]!["not_attempted"]!.Value<int>().Should().Be(77);
            response["data"]!["coverage"]!["completed"]!.Value<int>().Should().Be(0);
            process.ExitCode.Should().Be(1, "unvalidated fleet rows must make a one-shot check fail");
        }
        finally
        {
            Kill(process);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task OneShot_failed_diagnostic_returns_nonzero_and_keeps_json_error()
    {
        using Process process = Process.Start(Psi("diag", "logs", "synthetic", "--lines", "501"))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            JObject response = JObject.Parse(await output);
            response["success"]!.Value<bool>().Should().BeFalse();
            process.ExitCode.Should().Be(1);
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public void Mcp_failed_fleet_marks_error_and_preserves_coverage()
    {
        string home = Path.Combine(Path.GetTempPath(), "diagnostic-mcp-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        ProcessStartInfo start = Psi("--mcp");
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        using Process process = Process.Start(start)!;
        try
        {
            process.StandardInput.WriteLine(Req(1, "tools/call", "{\"name\":\"mt_diag_fleet_collect\",\"arguments\":{\"profiles\":[\"synthetic-missing\"],\"operation\":\"health\",\"diag_port\":4343}}"));
            process.StandardInput.Flush();
            JObject response = ReadResponses(process.StandardOutput, new[] { 1 }, 15)[1];
            response["result"]!["isError"]!.Value<bool>().Should().BeTrue();
            JObject content = JObject.Parse(response["result"]!["content"]![0]!["text"]!.Value<string>()!);
            content["success"]!.Value<bool>().Should().BeFalse();
            content["data"]!["coverage"]!["not_attempted"]!.Value<int>().Should().Be(1);
            content["data"]!["rows"]!.Should().HaveCount(1);
        }
        finally
        {
            Kill(process);
            Directory.Delete(home, recursive: true);
        }
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
