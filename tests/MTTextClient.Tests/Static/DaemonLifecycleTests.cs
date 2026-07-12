using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Daemon lifecycle: socket permissions, live/stale-socket handling, multiple
/// concurrent clients on one daemon, survival of a client disconnect, and
/// graceful SIGTERM shutdown (socket unlinked, process exits). Spawns the built
/// binary; no MTCore required. SSE is disabled in-test to avoid contending for
/// the shared event port.
/// </summary>
[Trait("Category", "Static")]
public sealed class DaemonLifecycleTests
{
    private static string TmpSock() => Path.Combine(Path.GetTempPath(), $"mtd_{Guid.NewGuid():N}.sock");

    private static Process StartDaemon(string sock)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RepoPaths.McpBinary,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--daemon");
        psi.ArgumentList.Add(sock);
        psi.Environment["MTC_SSE_DISABLE"] = "1"; // don't contend for the shared SSE port in tests
        psi.Environment["DOTNET_NOLOGO"] = "1";
        var p = Process.Start(psi)!;
        _ = Task.Run(() => { try { while (p.StandardError.ReadLine() is not null) { } } catch { } });
        _ = Task.Run(() => { try { while (p.StandardOutput.ReadLine() is not null) { } } catch { } });
        return p;
    }

    private static bool WaitForSocket(string sock, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (File.Exists(sock)) return true; Thread.Sleep(50); }
        return false;
    }

    private static string Req(int id, string method, string? paramsJson) =>
        paramsJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

    private const string InitParams =
        "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}";

    private static JObject Initialize(string sock, int id = 1, int timeoutSec = 15)
    {
        using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        c.Connect(new UnixDomainSocketEndPoint(sock));
        using var ns = new NetworkStream(c, ownsSocket: true);
        using var w = new StreamWriter(ns) { AutoFlush = true };
        using var r = new StreamReader(ns);
        w.WriteLine(Req(id, "initialize", InitParams));
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var t = r.ReadLineAsync();
            if (!t.Wait(TimeSpan.FromSeconds(timeoutSec))) break;
            string? line = t.Result;
            if (line is null) break;
            if (line.Length == 0) continue;
            try { var o = JObject.Parse(line); if (o["id"]?.Value<int?>() == id) return o; } catch { }
        }
        throw new TimeoutException($"no initialize response id={id}");
    }

    private static void Stop(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }

    [Fact]
    public void Daemon_creates_socket_with_0660_permissions()
    {
        string sock = TmpSock();
        var p = StartDaemon(sock);
        try
        {
            WaitForSocket(sock, 15).Should().BeTrue("the daemon must create the socket");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var mode = File.GetUnixFileMode(sock);
                var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                               UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
                mode.Should().Be(expected, "the socket must be 0660 (owner+group rw, no world access)");
            }
        }
        finally { Stop(p); try { File.Delete(sock); } catch { } }
    }

    [Fact]
    public void Daemon_refuses_to_start_on_a_live_socket()
    {
        string sock = TmpSock();
        var first = StartDaemon(sock);
        try
        {
            WaitForSocket(sock, 15).Should().BeTrue();
            var second = StartDaemon(sock);
            second.WaitForExit(15_000).Should().BeTrue("the second daemon must exit rather than clobber the live socket");
            second.ExitCode.Should().Be(3, "a live socket is a refuse-to-start condition");
        }
        finally { Stop(first); try { File.Delete(sock); } catch { } }
    }

    [Fact]
    public void Daemon_replaces_a_stale_socket_file()
    {
        string sock = TmpSock();
        File.WriteAllText(sock, "");           // a stale, non-listening file at the path
        var p = StartDaemon(sock);
        try
        {
            // The file exists from the start, so poll by actually connecting:
            // the daemon must delete the stale file, bind, and start serving.
            JObject? resp = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try { resp = Initialize(sock, timeoutSec: 3); break; }
                catch { Thread.Sleep(200); }
            }
            resp.Should().NotBeNull("the daemon must replace the stale file and serve");
            resp!["result"].Should().NotBeNull();
        }
        finally { Stop(p); try { File.Delete(sock); } catch { } }
    }

    [Fact]
    public void Daemon_serves_two_concurrent_clients()
    {
        string sock = TmpSock();
        var p = StartDaemon(sock);
        try
        {
            WaitForSocket(sock, 15).Should().BeTrue();
            Initialize(sock, id: 1)["result"].Should().NotBeNull();
            Initialize(sock, id: 2)["result"].Should().NotBeNull("a second client must be served concurrently by the same daemon");
        }
        finally { Stop(p); try { File.Delete(sock); } catch { } }
    }

    [Fact]
    public void Client_disconnect_leaves_daemon_serving()
    {
        string sock = TmpSock();
        var p = StartDaemon(sock);
        try
        {
            WaitForSocket(sock, 15).Should().BeTrue();
            // Client A connects and disconnects (Initialize opens+closes its own socket).
            Initialize(sock, id: 1)["result"].Should().NotBeNull();
            // Client B must still be served — one client's disconnect can't take the daemon down.
            Initialize(sock, id: 2)["result"].Should().NotBeNull("the daemon survives a client disconnect");
        }
        finally { Stop(p); try { File.Delete(sock); } catch { } }
    }

    [Fact]
    public void Sigterm_unlinks_socket_and_exits()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // SIGTERM is a POSIX signal
        string sock = TmpSock();
        var p = StartDaemon(sock);
        try
        {
            WaitForSocket(sock, 15).Should().BeTrue();
            using (var killer = Process.Start(new ProcessStartInfo("kill", $"-s TERM {p.Id}") { UseShellExecute = false }))
                killer!.WaitForExit(5_000);

            p.WaitForExit(15_000).Should().BeTrue("the daemon must exit on SIGTERM");
            File.Exists(sock).Should().BeFalse("SIGTERM must unlink the socket during graceful shutdown");
        }
        finally { Stop(p); try { File.Delete(sock); } catch { } }
    }
}
