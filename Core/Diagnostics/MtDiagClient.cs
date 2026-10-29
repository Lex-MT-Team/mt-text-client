using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MTShared.Network;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Executes one bounded native diagnostic session without using MTCore control state.</summary>
public sealed partial class MtDiagClient : IDiagnosticExecutor, IDisposable
{
    /// <summary>Default deadline used by the command surface.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Largest caller-selected deadline accepted by this client.</summary>
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(15);

    private const int MaximumFragments = 128;
    private const int PollIntervalMilliseconds = 5;
    private const int DisconnectTimeoutMilliseconds = 1000;
    private const int UpdateIntervalMilliseconds = 15;
    private const int MaximumWireBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <inheritdoc />
    public async Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.Cancelled, "caller-cancelled");
        }
        if (!TryValidateInputs(profile, route, request, evidence, timeout))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-request");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        SessionEntry? entry = null;
        bool entered = false;
        bool keep = false;
        bool pooled = request.History is not null;
        try
        {
            string seed = profile.GetConnectionKeySeed();
            entry = Acquire(profile, route, evidence, seed, pooled);
            if (entry is null) return DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-client-capacity");
            await entry.Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            deadline.Token.ThrowIfCancellationRequested();
            if (entry.Session is not null && !entry.Session.IsConnected) entry.Reset();
            if (entry.Session is null)
            {
                if (!NativeDiagnosticVendorAdapter.TryCreate(seed, out NativeDiagnosticVendorAdapter? adapter))
                    return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "native-adapter-unavailable");
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(route.Address, deadline.Token).ConfigureAwait(false);
                if (addresses.Length == 0) return DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-transport-failure");
                entry.Session = new NativeDiagnosticSession(adapter!, new IPEndPoint(addresses[0], route.Port), route, profile.Name, profile.ClientToken, seed, evidence);
                entry.Capabilities = await entry.Session.SendAsync(DiagnosticRequest.Capabilities(), deadline.Token).ConfigureAwait(false);
                if (entry.Capabilities.Kind != DiagnosticResultKind.Success) return entry.Capabilities;
            }
            DiagnosticResult result;
            if (request.Operation == DiagnosticOperation.Capabilities) result = entry.Capabilities!;
            else if (!Supports(entry.Capabilities!.Data, request.Operation) ||
                     (request.History?.UnknownTime is "report-only" && !(entry.Capabilities.Data?["extensions"]?["history_options_v1"] is JValue { Type: JTokenType.Boolean } option && option.Value<bool>())))
                result = DiagnosticResult.Failure(DiagnosticResultKind.UnsupportedCapability, "capability-not-advertised");
            else result = await entry.Session.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (request.Operation == DiagnosticOperation.HistoryPage && result.Kind == DiagnosticResultKind.Success &&
                (result.Data is not JObject page || !DiagnosticHistoryExport.ValidatePage(page, request.History!)))
                return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-history-page");
            keep = result.Kind is DiagnosticResultKind.Success or DiagnosticResultKind.ServerError or DiagnosticResultKind.UnsupportedCapability;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.Cancelled, "caller-cancelled");
        }
        catch (OperationCanceledException)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.Timeout, "deadline-exceeded");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or SocketException)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-transport-failure");
        }
        finally
        {
            if (entry is not null)
            {
                if (entered)
                {
                    // An uncertain reply must never be consumed by a later request. No replay here.
                    if (!keep || !pooled || _lifetime.IsCancellationRequested) entry.Reset();
                    entry.Gate.Release();
                }
                Release(entry, pooled);
            }
        }
    }

    private static bool TryValidateInputs(ServerProfile? profile, DiagnosticRoute? route, DiagnosticRequest? request, DiagnosticIdentityEvidence? evidence, TimeSpan timeout)
    {
        if (profile is null || route is null || request is null || evidence is null || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Address) || string.IsNullOrWhiteSpace(profile.ClientToken) || string.IsNullOrWhiteSpace(route.Address) || !string.Equals(profile.Address, route.Address, StringComparison.Ordinal) || route.Port is < 1 or > 65535 || route.Port == profile.Port || (route.Source is not "explicit" and not "tag") || !string.Equals(evidence.SelectedProfile, profile.Name, StringComparison.OrdinalIgnoreCase) || timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            return false;
        }
        if (request.Operation is DiagnosticOperation.HistoryPage or DiagnosticOperation.HistoryClose)
            return request.Lines is null && request.Grep is null && request.History is { IsValid: true } && (request.Operation != DiagnosticOperation.HistoryClose || request.History.Cursor is not null);
        if (request.History is not null) return false;
        if (request.Operation is DiagnosticOperation.Capabilities or DiagnosticOperation.Health or DiagnosticOperation.GuardianStatus)
        {
            return request.Lines is null && request.Grep is null;
        }
        if (request.Lines is not > 0 or > DiagnosticRequest.MaximumLines || request.Operation is not (DiagnosticOperation.Logs or DiagnosticOperation.GuardianEvents))
        {
            return false;
        }
        try
        {
            return request.Operation != DiagnosticOperation.GuardianEvents
                ? request.Grep is null || StrictUtf8.GetByteCount(request.Grep) <= DiagnosticRequest.MaximumGrepUtf8Bytes
                : request.Grep is null;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool Supports(JToken? data, DiagnosticOperation operation)
    {
        if (data is not JObject obj || obj["capabilities"] is not JArray values)
        {
            return false;
        }
        if (operation is DiagnosticOperation.HistoryPage or DiagnosticOperation.HistoryClose)
            return obj["extensions"]?["history_v1"]?.Type == JTokenType.Boolean && obj["extensions"]!["history_v1"]!.Value<bool>();
        string required = OperationName(operation);
        foreach (JToken value in values)
        {
            if (value.Type == JTokenType.String && string.Equals(value.Value<string>(), required, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCapabilitySet(JToken? data)
    {
        if (data is not JObject obj || obj["capabilities"] is not JArray values || values.Count == 0)
        {
            return false;
        }
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (JToken value in values)
        {
            if (value.Type != JTokenType.String || !NativeDiagnosticProtocol.IsOperationAllowed(value.Value<string>()) || !seen.Add(value.Value<string>()!))
            {
                return false;
            }
        }
        return seen.Contains("capabilities");
    }

    private static string OperationName(DiagnosticOperation operation) => operation switch
    {
        DiagnosticOperation.Capabilities => "capabilities",
        DiagnosticOperation.Health => "health",
        DiagnosticOperation.Logs => "logs",
        DiagnosticOperation.GuardianStatus => "guardian-status",
        DiagnosticOperation.GuardianEvents => "guardian-events",
        DiagnosticOperation.HistoryPage => "history-page",
        DiagnosticOperation.HistoryClose => "history-close",
        _ => string.Empty
    };

    private sealed class NativeDiagnosticSession : IDisposable
    {
        private readonly NativeDiagnosticVendorAdapter _adapter;
        private readonly IPEndPoint _endpoint;
        private readonly DiagnosticRoute _route;
        private readonly string _profile;
        private readonly string _sensitiveToken;
        private readonly string _connectionKeySeed;
        private readonly DiagnosticIdentityEvidence _evidence;
        private readonly EventBasedNetListener _listener;
        private readonly NetManager _manager;
        private NetPeer? _peer;
        private Reply? _reply;
        private bool _connectionRejected;
        private bool _disposed;

        public NativeDiagnosticSession(NativeDiagnosticVendorAdapter adapter, IPEndPoint endpoint, DiagnosticRoute route, string profile, string sensitiveToken, string connectionKeySeed, DiagnosticIdentityEvidence evidence)
        {
            _adapter = adapter;
            _endpoint = endpoint;
            _route = route;
            _profile = profile;
            _sensitiveToken = sensitiveToken;
            _connectionKeySeed = connectionKeySeed;
            _evidence = evidence;
            _listener = new EventBasedNetListener();
            _listener.ConnectionRequestEvent += request => request.Reject();
            _listener.PeerConnectedEvent += peer =>
            {
                if (_peer is null)
                {
                    _peer = peer;
                }
                else if (_peer.Id != peer.Id)
                {
                    peer.Disconnect();
                }
            };
            _listener.NetworkReceiveEvent += OnReceive;
            _listener.PeerDisconnectedEvent += (_, info) =>
            {
                if (info.Reason == DisconnectReason.ConnectionRejected)
                {
                    _connectionRejected = true;
                }
            };
            _manager = new NetManager(_listener)
            {
                MaxConnectAttempts = 1,
                DisconnectTimeout = DisconnectTimeoutMilliseconds,
                UpdateTime = UpdateIntervalMilliseconds,
                MaxFragmentsCount = MaximumFragments,
                UnconnectedMessagesEnabled = false
            };
            if (!_manager.Start())
            {
                _manager.Stop();
                throw new InvalidOperationException("Native diagnostic manager did not start.");
            }
        }

        public bool IsConnected => !_disposed && _peer?.ConnectionState == ConnectionState.Connected;

        public async Task<DiagnosticResult> SendAsync(DiagnosticRequest request, CancellationToken cancellationToken)
        {
            string envelopeId = Guid.NewGuid().ToString("N");
            int nativeId = Random.Shared.Next(1, int.MaxValue);
            NativeDiagnosticEnvelope envelope = NativeDiagnosticProtocol.CreateRequest(OperationName(request.Operation), _profile, envelopeId, BuildArgs(request));
            if (!NativeDiagnosticProtocol.TrySerialize(envelope, out byte[] bytes) || !_adapter.TryCreateWirePacket(NetworkMessageType.DEBUG_REQUEST, nativeId, StrictUtf8.GetString(bytes), out byte[] wire))
            {
                return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "request-serialization-failed");
            }

            _reply = null;
            if (_peer is null)
            {
                _peer = _manager.Connect(_endpoint, _connectionKeySeed);
            }
            Stopwatch wait = Stopwatch.StartNew();
            while (_peer?.ConnectionState != ConnectionState.Connected)
            {
                await PollAsync(wait, cancellationToken).ConfigureAwait(false);
                if (_connectionRejected)
                {
                    // A transport rejection is not authenticated proof of the remote identity
                    // or of a specific credential defect. Never expose its attached bytes.
                    return DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-connection-rejected");
                }
            }
            _peer.Send(wire, 0, DeliveryMethod.ReliableOrdered);
            while (_reply is null)
            {
                await PollAsync(wait, cancellationToken).ConfigureAwait(false);
            }

            Reply reply = _reply!;
            if (reply.Malformed || reply.NativeRequestId != nativeId || !NativeDiagnosticProtocol.TryParseReply(reply.Envelope, envelopeId, _profile, out NativeDiagnosticEnvelope response) || !string.Equals(response.Operation, OperationName(request.Operation), StringComparison.Ordinal))
            {
                return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "reply-correlation-failed");
            }
            DiagnosticResult result = DiagnosticReplyValidator.ValidateNative(response, _profile, _route, _evidence, _sensitiveToken, _connectionKeySeed);
            return request.Operation == DiagnosticOperation.Capabilities && result.Kind == DiagnosticResultKind.Success && !IsCapabilitySet(result.Data)
                ? DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-capability-handshake")
                : result;
        }

        private async Task PollAsync(Stopwatch wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manager.PollEvents();
            if (wait.Elapsed >= MaximumTimeout)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        private static JObject BuildArgs(DiagnosticRequest request)
        {
            if (request.History is not null) return request.History.ToArgs();
            var args = new JObject();
            if (request.Lines is not null) args["lines"] = request.Lines.Value;
            if (request.Grep is not null) args["grep"] = request.Grep;
            return args;
        }

        private void OnReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (_peer is null || peer.Id != _peer.Id || channelNumber != 0 || deliveryMethod != DeliveryMethod.ReliableOrdered || reader.AvailableBytes is <= 0 or > MaximumWireBytes)
                {
                    _reply = Reply.MalformedReply;
                    return;
                }
                byte[] wire = new byte[reader.AvailableBytes];
                reader.GetBytes(wire, wire.Length);
                if (_adapter.TryReadWirePacket(wire, NetworkMessageType.DEBUG_RESULT, out int nativeId, out string? message) && message is not null)
                {
                    _reply = new Reply(nativeId, StrictUtf8.GetBytes(message));
                }
                else
                {
                    _reply = Reply.MalformedReply;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _manager.Stop();
        }

        private sealed record Reply(int NativeRequestId, byte[] Envelope, bool Malformed = false)
        {
            public static Reply MalformedReply { get; } = new(0, Array.Empty<byte>(), true);
        }
    }
}
