using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MTShared.Network;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Hosts the explicitly configured, read-only diagnostic listeners. Native
/// polling and sends remain serialized while bounded filesystem work runs away
/// from the shared polling loop. macOS uses threaded receive because the pinned
/// LiteNetLib manual receive implementation can block after a packet burst.
/// </summary>
public sealed class NativeCompanionServer : IDisposable
{
    private const int MaximumListeners = 128;
    private const int MaximumGlobalInflight = 32;
    private const int MaximumListenerInflight = 4;
    private readonly List<Host> _hosts = new();
    private readonly SemaphoreSlim _globalInflight = new(MaximumGlobalInflight, MaximumGlobalInflight);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<CompanionProfileConfiguration, string, int?, string?, CancellationToken, JObject>? _sourceReader;
    private readonly object _lifecycleGate = new();
    private readonly HashSet<Task> _admittedWork = new();
    private Task? _drainTask;
    private bool _stopping;
    private bool _disposed;

    private NativeCompanionServer(Func<CompanionProfileConfiguration, string, int?, string?, CancellationToken, JObject>? sourceReader = null)
    {
        _sourceReader = sourceReader;
    }

    /// <summary>Creates one bounded native listener for each configured profile.</summary>
    public static NativeCompanionServer Create(CompanionConfiguration configuration)
    {
        return Create(configuration, null);
    }

    internal static NativeCompanionServer Create(CompanionConfiguration configuration, Func<CompanionProfileConfiguration, string, int?, string?, CancellationToken, JObject>? sourceReader)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Profiles.Count is < 1 or > MaximumListeners)
        {
            throw new InvalidOperationException("Companion configuration is invalid.");
        }

        var server = new NativeCompanionServer(sourceReader);
        try
        {
            foreach (CompanionProfileConfiguration profile in configuration.Profiles)
            {
                string seed = profile.CreateConnectionKeySeed();
                if (!NativeDiagnosticVendorAdapter.TryCreate(seed, out NativeDiagnosticVendorAdapter? adapter))
                {
                    throw new InvalidOperationException("Companion native transport is unavailable.");
                }

                server._hosts.Add(new Host(server, profile, seed, adapter!));
            }

            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>Runs all configured listeners until cancellation is requested.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        foreach (Host host in _hosts)
        {
            host.Start();
        }

        try
        {
            await Task.Factory.StartNew(
                () => RunPollingLoop(linked.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAndDrainAsync().ConfigureAwait(false);
        }
    }

    private void RunPollingLoop(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long previousMilliseconds = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            long currentMilliseconds = clock.ElapsedMilliseconds;
            float elapsedMilliseconds = Math.Clamp(currentMilliseconds - previousMilliseconds, 0, 1000);
            previousMilliseconds = currentMilliseconds;
            foreach (Host host in _hosts)
            {
                host.Update(elapsedMilliseconds);
            }

            if (cancellationToken.WaitHandle.WaitOne(5))
            {
                break;
            }
        }
    }

    internal bool TryAcquire(Host host)
    {
        lock (_lifecycleGate)
        {
            return TryAcquireUnsafe(host);
        }
    }

    internal bool TryStart(Host host, Func<Task> work)
    {
        lock (_lifecycleGate)
        {
            if (!TryAcquireUnsafe(host))
            {
                return false;
            }

            Task task;
            try
            {
                task = Task.Run(work, CancellationToken.None);
            }
            catch
            {
                Release(host);
                throw;
            }

            _admittedWork.Add(task);
            _ = task.ContinueWith(
                static (completed, state) => ((NativeCompanionServer)state!).CompleteAdmittedWork(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
    }

    internal void Release(Host host)
    {
        host.Release();
        _globalInflight.Release();
    }

    internal bool CanAccept(Host host)
    {
        lock (_lifecycleGate)
        {
            return !_stopping && _globalInflight.CurrentCount > 0 && host.CanAcceptPeer;
        }
    }

    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal IReadOnlyList<Host> Hosts => _hosts;

    private bool IsAcceptingWork
    {
        get
        {
            lock (_lifecycleGate)
            {
                return !_stopping;
            }
        }
    }

    internal void QueueResponseIfRunning(Action enqueue)
    {
        lock (_lifecycleGate)
        {
            if (!_stopping)
            {
                enqueue();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAndDrainAsync().GetAwaiter().GetResult();
        foreach (Host host in _hosts)
        {
            host.Dispose();
        }

        _shutdown.Dispose();
        _globalInflight.Dispose();
    }

    private bool TryAcquireUnsafe(Host host)
    {
        if (_stopping || !_globalInflight.Wait(0))
        {
            return false;
        }

        if (host.TryAcquire())
        {
            return true;
        }

        _globalInflight.Release();
        return false;
    }

    private Task StopAndDrainAsync()
    {
        lock (_lifecycleGate)
        {
            if (_drainTask is not null)
            {
                return _drainTask;
            }

            _stopping = true;
            _shutdown.Cancel();
            foreach (Host host in _hosts)
            {
                host.Stop();
            }

            _drainTask = DrainAdmittedWorkAsync(_admittedWork.ToArray());
            return _drainTask;
        }
    }

    private static async Task DrainAdmittedWorkAsync(Task[] admittedWork)
    {
        if (admittedWork.Length > 0)
        {
            await Task.WhenAll(admittedWork).ConfigureAwait(false);
        }
    }

    private void CompleteAdmittedWork(Task completed)
    {
        _ = completed.Exception;
        lock (_lifecycleGate)
        {
            _admittedWork.Remove(completed);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NativeCompanionServer));
        }
    }

    internal sealed class Host : IDisposable
    {
        private const int MaximumPeers = 4;
        private const int MaximumQueuedResponses = 64;
        private const int MaximumRequestsPerMinute = 30;
        private const int MaximumWireBytes = 64 * 1024;
        private readonly NativeCompanionServer _owner;
        private readonly CompanionProfileConfiguration _profile;
        private readonly string _seed;
        private readonly HistoryReader _history;
        private readonly NativeDiagnosticVendorAdapter _adapter;
        private readonly HashSet<int> _peers = new();
        private readonly SemaphoreSlim _inflight = new(MaximumListenerInflight, MaximumListenerInflight);
        private readonly Queue<long> _requestTimes = new();
        private readonly Queue<long> _historyRequestTimes = new();
        private readonly Queue<long> _capabilityRequestTimes = new();
        private readonly ConcurrentQueue<OutboundReply> _responses = new();
        private readonly NetManager _manager;
        private readonly bool _usesManualReceive;
        private int _queuedResponses;
        private bool _started;

        internal Host(NativeCompanionServer owner, CompanionProfileConfiguration profile, string seed, NativeDiagnosticVendorAdapter adapter)
        {
            _owner = owner;
            _profile = profile;
            _seed = seed;
            _history = new HistoryReader(profile, new[] { seed });
            _adapter = adapter;
            _usesManualReceive = !OperatingSystem.IsMacOS();
            var listener = new EventBasedNetListener();
            listener.ConnectionRequestEvent += request =>
            {
                if (_owner.CanAccept(this))
                {
                    request.AcceptIfKey(_seed);
                }
                else
                {
                    request.Reject();
                }
            };
            listener.PeerConnectedEvent += peer =>
            {
                if (_peers.Count < MaximumPeers)
                {
                    _peers.Add(peer.Id);
                }
                else
                {
                    peer.Disconnect();
                }
            };
            listener.PeerDisconnectedEvent += (peer, _) => _peers.Remove(peer.Id);
            listener.NetworkReceiveEvent += Receive;
            _manager = new NetManager(listener)
            {
                MaxFragmentsCount = 128,
                UnconnectedMessagesEnabled = false
            };
        }

        internal bool CanAcceptPeer => _peers.Count < MaximumPeers;

        internal int QueuedResponseCount => Volatile.Read(ref _queuedResponses);

        internal void Start()
        {
            if (_started)
            {
                throw new InvalidOperationException("Companion listener could not start.");
            }
            bool started = _usesManualReceive
                ? _manager.StartInManualMode(_profile.DiagnosticPort)
                : _manager.Start(_profile.DiagnosticPort);
            if (!started)
            {
                throw new InvalidOperationException("Companion listener could not start.");
            }

            _started = true;
        }

        internal void Update(float elapsedMilliseconds)
        {
            if (!_started)
            {
                return;
            }

            _manager.PollEvents();
            if (_usesManualReceive)
            {
                _manager.ManualUpdate(elapsedMilliseconds);
            }
            FlushResponses();
            _history.ExpireIdle();
        }

        internal bool TryAcquire()
        {
            return _inflight.Wait(0);
        }

        internal void Release()
        {
            _inflight.Release();
        }

        internal void Stop()
        {
            if (_started)
            {
                _manager.Stop();
                _started = false;
            }

            DiscardQueuedResponses();
        }

        private void Receive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                int wireLength = reader.AvailableBytes;
                if (!_peers.Contains(peer.Id) || channelNumber != 0 || deliveryMethod != DeliveryMethod.ReliableOrdered || wireLength is <= 0 or > MaximumWireBytes)
                {
                    return;
                }

                byte[] wire = new byte[wireLength];
                reader.GetBytes(wire, wire.Length);
                if (!_adapter.TryReadWirePacket(wire, NetworkMessageType.DEBUG_REQUEST, out int nativeRequestId, out string? text) || text is null)
                {
                    return;
                }

                byte[] requestBytes;
                try
                {
                    requestBytes = Encoding.UTF8.GetBytes(text);
                }
                catch (EncoderFallbackException)
                {
                    return;
                }

                if (!NativeDiagnosticProtocol.TryParseRequest(requestBytes, _profile.Name, out NativeDiagnosticEnvelope request))
                {
                    if (NativeDiagnosticProtocol.TryParseRequestForProfileRefusal(requestBytes, out NativeDiagnosticEnvelope unboundRequest))
                    {
                        QueueFailure(peer, nativeRequestId, unboundRequest, "profile-unbound", unboundRequest.Profile);
                    }
                    return;
                }

                if (!TryParseOperation(request, out RequestWork? work))
                {
                    QueueFailure(peer, nativeRequestId, request, "invalid-request");
                    return;
                }

                if (!TryConsumeRateSlot(request.Operation) || !_owner.TryStart(this, () => ReadSourceAsync(peer, nativeRequestId, request, work!)))
                {
                    QueueFailure(peer, nativeRequestId, request, "busy");
                    return;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        private async Task ReadSourceAsync(NetPeer peer, int nativeRequestId, NativeDiagnosticEnvelope request, RequestWork work)
        {
            try
            {
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_owner.ShutdownToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                JObject data = await Task.Run(() => ReadSource(work, deadline.Token), deadline.Token).ConfigureAwait(false);
                QueueSuccess(peer, nativeRequestId, request, data);
            }
            catch (HistoryReadException error)
            {
                QueueFailure(peer, nativeRequestId, request, error.Message);
            }
            catch (OperationCanceledException)
            {
                QueueFailure(peer, nativeRequestId, request, "deadline-exceeded");
            }
            catch (Exception)
            {
                QueueFailure(peer, nativeRequestId, request, "source-unavailable");
            }
            finally
            {
                _owner.Release(this);
            }
        }

        private JObject ReadSource(RequestWork work, CancellationToken cancellationToken)
        {
            if (_owner._sourceReader is not null)
            {
                return _owner._sourceReader(_profile, work.Operation, work.Lines, work.Grep, cancellationToken);
            }

            return work.Operation switch
            {
                "capabilities" => new JObject { ["capabilities"] = new JArray("capabilities", "health", "logs", "guardian-status", "guardian-events"), ["extensions"] = new JObject { ["history_v1"] = true, ["history_options_v1"] = true, ["history_gzip_v1"] = true, ["history_index_v1"] = true } },
                "history-page" => _history.Read(work.History!, cancellationToken),
                "history-close" => _history.Close(work.History!),
                "health" => new JObject { ["state"] = "companion", ["status"] = "available" },
                "logs" => new BoundedLogReader(_profile).Read(cancellationToken, work.Lines!.Value, work.Grep),
                "guardian-status" => new GuardianEvidenceReader(_profile).Read(cancellationToken),
                "guardian-events" => new GuardianEvidenceReader(_profile).Read(cancellationToken, work.Lines!.Value),
                _ => throw new InvalidOperationException("Operation is not allowlisted.")
            };
        }

        private bool TryParseOperation(NativeDiagnosticEnvelope request, out RequestWork? work)
        {
            work = null;
            JObject args = request.Args;
            if (request.Operation is "history-page" or "history-close")
            {
                if (!DiagnosticHistoryQuery.TryParse(args, out DiagnosticHistoryQuery? history) || request.Operation == "history-close" && history!.Cursor is null) return false;
                work = new RequestWork(request.Operation, null, null, history);
                return true;
            }
            if (request.Operation is "capabilities" or "health" or "guardian-status")
            {
                if (args.HasValues)
                {
                    return false;
                }

                work = new RequestWork(request.Operation, null, null);
                return true;
            }

            if (request.Operation is not ("logs" or "guardian-events") || args["lines"]?.Type != JTokenType.Integer)
            {
                return false;
            }

            int lines;
            try
            {
                lines = args["lines"]!.Value<int>();
            }
            catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException)
            {
                return false;
            }

            if (lines is < 1 or > BoundedLogReader.MaximumLines)
            {
                return false;
            }

            if (request.Operation == "guardian-events")
            {
                if (args.Properties().Count() != 1)
                {
                    return false;
                }

                work = new RequestWork(request.Operation, lines, null);
                return true;
            }

            if (args.Properties().Count() is < 1 or > 2)
            {
                return false;
            }

            string? grep = null;
            if (args["grep"] is not null)
            {
                if (args["grep"]!.Type != JTokenType.String)
                {
                    return false;
                }

                grep = args["grep"]!.Value<string>();
                if (string.IsNullOrWhiteSpace(grep) || Encoding.UTF8.GetByteCount(grep) > DiagnosticRequest.MaximumGrepUtf8Bytes)
                {
                    return false;
                }
            }

            work = new RequestWork(request.Operation, lines, grep);
            return true;
        }

        private bool TryConsumeRateSlot(string operation)
        {
            // History reuses authenticated sessions and needs sustained page throughput.
            // Separate bounded budgets preserve the original tail/control admission rate.
            Queue<long> times = operation == "capabilities" ? _capabilityRequestTimes : operation is "history-page" or "history-close" ? _historyRequestTimes : _requestTimes;
            int maximum = operation == "capabilities" ? 1200 : operation is "history-page" or "history-close" ? 6000 : MaximumRequestsPerMinute;
            long cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60;
            while (times.Count > 0 && times.Peek() < cutoff) times.Dequeue();
            if (times.Count >= maximum) return false;
            times.Enqueue(Stopwatch.GetTimestamp());
            return true;
        }

        private void QueueSuccess(NetPeer peer, int nativeRequestId, NativeDiagnosticEnvelope request, JObject data)
        {
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, request.RequestId, request.Operation, _profile.Name, new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DiagnosticReplyValidator.SanitizeEvidence(data, _seed), null);
            QueueReply(peer, nativeRequestId, reply);
        }

        private void QueueFailure(NetPeer peer, int nativeRequestId, NativeDiagnosticEnvelope request, string error, string? responseProfile = null)
        {
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, request.RequestId, request.Operation, responseProfile ?? _profile.Name, new JObject(), false, "refused", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, DiagnosticReplyValidator.SanitizeEvidence(error, _seed));
            QueueReply(peer, nativeRequestId, reply);
        }

        private void QueueReply(NetPeer peer, int nativeRequestId, NativeDiagnosticEnvelope reply)
        {
            _owner.QueueResponseIfRunning(() =>
            {
                if (Interlocked.Increment(ref _queuedResponses) > MaximumQueuedResponses)
                {
                    Interlocked.Decrement(ref _queuedResponses);
                    return;
                }

                _responses.Enqueue(new OutboundReply(peer, nativeRequestId, reply));
            });
        }

        private void FlushResponses()
        {
            if (!_owner.IsAcceptingWork)
            {
                while (_responses.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _queuedResponses);
                }
                return;
            }

            while (_responses.TryDequeue(out OutboundReply? reply))
            {
                Interlocked.Decrement(ref _queuedResponses);
                if (!_peers.Contains(reply.Peer.Id) || reply.Peer.ConnectionState != ConnectionState.Connected)
                {
                    continue;
                }

                if (NativeDiagnosticProtocol.TrySerialize(reply.Envelope, out byte[] bytes) && _adapter.TryCreateWirePacket(NetworkMessageType.DEBUG_RESULT, reply.NativeRequestId, Encoding.UTF8.GetString(bytes), out byte[] response))
                {
                    reply.Peer.Send(response, 0, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void DiscardQueuedResponses()
        {
            while (_responses.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedResponses);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            _manager.Stop();
            _history.Dispose();
            _inflight.Dispose();
        }

        private sealed record RequestWork(string Operation, int? Lines, string? Grep, DiagnosticHistoryQuery? History = null);

        private sealed record OutboundReply(NetPeer Peer, int NativeRequestId, NativeDiagnosticEnvelope Envelope);
    }
}
