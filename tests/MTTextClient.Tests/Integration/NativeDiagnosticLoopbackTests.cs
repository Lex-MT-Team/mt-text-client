using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FluentAssertions;
using LiteNetLib;
using MTShared.Network;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class NativeDiagnosticLoopbackTests
{
    [Fact]
    public void Protocol_UnboundProfileRequestCanCarryCorrelatedPayloadFreeRefusal()
    {
        NativeDiagnosticEnvelope request = NativeDiagnosticProtocol.CreateRequest("capabilities", "unbound-synthetic-profile", "unbound-request", new JObject());
        NativeDiagnosticProtocol.TrySerialize(request, out byte[] requestBytes).Should().BeTrue();
        NativeDiagnosticProtocol.TryParseRequestForProfileRefusal(requestBytes, out NativeDiagnosticEnvelope parsedRequest).Should().BeTrue();
        var refusal = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, parsedRequest.RequestId, parsedRequest.Operation, parsedRequest.Profile, new JObject(), false, "refused", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, "profile-unbound");

        NativeDiagnosticProtocol.TrySerialize(refusal, out byte[] refusalBytes).Should().BeTrue();
        NativeDiagnosticProtocol.TryParseReply(refusalBytes, "unbound-request", "unbound-synthetic-profile", out NativeDiagnosticEnvelope parsedRefusal).Should().BeTrue();

        parsedRefusal.Ok.Should().BeFalse();
        parsedRefusal.Data.Should().BeNull();
        parsedRefusal.Error.Should().Be("profile-unbound");
    }

    private const string SyntheticSeed = "BINANCE|synthetic-native-seed";

    [Fact]
    public void NativeEnvelope_CapabilitiesRoundTrip_PreservesEnvelopeAndVendorRequestIds()
    {
        const string requestId = "request-123";
        NativeDiagnosticEnvelope request = NativeDiagnosticProtocol.CreateRequest("capabilities", "synthetic-profile", requestId, null);

        NativeDiagnosticProtocol.TrySerialize(request, out byte[] encoded).Should().BeTrue();
        NativeDiagnosticProtocol.TryParseRequest(encoded, requestId, "synthetic-profile", out NativeDiagnosticEnvelope parsed).Should().BeTrue();

        parsed.Operation.Should().Be("capabilities");
        parsed.RequestId.Should().Be(requestId);
        parsed.Profile.Should().Be("synthetic-profile");
        Encoding.UTF8.GetByteCount(parsed.Domain).Should().BeGreaterThan(0);
    }

    [Fact]
    public void NativeVendorAdapter_UncompressedDebugPacket_RoundTripsAndRejectsCompressedMarker()
    {
        NativeDiagnosticVendorAdapter.TryCreate(SyntheticSeed, out NativeDiagnosticVendorAdapter? adapter).Should().BeTrue();
        NativeDiagnosticEnvelope request = NativeDiagnosticProtocol.CreateRequest("capabilities", "synthetic-profile", "envelope-123", null);
        NativeDiagnosticProtocol.TrySerialize(request, out byte[] envelope).Should().BeTrue();

        adapter!.TryCreateWirePacket(NetworkMessageType.DEBUG_REQUEST, 101, Encoding.UTF8.GetString(envelope), out byte[] wire).Should().BeTrue();
        adapter.TryReadWirePacket(wire, NetworkMessageType.DEBUG_REQUEST, out int nativeRequestId, out string? debugMessage).Should().BeTrue();
        NativeDiagnosticProtocol.TryParseRequest(Encoding.UTF8.GetBytes(debugMessage!), "envelope-123", "synthetic-profile", out _).Should().BeTrue();
        nativeRequestId.Should().Be(101);

        wire[7] |= 0x80;
        adapter.TryReadWirePacket(wire, NetworkMessageType.DEBUG_REQUEST, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void NativeLiteNetLibLoopback_CapabilitiesRequest_CorrelatesNativeAndEnvelopeIds()
    {
        NativeDiagnosticVendorAdapter.TryCreate(SyntheticSeed, out NativeDiagnosticVendorAdapter? serverAdapter).Should().BeTrue();
        NativeDiagnosticVendorAdapter.TryCreate(SyntheticSeed, out NativeDiagnosticVendorAdapter? clientAdapter).Should().BeTrue();
        using var portReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)portReservation.Client.LocalEndPoint!).Port;
        portReservation.Dispose();

        const int vendorRequestId = 101;
        const string envelopeRequestId = "envelope-101";
        NativeDiagnosticEnvelope request = NativeDiagnosticProtocol.CreateRequest("capabilities", "synthetic-profile", envelopeRequestId, null);
        NativeDiagnosticProtocol.TrySerialize(request, out byte[] requestEnvelope).Should().BeTrue();
        clientAdapter!.TryCreateWirePacket(NetworkMessageType.DEBUG_REQUEST, vendorRequestId, Encoding.UTF8.GetString(requestEnvelope), out byte[] requestWire).Should().BeTrue();

        string? receivedReply = null;
        int receivedNativeId = 0;
        var serverListener = new EventBasedNetListener();
        serverListener.ConnectionRequestEvent += connectionRequest => connectionRequest.AcceptIfKey(SyntheticSeed);
        serverListener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            try
            {
                byte[] wire = new[] { reader.GetByte() }.Concat(reader.GetRemainingBytes()).ToArray();
                if (!serverAdapter!.TryReadWirePacket(wire, NetworkMessageType.DEBUG_REQUEST, out int nativeId, out string? debugMessage) || !NativeDiagnosticProtocol.TryParseRequest(Encoding.UTF8.GetBytes(debugMessage!), envelopeRequestId, "synthetic-profile", out _))
                {
                    return;
                }
                var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, envelopeRequestId, "capabilities", "synthetic-profile", new Newtonsoft.Json.Linq.JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new Newtonsoft.Json.Linq.JObject { ["capabilities"] = new Newtonsoft.Json.Linq.JArray("capabilities") });
                NativeDiagnosticProtocol.TrySerialize(reply, out byte[] replyEnvelope).Should().BeTrue();
                serverAdapter.TryCreateWirePacket(NetworkMessageType.DEBUG_RESULT, nativeId, Encoding.UTF8.GetString(replyEnvelope), out byte[] replyWire).Should().BeTrue();
                peer.Send(replyWire, 0, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                reader.Recycle();
            }
        };

        var clientListener = new EventBasedNetListener();
        NetPeer? clientPeer = null;
        clientListener.PeerConnectedEvent += peer => clientPeer = peer;
        clientListener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try
            {
                byte[] wire = new[] { reader.GetByte() }.Concat(reader.GetRemainingBytes()).ToArray();
                if (clientAdapter!.TryReadWirePacket(wire, NetworkMessageType.DEBUG_RESULT, out int nativeId, out string? debugMessage))
                {
                    receivedNativeId = nativeId;
                    receivedReply = debugMessage;
                }
            }
            finally
            {
                reader.Recycle();
            }
        };

        var server = new NetManager(serverListener);
        var client = new NetManager(clientListener);
        try
        {
            server.Start(port).Should().BeTrue();
            client.Start().Should().BeTrue();
            client.Connect(IPAddress.Loopback.ToString(), port, SyntheticSeed);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (clientPeer is null && DateTime.UtcNow < deadline)
            {
                server.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }
            clientPeer.Should().NotBeNull();
            clientPeer!.Send(requestWire, 0, DeliveryMethod.ReliableOrdered);
            while (receivedReply is null && DateTime.UtcNow < deadline)
            {
                server.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            receivedNativeId.Should().Be(vendorRequestId);
            NativeDiagnosticProtocol.TryParseReply(Encoding.UTF8.GetBytes(receivedReply!), envelopeRequestId, "synthetic-profile", out NativeDiagnosticEnvelope parsed).Should().BeTrue();
            parsed.RequestId.Should().Be(envelopeRequestId);
            parsed.Operation.Should().Be("capabilities");
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }
}
