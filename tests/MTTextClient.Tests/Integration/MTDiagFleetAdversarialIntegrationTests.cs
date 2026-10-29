using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteNetLib;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MTTextClient.Tests.Integration;

/// <summary>Exercises the production client against disposable hostile native loopback peers.</summary>
[Trait("Category", "Integration")]
public sealed class MTDiagFleetAdversarialIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MTDiagFleetAdversarialIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(PeerMode.WrongDomain)]
    [InlineData(PeerMode.WrongVersion)]
    [InlineData(PeerMode.WrongRequest)]
    [InlineData(PeerMode.WrongProfile)]
    [InlineData(PeerMode.Malformed)]
    public async Task NativePeer_IncompatibleRepliesAreValidationFailuresWithoutEvidence(PeerMode mode)
    {
        using var peer = new ControlledPeer(mode);
        await peer.StartAsync();

        DiagnosticResult result = await peer.ExecuteAsync(DiagnosticRequest.Capabilities(), TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData(PeerMode.Silent)]
    [InlineData(PeerMode.Slow)]
    [InlineData(PeerMode.WrongSeed)]
    public async Task NativePeer_UnavailableRepliesStayBoundedAndDoNotSucceed(PeerMode mode)
    {
        using var peer = new ControlledPeer(mode);
        await peer.StartAsync();
        Stopwatch elapsed = Stopwatch.StartNew();

        DiagnosticResult result = await peer.ExecuteAsync(DiagnosticRequest.Capabilities(), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        _output.WriteLine("mode={0}; elapsed_ms={1}; code={2}; kind={3}", mode, elapsed.ElapsedMilliseconds, result.Code, result.Kind);
        result.Kind.Should().BeOneOf(DiagnosticResultKind.Timeout, DiagnosticResultKind.TransportFailure);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task NativePeer_DeniedSourceRemainsValidatedButCoverageDoesNotClaimLogsAvailable()
    {
        using var peer = new ControlledPeer(PeerMode.DeniedSource);
        await peer.StartAsync();
        var collector = new DiagnosticFleetCollector(new MtDiagClient());

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { peer.Profile.Name }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromSeconds(2), false),
            new[] { peer.Profile }, CancellationToken.None);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Result!.Kind.Should().Be(DiagnosticResultKind.Success);
        ((JObject)result.Rows[0].Result!.Data!)["status"]!.Value<string>().Should().Be("denied");
        result.Coverage.Validated.Should().Be(1);
        result.Coverage.SourceAvailable.Should().Be(0);
        result.Coverage.SourceUnavailable.Should().Be(1);
    }

    [Fact]
    public async Task NativePeer_CallerCancellationCompletesAnHonestCancelledRow()
    {
        using var peer = new ControlledPeer(PeerMode.Silent);
        await peer.StartAsync();
        var collector = new DiagnosticFleetCollector(new MtDiagClient());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { peer.Profile.Name }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromSeconds(2), false),
            new[] { peer.Profile }, cancellation.Token);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Kind.Should().Be(DiagnosticResultKind.Cancelled);
        result.Rows[0].Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task NativePeer_ValidationFailureIsNeverRetriedOrRecovered()
    {
        using var peer = new ControlledPeer(PeerMode.Malformed);
        await peer.StartAsync();
        var collector = new DiagnosticFleetCollector(new MtDiagClient());

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { peer.Profile.Name }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromSeconds(2), true),
            new[] { peer.Profile }, CancellationToken.None);

        result.Rows[0].Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Rows[0].Attempts.Should().ContainSingle();
        result.Rows[0].Recovered.Should().BeFalse();
    }

    [Theory]
    [InlineData(PeerMode.MaliciousSuccess)]
    [InlineData(PeerMode.MaliciousFailure)]
    public async Task NativePeer_EchoedRawTokenAndDerivedSeedNeverEscapeClientBoundary(PeerMode mode)
    {
        using var peer = new ControlledPeer(mode);
        await peer.StartAsync();

        DiagnosticResult result = await peer.ExecuteAsync(DiagnosticRequest.Capabilities(), TimeSpan.FromSeconds(1), CancellationToken.None);

        string visible = string.Join(" ", result.Code, result.Error ?? string.Empty, result.Data?.ToString() ?? string.Empty, result.Metadata?.ToString() ?? string.Empty);
        visible.Should().NotContain(peer.Profile.ClientToken);
        visible.Should().NotContain(peer.Profile.GetConnectionKeySeed());
        if (mode == PeerMode.MaliciousSuccess) result.Kind.Should().Be(DiagnosticResultKind.Success);
        else result.Kind.Should().Be(DiagnosticResultKind.ServerError);
    }

    public enum PeerMode { WrongDomain, WrongVersion, WrongRequest, WrongProfile, WrongSeed, Malformed, Silent, Slow, DeniedSource, MaliciousSuccess, MaliciousFailure }

    private sealed class ControlledPeer : IDisposable
    {
        private readonly PeerMode _mode;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _manager;
        private readonly NativeDiagnosticVendorAdapter _adapter;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly List<(DateTimeOffset Due, NetPeer Peer, byte[] Wire)> _scheduled = new();
        private Task? _pump;
        private int _requests;

        internal ControlledPeer(PeerMode mode)
        {
            _mode = mode;
            int port = ReservePort();
            Profile = new ServerProfile
            {
                Name = "adversarial-peer",
                Address = IPAddress.Loopback.ToString(),
                Port = port == 65535 ? 65534 : port + 1,
                ClientToken = "synthetic-client-token",
                Exchange = ExchangeType.BINANCE,
                Tags = new Dictionary<string, string> { ["mtdiag_port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            };
            NativeDiagnosticVendorAdapter.TryCreate(Profile.GetConnectionKeySeed(), out NativeDiagnosticVendorAdapter? adapter).Should().BeTrue();
            _adapter = adapter!;
            Port = port;
            _listener.ConnectionRequestEvent += request =>
            {
                if (_mode == PeerMode.WrongSeed) request.AcceptIfKey("BINANCE|wrong-synthetic-seed");
                else request.AcceptIfKey(Profile.GetConnectionKeySeed());
            };
            _listener.NetworkReceiveEvent += Receive;
            _manager = new NetManager(_listener) { UpdateTime = 15, MaxConnectAttempts = 1, DisconnectTimeout = 250 };
        }

        internal int Port { get; }
        internal ServerProfile Profile { get; }

        internal Task StartAsync()
        {
            _manager.Start(Port).Should().BeTrue();
            _pump = Task.Run(PumpAsync);
            return Task.CompletedTask;
        }

        internal Task<DiagnosticResult> ExecuteAsync(DiagnosticRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
            new MtDiagClient().ExecuteAsync(Profile, new DiagnosticRoute(Profile.Address, Port, "tag"), request, new DiagnosticIdentityEvidence(Profile.Name, null, new[] { Profile.Name }), timeout, cancellationToken);

        private async Task PumpAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    _manager.PollEvents();
                    FlushScheduled();
                    await Task.Delay(2, _shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Receive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            try
            {
                byte[] wire = new[] { reader.GetByte() }.Concat(reader.GetRemainingBytes()).ToArray();
                if (channel != 0 || delivery != DeliveryMethod.ReliableOrdered || !_adapter.TryReadWirePacket(wire, NetworkMessageType.DEBUG_REQUEST, out int nativeRequestId, out string? text) || text is null || !NativeDiagnosticProtocol.TryParseRequest(Encoding.UTF8.GetBytes(text), Profile.Name, out NativeDiagnosticEnvelope request)) return;
                _requests++;
                if (_mode is PeerMode.Silent or PeerMode.WrongSeed) return;
                if (_mode == PeerMode.Malformed)
                {
                    peer.Send(new byte[] { 1, 2, 3 }, 0, DeliveryMethod.ReliableOrdered);
                    return;
                }

                byte[] reply = CreateReply(nativeRequestId, request);
                if (_mode == PeerMode.Slow)
                {
                    _scheduled.Add((DateTimeOffset.UtcNow.AddMilliseconds(600), peer, reply));
                    return;
                }
                peer.Send(reply, 0, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                reader.Recycle();
            }
        }

        private byte[] CreateReply(int nativeRequestId, NativeDiagnosticEnvelope request)
        {
            if (_mode is PeerMode.WrongDomain or PeerMode.WrongVersion or PeerMode.WrongRequest or PeerMode.WrongProfile)
            {
                var invalid = new JObject
                {
                    ["domain"] = _mode == PeerMode.WrongDomain ? "wrong-domain" : NativeDiagnosticProtocol.Domain,
                    ["version"] = _mode == PeerMode.WrongVersion ? 99 : NativeDiagnosticProtocol.Version,
                    ["request_id"] = _mode == PeerMode.WrongRequest ? "wrong-request" : request.RequestId,
                    ["operation"] = request.Operation,
                    ["profile"] = _mode == PeerMode.WrongProfile ? "wrong-profile" : Profile.Name,
                    ["args"] = new JObject(),
                    ["ok"] = true,
                    ["code"] = "success",
                    ["source"] = "companion",
                    ["observed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["data"] = new JObject { ["capabilities"] = new JArray("capabilities", "health") }
                };
                return Pack(nativeRequestId, invalid.ToString(Newtonsoft.Json.Formatting.None));
            }

            bool maliciousFailure = _mode == PeerMode.MaliciousFailure;
            JObject data = request.Operation == "capabilities"
                ? new JObject { ["capabilities"] = new JArray("capabilities", "health") }
                : new JObject { ["status"] = _mode == PeerMode.DeniedSource ? "denied" : "available" };
            if (_mode == PeerMode.MaliciousSuccess) data["message"] = Profile.ClientToken + " " + Profile.GetConnectionKeySeed();
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, request.RequestId, request.Operation, Profile.Name, new JObject(), !maliciousFailure, maliciousFailure ? "refused" : "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), maliciousFailure ? null : data, maliciousFailure ? Profile.ClientToken + " " + Profile.GetConnectionKeySeed() : null);
            NativeDiagnosticProtocol.TrySerialize(reply, out byte[] envelope).Should().BeTrue();
            return Pack(nativeRequestId, Encoding.UTF8.GetString(envelope));
        }

        private byte[] Pack(int nativeRequestId, string message)
        {
            _adapter.TryCreateWirePacket(NetworkMessageType.DEBUG_RESULT, nativeRequestId, message, out byte[] wire).Should().BeTrue();
            return wire;
        }

        private void FlushScheduled()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((DateTimeOffset _, NetPeer peer, byte[] wire) in _scheduled.Where(item => item.Due <= now).ToArray())
            {
                peer.Send(wire, 0, DeliveryMethod.ReliableOrdered);
            }
            _scheduled.RemoveAll(item => item.Due <= now);
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try { _pump?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _manager.Stop();
            _shutdown.Dispose();
        }

        private static int ReservePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }
}
