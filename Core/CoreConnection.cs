using System.Reflection;
using MTShared.Network.GraphTools;
using MTShared.LiveMarket;
using MTShared.Network.Notifications;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MTShared;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Monitoring;
namespace MTTextClient.Core;

/// <summary>
/// A single connection to an MT-Core instance. Bundles UDPClient + all
/// data stores (algorithms, account, core status, exchange info, profile
/// settings) plus the wire-level request surface (algorithm lifecycle,
/// order &amp; position management, MonitorBuffer integration for UDP-based
/// core status tracking, etc.). Each CoreConnection is independent —
/// multiple can run concurrently.
/// </summary>
public sealed class CoreConnection : IDisposable
{
    private UDPClient? _udpClient;

    // GUIDELINE EXCEPTION (Rule 24): Reflection used here to work around a bug in MTShared.dll
    // where UDPClient.Stop() does not call NetManager.Stop(), causing zombie threads and memory leaks.
    // This is NOT network mapping code — it is a necessary workaround until MTShared.dll is fixed upstream.
    // See: memory leak root cause analysis (March 2026). Remove when UDPClient.Stop() is fixed.
    // Cached reflection fields for NetManager cleanup (avoid repeated lookups in fleet disconnect)
    private static readonly FieldInfo? s_netManagerField =
        typeof(UDPClient).GetField("_netManager", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? s_eventQueueField =
        typeof(UDPClient).GetField("_eventDataQueue", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? s_importantQueueField =
        typeof(UDPClient).GetField("_importantEventDataQueue", BindingFlags.Instance | BindingFlags.NonPublic);

    private int _algorithmsSubscriptionId;
    private int _exchangeInfoSubscriptionId;
    private int _coreStatusSubscriptionId;
    private int _udsSubscriptionId;
    private int _tpslSubscriptionId;
    private int _tradingPerfSubscriptionId;
    private int _notificationSubscriptionId;
    // The MTCore 0.7.23902 wire layer returns 0 from SendNotificationSubscribe
    // even when the callback registers correctly, so the subscription-id
    // sentinel can't be used to decide whether notifications are active.
    // Track the registered state on a separate flag.
    private bool _notificationCallbackRegistered;
    // Note: the previous _pendingAlgoRequests / _pendingAlgoListRequests /
    // _pendingReportRequests FIFO queues + their SubscribeNotifications
    // dispatcher branches were a pre-wire-migration approximation. The
    // canonical pattern (see SendAndAwaitNotification) opens a transient
    // SendNotificationSubscribe per request with a typed handler, so the
    // long-lived dispatcher no longer needs to fan responses out by FIFO.
    private int _alertsSubscriptionId;
    private int _alertHistorySubscriptionId;
    private readonly ConcurrentDictionary<string, int> _tradeSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _depthSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _markPriceSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _klineSubscriptionIds = new ConcurrentDictionary<string, int>();
    private int _tickerSubscriptionId;
    private readonly ConcurrentDictionary<string, int> _profilingSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, Action<MTShared.Network.AlgorithmProfilingData>> _profilingCallbacks = new ConcurrentDictionary<string, Action<MTShared.Network.AlgorithmProfilingData>>();

    private bool _isConnected;
    private bool _disposed;
    private DateTime _connectedAt;

    // Track connectionId + serverStartTime so we detect Core restarts
    // without a full disconnect/reconnect cycle
    private int   _lastConnectionId;
    private long  _lastServerStartTime;

    // Per-connection token bucket rate limiter (120/s, burst 600)
    public RateLimiter RateLimit { get; } = new RateLimiter("connection", capacity: 600, refillPerSecond: 120);

    // Per-connection circuit breaker (trip after 5 failures, 30s open window)
    public CircuitBreaker Circuit { get; } = new CircuitBreaker("connection", failureThreshold: 5, openDurationMs: 30_000);

    /// <summary>The profile used to create this connection.</summary>
    public ServerProfile Profile { get; }

    /// <summary>Per-connection algorithm store.</summary>
    public AlgorithmStore AlgoStore { get; } = new();

    /// <summary>Per-connection account data store (balances, orders, positions).</summary>
    public AccountStore AccountStore { get; } = new();

    /// <summary>Per-connection core status store (CPU, memory, latency, license).</summary>
    public CoreStatusStore CoreStatusStore { get; } = new();

    /// <summary>Per-connection exchange info store (trade pairs, API limits).</summary>
    public ExchangeInfoStore ExchangeInfoStore { get; } = new();

    /// <summary>Per-connection profile settings store (key-value server config).</summary>
    public ProfileSettingsStore ProfileSettingsStore { get; } = new();

    /// <summary>Per-connection monitor buffer for real-time status tracking. Null until StartMonitor() called.</summary>
    public MonitorBuffer? MonitorBuffer { get; private set; }

    /// <summary>Per-connection TPSL store. Created on first SubscribeTPSL().</summary>
    public TPSLStore? TPSLStore { get; private set; }

    /// <summary>Per-connection trading performance store. Created on first SubscribeTradingPerformance().</summary>
    public TradingPerformanceStore? TradingPerfStore { get; private set; }


    /// <summary>Per-connection notification store. Holds recent notifications from core.</summary>
    public NotificationStore NotificationStore { get; } = new NotificationStore();

    /// <summary>Per-connection market data store. Holds real-time trade/depth/markprice/kline/ticker data.</summary>
    public MarketDataStore MarketDataStore { get; } = new MarketDataStore();

    /// <summary>Per-connection alert store. Holds active alerts and alert history.</summary>
    public AlertStore AlertStore { get; } = new AlertStore();

    /// <summary>Whether the notifications subscription is active. Reads the
    /// registered-flag rather than the subscription id because MTCore 0.7.23902's
    /// SendNotificationSubscribe returns 0 even when the callback successfully
    /// registers.</summary>
    public bool IsNotificationSubscribed { get { return _notificationCallbackRegistered; } }

    /// <summary>Whether alerts subscription is active.</summary>
    public bool IsAlertsSubscribed { get { return _alertsSubscriptionId != 0; } }

    /// <summary>Whether alert history subscription is active.</summary>
    public bool IsAlertHistorySubscribed { get { return _alertHistorySubscriptionId != 0; } }
    /// <summary>Short name (profile name) for display.</summary>
    public string Name => Profile.Name;

    /// <summary>Connection state.</summary>
    public bool IsConnected => _isConnected && _udpClient != null;

    /// <summary>Uptime since connected.</summary>
    public TimeSpan Uptime => _isConnected ? DateTime.UtcNow - _connectedAt : TimeSpan.Zero;

    /// <summary>The raw UDPClient for direct Send* calls.</summary>
    public UDPClient? Client => _udpClient;

    // Events
    public event Action<CoreConnection>? OnConnected;
    public event Action<CoreConnection>? OnDisconnected;
    public event Action<CoreConnection, string>? OnError;
    public event Action<CoreConnection, int>? OnAlgorithmsLoaded;
    public event Action<CoreConnection>? OnCoreStatusReceived;
    public event Action<CoreConnection, int>? OnTradePairsLoaded;
    public event Action<CoreConnection>? OnAccountDataReceived;
    public event Action<CoreConnection, MTShared.Network.AlgorithmProfilingData>? OnProfilingDataReceived;
    /// <summary>
    /// Fired when MTCore restarts while the UDP connection stays alive.
    /// Detected via connectionId or serverStartTime change in ConnectionInfoData.
    /// When fired: AlgoStore is stale — agents must re-query algo state.
    /// </summary>
    public event Action<CoreConnection>? OnCoreRestarted;

    public CoreConnection(ServerProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));

        // Wire internal store events to connection-level events
        CoreStatusStore.OnStatusUpdated += _ => OnCoreStatusReceived?.Invoke(this);
        ExchangeInfoStore.OnTradePairsLoaded += count => OnTradePairsLoaded?.Invoke(this, count);
        AccountStore.OnAccountInfoChanged += () => OnAccountDataReceived?.Invoke(this);
    }

    /// <summary>
    /// Initiate connection. The UDPClient constructor connects immediately.
    /// Returns true if connection was initiated (not yet fully connected).
    /// </summary>
    public bool Connect()
    {
        if (IsConnected)
        {
            OnError?.Invoke(this, $"[{Name}] Already connected.");
            return false;
        }

        string? keySeed = Profile.GetConnectionKeySeed();

        try
        {
            _udpClient = new UDPClient(Profile.Address, Profile.Port, keySeed);

            _udpClient.onConnect = HandleConnect;
            _udpClient.onDisconnect = HandleDisconnect;
            _udpClient.onReconnectStart = HandleReconnectStart;
            // Detect Core restart (connectionId/serverStartTime change)
            _udpClient.onConnectionInfoResult = HandleConnectionInfoChange;

            // Polling handled externally by ConnectionPump — no per-connection timer

            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"[{Name}] Connection failed: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>Disconnect and clean up resources.</summary>
    public void Disconnect()
    {
        if (_udpClient != null)
        {
            Unsubscribe();
            try { _udpClient.Stop(); }
            catch { /* swallow */ }

            // FIX: UDPClient.Stop() only sends a disconnect packet to the peer
            // but does NOT call NetManager.Stop() — leaving 3 zombie threads
            // (logic + socket recv IPv4/IPv6) and 2 sockets per connection.
            // We must stop the NetManager via reflection since _netManager is private.
            if (_udpClient != null)
            {
                StopNetManager(_udpClient);
            }
        }
        StopMonitor();
        Cleanup();
    }

    #region Monitor

    /// <summary>
    /// Start collecting core status snapshots into a ring buffer.
    /// Uses the existing CoreStatusSubscription (UDP) — no filesystem access needed.
    /// </summary>
    public void StartMonitor()
    {
        if (MonitorBuffer != null)
        {
            return;
        }

        MonitorBuffer = new MonitorBuffer(capacity: 1000);

        // If there's already a current snapshot, seed the buffer
        var current = CoreStatusStore.GetStatus();
        if (current != null)
        {
            MonitorBuffer.Add(current);
        }

        // Subscribe to future updates
        CoreStatusStore.OnStatusUpdated += OnMonitorStatusUpdate;
    }

    /// <summary>Stop monitoring and release the buffer.</summary>
    public void StopMonitor()
    {
        if (MonitorBuffer != null)
        {
            CoreStatusStore.OnStatusUpdated -= OnMonitorStatusUpdate;
            MonitorBuffer = null;
        }
    }

    private void OnMonitorStatusUpdate(CoreStatusSnapshot snapshot)
    {
        MonitorBuffer?.Add(snapshot);
    }

    #endregion

    #region Subscriptions

    private void Subscribe()
    {
        if (_udpClient == null)
        {
            return;
        }

        ExchangeType exchange = Profile.Exchange;

        // 1. Algorithms subscription
        _algorithmsSubscriptionId = _udpClient.SendAlgorithmsSubscribe(
            (msgType, data) =>
            {
                int prevCount = AlgoStore.Count;
                AlgoStore.ProcessData(msgType, data);
                int newCount = AlgoStore.Count;
                if (newCount > prevCount)
                {
                    OnAlgorithmsLoaded?.Invoke(this, newCount);
                }
            });

        // 2. Exchange info subscription (trade pairs, prices, API limits)
        _exchangeInfoSubscriptionId = _udpClient.SendExchangeInfoSubscribe(
            exchange,
            (msgType, data) =>
            {
                ExchangeInfoStore.ProcessData(msgType, data);
            });

        // 3. Core status subscription (CPU, memory, latency, license)
        _coreStatusSubscriptionId = _udpClient.SendCoreStatusSubscribe(
            exchange,
            (msgType, data) =>
            {
                CoreStatusStore.ProcessData(msgType, data);
            });

        // 4. UDS subscription (balances, orders, positions)
        _udsSubscriptionId = _udpClient.SendUDSSubscribe(
            exchange,
            (msgType, data) =>
            {
                AccountStore.ProcessData(msgType, data);
                if (msgType == NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA)
                {
                    ExchangeInfoStore.ProcessData(msgType, data);
                }
            });

        // 5. Notifications subscription — required by MTCore 0.7.23902's push
        // model: algorithm-request responses arrive here as
        // Algorithm{Update,ListUpdate}NotificationData rather than via the
        // inline send callback. Calling SubscribeNotifications during the
        // initial Subscribe means SendAlgorithmRequest etc. can rely on the
        // TCS queue path from the first connect onward.
        SubscribeNotifications();
    }

    private void Unsubscribe()
    {
        if (_udpClient == null)
        {
            return;
        }

        try
        {
            _udpClient.SendAlgorithmsUnsubscribe(ref _algorithmsSubscriptionId);
            ExchangeType exchange = Profile.Exchange;
            _udpClient.SendExchangeInfoUnsubscribe(ref _exchangeInfoSubscriptionId, exchange);
            _udpClient.SendCoreStatusUnsubscribe(ref _coreStatusSubscriptionId, exchange);
            _udpClient.SendUDSUnsubscribe(ref _udsSubscriptionId, exchange);
            if (_tpslSubscriptionId != 0)
            {
                _udpClient.SendAlgorithmTPSLsUnsubscribe(ref _tpslSubscriptionId);
            }
            if (_tradingPerfSubscriptionId != 0)
            {
                ExchangeType perfExchange = Profile.Exchange;
                _udpClient.SendTradingPerformanceUnsubscribe(ref _tradingPerfSubscriptionId, perfExchange, MarketType.FUTURES);
            }
            UnsubscribeNotifications();
            UnsubscribeAlerts();
            UnsubscribeAlertHistory();
            UnsubscribeTicker(exchange, MarketType.FUTURES);
            foreach (KeyValuePair<string, int> kvp in _tradeSubscriptionIds)
            {
                int subId = kvp.Value;
                if (subId != 0)
                {
                    _udpClient.SendTradeUnsubscribe(ref subId, exchange, MarketType.FUTURES, "");
                }
            }
            _tradeSubscriptionIds.Clear();
            foreach (KeyValuePair<string, int> kvp in _depthSubscriptionIds)
            {
                int subId = kvp.Value;
                if (subId != 0)
                {
                    _udpClient.SendDepthUnsubscribe(ref subId, exchange, MarketType.FUTURES, "", false, false);
                }
            }
            _depthSubscriptionIds.Clear();
            foreach (KeyValuePair<string, int> kvp in _markPriceSubscriptionIds)
            {
                int subId = kvp.Value;
                if (subId != 0)
                {
                    _udpClient.SendMarkPriceUnsubscribe(ref subId, exchange, MarketType.FUTURES, "");
                }
            }
            _markPriceSubscriptionIds.Clear();
            foreach (KeyValuePair<string, int> kvp in _klineSubscriptionIds)
            {
                int subId = kvp.Value;
                if (subId != 0)
                {
                    _udpClient.SendKlineUnsubscribe(ref subId, exchange, MarketType.FUTURES, "", KlineInterval.MIN_1);
                }
            }
            _klineSubscriptionIds.Clear();
            // Cleanup new P2+ subscriptions
            UnsubscribeTriggers();
            UnsubscribeLiveMarkets(MarketType.FUTURES, "", "");
            UnsubscribeAutoBuy();
            UnsubscribeGraphTool();
            foreach (var kv in _profilingSubscriptionIds)
            {
                int pid = kv.Value;
                if (pid != 0)
                {
                    _udpClient.SendAlgorithmProfilingDataUnsubscribe(
                        ref pid, Profile.Exchange, MarketType.FUTURES, "", 0);
                }
            }
            _profilingSubscriptionIds.Clear();
            _profilingCallbacks.Clear();
        }
        catch { /* swallow on cleanup */ }
    }

    #endregion


    /// <summary>
    /// Poll the UDPClient for pending events. Called by ConnectionPump
    /// on a single dedicated thread (no per-connection timer needed).
    /// </summary>
    public void PollEvents()
    {
        try { _udpClient?.ProcessEventData(); }
        catch { /* suppress processing errors */ }
    }

    // ── TCS-based request helper ─────────────────────────────────────
    // Replaces ManualResetEventSlim.Wait() which held ThreadPool threads hostage
    // for up to timeoutMs on slow/unresponsive Core instances.
    // TaskCompletionSource runs continuations on the ThreadPool
    // (RunContinuationsAsynchronously), so the callback never deadlocks
    // even if the pump thread fires it while the caller is already unwinding.
    //
    // Usage: result = SendAndWait<NotificationMessageData>(
    //            send: cb => _udpClient.SendXxx(data, cb),
    //            timeoutMs: 10_000);

    // Guarded send — checks circuit breaker and rate limiter before dispatching.
    // timeoutMs=0 means "skip guard, internal use only" (e.g. subscribe calls).
    private T? SendAndWait<T>(Action<Action<T?>> send, int timeoutMs) where T : class
    {
        if (timeoutMs > 0)
        {
            // Circuit breaker fast-fail
            if (!Circuit.AllowCall())
            {
                return null;
            }

            // Rate limiter — wait up to 500ms for a token
            if (!RateLimit.ConsumeBlocking(500))
            {
                Circuit.RecordFailure();
                return null;
            }
        }

        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : 30_000);
        using var reg = cts.Token.Register(
            static state => ((TaskCompletionSource<T?>)state!).TrySetResult(null), tcs);
        send(result => tcs.TrySetResult(result));
        T? result = tcs.Task.GetAwaiter().GetResult();

        // Record outcome for circuit breaker
        if (timeoutMs > 0)
        {
            if (result != null) Circuit.RecordSuccess();
            else                Circuit.RecordFailure();
        }

        return result;
    }

    // Struct-return variant (for value types / tuples that can't be class-constrained)
    private static T SendAndWaitStruct<T>(Action<Action<T>> send, T timeoutValue, int timeoutMs)
        where T : struct
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(
            static state =>
            {
                var (t, tv) = ((TaskCompletionSource<T>, T))state!;
                t.TrySetResult(tv);
            }, (tcs, timeoutValue));
        send(result => tcs.TrySetResult(result));
        return tcs.Task.GetAwaiter().GetResult();
    }

    // ── Canonical push-model request helper ─────────────────────────────
    //
    // Mirrors MTBotClient.Client.ServicesController.SendToCore. The MTCore
    // 0.7.23902 wire protocol stopped invoking the inline send callback for
    // most Send*Request methods — responses arrive on the notification
    // channel as typed *NotificationData subclasses instead. The pattern:
    //
    //   1. Subscribe a transient SendNotificationSubscribe with a handler
    //      that matches on the expected response type via an `is` check.
    //   2. Fire the send action (we still pass a no-op callback because our
    //      committed lib/MTShared.dll signature requires one; the actual
    //      response arrives on the notification channel).
    //   3. Block on a BlockingCollection until the handler pushes a result
    //      or the timeout sentinel fires.
    //   4. Unsubscribe in finally{} so we don't leak per-request handlers.
    //
    // The official BotClient uses a 3-minute wait; we keep a per-call
    // timeoutMs (default 10s) so REPL/tests don't hang.
    //
    // Usage:
    //   var resp = SendAndAwaitNotification<OrderPlaceNotificationData>(
    //       send: () => _udpClient.SendPlaceOrderRequest(orderReq, _ => { }),
    //       build: n => new NotificationMessageData {
    //           notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
    //           msgString = n.message ?? string.Empty,
    //       },
    //       timeoutMs: 10_000);
    //
    private NotificationMessageData? SendAndAwaitNotification<TNotification>(
        Action send,
        Func<TNotification, NotificationMessageData> build,
        int timeoutMs = 10_000)
        where TNotification : AbstractNotificationData
    {
        if (_udpClient == null) { return null; }

        if (timeoutMs > 0)
        {
            if (!Circuit.AllowCall()) { return null; }
            if (!RateLimit.ConsumeBlocking(500))
            {
                Circuit.RecordFailure();
                return null;
            }
        }

        var wait = new System.Collections.Concurrent.BlockingCollection<NotificationMessageData>(boundedCapacity: 8);
        int subId = -1;
        try
        {
            subId = _udpClient.SendNotificationSubscribe(
                Profile.Exchange,
                (AbstractNotificationData data) =>
                {
                    if (data is TNotification typed)
                    {
                        try { wait.TryAdd(build(typed)); }
                        catch { /* collection disposed in finally — drop */ }
                    }
                },
                -1);

            send();

            // Use Take(timeout) — BlockingCollection itself supports TryTake
            // with a TimeSpan, but Take(CancellationToken) is the idiom used
            // in the BotClient reference. Either is fine; we use TryTake for
            // simplicity (no extra cts allocation).
            if (wait.TryTake(out var result, timeoutMs))
            {
                if (timeoutMs > 0) { Circuit.RecordSuccess(); }
                return result;
            }
            if (timeoutMs > 0) { Circuit.RecordFailure(); }
            return null;
        }
        finally
        {
            if (subId != -1 && _udpClient != null)
            {
                try { _udpClient.SendNotificationUnsubscribe(ref subId, Profile.Exchange); }
                catch { /* unsubscribe is best-effort */ }
            }
            wait.Dispose();
        }
    }

    // ── Transient UDS read ──────────────────────────────────────────────
    //
    // The long-lived UDS subscription (set up at Subscribe()) only delivers
    // OrderListData / BalanceListData / etc. when MTCore decides to push them
    // — typically on order events, not at subscribe time. On a fresh profile
    // with no recent orders, the AccountStore stays empty even though the
    // subscription is healthy.
    //
    // ReadFreshUDSData opens a TRANSIENT SendUDSSubscribe, waits for any
    // data drops of type T (per MarketType), takes them, then unsubscribes.
    // Mirrors MTBotClient.Client.ServicesController.GetOrdersListData /
    // GetPostitionsListData — the official pattern documented in
    // internal vendor wire-pattern reference notes.
    //
    // The data is fed back through AccountStore.ProcessData so subsequent
    // reads from the long-lived store see the snapshot.
    private bool ReadFreshUDSData<T>(NetworkMessageType msgType, int timeoutMs = 5_000)
        where T : NetworkData
    {
        if (_udpClient == null) { return false; }

        var collected = new List<T>();
        var done = new System.Threading.ManualResetEventSlim(false);
        int subId = -1;
        try
        {
            subId = _udpClient.SendUDSSubscribe(
                Profile.Exchange,
                (NetworkMessageType _, NetworkData data) =>
                {
                    if (data is T typed)
                    {
                        lock (collected) { collected.Add(typed); }
                        // Don't break early — multiple market types may arrive.
                        done.Set();
                    }
                },
                -1,
                NetworkMessagePriority.HIGH);

            // Wait for first drop; once we have one, give a small grace
            // window for any remaining markets to arrive.
            if (done.Wait(timeoutMs))
            {
                System.Threading.Thread.Sleep(250);
            }

            lock (collected)
            {
                foreach (var item in collected)
                {
                    AccountStore.ProcessData(msgType, item);
                    if (msgType == NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA)
                    {
                        ExchangeInfoStore.ProcessData(msgType, item);
                    }
                }
                return collected.Count > 0;
            }
        }
        finally
        {
            if (subId != -1 && _udpClient != null)
            {
                try { _udpClient.SendUDSUnsubscribe(ref subId, Profile.Exchange, NetworkMessagePriority.HIGH); }
                catch { /* best-effort */ }
            }
            done.Dispose();
        }
    }

    /// <summary>Force-refresh the orders cache by opening a transient UDS
    /// subscribe and feeding any received OrderListData back into AccountStore.
    /// Use when the long-lived store hasn't been populated (no events since
    /// connect). Returns true if at least one OrderListData drop arrived.
    /// A false return is intentionally not treated as "empty orders": MTCore
    /// can also fail to push a fresh list within the read window.</summary>
    public bool ForceRefreshOrders(int timeoutMs = 5_000)
    {
        return ReadFreshUDSData<OrderListData>(NetworkMessageType.UDS_ORDER_LIST_RESULT, timeoutMs);
    }

    /// <summary>Force-refresh positions + balances via a transient UDS read.
    /// AccountInfoData arrives on the same UDS channel and carries both
    /// position list and balance dictionary. Always marks Last{Position,Balance}Update
    /// after the call so callers can distinguish "queried, empty" from "never queried".</summary>
    public bool ForceRefreshAccount(int timeoutMs = 5_000)
    {
        bool gotData = ReadFreshUDSData<AccountInfoData>(NetworkMessageType.UDS_ACCOUNT_INFO_RESULT, timeoutMs);
        if (AccountStore.LastPositionUpdate == default) { AccountStore.LastPositionUpdate = DateTime.UtcNow; }
        if (AccountStore.LastBalanceUpdate == default)  { AccountStore.LastBalanceUpdate  = DateTime.UtcNow; }
        return gotData;
    }

    /// <summary>Force-refresh standalone balance list (some venues push
    /// BalanceListData separately from AccountInfoData).</summary>
    public bool ForceRefreshBalances(int timeoutMs = 5_000)
    {
        bool gotData = ReadFreshUDSData<BalanceListData>(NetworkMessageType.UDS_BALANCE_LIST_RESULT, timeoutMs);
        if (AccountStore.LastBalanceUpdate == default) { AccountStore.LastBalanceUpdate = DateTime.UtcNow; }
        return gotData;
    }

    /// <summary>Force-refresh leverage/max-leverage/risk-limit cache by
    /// opening a transient UDS read. Some cores only push leverage info on
    /// their own refresh cadence, so false means "not replayed now", not
    /// "no leverage data exists".</summary>
    public bool ForceRefreshLeverageInfo(int timeoutMs = 5_000)
    {
        return ReadFreshUDSData<LeverageInfoUpdateData>(NetworkMessageType.LEVERAGE_INFO_UPDATE_DATA, timeoutMs);
    }

    /// <summary>Force-refresh the algorithms cache by opening a transient
    /// SendAlgorithmsSubscribe and feeding any drops back through AlgoStore.
    /// The long-lived subscribe established at connect time does not always
    /// receive an initial snapshot — typically only event-driven updates —
    /// so the AlgoStore stays empty on quiet profiles. This forces a fresh
    /// snapshot pull.</summary>
    public bool ForceRefreshAlgos(int timeoutMs = 5_000)
    {
        if (_udpClient == null) { return false; }
        int subId = -1;
        var done = new System.Threading.ManualResetEventSlim(false);
        int dropsReceived = 0;
        try
        {
            subId = _udpClient.SendAlgorithmsSubscribe(
                (NetworkMessageType msgType, NetworkData data) =>
                {
                    AlgoStore.ProcessData(msgType, data);
                    System.Threading.Interlocked.Increment(ref dropsReceived);
                    done.Set();
                });
            if (done.Wait(timeoutMs))
            {
                // Grace window for the rest of the snapshot to arrive.
                System.Threading.Thread.Sleep(500);
            }
            return dropsReceived > 0;
        }
        finally
        {
            if (subId != -1 && _udpClient != null)
            {
                try { _udpClient.SendAlgorithmsUnsubscribe(ref subId); }
                catch { /* best-effort */ }
            }
            done.Dispose();
        }
    }

    /// <summary>Force-refresh the TPSL cache by opening a transient
    /// SendAlgorithmTPSLsSubscribe and feeding any drops back through
    /// TPSLStore. Matches the vendor read pattern: fresh subscribe with
    /// sentinel id (-1), take the first list, unsubscribe in finally. Used
    /// by `mt_tpsl_list` and the `*_many` variants so the first call on a
    /// cold connection primes the store without requiring an explicit
    /// `mt_tpsl_subscribe` first. Returns true if at least one
    /// TPSLInfoListData drop arrived. Lazily creates TPSLStore if absent so
    /// the cancel/split/join paths can read the cached vendor payload.</summary>
    public bool ForceRefreshTPSL(int timeoutMs = 5_000)
    {
        if (_udpClient == null) { return false; }
        if (TPSLStore == null) { TPSLStore = new TPSLStore(); }
        int subId = -1;
        var done = new System.Threading.ManualResetEventSlim(false);
        int dropsReceived = 0;
        try
        {
            subId = _udpClient.SendAlgorithmTPSLsSubscribe(
                (TPSLInfoListData data) =>
                {
                    TPSLStore.ProcessData(data);
                    System.Threading.Interlocked.Increment(ref dropsReceived);
                    done.Set();
                },
                -1);
            if (done.Wait(timeoutMs))
            {
                // Grace window for the rest of the snapshot to arrive
                // (multi-market initial-list comes in segments on some venues).
                System.Threading.Thread.Sleep(500);
            }
            return dropsReceived > 0;
        }
        finally
        {
            if (subId != -1 && _udpClient != null)
            {
                try { _udpClient.SendAlgorithmTPSLsUnsubscribe(ref subId); }
                catch { /* best-effort */ }
            }
            done.Dispose();
        }
    }

    #region Algorithm Lifecycle Requests

    /// <summary>
    /// Send an algorithm request (START, STOP, SAVE, DELETE, TOGGLE_DEBUG, etc.).
    /// MTCore responds with AlgorithmUpdateNotificationData on the notification
    /// channel. See internal vendor wire-pattern reference notes.
    /// </summary>
    public NotificationMessageData? SendAlgorithmRequest(AlgorithmData algoData, int timeoutMs = 30_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<AlgorithmUpdateNotificationData>(
            send: () => _udpClient.SendAlgorithmRequest(algoData),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    public bool TrySendAlgorithmRequestNoWait(AlgorithmData algoData)
    {
        if (_udpClient == null) { return false; }
        try
        {
            _udpClient.SendAlgorithmRequest(algoData);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Send an algorithm list request (START_ALL, STOP_ALL, SAVE_GROUP, DELETE_GROUP, CLONE_GROUP).
    /// MTCore responds with AlgorithmListUpdateNotificationData.
    /// </summary>
    public NotificationMessageData? SendAlgorithmListRequest(AlgorithmListData listData, int timeoutMs = 30_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<AlgorithmListUpdateNotificationData>(
            send: () => _udpClient.SendAlgorithmListRequest(listData),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Request current profile settings from Core.
    /// Stores result in ProfileSettingsStore.
    /// Returns (success, errorMessage).
    /// </summary>
    public (bool Success, string? Error) RequestProfileSettings(int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return (false, (string?)"Not connected.");
        }

        (bool, string?) tcsResult = default;
        {
            var tcs = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(static s => ((TaskCompletionSource<(bool, string?)>)s!).TrySetResult((false, "Timeout")), tcs);
            _udpClient.SendGetCurrentProfileSettingsRequest(response =>
            {
                if (response != null && response.isSucceeded)
                {
                    ProfileSettingsStore.Update(response.profileName, response.settings);
                    tcs.TrySetResult((true, null));
                }
                else
                {
                    tcs.TrySetResult((false, response?.errorMessage ?? "No response from Core."));
                }
            });
            tcsResult = tcs.Task.GetAwaiter().GetResult();
        }
        return tcsResult;
    }

    /// <summary>
    /// Update profile settings on Core.
    /// Returns (success, coreRestartNeeded, errorMessage).
    /// </summary>
    public (bool Success, bool CoreRestartNeeded, string? Error) UpdateProfileSettings(
        Dictionary<string, string> updated, HashSet<string>? deleted = null, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return (false, false, (string?)"Not connected.");
        }

        string? profileName = ProfileSettingsStore.HasData ? ProfileSettingsStore.ProfileName : "";

        (bool, bool, string?) result;
        {
            var tcs = new TaskCompletionSource<(bool, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(static s => ((TaskCompletionSource<(bool, bool, string?)>)s!).TrySetResult((false, false, "Timeout")), tcs);
            _udpClient.SendUpdateProfileSettingsRequest(
                profileName,
                updated,
                deleted ?? new HashSet<string>(),
                response =>
                {
                    if (response != null && response.isSucceeded)
                    {
                        ProfileSettingsStore.Update(response.profileName, response.settings);
                        tcs.TrySetResult((true, response.isCoreRestartNeeded, null));
                    }
                    else
                    {
                        tcs.TrySetResult((false, false, response?.errorMessage ?? "No response from Core."));
                    }
                });
            result = tcs.Task.GetAwaiter().GetResult();
        }
        return result;
    }

    #endregion

    #region Order & Position Management

    /// <summary>
    /// Place an order via Core. MTCore responds via OrderPlaceNotificationData.
    /// </summary>
    public NotificationMessageData? PlaceOrder(OrderRequestData orderRequest, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<OrderPlaceNotificationData>(
            send: () => _udpClient.SendPlaceOrderRequest(orderRequest, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Move (modify price of) an existing order. Responds via OrderMoveNotificationData.
    /// </summary>
    public NotificationMessageData? MoveOrder(
        ExchangeType exchangeType, MarketType marketType,
        string clientOrderId, double newPrice, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        OrderSettings empty = default;
        return SendAndAwaitNotification<OrderMoveNotificationData>(
            send: () => _udpClient.SendMoveOrderRequest(exchangeType, marketType, clientOrderId, newPrice, ref empty, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Cancel a specific order by clientOrderId. Responds via OrderCancelNotificationData.
    /// </summary>
    public NotificationMessageData? CancelOrder(
        ExchangeType exchangeType, MarketType marketType,
        string symbol, string clientOrderId, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<OrderCancelNotificationData>(
            send: () => _udpClient.SendCancelOrderRequest(exchangeType, marketType, symbol, clientOrderId, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Cancel all orders (or all for a specific symbol). Responds via OrderCancelListNotificationData.
    /// </summary>
    public NotificationMessageData? CancelAllOrders(
        ExchangeType exchangeType, MarketType marketType, string? symbol = null, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<OrderCancelListNotificationData>(
            send: () => _udpClient.SendCancelOrderListRequest(
                exchangeType,
                cancelAll: string.IsNullOrEmpty(symbol),
                new OrderListData(),
                symbol ?? "",
                marketType,
                NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Close a position (market or limit) by percentage (1.0 = 100%).
    /// Responds via ClosePositionNotificationData.
    /// </summary>
    public NotificationMessageData? ClosePosition(
        ExchangeType exchangeType, PositionData positionData,
        OrderType orderType, double percentage = 1.0, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<ClosePositionNotificationData>(
            send: () => _udpClient.SendClosePositionRequest(exchangeType, positionData, orderType, percentage, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Close position using TP/SL order. Responds via ClosePositionNotificationData.
    /// </summary>
    public NotificationMessageData? ClosePositionByTPSL(
        ExchangeType exchangeType, PositionData positionData,
        OrderType orderType, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<ClosePositionNotificationData>(
            send: () => _udpClient.SendClosePositionByTPSLRequest(exchangeType, positionData, orderType, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Reset TP/SL on an existing position. Responds via ResetTPSLNotificationData.
    /// </summary>
    public NotificationMessageData? ResetTPSL(
        ExchangeType exchangeType, PositionData positionData,
        TakeProfitSettings tpSettings, StopLossSettings slSettings, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<ResetTPSLNotificationData>(
            send: () => _udpClient.SendResetTPSLRequest(exchangeType, positionData, tpSettings, slSettings, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    #endregion


    /// <summary>
    /// Request historical trade reports from MT-Core's report storage.
    /// This is the historical trading data — closed trades, not just live fills.
    /// Supports filters: excludeEmulated, closedBy, marketTypes, orderSideTypes, tradeModeType.
    /// </summary>
    public ReportListData? RequestReports(
        long unixFrom, long unixTo,
        string symbolFilter = "", string algoNameFilter = "",
        string signaturesFilter = "", bool includeMetrics = false,
        bool excludeEmulated = false,
        List<ReportClosedByType>? closedBy = null,
        List<MarketType>? marketTypes = null,
        List<OrderSideType>? orderSideTypes = null,
        TradeModeType tradeModeType = TradeModeType.UNKNOWN,
        int timeoutMs = 5_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new ReportRequestData
        {
            exchangeType = Profile.Exchange,
            unixTimeFrom = unixFrom,
            unixTimeTo = unixTo,
            symbolsFilter = symbolFilter ?? "",
            algoNamesFilter = algoNameFilter ?? "",
            signaturesFilter = !string.IsNullOrEmpty(signaturesFilter)
                ? new List<string> { signaturesFilter }
                : new List<string>(),
            includeMetricsData = includeMetrics,
            excludeEmulated = excludeEmulated,
            tradeModeType = tradeModeType,
        };

        // B6: Apply optional list filters
        if (closedBy != null && closedBy.Count > 0)
        {
            request.closedBy = closedBy;
        }

        if (marketTypes != null && marketTypes.Count > 0)
        {
            request.marketTypes = marketTypes;
        }

        if (orderSideTypes != null && orderSideTypes.Count > 0)
        {
            request.orderSideTypes = orderSideTypes;
        }

        // SendReportListRequest is one of the few methods MTCore 0.7.23902
        // still delivers via inline callback (typed Action<ReportListData>),
        // not via the notification push channel. Keep the legacy SendAndWait
        // path here.
        return SendAndWait<ReportListData>(
            cb => _udpClient.SendReportListRequest(request, cb), timeoutMs);
    }


    #region Read Queries

    /// <summary>
    /// Get 24h ticker price statistics for a symbol.
    /// </summary>
    public TickerPrice24ListData? RequestTicker24(
        MarketType marketType, string symbol, int timeoutMs = 5_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<TickerPrice24ListData>(
            cb => _udpClient.SendTickerPrice24Request(Profile.Exchange, marketType, symbol, cb),
            timeoutMs);
    }

    /// <summary>
    /// Get kline (candlestick) data for a symbol.
    /// </summary>
    public KlineListData? RequestKlines(
        MarketType marketType, string symbol, KlineInterval interval, short limit = 100, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<KlineListData>(
            cb => _udpClient.SendGetKlineListRequest(Profile.Exchange, marketType, symbol, interval, limit, cb),
            timeoutMs);
    }

    /// <summary>
    /// Get position mode (HEDGE/ONE_WAY) for a symbol.
    /// </summary>
    public NotificationMessageData? GetPositionMode(
        MarketType marketType, string symbol, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new PositionModeTypeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType   = marketType,
            symbol       = symbol
        };
        return SendAndAwaitNotification<GetPositionModeTypeNotificationData>(
            send: () => _udpClient.SendGetPositionModeType(request),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Get recent trades for a symbol from the exchange.
    /// </summary>
    public (TradeListData? Data, NotificationCode Code) RequestTrades(
        MarketType marketType, string symbol, long toTimestamp = 0, long minTradeId = 0, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return (null, NotificationCode.ERROR);
        }

        (TradeListData?, NotificationCode) tradesResult;
        {
            var tcs = new TaskCompletionSource<(TradeListData?, NotificationCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(static s => ((TaskCompletionSource<(TradeListData?, NotificationCode)>)s!).TrySetResult((null, NotificationCode.ERROR)), tcs);
            _udpClient.SendTradesRequest(Profile.Exchange, marketType, symbol, toTimestamp, minTradeId,
                (data, code) => tcs.TrySetResult((data, code)));
            tradesResult = tcs.Task.GetAwaiter().GetResult();
        }
        return tradesResult;
    }

    /// <summary>
    /// Get report comment labels.
    /// </summary>
    public ReportsFieldData? RequestReportComments(int timeoutMs = 30_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<ReportsFieldData>(
            cb => _udpClient.SendReportCommentsRequest(cb), timeoutMs);
    }

    /// <summary>
    /// Get report date markers.
    /// </summary>
    public ReportsFieldData? RequestReportDates(int timeoutMs = 30_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<ReportsFieldData>(
            cb => _udpClient.SendReportsDateRequest(cb), timeoutMs);
    }

    #endregion

    #region Write Operations

    /// <summary>
    /// Set position mode (HEDGE/ONE_WAY) for a symbol.
    /// </summary>
    public NotificationMessageData? SetPositionMode(
        MarketType marketType, string symbol, PositionModeType mode, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new PositionModeTypeRequestData
        {
            exchangeType     = Profile.Exchange,
            marketType       = marketType,
            symbol           = symbol,
            positionModeType = mode
        };
        return SendAndAwaitNotification<SetPositionModeTypeNotificationData>(
            send: () => _udpClient.SendSetPositionModeType(request),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Modify leverage for a symbol.
    /// </summary>
    public NotificationMessageData? ModifyLeverage(
        MarketType marketType, string symbol, short leverage, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new ModifyLeverageRequestData
        {
            exchangeType = Profile.Exchange,
            marketType   = marketType,
            asset        = symbol,
            newLeverage  = leverage,
            leverageType = LeverageType.CROSS
        };
        return SendAndAwaitNotification<ModifyLeverageNotificationData>(
            send: () => _udpClient.SendModifyLeverageRequest(request),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Modify margin type (CROSS/ISOLATED) for a symbol.
    /// </summary>
    public NotificationMessageData? ModifyMarginType(
        MarketType marketType, string symbol, MarginType marginType, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new ModifyMarginTypeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType   = marketType,
            symbol       = symbol,
            marginType   = marginType
        };
        return SendAndAwaitNotification<ModifyMarginTypeNotificationData>(
            send: () => _udpClient.SendModifyMarginTypeRequest(request),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Panic sell — emergency market-close all positions for an asset.
    /// Responds via PanicSellNotificationData.
    /// </summary>
    public NotificationMessageData? PanicSell(
        MarketType marketType, string asset, bool activate = true, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        return SendAndAwaitNotification<PanicSellNotificationData>(
            send: () => _udpClient.SendPanicSellRequest(Profile.Exchange, marketType, asset, activate, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Panic sell a single TPSL position (the per-TPSL overload). Echoes
    /// the full cached TPSLInfoData. Responds via PanicSellNotificationData
    /// (isTPSL=true). The server binds by the full identity tuple, not the
    /// id alone — passing a stub fails silently.
    /// </summary>
    public NotificationMessageData? PanicSellTpsl(long tpslId, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        TPSLInfoData? cached = TPSLStore.GetRawById(tpslId);
        TPSLInfoData msgData = cached ?? new TPSLInfoData { id = tpslId };
        msgData.requestExchangeType = Profile.Exchange;
        return SendAndAwaitNotification<PanicSellNotificationData>(
            send: () => _udpClient.SendPanicSellRequest(msgData, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Add or reduce margin on an isolated-margin position.
    /// Responds via MarginChangeNotificationData.
    /// </summary>
    public NotificationMessageData? ChangePositionMargin(
        MarketType marketType, string symbol, PositionSide positionSide,
        decimal amount, bool isAdd = true, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        var request = new ChangePositionMarginRequest
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            symbol = symbol,
            positionSide = positionSide,
            amount = amount,
            actionType = isAdd
                ? ChangePositionMarginRequest.ActionType.ADD
                : ChangePositionMarginRequest.ActionType.REDUCE
        };
        return SendAndAwaitNotification<MarginChangeNotificationData>(
            send: () => _udpClient.SendChangePositionMargin(Profile.Exchange, request, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    /// <summary>
    /// Transfer funds between spot and futures (market type transfer).
    /// MTCore now uses a typed Action&lt;TransferFundsNotificationData&gt; callback.
    /// </summary>
    public NotificationMessageData? TransferFunds(
        MarketType fromMarket, MarketType toMarket, string asset, double amount, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        var tcs = new TaskCompletionSource<NotificationMessageData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(
            static state => ((TaskCompletionSource<NotificationMessageData?>)state!).TrySetResult(null), tcs);
        _udpClient.SendTransferFundsRequest(Profile.Exchange, fromMarket, asset, amount, toMarket, 0, "",
            (TransferFundsNotificationData n) => tcs.TrySetResult(new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            }));
        return tcs.Task.GetAwaiter().GetResult();
    }

    #endregion

    #region AutoStops

    /// <summary>
    /// Request autostop baseline recalculation (fire-and-forget).
    /// </summary>
    public void SendAutoStopsBaselineRequest()
    {
        _udpClient?.SendAutoStopsBaselineRequest();
    }

    /// <summary>
    /// Request autostop algorithm report data for specific algorithm IDs.
    /// </summary>
    public ReportListData? RequestAutoStopsReports(List<long> algorithmIds, int timeoutMs = 30_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new AutoStopsAlgorithmsRequestData
        {
            exchangeType = Profile.Exchange,
            algorithmIds = algorithmIds ?? new List<long>()
        };

        return SendAndWait<ReportListData>(
            cb => _udpClient.SendAutoStopsAlgorithmsRequest(request, cb), timeoutMs);
    }

    #endregion

    #region TPSL Subscriptions

    /// <summary>
    /// Subscribe to TPSL position updates. Creates TPSLStore if not already created.
    /// </summary>
    public bool SubscribeTPSL()
    {
        if (_udpClient == null)
        {
            return false;
        }

        if (TPSLStore == null)
        {
            TPSLStore = new TPSLStore();
        }

        _tpslSubscriptionId = _udpClient.SendAlgorithmTPSLsSubscribe(
            (TPSLInfoListData data) =>
            {
                TPSLStore.ProcessData(data);
            },
            _tpslSubscriptionId);

        return true;
    }

    /// <summary>
    /// Unsubscribe from TPSL updates.
    /// </summary>
    public void UnsubscribeTPSL()
    {
        if (_udpClient != null && _tpslSubscriptionId != 0)
        {
            _udpClient.SendAlgorithmTPSLsUnsubscribe(ref _tpslSubscriptionId);
        }
    }

    /// <summary>
    /// Cancel a TPSL position by ID. The server binds the target by the
    /// full identity tuple carried in TPSLInfoData, not just the id —
    /// the request must echo the full cached vendor object. An id-only
    /// stub is silently rejected.
    /// </summary>
    public NotificationMessageData? CancelTPSL(long tpslId, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        // Echo the full TPSLInfoData from the local cache; fall back to an
        // id-only stub if the TPSL feed hasn't populated yet so callers
        // still get a structured response instead of a hard null.
        TPSLInfoData? cached = TPSLStore.GetRawById(tpslId);
        TPSLInfoData msgData = cached ?? new TPSLInfoData { id = tpslId };
        msgData.requestExchangeType = Profile.Exchange;

        return SendAndAwaitNotification<TPSLCancelNotificationData>(
            send: () => _udpClient.SendCancelTPSLRequest(msgData, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    #endregion

    #region Trading Performance

    /// <summary>
    /// Subscribe to trading performance updates. Creates TradingPerfStore if not already created.
    /// </summary>
    public bool SubscribeTradingPerformance(MarketType marketType = MarketType.FUTURES)
    {
        if (_udpClient == null)
        {
            return false;
        }

        if (TradingPerfStore == null)
        {
            TradingPerfStore = new TradingPerformanceStore();
        }

        _tradingPerfSubscriptionId = _udpClient.SendTradingPerformanceSubscribe(
            Profile.Exchange,
            marketType,
            (TradingPerformanceListData data) =>
            {
                TradingPerfStore.ProcessData(data);
            },
            _tradingPerfSubscriptionId);

        return true;
    }

    /// <summary>
    /// Unsubscribe from trading performance updates.
    /// </summary>
    public void UnsubscribeTradingPerformance()
    {
        if (_udpClient != null && _tradingPerfSubscriptionId != 0)
        {
            _udpClient.SendTradingPerformanceUnsubscribe(
                ref _tradingPerfSubscriptionId, Profile.Exchange, MarketType.FUTURES);
        }
    }

    /// <summary>
    /// Request a trading performance refresh or reset (fire-and-forget).
    /// </summary>
    public void SendTradingPerformanceRequest(
        TradingPerformanceRequestData.ActionType actionType = TradingPerformanceRequestData.ActionType.REFRESH)
    {
        if (_udpClient == null)
        {
            return;
        }

        var request = new TradingPerformanceRequestData
        {
            exchangeType = Profile.Exchange,
            actionType = actionType
        };

        _udpClient.SendTradingPerformanceRequest(request);
    }

    #endregion

    #region Notifications

    public void SubscribeNotifications()
    {
        if (_udpClient == null)
        {
            return;
        }

        // Long-lived passive subscription — only used to populate
        // NotificationStore for the mt_notifications_list tool. Per-request
        // wire dispatching is handled by transient subscriptions inside
        // SendAndAwaitNotification, mirroring the vendor BotClient pattern.
        _notificationSubscriptionId = _udpClient.SendNotificationSubscribe(
            Profile.Exchange,
            (AbstractNotificationData data) =>
            {
                string typeName = data.GetType().Name.Replace("NotificationData", "");
                string message = data.notificationDescriptor.Id ?? "";
                string profileName = data.profileName ?? Profile.Name;
                var entry = new NotificationEntry(profileName, typeName, message, "", data.creationTime);
                NotificationStore.Add(entry);
            },
            _notificationSubscriptionId);

        // SendNotificationSubscribe returns 0 on some MTCore builds even when
        // the callback registers correctly. Record the registered state on a
        // separate flag for diagnostics; the per-request path no longer
        // depends on it.
        _notificationCallbackRegistered = true;
    }

    public void UnsubscribeNotifications()
    {
        if (_udpClient != null && _notificationCallbackRegistered)
        {
            _udpClient.SendNotificationUnsubscribe(ref _notificationSubscriptionId, Profile.Exchange);
            _notificationCallbackRegistered = false;
        }
    }

    #endregion

    #region Market Data Subscriptions

    public void SubscribeTrades(ExchangeType exchange, MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return;
        }

        string key = $"{exchange}:{marketType}:{symbol}";
        int existingId = 0;
        _tradeSubscriptionIds.TryGetValue(key, out existingId);

        int newId = _udpClient.SendTradeSubscribe(
            exchange, marketType, symbol,
            (TradeListData data) =>
            {
                if (data.trades == null)
                {
                    return;
                }

                foreach (TradeUpdateData trade in data.trades)
                {
                    MarketDataStore.UpdateTrade(key, trade);
                }
            },
            existingId);

        _tradeSubscriptionIds[key] = newId;
    }

    public void UnsubscribeTrades(ExchangeType exchange, MarketType marketType, string symbol)
    {
        string key = $"{exchange}:{marketType}:{symbol}";
        if (_udpClient != null && _tradeSubscriptionIds.TryRemove(key, out int subId) && subId != 0)
        {
            _udpClient.SendTradeUnsubscribe(ref subId, exchange, marketType, symbol);
        }
    }

    public void SubscribeDepth(ExchangeType exchange, MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return;
        }

        string key = $"{exchange}:{marketType}:{symbol}";
        int existingId = 0;
        _depthSubscriptionIds.TryGetValue(key, out existingId);

        int newId = _udpClient.SendDepthSubscribe(
            exchange, marketType, symbol, false, false,
            (DepthUpdateData data) =>
            {
                MarketDataStore.UpdateDepth(key, data);
            },
            existingId);

        _depthSubscriptionIds[key] = newId;
    }

    public void UnsubscribeDepth(ExchangeType exchange, MarketType marketType, string symbol)
    {
        string key = $"{exchange}:{marketType}:{symbol}";
        if (_udpClient != null && _depthSubscriptionIds.TryRemove(key, out int subId) && subId != 0)
        {
            _udpClient.SendDepthUnsubscribe(ref subId, exchange, marketType, symbol, false, false);
        }
    }

    public void SubscribeMarkPrice(ExchangeType exchange, MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return;
        }

        string key = $"{exchange}:{marketType}:{symbol}";
        int existingId = 0;
        _markPriceSubscriptionIds.TryGetValue(key, out existingId);

        int newId = _udpClient.SendMarkPriceSubscribe(
            exchange, marketType, symbol,
            (MarkPriceUpdateData data) =>
            {
                MarketDataStore.UpdateMarkPrice(key, data);
            },
            existingId);

        _markPriceSubscriptionIds[key] = newId;
    }

    public void UnsubscribeMarkPrice(ExchangeType exchange, MarketType marketType, string symbol)
    {
        string key = $"{exchange}:{marketType}:{symbol}";
        if (_udpClient != null && _markPriceSubscriptionIds.TryRemove(key, out int subId) && subId != 0)
        {
            _udpClient.SendMarkPriceUnsubscribe(ref subId, exchange, marketType, symbol);
        }
    }

    public void SubscribeKlines(ExchangeType exchange, MarketType marketType, string symbol, KlineInterval interval)
    {
        if (_udpClient == null)
        {
            return;
        }

        string key = $"{exchange}:{marketType}:{symbol}:{interval}";
        int existingId = 0;
        _klineSubscriptionIds.TryGetValue(key, out existingId);

        int newId = _udpClient.SendKlineSubscribe(
            exchange, marketType, symbol, interval,
            (KlineListData data) =>
            {
                if (data.klines == null)
                {
                    return;
                }

                foreach (KlineUpdateData kline in data.klines)
                {
                    MarketDataStore.UpdateKline(key, kline);
                }
            },
            existingId);

        _klineSubscriptionIds[key] = newId;
    }

    public void UnsubscribeKlines(ExchangeType exchange, MarketType marketType, string symbol, KlineInterval interval)
    {
        string key = $"{exchange}:{marketType}:{symbol}:{interval}";
        if (_udpClient != null && _klineSubscriptionIds.TryRemove(key, out int subId) && subId != 0)
        {
            _udpClient.SendKlineUnsubscribe(ref subId, exchange, marketType, symbol, interval);
        }
    }

    public void SubscribeTicker(ExchangeType exchange, MarketType marketType)
    {
        if (_udpClient == null)
        {
            return;
        }

        _tickerSubscriptionId = _udpClient.SendTickerSubscribe(
            exchange, marketType,
            (NetworkMessageType msgType, NetworkData data) =>
            {
                if (data is TickerListData tickerList && tickerList.tickers != null)
                {
                    foreach (KeyValuePair<string, TickerUpdateData> kvp in tickerList.tickers)
                    {
                        string key = $"{exchange}:{marketType}:{kvp.Key}";
                        MarketDataStore.UpdateTicker(key, kvp.Value);
                    }
                }
            },
            _tickerSubscriptionId);
    }

    /// <summary>Snapshot-prime the ticker cache for a market without leaving
    /// a long-lived subscription open. Opens a transient SendTickerSubscribe,
    /// awaits the first TickerListData drop (which carries every symbol's
    /// last price), feeds it into MarketDataStore via UpdateTicker, then
    /// unsubscribes. Returns the count of tickers received (0 = wire still
    /// not responding within timeoutMs).
    ///
    /// Used by HandleTicker as the fallback when the cache is empty — the
    /// long-lived subscribe is best-effort and never warms a cold cache
    /// quickly, so an explicit prime is the only path to a one-shot snapshot.</summary>
    public int ForceRefreshTicker(ExchangeType exchange, MarketType marketType, int timeoutMs = 5_000)
    {
        if (_udpClient == null) { return 0; }
        int subId = -1;
        var done = new System.Threading.ManualResetEventSlim(false);
        int received = 0;
        try
        {
            subId = _udpClient.SendTickerSubscribe(
                exchange, marketType,
                (NetworkMessageType _, NetworkData data) =>
                {
                    if (data is TickerListData tickerList && tickerList.tickers != null)
                    {
                        foreach (KeyValuePair<string, TickerUpdateData> kvp in tickerList.tickers)
                        {
                            string key = $"{exchange}:{marketType}:{kvp.Key}";
                            MarketDataStore.UpdateTicker(key, kvp.Value);
                            System.Threading.Interlocked.Increment(ref received);
                        }
                        done.Set();
                    }
                },
                -1);
            done.Wait(timeoutMs);
            return received;
        }
        finally
        {
            if (subId != -1 && _udpClient != null)
            {
                try { _udpClient.SendTickerUnsubscribe(ref subId, exchange, marketType); }
                catch { /* best-effort */ }
            }
            done.Dispose();
        }
    }

    public void UnsubscribeTicker(ExchangeType exchange, MarketType marketType)
    {
        if (_udpClient != null && _tickerSubscriptionId != 0)
        {
            _udpClient.SendTickerUnsubscribe(ref _tickerSubscriptionId, exchange, marketType);
        }
    }

    #endregion

    #region Alerts

    public void SubscribeAlerts()
    {
        if (_udpClient == null)
        {
            return;
        }

        _alertsSubscriptionId = _udpClient.SendAlertsSubscribe(
            Profile.Exchange,
            (AlertResultData data) =>
            {
                if (data is AlertResultSubscribedData subscribed)
                {
                    if (subscribed.alertInfos != null)
                    {
                        AlertStore.SetAlerts(subscribed.alertInfos);
                    }
                }
                else if (data is AlertResultAddedData added)
                {
                    AlertStore.AddOrUpdate(added.alertInfo);
                }
                else if (data is AlertResultUpdatedData updated)
                {
                    AlertStore.AddOrUpdate(updated.alertInfo);
                }
                else if (data is AlertResultDeletedData deleted)
                {
                    AlertStore.Remove(deleted.alertId);
                }
            },
            _alertsSubscriptionId);
    }

    public void UnsubscribeAlerts()
    {
        if (_udpClient != null && _alertsSubscriptionId != 0)
        {
            _udpClient.SendAlertsUnsubscribe(ref _alertsSubscriptionId, Profile.Exchange);
        }
    }

    public void SubscribeAlertHistory()
    {
        if (_udpClient == null)
        {
            return;
        }

        _alertHistorySubscriptionId = _udpClient.SendAlertsHistorySubscribe(
            Profile.Exchange,
            (AlertHistoryResultData data) =>
            {
                AlertStore.AddHistory(new AlertHistoryEntry(data.exchangeType, data.ActionType.ToString(), data.GetType().Name));
            },
            _alertHistorySubscriptionId);
    }

    public void UnsubscribeAlertHistory()
    {
        if (_udpClient != null && _alertHistorySubscriptionId != 0)
        {
            _udpClient.SendAlertsHistoryUnsubscribe(ref _alertHistorySubscriptionId, Profile.Exchange);
        }
    }

    // CRUD on alerts via SendAlertsRequest with the right
    // AlertRequestSaveData / DeleteData / StartData / StopData subtype.
    // MTCore dispatches by ActionType; the populated subtype carries the
    // alert ids / records. Each helper blocks up to ~2 s waiting for the
    // NotificationMessageData callback (mirroring SendAutoBuyRequest's
    // sleep-2s pattern), then returns the server's msg string.

    public string SendAlertsSave(List<AlertInfoData> alerts, bool returnSaved = true, int waitMs = 2000)
    {
        if (_udpClient == null) return "Not connected";
        var req = new AlertRequestSaveData
        {
            alerts = alerts,
            returnSavedAlerts = returnSaved,
            exchangeType = Profile.Exchange,
        };
        string resultMsg = "Waiting...";
        _udpClient.SendAlertsRequest(req, (AlertNotificationData result) =>
        {
            resultMsg = result?.message ?? "OK";
        });
        System.Threading.Thread.Sleep(waitMs);
        return resultMsg;
    }

    public string SendAlertsDelete(List<long> alertIds, bool applyToAll = false, int waitMs = 2000)
    {
        if (_udpClient == null) return "Not connected";
        var req = new AlertRequestDeleteData
        {
            alertIds = alertIds ?? new List<long>(),
            applyToAll = applyToAll,
            exchangeType = Profile.Exchange,
        };
        string resultMsg = "Waiting...";
        _udpClient.SendAlertsRequest(req, (AlertNotificationData result) =>
        {
            resultMsg = result?.message ?? "OK";
        });
        System.Threading.Thread.Sleep(waitMs);
        return resultMsg;
    }

    public string SendAlertsSetRunning(List<long> alertIds, bool running, bool applyToAll = false, int waitMs = 2000)
    {
        if (_udpClient == null) return "Not connected";
        AlertRequestData req = running
            ? new AlertRequestStartData
            {
                alertIds = alertIds ?? new List<long>(),
                applyToAll = applyToAll,
                exchangeType = Profile.Exchange,
            }
            : new AlertRequestStopData
            {
                alertIds = alertIds ?? new List<long>(),
                applyToAll = applyToAll,
                exchangeType = Profile.Exchange,
            };
        string resultMsg = "Waiting...";
        _udpClient.SendAlertsRequest(req, (AlertNotificationData result) =>
        {
            resultMsg = result?.message ?? "OK";
        });
        System.Threading.Thread.Sleep(waitMs);
        return resultMsg;
    }

    #endregion

    #region Leverage Extensions

    public NotificationMessageData? ModifyLeverageBuySell(
        MarketType marketType, string asset, short buyLeverage, short sellLeverage, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new ModifyLeverageBuySellRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            asset = asset,
            buyLeverage = buyLeverage,
            sellLeverage = sellLeverage
        };

        return SendAndAwaitNotification<ModifyLeverageNotificationData>(
            send: () => _udpClient.SendModifyLeverageBuySellRequest(request),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    public MultiAssetModeResultData? GetMultiAssetMode(MarketType marketType, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new MultiAssetModeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            getMultiAssetMode = true,
            setEnabled = false
        };

        return SendAndWait<MultiAssetModeResultData>(
            cb => _udpClient.SendModifyMultiAssetMode(request, cb), timeoutMs);
    }

    public MultiAssetModeResultData? SetMultiAssetMode(
        MarketType marketType, bool enabled, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        var request = new MultiAssetModeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            getMultiAssetMode = false,
            setEnabled = enabled
        };

        return SendAndWait<MultiAssetModeResultData>(
            cb => _udpClient.SendModifyMultiAssetMode(request, cb), timeoutMs);
    }

    #endregion

    #region Profiling

    public void SubscribeProfiling(MarketType marketType, string symbol, long algorithmId)
    {
        if (_udpClient == null)
        {
            return;
        }

        symbol = (symbol ?? string.Empty).ToLowerInvariant();
        string key = $"{marketType}:{symbol}:{algorithmId}";
        // Strong ref to the callback so the SDK's WeakDelegate isn't GC'd.
        Action<MTShared.Network.AlgorithmProfilingData> cb = data => OnProfilingDataReceived?.Invoke(this, data);
        _profilingCallbacks[key] = cb;
        int existing = _profilingSubscriptionIds.TryGetValue(key, out int prev) ? prev : -1;
        int newId = _udpClient.SendAlgorithmProfilingDataSubscribe(
            Profile.Exchange, marketType, symbol, algorithmId, cb, existing);
        _profilingSubscriptionIds[key] = newId;
    }

    public void UnsubscribeProfiling(MarketType marketType, string symbol, long algorithmId)
    {
        symbol = (symbol ?? string.Empty).ToLowerInvariant();
        string key = $"{marketType}:{symbol}:{algorithmId}";
        if (_udpClient != null && _profilingSubscriptionIds.TryRemove(key, out int id) && id != 0)
        {
            _udpClient.SendAlgorithmProfilingDataUnsubscribe(
                ref id, Profile.Exchange, marketType, symbol, algorithmId);
        }
        _profilingCallbacks.TryRemove(key, out _);
    }

    #endregion


    #region Triggers

    private int _triggersSubscriptionId;

    /// <summary>Per-connection trigger store.</summary>
    public TriggerStore TriggerStore { get; } = new TriggerStore();

    public void SubscribeTriggers()
    {
        if (_udpClient == null)
        {
            return;
        }

        _triggersSubscriptionId = _udpClient.SendTriggersSubscribe(
            Profile.Exchange,
            (msgType, data) =>
            {
                TriggerStore.Add(new TriggerEntry(Name, msgType.ToString(), data?.ToString() ?? ""));
            },
            _triggersSubscriptionId);
    }

    public void UnsubscribeTriggers()
    {
        if (_udpClient != null && _triggersSubscriptionId != 0)
        {
            _udpClient.SendTriggersUnsubscribe(ref _triggersSubscriptionId, Profile.Exchange);
        }
    }

    public string SendTriggerRequest(string actionType, string dataJson)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new TriggerRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.dataJson = dataJson;

        if (Enum.TryParse<TriggerRequestData.ActionType>(actionType, true, out var action))
        {
            reqData.actionType = action;
        }

        var r = SendAndAwaitNotification<TriggerNotificationData>(
            send: () => _udpClient.SendTriggerRequest(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    #endregion

    #region LiveMarkets

    private int _liveMarketsSubscriptionId;

    /// <summary>Per-connection live market store.</summary>
    public LiveMarketStore LiveMarketStore { get; } = new LiveMarketStore();

    public void SubscribeLiveMarkets(MarketType marketType, string symbol, string quoteAsset)
    {
        if (_udpClient == null)
        {
            return;
        }

        _liveMarketsSubscriptionId = _udpClient.SendLiveMarketsSubscribe(
            Profile.Exchange, marketType,
            (LiveMarketMetricsData data) =>
            {
                string key = $"{data.symbol}:{data.marketType}";
                string metricsJson = Newtonsoft.Json.JsonConvert.SerializeObject(data.metrics);
                LiveMarketStore.Update(key, new LiveMarketEntry(data.symbol, data.marketType.ToString(), metricsJson));
            },
            _liveMarketsSubscriptionId, quoteAsset, symbol);
    }

    public void UnsubscribeLiveMarkets(MarketType marketType, string symbol, string quoteAsset)
    {
        if (_udpClient != null && _liveMarketsSubscriptionId != 0)
        {
            _udpClient.SendLiveMarketsUnsubscribe(
                ref _liveMarketsSubscriptionId, Profile.Exchange, marketType, quoteAsset, symbol);
        }
    }

    #endregion

    #region AutoBuy

    private int _autoBuySubscriptionId;

    /// <summary>Per-connection auto-buy store.</summary>
    public AutoBuyStore AutoBuyStore { get; } = new AutoBuyStore();

    public void SubscribeAutoBuy()
    {
        if (_udpClient == null)
        {
            return;
        }

        _autoBuySubscriptionId = _udpClient.SendAutoBuySubscribe(
            Profile.Exchange,
            (AutoBuyResultData data) =>
            {
                AutoBuyStore.Add(new AutoBuyEntry(Name, data.ActionType.ToString(), data.GetType().Name));
            },
            _autoBuySubscriptionId);
    }

    public void UnsubscribeAutoBuy()
    {
        if (_udpClient != null && _autoBuySubscriptionId != 0)
        {
            _udpClient.SendAutoBuyUnsubscribe(ref _autoBuySubscriptionId, Profile.Exchange);
        }
    }

    public string SendAutoBuyRequest(string actionType, string dataJson)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        if (!Enum.TryParse<AutoBuyRequestData.RequestActionType>(actionType, true, out var action))
        {
            return $"Unknown autobuy action: {actionType}. " +
                "Expected one of SAVE, DELETE, START, STOP, REFRESH_ASSET_PAIRS.";
        }

        AutoBuyRequestData reqData;
        try
        {
            reqData = BuildAutoBuyRequest(action, dataJson);
        }
        catch (Exception ex)
        {
            return $"Invalid dataJson for {action}: {ex.Message}";
        }
        reqData.exchangeType = Profile.Exchange;

        var r = SendAndAwaitNotification<AutoBuyInfoNotificationData>(
            send: () => _udpClient.SendAutoBuyRequest(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    // Per-action subtype pattern: each RequestActionType maps to a
    // dedicated AutoBuyRequest*Data subtype
    // with the per-action payload populated via object initializer. Sending
    // the base AutoBuyRequestData(action) leaves MTCore reading past EOF on
    // the deserialise side — instantiate the subtype.
    //
    // dataJson contract per action:
    //   SAVE                 → { "autoBuys": [ AutoBuyInfoData{...}, ... ] }
    //   DELETE               → { "autoBuyIds": [<long>, ...] }
    //   START / STOP         → { "autoBuyIds": [<long>, ...], "applyToAll": <bool> }
    //   REFRESH_ASSET_PAIRS  → { } (no payload)
    private static AutoBuyRequestData BuildAutoBuyRequest(
        AutoBuyRequestData.RequestActionType action, string dataJson)
    {
        Newtonsoft.Json.Linq.JObject obj = string.IsNullOrWhiteSpace(dataJson)
            ? new Newtonsoft.Json.Linq.JObject()
            : Newtonsoft.Json.Linq.JObject.Parse(dataJson);

        switch (action)
        {
            case AutoBuyRequestData.RequestActionType.SAVE:
            {
                var save = new AutoBuyRequestSaveData();
                if (obj["autoBuys"] is Newtonsoft.Json.Linq.JArray arr)
                {
                    save.autoBuys = arr.ToObject<List<AutoBuyInfoData>>()
                        ?? new List<AutoBuyInfoData>();
                }
                else
                {
                    save.autoBuys = new List<AutoBuyInfoData>();
                }
                return save;
            }
            case AutoBuyRequestData.RequestActionType.DELETE:
            {
                var del = new AutoBuyRequestDeleteData();
                if (obj["autoBuyIds"] is Newtonsoft.Json.Linq.JArray ids)
                {
                    del.autoBuyIds = ids.ToObject<List<long>>() ?? new List<long>();
                }
                else
                {
                    del.autoBuyIds = new List<long>();
                }
                return del;
            }
            case AutoBuyRequestData.RequestActionType.START:
            {
                var start = new AutoBuyRequestStartData();
                if (obj["applyToAll"] != null) { start.applyToAll = obj["applyToAll"]!.ToObject<bool>(); }
                if (obj["autoBuyIds"] is Newtonsoft.Json.Linq.JArray ids)
                {
                    start.autoBuyIds = ids.ToObject<List<long>>() ?? new List<long>();
                }
                return start;
            }
            case AutoBuyRequestData.RequestActionType.STOP:
            {
                var stop = new AutoBuyRequestStopData();
                if (obj["applyToAll"] != null) { stop.applyToAll = obj["applyToAll"]!.ToObject<bool>(); }
                if (obj["autoBuyIds"] is Newtonsoft.Json.Linq.JArray ids)
                {
                    stop.autoBuyIds = ids.ToObject<List<long>>() ?? new List<long>();
                }
                return stop;
            }
            case AutoBuyRequestData.RequestActionType.REFRESH_ASSET_PAIRS:
                return new AutoBuyRequestRefreshAssetPairsData();
            default:
                // Unknown action — fall back to the action-only base so the wire
                // doesn't get an unset enum, but this code path is unreachable
                // unless MTShared adds a new RequestActionType.
                return new AutoBuyRequestData(action);
        }
    }

    #endregion

    #region GraphTool

    private int _graphToolSubscriptionId;

    /// <summary>Per-connection graph tool store.</summary>
    public GraphToolStore GraphToolStore { get; } = new GraphToolStore();

    public void SubscribeGraphTool()
    {
        if (_udpClient == null)
        {
            return;
        }

        _graphToolSubscriptionId = _udpClient.SendGraphToolSubscribe(
            Profile.Exchange,
            (GraphToolEventData data) =>
            {
                GraphToolStore.Add(new GraphToolEntry(Name, data.EventType ?? "", data.tools?.Count.ToString() ?? "0"));
            },
            _graphToolSubscriptionId);
    }

    public void UnsubscribeGraphTool()
    {
        if (_udpClient != null && _graphToolSubscriptionId != 0)
        {
            _udpClient.SendGraphToolUnsubscribe(ref _graphToolSubscriptionId, Profile.Exchange);
        }
    }

    public string SendGraphToolRequest(string requestType, string dataJson)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new GraphToolRequestData();
        reqData.exchangeType = Profile.Exchange;

        var r = SendAndAwaitNotification<GraphToolNotificationData>(
            send: () => _udpClient.SendGraphToolRequest(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    #endregion

    #region Signals

    public void SendSignal(string channelId, MarketType marketType, OrderSideType side,
        string symbol, decimal price, float tpPct, float slPct)
    {
        if (_udpClient == null)
        {
            return;
        }

        var signal = new SignalData();
        signal.channelId = channelId;
        signal.exchangeType = Profile.Exchange;
        signal.marketType = marketType;
        signal.orderSide = side;
        signal.symbol = symbol;
        signal.price = price;
        signal.useTakeProfit = tpPct > 0;
        signal.useStopLoss = slPct > 0;
        signal.takeProfitPercentage = tpPct;
        signal.stopLossPersentage = slPct;

        _udpClient.SendSignalDataRequest(signal);
    }

    #endregion

    #region Dust

    public string GetDust()
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendDustRequest(DustRequestType.GET_INITIAL_STATE, Profile.Exchange,
            (DustResultData result) =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Result: {result.resultCode}");
                if (result.assets != null && result.assets.Length > 0)
                {
                    sb.AppendLine($"Assets: {result.AssetsAsString}");
                }
                sb.AppendLine($"Convert to: {result.convertToAsset}");
                sb.AppendLine($"Total: {result.totalAmount}");
                sb.AppendLine($"Fee: {result.feeAmopunt}");
                resultMsg = sb.ToString();
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string ConvertDust()
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendDustRequest(DustRequestType.CONVERT_DUST, Profile.Exchange,
            (DustResultData result) =>
            {
                resultMsg = $"Result: {result.resultCode}, Total: {result.totalAmount}, Fee: {result.feeAmopunt}";
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    #endregion

    #region Deposit

    public string GetDepositInfo(string coin)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new DepositRequestData();
        reqData.requestCommand = DepositRequestCommand.GET_INFO;
        reqData.exchangeType = Profile.Exchange;
        reqData.coin = coin;

        string resultMsg = "Waiting...";
        _udpClient.SendDepositRequest(reqData,
            (DepositRequestData result) =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Coin: {result.coin}");
                if (result.networks != null)
                {
                    sb.AppendLine($"Networks: {result.networks.Count}");
                }
                if (result.depositCoins != null)
                {
                    sb.AppendLine($"Deposit coins: {result.depositCoins.Count}");
                }
                resultMsg = sb.ToString();
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string GetDepositAddress(string coin, string network)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new DepositRequestData();
        reqData.requestCommand = DepositRequestCommand.GET_ADDRESS;
        reqData.exchangeType = Profile.Exchange;
        reqData.coin = coin;
        reqData.network = network;

        string resultMsg = "Waiting...";
        _udpClient.SendDepositRequest(reqData,
            (DepositRequestData result) =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Coin: {result.coin}");
                sb.AppendLine($"Network: {result.network}");
                if (result.address != null)
                {
                    sb.AppendLine($"Address: {result.address}");
                }
                resultMsg = sb.ToString();
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    #endregion

    #region Extended Orders

    public string MoveOrder(MarketType marketType, string clientOrderId, double newPrice)
    {
        if (_udpClient == null) { return "Not connected"; }
        OrderSettings empty = default;
        var r = SendAndAwaitNotification<OrderMoveNotificationData>(
            send: () => _udpClient.SendMoveOrderRequest(Profile.Exchange, marketType, clientOrderId, newPrice, ref empty, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string MoveBatchOrders(MarketType marketType, Dictionary<string, decimal> orders)
    {
        if (_udpClient == null) { return "Not connected"; }
        var r = SendAndAwaitNotification<OrderMoveNotificationData>(
            send: () => _udpClient.SendMoveBatchOrdersRequest(Profile.Exchange, marketType, orders, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string JoinOrder(MarketType marketType, string clientOrderId)
    {
        if (_udpClient == null) { return "Not connected"; }
        var reqData = new OrderJoinRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            clOrderId = clientOrderId
        };
        var r = SendAndAwaitNotification<OrderJoinNotificationData>(
            send: () => _udpClient.SendJoinOrderRequest(reqData, NetworkMessagePriority.DEFAULT),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string SplitOrder(MarketType marketType, string clientOrderId, byte count, float percentage)
    {
        if (_udpClient == null) { return "Not connected"; }
        var reqData = new OrderSplitRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            clOrderId = clientOrderId,
            count = count,
            percentage = percentage
        };
        var r = SendAndAwaitNotification<OrderSplitNotificationData>(
            send: () => _udpClient.SendSplitOrderRequest(reqData, NetworkMessagePriority.DEFAULT),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string ChangePositionMargin(MarketType marketType, string symbol, string action,
        PositionSide positionSide, decimal amount)
    {
        if (_udpClient == null) { return "Not connected"; }
        var req = new ChangePositionMarginRequest
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            symbol = symbol,
            positionSide = positionSide,
            amount = amount
        };
        if (Enum.TryParse<ChangePositionMarginRequest.ActionType>(action, true, out var actionType))
        {
            req.actionType = actionType;
        }
        var r = SendAndAwaitNotification<MarginChangeNotificationData>(
            send: () => _udpClient.SendChangePositionMargin(Profile.Exchange, req, NetworkMessagePriority.DEFAULT),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string ModifyMarginType(MarketType marketType, string symbol, MarginType marginType)
    {
        if (_udpClient == null) { return "Not connected"; }
        var reqData = new ModifyMarginTypeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            symbol = symbol,
            marginType = marginType
        };
        var r = SendAndAwaitNotification<ModifyMarginTypeNotificationData>(
            send: () => _udpClient.SendModifyMarginTypeRequest(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string GetPositionMode(MarketType marketType, string symbol)
    {
        if (_udpClient == null) { return "Not connected"; }
        var reqData = new PositionModeTypeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            symbol = symbol ?? ""
        };
        var r = SendAndAwaitNotification<GetPositionModeTypeNotificationData>(
            send: () => _udpClient.SendGetPositionModeType(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string SetPositionMode(MarketType marketType, string symbol, PositionModeType mode)
    {
        if (_udpClient == null) { return "Not connected"; }
        var reqData = new PositionModeTypeRequestData
        {
            exchangeType = Profile.Exchange,
            marketType = marketType,
            symbol = symbol ?? "",
            positionModeType = mode
        };
        var r = SendAndAwaitNotification<SetPositionModeTypeNotificationData>(
            send: () => _udpClient.SendSetPositionModeType(reqData),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string TransferFunds(AccountType fromAccount, string asset, double amount, AccountType toAccount)
    {
        if (_udpClient == null) { return "Not connected"; }
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(5_000);
        using var reg = cts.Token.Register(static s => ((TaskCompletionSource<string>)s!).TrySetResult("Timeout"), tcs);
        _udpClient.SendTransferAccountFundsRequest(Profile.Exchange, fromAccount, asset, amount, toAccount,
            (TransferFundsNotificationData result) => tcs.TrySetResult(result?.message ?? "OK"));
        return tcs.Task.GetAwaiter().GetResult();
    }

    public string PanicSell(MarketType marketType, string asset, bool isPanicSelling)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var r = SendAndAwaitNotification<PanicSellNotificationData>(
            send: () => _udpClient.SendPanicSellRequest(Profile.Exchange, marketType, asset, isPanicSelling, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData { msgString = n.message ?? "OK" },
            timeoutMs: 5_000);
        return r?.msgString ?? "Timeout";
    }

    public string GetKlineList(MarketType marketType, string symbol, KlineInterval interval, short limit)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendGetKlineListRequest(Profile.Exchange, marketType, symbol, interval, limit,
            (KlineListData result) =>
            {
                resultMsg = $"Klines received: {result?.klines?.Count ?? 0} entries";
            }, 0);
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string GetTicker24(MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendTickerPrice24Request(Profile.Exchange, marketType, symbol,
            (TickerPrice24ListData result) =>
            {
                resultMsg = $"Ticker24: {result?.symbol} - {result?.tickerPriceList?.Count ?? 0} entries";
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string GetTradesHistory(MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendTradesRequest(Profile.Exchange, marketType, symbol, 0, 0,
            (TradeListData tradeData, NotificationCode code) =>
            {
                int count = tradeData?.trades?.Count ?? 0;
                resultMsg = $"Trades: {count} entries, code={code}";
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string GetProfileSettings(string profileName)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        if (string.IsNullOrEmpty(profileName))
        {
            _udpClient.SendGetCurrentProfileSettingsRequest(
                (ProfileSettingsData result) =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Profile: {result.profileName} (success={result.isSucceeded})");
                    if (result.settings != null)
                    {
                        sb.AppendLine($"Settings count: {result.settings.Count}");
                        foreach (var kvp in result.settings)
                        {
                            sb.AppendLine($"  {kvp.Key} = {kvp.Value}");
                        }
                    }
                    if (!string.IsNullOrEmpty(result.errorMessage))
                    {
                        sb.AppendLine($"Error: {result.errorMessage}");
                    }
                    resultMsg = sb.ToString();
                });
        }
        else
        {
            _udpClient.SendGetProfileSettingsRequest(profileName,
                (ProfileSettingsData result) =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Profile: {result.profileName} (success={result.isSucceeded})");
                    if (result.settings != null)
                    {
                        sb.AppendLine($"Settings count: {result.settings.Count}");
                        foreach (var kvp in result.settings)
                        {
                            sb.AppendLine($"  {kvp.Key} = {kvp.Value}");
                        }
                    }
                    if (!string.IsNullOrEmpty(result.errorMessage))
                    {
                        sb.AppendLine($"Error: {result.errorMessage}");
                    }
                    resultMsg = sb.ToString();
                });
        }
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string UpdateProfileSettings(string profileName, Dictionary<string, string> updated, HashSet<string> deleted)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendUpdateProfileSettingsRequest(profileName, updated, deleted,
            (ProfileSettingsData result) =>
            {
                resultMsg = $"Profile: {result.profileName}, success={result.isSucceeded}, restart_needed={result.isCoreRestartNeeded}";
                if (!string.IsNullOrEmpty(result.errorMessage))
                {
                    resultMsg += $", error={result.errorMessage}";
                }
            });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string GetReportComments()
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendReportCommentsRequest(
            (ReportsFieldData result) =>
            {
                if (result.reportComments != null)
                {
                    resultMsg = $"Report comments: {result.reportComments.Count} entries";
                }
                else
                {
                    resultMsg = "No report comments";
                }
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    public string GetReportDates()
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendReportsDateRequest(
            (ReportsFieldData result) =>
            {
                if (result.reportsDate != null)
                {
                    resultMsg = $"Report dates: {result.reportsDate.Count} entries";
                }
                else
                {
                    resultMsg = "No report dates";
                }
            });
        System.Threading.Thread.Sleep(3000);
        return resultMsg;
    }

    #endregion

    
    #region Funding Balances

    public void RequestFundingBalances()
    {
        _udpClient?.SendFundingBalancesRequest();
    }

    #endregion

    #region BuyApiLimit

    public string RequestBuyApiLimit(int amount)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string result = "No response";
        using (ManualResetEventSlim wait = new ManualResetEventSlim(false))
        {
            _udpClient.SendBuyApiLimitRequest(
                new BuyApiLimitRequestData { amount = amount },
                data =>
                {
                    result = System.Text.Json.JsonSerializer.Serialize(data);
                    wait.Set();
                });
            wait.Wait(5000);
        }

        return result;
    }

    #endregion

    #region MarketLiveAlgorithms

    public string RequestMarketLiveAlgorithms(
        MarketType marketType, string symbol, List<long> algorithmIds)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        LiveMarketAlgorithmsRequestData request = new LiveMarketAlgorithmsRequestData();
        request.exchangeType = Profile.Exchange;
        request.marketType = marketType;
        request.symbol = symbol;
        request.algorithmIds = algorithmIds;

        string result = "No response";
        using (ManualResetEventSlim wait = new ManualResetEventSlim(false))
        {
            _udpClient.SendMarketLiveAlgorithmsRequest(request,
                data =>
                {
                    result = Newtonsoft.Json.JsonConvert.SerializeObject(data);
                    wait.Set();
                });
            wait.Wait(5000);
        }

        return result;
    }

    #endregion

    #region OrderTPSLUpdate

    public NotificationMessageData? UpdateOrderTPSL(
        OrderRequestData orderData, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndAwaitNotification<OrderTPSLUpdateNotificationData>(
            send: () => _udpClient.SendOrderTPSLUpdateRequest(orderData, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    #endregion

    #region TPSL Join/Split

    public NotificationMessageData? JoinTPSL(
        TPSLInfoListData tpslData, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        // Vendor's wire layer binds each entry by the full identity tuple
        // (marketType / symbol / side / qty / entryPrice / settings), not the
        // id alone — same constraint already documented on CancelTPSL.
        // Echo the cached record for any id-only stub so a JOIN request issued
        // straight from the CLI succeeds without the caller having to assemble
        // the full payload.
        if (tpslData.infoData != null)
        {
            for (int i = 0; i < tpslData.infoData.Count; i++)
            {
                var entry = tpslData.infoData[i];
                TPSLInfoData? cached = TPSLStore.GetRawById(entry.id);
                if (cached != null)
                {
                    cached.requestExchangeType = Profile.Exchange;
                    tpslData.infoData[i] = cached;
                }
            }
        }
        // TPSL join response reuses OrderJoinNotificationData (per BotClient ServicesController).
        return SendAndAwaitNotification<OrderJoinNotificationData>(
            send: () => _udpClient.SendJoinRequest(tpslData, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    public NotificationMessageData? SplitTPSL(
        TPSLInfoData tpslData, int timeoutMs = 10_000)
    {
        if (_udpClient == null) { return null; }
        // Echo the cached TPSLInfoData when the caller passed an id-only stub —
        // same reasoning as CancelTPSL / JoinTPSL.
        TPSLInfoData? cached = TPSLStore.GetRawById(tpslData.id);
        TPSLInfoData payload = cached ?? tpslData;
        payload.requestExchangeType = Profile.Exchange;
        // TPSL split response is OrderSplitNotificationData (per BotClient ServicesController).
        return SendAndAwaitNotification<OrderSplitNotificationData>(
            send: () => _udpClient.SendSplitRequest(payload, NetworkMessagePriority.HIGH),
            build: n => new NotificationMessageData
            {
                notificationCode = n.success ? NotificationCode.OK : NotificationCode.ERROR,
                msgString = n.message ?? string.Empty,
            },
            timeoutMs: timeoutMs);
    }

    #endregion

    #region CoreService Extended

    public void SendCoreRestart()
    {
        SendServiceCommand(CoreServiceCommand.RESTART);
    }

    public void SendCoreRestartWithUpdate()
    {
        SendServiceCommand(CoreServiceCommand.RESTART_WITH_UPDATE);
    }

    public void SendCoreClearOrdersCache()
    {
        SendServiceCommand(CoreServiceCommand.RESTART_WITH_CLEAR_ORDERS_CACHE);
    }

    public void SendCoreClearArchiveData()
    {
        SendServiceCommand(CoreServiceCommand.RESTART_WITH_CLEAR_ARCHIVE_DATA);
    }

    /// <summary>
    /// Composite restart matching the vendor CommandAdvancedRestart shape.
    /// Builds a CoreServiceControllerData with an advancedCommands HashSet and
    /// sends via the SendCoreServiceCommand(CoreServiceControllerData) overload,
    /// so a single restart cycle can combine update behaviour with
    /// clear-orders-cache and clear-data-archive in one operator command.
    /// </summary>
    /// <param name="includeUpdate">If true, includes RESTART_WITH_UPDATE in the
    /// command set; otherwise plain RESTART. Matches vendor's NO_UPDATE step
    /// (value=true picks plain RESTART (2); value=false picks RESTART_WITH_UPDATE (3)).</param>
    /// <param name="clearOrdersCache">If true, adds
    /// RESTART_WITH_CLEAR_ORDERS_CACHE (4) to the command set.</param>
    /// <param name="clearDataArchive">If true, adds
    /// RESTART_WITH_CLEAR_ARCHIVE_DATA (5) to the command set.</param>
    public void SendCoreAdvancedRestart(bool includeUpdate, bool clearOrdersCache, bool clearDataArchive)
    {
        if (_udpClient == null) { return; }
        var data = new CoreServiceControllerData
        {
            advancedCommands = new System.Collections.Generic.HashSet<CoreServiceCommand>
            {
                includeUpdate ? CoreServiceCommand.RESTART_WITH_UPDATE : CoreServiceCommand.RESTART
            }
        };
        if (clearOrdersCache)  { data.advancedCommands.Add(CoreServiceCommand.RESTART_WITH_CLEAR_ORDERS_CACHE); }
        if (clearDataArchive)  { data.advancedCommands.Add(CoreServiceCommand.RESTART_WITH_CLEAR_ARCHIVE_DATA); }
        _udpClient.SendCoreServiceCommand(data);
    }

    #endregion

#region Connection Lifecycle

    private void HandleConnect(NetPeer peer)
    {
        _isConnected = true;
        _connectedAt = DateTime.UtcNow;
        Subscribe();
        OnConnected?.Invoke(this);
    }

    private void HandleDisconnect(NetPeer peer, DisconnectInfo info)
    {
        _isConnected = false;
        AlgoStore.Clear();
        AccountStore.Clear();
        CoreStatusStore.Clear();
        ExchangeInfoStore.Clear();
        ProfileSettingsStore.Clear();
        TPSLStore?.Clear();
        TradingPerfStore?.Clear();

        // FIX: Stop NetManager when remote peer disconnects to prevent thread leak.
        // Without this, 3 zombie threads per connection survive until explicit Disconnect().
        if (_udpClient != null)
        {
            StopNetManager(_udpClient);
        }

        OnDisconnected?.Invoke(this);
    }

    private void HandleReconnectStart(string address, int port, int tryCount)
    {
        OnError?.Invoke(this, $"[{Name}] Reconnecting to {address}:{port} (attempt {tryCount})...");
    }

    /// <summary>
    /// Called by UDPClient when ConnectionInfoData changes.
    /// If the connectionId or serverStartTime has changed since our last record,
    /// the Core has restarted while the socket stayed alive.
    /// We invalidate all cached stores and fire OnCoreRestarted.
    /// </summary>
    private void HandleConnectionInfoChange(ConnectionInfoData info)
    {
        int   newId        = info.connectionId;
        long  newStartTime = info.serverStartTime;

        bool firstTime = _lastConnectionId == 0;

        if (!firstTime &&
            (newId != _lastConnectionId || newStartTime != _lastServerStartTime))
        {
            // Core has restarted — cached data is stale
            AlgoStore.Clear();
            AccountStore.Clear();
            CoreStatusStore.Clear();
            ExchangeInfoStore.Clear();
            ProfileSettingsStore.Clear();
        TPSLStore?.Clear();
        TradingPerfStore?.Clear();

            OnError?.Invoke(this, $"[{Name}] Core restart detected (connectionId {_lastConnectionId} -> {newId}). Stores cleared.");
            OnCoreRestarted?.Invoke(this);

            // Re-subscribe so we get fresh data pushed immediately
            Unsubscribe();
            Subscribe();
        }

        _lastConnectionId    = newId;
        _lastServerStartTime = newStartTime;
    }

    // Send a TP/SL algorithm change request (fire-and-forget)
    public void SendTpSlAlgorithmChangeRequest(
        TPSLInfoData msgData,
        NetworkMessagePriority priority = NetworkMessagePriority.DEFAULT)
    {
        _udpClient?.SendTpSlAlgorithmChangeRequest(msgData, priority);
    }

    // Send algorithm profiling data request (asynchronous — response comes via event subscription)
    public void SendAlgorithmProfilingDataRequest(
        ExchangeType exchangeType,
        MarketType marketType,
        string symbol,
        long algorithmId = 0L)
    {
        _udpClient?.SendAlgorithmProfilingDataRequest(exchangeType, marketType, symbol, algorithmId);
    }

    // Send a service command to MTCore (shutdown / restart variants)
    public void SendServiceCommand(CoreServiceCommand command)
    {
        if (_udpClient == null) return;
        _udpClient.SendCoreServiceCommand(command);
    }

    private void Cleanup()
    {
        _udpClient = null;
        _isConnected = false;
        // Reset restart-detection sentinels so a fresh connect starts clean
        _lastConnectionId    = 0;
        _lastServerStartTime = 0;
        AlgoStore.Clear();
        AccountStore.Clear();
        CoreStatusStore.Clear();
        ExchangeInfoStore.Clear();
        ProfileSettingsStore.Clear();
        TPSLStore?.Clear();
        TradingPerfStore?.Clear();
        TriggerStore.Clear();
        LiveMarketStore.Clear();
        AutoBuyStore.Clear();
        GraphToolStore.Clear();
        NotificationStore.Clear();
        AlertStore.Clear();

        // Unhook event delegates to break reference chains for GC
        OnConnected = null;
        OnDisconnected = null;
        OnError = null;
        OnAlgorithmsLoaded = null;
        OnCoreStatusReceived = null;
        OnTradePairsLoaded = null;
        OnAccountDataReceived = null;
        OnProfilingDataReceived = null;
    }

    /// <summary>
    /// Stop the LiteNetLib NetManager inside a UDPClient via reflection.
    /// UDPClient.Stop() only disconnects the peer but leaves NetManager running
    /// with 3 zombie threads (logic, socket recv IPv4/IPv6) and open sockets.
    /// This must be called after UDPClient.Stop() to fully release resources.
    /// </summary>
    private static void StopNetManager(UDPClient client)
    {
        try
        {
            if (s_netManagerField == null)
            {
                return;
            }

            var netManager = s_netManagerField.GetValue(client) as NetManager;
            if (netManager == null || !netManager.IsRunning)
            {
                return;
            }

            netManager.Stop();

            // Drain event queues to release EventData/NetworkData byte[] references
            DrainQueue(s_eventQueueField, client);
            DrainQueue(s_importantQueueField, client);
        }
        catch
        {
            // Best-effort cleanup — don't let reflection failures break disconnect
        }
    }

    /// <summary>Drain a ConcurrentQueue field via cached FieldInfo.</summary>
    private static void DrainQueue(FieldInfo? field, object instance)
    {
        try
        {
            var queue = field?.GetValue(instance);
            if (queue == null)
            {
                return;
            }

            var tryDequeue = queue.GetType().GetMethod("TryDequeue");
            if (tryDequeue == null)
            {
                return;
            }

            var args = new object?[1];
            while ((bool)tryDequeue.Invoke(queue, args)!) { }
        }
        catch { /* best effort */ }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
    }

    public override string ToString() =>
        $"{Name} ({Profile.Exchange}) @ {Profile.Address}:{Profile.Port} [{(IsConnected ? "CONNECTED" : "DISCONNECTED")}]";
}
