using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Collects a finite read-only diagnostic bundle under one shared deadline.</summary>
public sealed class DiagnosticInvestigationCollector
{
    /// <summary>Gets the default collection deadline.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Gets the maximum collection deadline.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(60);

    private readonly IDiagnosticExecutor _executor;

    /// <summary>Creates a collector using the existing native diagnostic execution seam.</summary>
    public DiagnosticInvestigationCollector(IDiagnosticExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>Collects supported sources sequentially while preserving one absolute deadline.</summary>
    public async Task<DiagnosticEvidenceBundle> CollectAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticIdentityEvidence identity, TimeSpan deadline, bool includeHashes, CancellationToken cancellationToken, string trigger = "one_shot")
    {
        TimeSpan boundedDeadline = deadline <= TimeSpan.Zero ? DefaultDeadline : deadline > MaximumDeadline ? MaximumDeadline : deadline;
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(boundedDeadline);
        var stopwatch = Stopwatch.StartNew();
        var observations = new List<DiagnosticEvidenceObservation>();
        foreach ((string source, DiagnosticRequest request) in Sources)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            TimeSpan remaining = boundedDeadline - stopwatch.Elapsed;
            if (cancellationToken.IsCancellationRequested)
            {
                observations.Add(new DiagnosticEvidenceObservation(source, "cancelled", null, startedAt, null, true, false, null));
                continue;
            }
            if (remaining <= TimeSpan.Zero || deadlineSource.IsCancellationRequested)
            {
                observations.Add(new DiagnosticEvidenceObservation(source, "timeout", null, startedAt, null, true, false, null));
                continue;
            }

            DiagnosticResult result;
            try
            {
                TimeSpan operationTimeout = remaining > MtDiagClient.MaximumTimeout ? MtDiagClient.MaximumTimeout : remaining;
                result = await _executor.ExecuteAsync(profile, route, request, identity, operationTimeout, deadlineSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                observations.Add(new DiagnosticEvidenceObservation(source, "cancelled", null, DateTimeOffset.UtcNow, null, true, false, null));
                continue;
            }
            catch (OperationCanceledException)
            {
                observations.Add(new DiagnosticEvidenceObservation(source, "timeout", null, DateTimeOffset.UtcNow, null, true, false, null));
                continue;
            }
            catch
            {
                observations.Add(new DiagnosticEvidenceObservation(source, "transport_failure", null, DateTimeOffset.UtcNow, null, true, false, null));
                continue;
            }

            DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
            DateTimeOffset? observedAt = TryReadObservedAt(result.Metadata);
            long? ageSeconds = observedAt is null ? null : Math.Max(0, (long)(collectedAt - observedAt.Value).TotalSeconds);
            bool truncated = HasBoolean(result.Data, "truncated") || HasBoolean(result.Metadata, "truncated");
            bool gap = result.Kind != DiagnosticResultKind.Success || truncated || HasGapState(result.Data) || HasGapState(result.Metadata);
            observations.Add(new DiagnosticEvidenceObservation(source, ToStatus(result), observedAt, collectedAt, ageSeconds, gap, truncated, result.Data));
        }
        return DiagnosticEvidenceBundle.Create(profile.Name, trigger, DateTimeOffset.UtcNow, observations, includeHashes);
    }

    private static readonly (string Source, DiagnosticRequest Request)[] Sources =
    {
        ("capabilities", DiagnosticRequest.Capabilities()),
        ("health", DiagnosticRequest.Health()),
        ("logs", DiagnosticRequest.Logs(50, null)),
        ("guardian_status", DiagnosticRequest.GuardianStatus()),
        ("guardian_events", DiagnosticRequest.GuardianEvents(50))
    };

    private static string ToStatus(DiagnosticResult result) => result.Kind switch
    {
        DiagnosticResultKind.Success => "success",
        DiagnosticResultKind.UnsupportedCapability => "unsupported",
        DiagnosticResultKind.Timeout => "timeout",
        DiagnosticResultKind.Cancelled => "cancelled",
        DiagnosticResultKind.ValidationFailure => "validation_failure",
        _ => "partial"
    };

    private static DateTimeOffset? TryReadObservedAt(JToken? metadata)
    {
        if (metadata?["observed_at"]?.Type != JTokenType.Integer) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(metadata["observed_at"]!.Value<long>()); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool HasBoolean(JToken? token, string name) => token is not null && AllTokens(token).OfType<JProperty>().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.Type == JTokenType.Boolean && property.Value.Value<bool>());

    private static bool HasGapState(JToken? token) => token is not null && AllTokens(token).OfType<JProperty>().Any(property =>
        (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)) &&
        property.Value.Type == JTokenType.String && property.Value.Value<string>() is string value && (value.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || value.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || value.Contains("denied", StringComparison.OrdinalIgnoreCase) || value.Contains("stale", StringComparison.OrdinalIgnoreCase) || value.Contains("deadline", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<JToken> AllTokens(JToken token) => token is JContainer container ? container.DescendantsAndSelf() : new[] { token };
}

/// <summary>Sanitized local dispatch facts that never include arguments, messages, or payloads.</summary>
public sealed record DiagnosticDispatchFact(string Noun, string? Subcommand, string? Profile, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool Success);

/// <summary>One bounded, in-memory session record.</summary>
public sealed record DiagnosticInvestigationRecord(string Kind, DateTimeOffset RecordedAt, string? Noun, string? Subcommand, string? EventType, bool? Success, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null);

/// <summary>Immutable, portable view of an in-memory investigation session.</summary>
public sealed record DiagnosticInvestigationSnapshot(string Id, string Profile, DateTimeOffset ExpiresAt, IReadOnlyList<DiagnosticInvestigationRecord> Records, JObject? FollowUpEvidence);

/// <summary>Builds the only portable boundary for investigation session metadata and exports.</summary>
public static class DiagnosticInvestigationExportSerializer
{
    /// <summary>Maximum UTF-8 bytes returned by a complete session response.</summary>
    public const int MaximumSerializedBytes = DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes;
    private const int MaximumStringBytes = 256;

    /// <summary>Serializes safe metadata for an explicitly started persistent session.</summary>
    public static JObject SerializeStart(DiagnosticInvestigationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SanitizeAndBound(new JObject
        {
            ["session_id"] = session.Id,
            ["profile"] = session.Profile,
            ["expires_at"] = session.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["event_types"] = new JArray(session.EventTypes)
        }, recordCount: 0);
    }

    /// <summary>Serializes one complete session without exposing raw metadata or exceeding the aggregate export budget.</summary>
    public static JObject Serialize(DiagnosticInvestigationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var value = new JObject
        {
            ["schema"] = "mtdiag.investigation",
            ["version"] = "1",
            ["session_id"] = snapshot.Id,
            ["profile"] = snapshot.Profile,
            ["expires_at"] = snapshot.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["records"] = new JArray(snapshot.Records.Select(record => new JObject
            {
                ["kind"] = record.Kind,
                ["recorded_at"] = record.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["noun"] = record.Noun,
                ["subcommand"] = record.Subcommand,
                ["event_type"] = record.EventType,
                ["success"] = record.Success,
                ["started_at"] = record.StartedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["ended_at"] = record.EndedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }))
        };
        if (snapshot.FollowUpEvidence is not null) value["follow_up_evidence"] = snapshot.FollowUpEvidence.DeepClone();
        return SanitizeAndBound(value, snapshot.Records.Count);
    }

    private static JObject SanitizeAndBound(JObject value, int recordCount)
    {
        JToken normalized = NormalizeNullStrings(value);
        var sanitized = (JObject)DiagnosticReplyValidator.SanitizeEvidence(normalized);
        bool changed = !JToken.DeepEquals(normalized, sanitized);
        JObject result = (JObject)BoundStrings(sanitized, ref changed);
        if (Utf8Length(result) > MaximumSerializedBytes - 512 && result.Remove("follow_up_evidence"))
        {
            result["follow_up_evidence"] = new JObject { ["omitted"] = true, ["truncated"] = true };
            changed = true;
        }
        if (Utf8Length(result) > MaximumSerializedBytes - 256)
        {
            result["records"] = new JArray();
            result["records_omitted"] = recordCount;
            changed = true;
        }
        if (changed) result["truncated"] = true;
        return result;
    }

    private static JToken BoundStrings(JToken token, ref bool changed)
    {
        if (token is JObject sourceObject)
        {
            var result = new JObject();
            foreach (JProperty property in sourceObject.Properties()) result.Add(property.Name, BoundStrings(property.Value, ref changed));
            return result;
        }
        if (token is JArray sourceArray)
        {
            var result = new JArray();
            foreach (JToken item in sourceArray) result.Add(BoundStrings(item, ref changed));
            return result;
        }
        return token is JValue value && value.Type == JTokenType.String ? BoundString(value.Value<string>()!, ref changed) : token.DeepClone();
    }

    private static JToken NormalizeNullStrings(JToken token)
    {
        if (token is JObject sourceObject)
        {
            var result = new JObject();
            foreach (JProperty property in sourceObject.Properties()) result.Add(property.Name, NormalizeNullStrings(property.Value));
            return result;
        }
        if (token is JArray sourceArray) return new JArray(sourceArray.Select(NormalizeNullStrings));
        return token is JValue value && value.Type == JTokenType.String && value.Value<string>() is null ? JValue.CreateNull() : token.DeepClone();
    }

    private static JValue BoundString(string value, ref bool changed)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumStringBytes) return new JValue(value);
        int length = Math.Min(value.Length, MaximumStringBytes);
        while (length > 0 && (char.IsHighSurrogate(value[length - 1]) || Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > MaximumStringBytes)) length--;
        changed = true;
        return new JValue(value[..length]);
    }

    private static int Utf8Length(JObject value) => Encoding.UTF8.GetByteCount(value.ToString(Newtonsoft.Json.Formatting.None));
}

/// <summary>Snapshot of an explicitly started finite investigation session.</summary>
public sealed class DiagnosticInvestigationSession
{
    internal DiagnosticInvestigationSession(string id, string profile, DateTimeOffset expiresAt, IReadOnlyCollection<string> eventTypes)
    {
        Id = id;
        Profile = profile;
        ExpiresAt = expiresAt;
        EventTypes = eventTypes;
    }

    /// <summary>Gets the opaque, caller-safe session identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the selected profile name.</summary>
    public string Profile { get; }

    /// <summary>Gets the finite local expiry time.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets event types that were explicitly enabled for this session.</summary>
    public IReadOnlyCollection<string> EventTypes { get; }

    private readonly List<DiagnosticInvestigationRecord> _records = new();

    /// <summary>Gets only sanitized correlation facts retained in memory.</summary>
    public IReadOnlyList<DiagnosticInvestigationRecord> Records => _records;

    internal bool ActionCaptured { get; set; }
    internal bool FollowUpInFlight { get; set; }
    internal int FollowUpCount { get; set; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Timer? ExpiryTimer { get; set; }
    internal JObject? FollowUpEvidence { get; set; }
    internal Task? FollowUpTask { get; set; }
    internal List<DiagnosticInvestigationRecord> MutableRecords => _records;
}

/// <summary>Owns bounded opt-in investigation sessions without a persistent ledger.</summary>
public sealed class DiagnosticInvestigationManager : IDisposable
{
    /// <summary>Maximum active sessions held by one process.</summary>
    public const int MaximumSessions = 8;

    /// <summary>Maximum records retained in an individual session.</summary>
    public const int MaximumRecordsPerSession = 64;

    /// <summary>Maximum queued follow-up callbacks held by one process.</summary>
    public const int MaximumQueuedFollowUps = 8;

    /// <summary>Maximum event-triggered collections retained for one session.</summary>
    public const int MaximumFollowUpsPerSession = 4;

    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.Ordinal)
    {
        "connection_established", "connection_lost", "connection_error", "core_status"
    };

    private static readonly HashSet<string> AllowedNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "algos", "orders", "connect", "disconnect"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, DiagnosticInvestigationSession> _sessions = new(StringComparer.Ordinal);
    private ConnectionManager? _connectionManager;
    private Action<CoreConnection>? _onConnected;
    private Action<CoreConnection>? _onLost;
    private Action<CoreConnection, string>? _onError;
    private Action<CoreConnection>? _onCoreStatus;
    private Func<string, string, CancellationToken, Task<DiagnosticEvidenceBundle>>? _followUpCollector;
    private readonly HashSet<Task> _followUpTasks = new();
    private int _queuedFollowUps;
    private bool _disposed;

    /// <summary>Gets the number of active sessions after expiry cleanup.</summary>
    public int SessionCount { get { lock (_gate) { ExpireLocked(DateTimeOffset.UtcNow); return _sessions.Count; } } }

    /// <summary>Gets the number of explicit event subscriptions currently attached.</summary>
    public int SubscriptionCount { get { lock (_gate) { ExpireLocked(DateTimeOffset.UtcNow); return _sessions.Values.Sum(session => session.EventTypes.Count); } } }

    /// <summary>Gets queued or executing follow-up work across all sessions.</summary>
    public int QueuedFollowUpCount => Volatile.Read(ref _queuedFollowUps);

    /// <summary>Awaits the currently bounded follow-up work for deterministic local verification.</summary>
    public Task WaitForFollowUpsAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (_gate) pending = _followUpTasks.ToArray();
        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>Configures the single bounded collection callback used only after an opted-in event.</summary>
    public void ConfigureFollowUp(Func<string, string, CancellationToken, Task<DiagnosticEvidenceBundle>> followUpCollector)
    {
        if (followUpCollector is null) throw new ArgumentNullException(nameof(followUpCollector));
        lock (_gate) _followUpCollector = followUpCollector;
    }

    /// <summary>Configures a profile-only follow-up collector for compatibility with local synthetic tests.</summary>
    public void ConfigureFollowUp(Func<string, CancellationToken, Task<DiagnosticEvidenceBundle>> followUpCollector)
    {
        if (followUpCollector is null) throw new ArgumentNullException(nameof(followUpCollector));
        ConfigureFollowUp((profile, _, cancellationToken) => followUpCollector(profile, cancellationToken));
    }

    /// <summary>Starts an explicit bounded session for a single exact profile.</summary>
    public DiagnosticInvestigationSession Start(string profile, TimeSpan ttl, IEnumerable<string> eventTypes, DateTimeOffset? startedAt = null)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 128) throw new ArgumentException("A valid profile is required.", nameof(profile));
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromSeconds(300)) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be between one and 300 seconds.");
        string[] types = (eventTypes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0 || types.Any(type => !AllowedEventTypes.Contains(type))) throw new ArgumentException("At least one allowlisted event type is required.", nameof(eventTypes));

        lock (_gate)
        {
            ThrowIfDisposed();
            DateTimeOffset now = startedAt ?? DateTimeOffset.UtcNow;
            ExpireLocked(now);
            if (_sessions.Count >= MaximumSessions) throw new InvalidOperationException("The diagnostic investigation session limit has been reached.");
            var session = new DiagnosticInvestigationSession(Guid.NewGuid().ToString("N"), profile, now.Add(ttl), Array.AsReadOnly(types));
            _sessions.Add(session.Id, session);
            session.ExpiryTimer = new Timer(_ => ExpireFromTimer(session), null, ttl, Timeout.InfiniteTimeSpan);
            AttachEventsLocked();
            return session;
        }
    }

    /// <summary>Stops one session and promptly detaches callbacks when it was the last session.</summary>
    public bool Stop(string id)
    {
        Task? pending;
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(id, out DiagnosticInvestigationSession? session);
            EndSession(session);
            pending = session?.FollowUpTask;
            if (_sessions.Count == 0) DetachEventsLocked();
        }
        try { pending?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        return removed;
    }

    /// <summary>Gets a finite sanitized session snapshot without writing an export file.</summary>
    public bool TryGetSnapshot(string id, out DiagnosticInvestigationSnapshot? snapshot)
    {
        snapshot = null;
        if (!IsSessionId(id)) return false;
        lock (_gate)
        {
            ExpireLocked(DateTimeOffset.UtcNow);
            if (!_sessions.TryGetValue(id, out DiagnosticInvestigationSession? session)) return false;
            snapshot = new DiagnosticInvestigationSnapshot(session.Id, session.Profile, session.ExpiresAt, session.Records.ToArray(), session.FollowUpEvidence);
            return true;
        }
    }

    /// <summary>Removes expired sessions without starting a background monitor.</summary>
    public void Expire(DateTimeOffset now)
    {
        lock (_gate) ExpireLocked(now);
    }

    private void ExpireFromTimer(DiagnosticInvestigationSession session)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ExpireLocked(now);
            if (_sessions.ContainsKey(session.Id))
            {
                // Timer resolution or a wall-clock adjustment can put this callback
                // before ExpiresAt. A one-shot callback must not strand that session.
                double remainingMilliseconds = Math.Clamp(Math.Ceiling((session.ExpiresAt - now).TotalMilliseconds), 1, 300_000);
                session.ExpiryTimer?.Change(TimeSpan.FromMilliseconds(remainingMilliseconds), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Attaches only existing connection callbacks while at least one session is active.</summary>
    public void UseConnectionEvents(ConnectionManager manager)
    {
        if (manager is null) throw new ArgumentNullException(nameof(manager));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_connectionManager is not null && !ReferenceEquals(_connectionManager, manager)) throw new InvalidOperationException("A session manager can use one connection manager.");
            _connectionManager = manager;
            if (_sessions.Count > 0) AttachEventsLocked();
        }
    }

    /// <summary>Records one post-dispatch fact. Failures here never change the original command result.</summary>
    public void RecordDispatch(DiagnosticDispatchFact fact)
    {
        try
        {
            if (fact is null || !AllowedNouns.Contains(fact.Noun) || string.IsNullOrWhiteSpace(fact.Profile)) return;
            lock (_gate)
            {
                ExpireLocked(DateTimeOffset.UtcNow);
                foreach (DiagnosticInvestigationSession session in _sessions.Values.Where(session => !session.ActionCaptured && string.Equals(session.Profile, fact.Profile, StringComparison.Ordinal)))
                {
                    session.ActionCaptured = true;
                    AddRecord(session, new DiagnosticInvestigationRecord("action", fact.EndedAt, fact.Noun, fact.Subcommand, null, fact.Success, fact.StartedAt, fact.EndedAt));
                }
            }
        }
        catch
        {
            // Observation is optional and must never replace the dispatched command result.
        }
    }

    /// <summary>Records an allowlisted event without retaining its raw message or payload.</summary>
    public void RecordEvent(string eventType, string profile, DateTimeOffset? occurredAt = null)
    {
        try
        {
            if (!AllowedEventTypes.Contains(eventType) || string.IsNullOrWhiteSpace(profile)) return;
            var followUps = new List<DiagnosticInvestigationSession>();
            lock (_gate)
            {
                DateTimeOffset now = occurredAt ?? DateTimeOffset.UtcNow;
                ExpireLocked(now);
                foreach (DiagnosticInvestigationSession session in _sessions.Values.Where(session => string.Equals(session.Profile, profile, StringComparison.Ordinal) && session.EventTypes.Contains(eventType, StringComparer.Ordinal)))
                {
                    if (session.FollowUpInFlight || session.FollowUpCount >= MaximumFollowUpsPerSession)
                    {
                        AddRecord(session, new DiagnosticInvestigationRecord("event_dropped", now, null, null, eventType, false));
                        continue;
                    }
                    session.FollowUpInFlight = true;
                    session.FollowUpCount++;
                    AddRecord(session, new DiagnosticInvestigationRecord("event", now, null, null, eventType, null));
                    followUps.Add(session);
                }
            }
            foreach (DiagnosticInvestigationSession session in followUps) QueueFollowUp(session, "event:" + eventType);
        }
        catch
        {
            // Core polling callbacks stay non-blocking even when observation storage fails.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (DiagnosticInvestigationSession session in _sessions.Values) EndSession(session);
            _sessions.Clear();
            DetachEventsLocked();
            _disposed = true;
            pending = _followUpTasks.ToArray();
        }
        try { Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
    }

    private void AttachEventsLocked()
    {
        if (_connectionManager is null || _onConnected is not null) return;
        _onConnected = connection => RecordEvent("connection_established", connection.Name);
        _onLost = connection => RecordEvent("connection_lost", connection.Name);
        _onError = (connection, _) => RecordEvent("connection_error", connection.Name);
        _onCoreStatus = connection => RecordEvent("core_status", connection.Name);
        _connectionManager.OnConnectionEstablished += _onConnected;
        _connectionManager.OnConnectionLost += _onLost;
        _connectionManager.OnConnectionError += _onError;
        _connectionManager.OnCoreStatusReceived += _onCoreStatus;
    }

    private void DetachEventsLocked()
    {
        if (_connectionManager is null || _onConnected is null) return;
        _connectionManager.OnConnectionEstablished -= _onConnected;
        _connectionManager.OnConnectionLost -= _onLost;
        _connectionManager.OnConnectionError -= _onError!;
        _connectionManager.OnCoreStatusReceived -= _onCoreStatus!;
        _onConnected = null;
        _onLost = null;
        _onError = null;
        _onCoreStatus = null;
    }

    private void ExpireLocked(DateTimeOffset now)
    {
        foreach (DiagnosticInvestigationSession session in _sessions.Values.Where(session => session.ExpiresAt <= now).ToArray())
        {
            EndSession(session);
            _sessions.Remove(session.Id);
        }
        if (_sessions.Count == 0) DetachEventsLocked();
    }

    private void QueueFollowUp(DiagnosticInvestigationSession session, string trigger)
    {
        if (Interlocked.Increment(ref _queuedFollowUps) > MaximumQueuedFollowUps)
        {
            Interlocked.Decrement(ref _queuedFollowUps);
            lock (_gate)
            {
                session.FollowUpInFlight = false;
                AddRecord(session, new DiagnosticInvestigationRecord("follow_up_dropped", DateTimeOffset.UtcNow, null, null, null, false));
            }
            return;
        }
        Task task = Task.Run(async () =>
        {
            try
            {
                Func<string, string, CancellationToken, Task<DiagnosticEvidenceBundle>>? collector;
                lock (_gate) collector = _followUpCollector;
                if (collector is null)
                {
                    lock (_gate) AddRecord(session, new DiagnosticInvestigationRecord("follow_up", DateTimeOffset.UtcNow, null, null, null, false));
                    return;
                }
                DiagnosticEvidenceBundle bundle = await collector(session.Profile, trigger, session.Cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_sessions.ContainsKey(session.Id))
                    {
                        session.FollowUpEvidence = JObject.Parse(DiagnosticEvidenceBundleSerializer.Serialize(bundle));
                        AddRecord(session, new DiagnosticInvestigationRecord("follow_up", DateTimeOffset.UtcNow, null, null, null, bundle.Observations.All(observation => observation.Status == "success")));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop and expiry cancel work without affecting the source action.
            }
            catch
            {
                lock (_gate)
                {
                    if (_sessions.ContainsKey(session.Id)) AddRecord(session, new DiagnosticInvestigationRecord("follow_up", DateTimeOffset.UtcNow, null, null, null, false));
                }
            }
            finally
            {
                lock (_gate)
                {
                    session.FollowUpInFlight = false;
                    session.FollowUpTask = null;
                }
                Interlocked.Decrement(ref _queuedFollowUps);
            }
        });
        lock (_gate)
        {
            session.FollowUpTask = task;
            _followUpTasks.Add(task);
        }
        _ = task.ContinueWith(completed => { lock (_gate) _followUpTasks.Remove(completed); }, TaskScheduler.Default);
    }

    private static void AddRecord(DiagnosticInvestigationSession session, DiagnosticInvestigationRecord record)
    {
        if (session.MutableRecords.Count < MaximumRecordsPerSession) session.MutableRecords.Add(record);
    }

    /// <summary>Checks the opaque bounded session identifier accepted at the lifecycle boundary.</summary>
    public static bool IsSessionId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    private static void EndSession(DiagnosticInvestigationSession? session)
    {
        if (session is null) return;
        session.Cancellation.Cancel();
        session.ExpiryTimer?.Dispose();
        session.ExpiryTimer = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DiagnosticInvestigationManager));
    }
}
