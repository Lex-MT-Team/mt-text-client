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
/// A single connection to an MT-Core instance.
/// Bundles UDPClient + all data stores (algorithms, account, core status, exchange info, profile settings).
/// Each CoreConnection is independent — multiple can run concurrently.
/// 
/// Phase B: Algorithm lifecycle + profile settings requests.
/// Phase D: Order & Position management requests.
/// Phase E: Real-time monitoring — MonitorBuffer integration for UDP-based core status tracking.
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
    private int _alertsSubscriptionId;
    private int _alertHistorySubscriptionId;
    private readonly ConcurrentDictionary<string, int> _tradeSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _depthSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _markPriceSubscriptionIds = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<string, int> _klineSubscriptionIds = new ConcurrentDictionary<string, int>();
    private int _tickerSubscriptionId;
    private int _profilingSubscriptionId;

    private bool _isConnected;
    private bool _disposed;
    private DateTime _connectedAt;

    // MT-015: track connectionId + serverStartTime so we detect Core restarts
    // without a full disconnect/reconnect cycle
    private int   _lastConnectionId;
    private long  _lastServerStartTime;

    // MT-017: per-connection token bucket rate limiter (120/s, burst 600)
    public RateLimiter RateLimit { get; } = new RateLimiter("connection", capacity: 600, refillPerSecond: 120);

    // MT-021: per-connection circuit breaker (trip after 5 failures, 30s open window)
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

    /// <summary>Whether notifications subscription is active.</summary>
    public bool IsNotificationSubscribed { get { return _notificationSubscriptionId != 0; } }

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
    /// <summary>
    /// MT-015: Fired when MTCore restarts while the UDP connection stays alive.
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
            // MT-015: detect Core restart (connectionId/serverStartTime change)
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

    #region Monitor (Phase E)

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
            });
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
            if (_profilingSubscriptionId != 0)
            {
                _udpClient.SendAlgorithmProfilingDataUnsubscribe(
                    ref _profilingSubscriptionId, Profile.Exchange, MarketType.FUTURES, "", 0);
            }
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

    // ── MT-014: TCS-based request helper ─────────────────────────────────────
    // Replaces ManualResetEventSlim.Wait() which held ThreadPool threads hostage
    // for up to timeoutMs on slow/unresponsive Core instances.
    // TaskCompletionSource runs continuations on the ThreadPool
    // (RunContinuationsAsynchronously), so the callback never deadlocks
    // even if the pump thread fires it while the caller is already unwinding.
    //
    // Usage: result = SendAndWait<NotificationMessageData>(
    //            send: cb => _udpClient.SendXxx(data, cb),
    //            timeoutMs: 10_000);

    // MT-017 + MT-021: guarded send — checks circuit breaker and rate limiter before dispatching.
    // timeoutMs=0 means "skip guard, internal use only" (e.g. subscribe calls).
    private T? SendAndWait<T>(Action<Action<T?>> send, int timeoutMs) where T : class
    {
        if (timeoutMs > 0)
        {
            // MT-021: circuit breaker fast-fail
            if (!Circuit.AllowCall())
            {
                return null;
            }

            // MT-017: rate limiter — wait up to 500ms for a token
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

        // MT-021: record outcome for circuit breaker
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

    #region Algorithm Lifecycle Requests (Phase B)

    /// <summary>
    /// Send an algorithm request (START, STOP, SAVE, DELETE, TOGGLE_DEBUG, etc.).
    /// The AlgorithmData.actionType must be set before calling.
    /// Returns a task that completes with the NotificationMessageData response.
    /// </summary>
    public NotificationMessageData? SendAlgorithmRequest(AlgorithmData algoData, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendAlgorithmRequest(algoData, cb), timeoutMs);
    }

    /// <summary>
    /// Send an algorithm list request (START_ALL, STOP_ALL, SAVE_GROUP, DELETE_GROUP, CLONE_GROUP).
    /// Returns a task that completes with the NotificationMessageData response.
    /// </summary>
    public NotificationMessageData? SendAlgorithmListRequest(AlgorithmListData listData, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendAlgorithmListRequest(listData, cb), timeoutMs);
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

    #region Order & Position Management (Phase D)

    /// <summary>
    /// Place an order via Core.
    /// </summary>
    public NotificationMessageData? PlaceOrder(OrderRequestData orderRequest, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendPlaceOrderRequest(orderRequest, cb), timeoutMs);
    }

    /// <summary>
    /// Move (modify price of) an existing order.
    /// </summary>
    public NotificationMessageData? MoveOrder(
        ExchangeType exchangeType, MarketType marketType,
        string clientOrderId, double newPrice, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendMoveOrderRequest(exchangeType, marketType, clientOrderId, newPrice, default, cb),
            timeoutMs);
    }

    /// <summary>
    /// Cancel a specific order by clientOrderId.
    /// </summary>
    public NotificationMessageData? CancelOrder(
        ExchangeType exchangeType, MarketType marketType,
        string symbol, string clientOrderId, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendCancelOrderRequest(exchangeType, marketType, symbol, clientOrderId, cb),
            timeoutMs);
    }

    /// <summary>
    /// Cancel all orders (or all for a specific symbol).
    /// </summary>
    public NotificationMessageData? CancelAllOrders(
        ExchangeType exchangeType, MarketType marketType, string? symbol = null, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendCancelOrderListRequest(
                exchangeType,
                cancelAll: string.IsNullOrEmpty(symbol),
                new OrderListData(),
                symbol ?? "",
                marketType,
                cb),
            timeoutMs);
    }

    /// <summary>
    /// Close a position (market or limit) by percentage (1.0 = 100%).
    /// </summary>
    public NotificationMessageData? ClosePosition(
        ExchangeType exchangeType, PositionData positionData,
        OrderType orderType, double percentage = 1.0, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendClosePositionRequest(exchangeType, positionData, orderType, percentage, cb),
            timeoutMs);
    }

    /// <summary>
    /// Close position using TP/SL order.
    /// </summary>
    public NotificationMessageData? ClosePositionByTPSL(
        ExchangeType exchangeType, PositionData positionData,
        OrderType orderType, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendClosePositionByTPSLRequest(exchangeType, positionData, orderType, cb),
            timeoutMs);
    }

    /// <summary>
    /// Reset TP/SL on an existing position.
    /// </summary>
    public NotificationMessageData? ResetTPSL(
        ExchangeType exchangeType, PositionData positionData,
        TakeProfitSettings tpSettings, StopLossSettings slSettings, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendResetTPSLRequest(exchangeType, positionData, tpSettings, slSettings, cb),
            timeoutMs);
    }

    #endregion


    /// <summary>
    /// Request historical trade reports from MT-Core's report storage.
    /// This is the historical trading data — closed trades, not just live fills.
    /// Phase H: Extended with B6 filters (excludeEmulated, closedBy, marketTypes, orderSideTypes, tradeModeType).
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
        int timeoutMs = 30_000)
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

        return SendAndWait<ReportListData>(
            cb => _udpClient.SendReportListRequest(request, cb), timeoutMs);
    }


    #region Read Queries (Phase K)

    /// <summary>
    /// Get 24h ticker price statistics for a symbol.
    /// </summary>
    public TickerPrice24ListData? RequestTicker24(
        MarketType marketType, string symbol, int timeoutMs = 10_000)
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
        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendGetPositionModeType(request, cb), timeoutMs);
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
    public ReportsFieldData? RequestReportComments(int timeoutMs = 10_000)
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
    public ReportsFieldData? RequestReportDates(int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<ReportsFieldData>(
            cb => _udpClient.SendReportsDateRequest(cb), timeoutMs);
    }

    #endregion

    #region Write Operations (Phase K)

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
        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendSetPositionModeType(request, cb), timeoutMs);
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
        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendModifyLeverageRequest(request, cb), timeoutMs);
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
        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendModifyMarginTypeRequest(request, cb), timeoutMs);
    }

    /// <summary>
    /// Panic sell — emergency market-close all positions for an asset.
    /// </summary>
    public NotificationMessageData? PanicSell(
        MarketType marketType, string asset, bool activate = true, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendPanicSellRequest(Profile.Exchange, marketType, asset, activate, cb),
            timeoutMs);
    }

    /// <summary>
    /// Add or reduce margin on an isolated-margin position.
    /// </summary>
    public NotificationMessageData? ChangePositionMargin(
        MarketType marketType, string symbol, PositionSide positionSide,
        decimal amount, bool isAdd = true, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

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

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendChangePositionMargin(Profile.Exchange, request, cb),
            timeoutMs);
    }

    /// <summary>
    /// Transfer funds between spot and futures (market type transfer).
    /// </summary>
    public NotificationMessageData? TransferFunds(
        MarketType fromMarket, MarketType toMarket, string asset, double amount, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendTransferFundsRequest(Profile.Exchange, fromMarket, asset, amount, toMarket, 0, "", cb),
            timeoutMs);
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
    public ReportListData? RequestAutoStopsReports(List<long> algorithmIds, int timeoutMs = 15_000)
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
    /// Cancel a TPSL position by ID.
    /// </summary>
    public NotificationMessageData? CancelTPSL(long tpslId, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        // Build TPSLInfoData with just the ID set
        var msgData = new TPSLInfoData
        {
            id = tpslId,
            requestExchangeType = Profile.Exchange
        };

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendCancelTPSLRequest(msgData, cb, NetworkMessagePriority.HIGH), timeoutMs);
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
    }

    public void UnsubscribeNotifications()
    {
        if (_udpClient != null && _notificationSubscriptionId != 0)
        {
            _udpClient.SendNotificationUnsubscribe(ref _notificationSubscriptionId, Profile.Exchange);
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

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendModifyLeverageBuySellRequest(request, cb), timeoutMs);
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

        _profilingSubscriptionId = _udpClient.SendAlgorithmProfilingDataSubscribe(
            Profile.Exchange, marketType, symbol, algorithmId,
            (AlgorithmProfilingData data) =>
            {
                // Store latest profiling data - reuse existing store or log
            },
            _profilingSubscriptionId);
    }

    public void UnsubscribeProfiling(MarketType marketType, string symbol, long algorithmId)
    {
        if (_udpClient != null && _profilingSubscriptionId != 0)
        {
            _udpClient.SendAlgorithmProfilingDataUnsubscribe(
                ref _profilingSubscriptionId, Profile.Exchange, marketType, symbol, algorithmId);
        }
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

        string resultMsg = "Waiting...";
        _udpClient.SendTriggerRequest(reqData, (NotificationMessageData result) =>
        {
            resultMsg = result?.msgString ?? "OK";
        });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
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

        var reqData = new AutoBuyRequestData();
        reqData.exchangeType = Profile.Exchange;

        if (Enum.TryParse<AutoBuyRequestData.RequestActionType>(actionType, true, out var action))
        {
            reqData = new AutoBuyRequestData(action);
            reqData.exchangeType = Profile.Exchange;
        }

        string resultMsg = "Waiting...";
        _udpClient.SendAutoBuyRequest(reqData, (NotificationMessageData result) =>
        {
            resultMsg = result?.msgString ?? "OK";
        });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
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

        string resultMsg = "Waiting...";
        _udpClient.SendGraphToolRequest(reqData, (NotificationMessageData result) =>
        {
            resultMsg = result?.msgString ?? "OK";
        });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
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
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var orderSettings = new OrderSettings();
        string resultMsg = "Waiting...";
        _udpClient.SendMoveOrderRequest(Profile.Exchange, marketType, clientOrderId, newPrice,
            ref orderSettings,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.HIGH);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string MoveBatchOrders(MarketType marketType, Dictionary<string, decimal> orders)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendMoveBatchOrdersRequest(Profile.Exchange, marketType, orders,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.HIGH);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string JoinOrder(MarketType marketType, string clientOrderId)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new OrderJoinRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.marketType = marketType;
        reqData.clOrderId = clientOrderId;

        string resultMsg = "Waiting...";
        _udpClient.SendJoinOrderRequest(reqData,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.DEFAULT);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string SplitOrder(MarketType marketType, string clientOrderId, byte count, float percentage)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new OrderSplitRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.marketType = marketType;
        reqData.clOrderId = clientOrderId;
        reqData.count = count;
        reqData.percentage = percentage;

        string resultMsg = "Waiting...";
        _udpClient.SendSplitOrderRequest(reqData,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.DEFAULT);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string ChangePositionMargin(MarketType marketType, string symbol, string action,
        PositionSide positionSide, decimal amount)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var req = new ChangePositionMarginRequest();
        req.exchangeType = Profile.Exchange;
        req.marketType = marketType;
        req.symbol = symbol;
        req.positionSide = positionSide;
        req.amount = amount;

        if (Enum.TryParse<ChangePositionMarginRequest.ActionType>(action, true, out var actionType))
        {
            req.actionType = actionType;
        }

        string resultMsg = "Waiting...";
        _udpClient.SendChangePositionMargin(Profile.Exchange, req,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.DEFAULT);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string ModifyMarginType(MarketType marketType, string symbol, MarginType marginType)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new ModifyMarginTypeRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.marketType = marketType;
        reqData.symbol = symbol;
        reqData.marginType = marginType;

        string resultMsg = "Waiting...";
        _udpClient.SendModifyMarginTypeRequest(reqData,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string GetPositionMode(MarketType marketType, string symbol)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new PositionModeTypeRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.marketType = marketType;
        reqData.symbol = symbol ?? "";

        string resultMsg = "Waiting...";
        _udpClient.SendGetPositionModeType(reqData,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string SetPositionMode(MarketType marketType, string symbol, PositionModeType mode)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        var reqData = new PositionModeTypeRequestData();
        reqData.exchangeType = Profile.Exchange;
        reqData.marketType = marketType;
        reqData.symbol = symbol ?? "";
        reqData.positionModeType = mode;

        string resultMsg = "Waiting...";
        _udpClient.SendSetPositionModeType(reqData,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string TransferFunds(AccountType fromAccount, string asset, double amount, AccountType toAccount)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendTransferAccountFundsRequest(Profile.Exchange, fromAccount, asset, amount, toAccount,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            });
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
    }

    public string PanicSell(MarketType marketType, string asset, bool isPanicSelling)
    {
        if (_udpClient == null)
        {
            return "Not connected";
        }

        string resultMsg = "Waiting...";
        _udpClient.SendPanicSellRequest(Profile.Exchange, marketType, asset, isPanicSelling,
            (NotificationMessageData result) =>
            {
                resultMsg = result?.msgString ?? "OK";
            },
            NetworkMessagePriority.HIGH);
        System.Threading.Thread.Sleep(2000);
        return resultMsg;
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
                    result = System.Text.Json.JsonSerializer.Serialize(data);
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

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendOrderTPSLUpdateRequest(orderData, cb, NetworkMessagePriority.HIGH),
            timeoutMs);
    }

    #endregion

    #region TPSL Join/Split

    public NotificationMessageData? JoinTPSL(
        TPSLInfoListData tpslData, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendJoinRequest(tpslData, cb, NetworkMessagePriority.HIGH),
            timeoutMs);
    }

    public NotificationMessageData? SplitTPSL(
        TPSLInfoData tpslData, int timeoutMs = 10_000)
    {
        if (_udpClient == null)
        {
            return null;
        }

        return SendAndWait<NotificationMessageData>(
            cb => _udpClient.SendSplitRequest(tpslData, cb, NetworkMessagePriority.HIGH),
            timeoutMs);
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
    /// MT-015: Called by UDPClient when ConnectionInfoData changes.
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

    // MT-024: Send a TP/SL algorithm change request (fire-and-forget)
    public void SendTpSlAlgorithmChangeRequest(
        TPSLInfoData msgData,
        NetworkMessagePriority priority = NetworkMessagePriority.DEFAULT)
    {
        _udpClient?.SendTpSlAlgorithmChangeRequest(msgData, priority);
    }

    // MT-024: Send algorithm profiling data request (asynchronous — response comes via event subscription)
    public void SendAlgorithmProfilingDataRequest(
        ExchangeType exchangeType,
        MarketType marketType,
        string symbol,
        long algorithmId = 0L)
    {
        _udpClient?.SendAlgorithmProfilingDataRequest(exchangeType, marketType, symbol, algorithmId);
    }

    // MT-023: Send a service command to MTCore (shutdown / restart variants)
    public void SendServiceCommand(CoreServiceCommand command)
    {
        if (_udpClient == null) return;
        _udpClient.SendCoreServiceCommand(command);
    }

    private void Cleanup()
    {
        _udpClient = null;
        _isConnected = false;
        // MT-015: reset restart-detection sentinels so a fresh connect starts clean
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
