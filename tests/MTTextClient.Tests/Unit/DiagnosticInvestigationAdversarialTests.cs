using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class DiagnosticInvestigationAdversarialTests
{
    [Fact]
    public void Bundle_RedactsNestedAndEscapedCredentials_BoundsOutput_AndHashesFinalPortableRepresentation()
    {
        DateTimeOffset collectedAt = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        var evidence = new JObject
        {
            ["nested"] = new JObject { ["client_token"] = "nested-canary-credential" },
            ["escaped"] = "{\"authorization\":\"Bearer escaped-canary-credential\",\"path\":\"/private/canary\"}",
            ["large"] = new string('x', DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes * 2)
        };
        var observations = Enumerable.Range(0, 8).Select(index => new DiagnosticEvidenceObservation(
            "source-" + index,
            "success",
            collectedAt.AddSeconds(-10),
            collectedAt,
            10,
            false,
            false,
            evidence.DeepClone())).ToArray();

        DiagnosticEvidenceBundle bundle = DiagnosticEvidenceBundle.Create("safe-profile", "one_shot", collectedAt, observations, includeHashes: true);
        string serialized = DiagnosticEvidenceBundleSerializer.Serialize(bundle);
        JObject portable = JObject.Parse(serialized);
        JObject unhashedPortable = (JObject)portable.DeepClone();
        unhashedPortable.Remove("hashes");
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unhashedPortable.ToString(Formatting.None)))).ToLowerInvariant();

        Encoding.UTF8.GetByteCount(serialized).Should().BeLessOrEqualTo(DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        portable["truncated"]!.Value<bool>().Should().BeTrue("large evidence must be represented honestly rather than silently sliced");
        serialized.Should().NotContain("nested-canary-credential");
        serialized.Should().NotContain("escaped-canary-credential");
        serialized.Should().NotContain("/private/canary");
        bundle.Hashes["bundle"].Should().Be(expectedHash, "the hash must cover exactly the final sanitized bounded export");
        DiagnosticEvidenceBundleSerializer.Serialize(bundle).Should().Be(serialized);
    }

    [Fact]
    public async Task Collector_PreservesNativeUnixObservationTime_AndMakesNestedGapsAndCallerCancellationHonest()
    {
        DateTimeOffset observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-37).ToUnixTimeSeconds());
        var executor = new ScriptedExecutor(observedAt.ToUnixTimeSeconds(), request => request.Operation switch
        {
            DiagnosticOperation.Capabilities => new JObject { ["nested"] = new JObject { ["state"] = "unavailable" } },
            DiagnosticOperation.Health => new JObject { ["nested"] = new JObject { ["status"] = "denied" } },
            DiagnosticOperation.Logs => new JObject { ["nested"] = new JObject { ["state"] = "unsupported" } },
            DiagnosticOperation.GuardianStatus => new JObject { ["nested"] = new JObject { ["status"] = "stale" } },
            _ => new JObject { ["nested"] = new JObject { ["truncated"] = true } }
        });
        var collector = new DiagnosticInvestigationCollector(executor);

        DiagnosticEvidenceBundle bundle = await collector.CollectAsync(Profile(), Route(), Identity(), TimeSpan.FromSeconds(60), false, CancellationToken.None);

        bundle.Observations.Should().HaveCount(5);
        bundle.Observations.Should().OnlyContain(observation => observation.ObservedAt == observedAt && observation.CollectedAt >= observedAt && observation.AgeSeconds >= 37 && observation.HasGap);
        executor.Timeouts.Should().OnlyContain(timeout => timeout <= TimeSpan.FromSeconds(15));
        executor.Timeouts.Should().HaveCount(5);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        DiagnosticEvidenceBundle cancelledBundle = await collector.CollectAsync(Profile(), Route(), Identity(), TimeSpan.FromSeconds(60), false, cancelled.Token);

        cancelledBundle.Observations.Should().OnlyContain(observation => observation.Status == "cancelled", "caller cancellation is distinct from collection deadline expiration");
    }

    [Fact]
    public async Task Session_EventFloodIsBounded_RepeatedTriggersRetainFollowUpEvidence_AndStopCancelsWork()
    {
        using var manager = new DiagnosticInvestigationManager();
        var calls = new ConcurrentQueue<TaskCompletionSource<DiagnosticEvidenceBundle>>();
        string lastTrigger = string.Empty;
        manager.ConfigureFollowUp((_, trigger, token) =>
        {
            lastTrigger = trigger;
            var completion = new TaskCompletionSource<DiagnosticEvidenceBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            calls.Enqueue(completion);
            return completion.Task;
        });
        DiagnosticInvestigationSession session = manager.Start("active", TimeSpan.FromSeconds(30), new[] { "connection_error" });

        for (int index = 0; index < 200; index++) manager.RecordEvent("connection_error", "active");
        await EventuallyAsync(() => calls.Count == 1);
        session.Records.Should().HaveCountLessOrEqualTo(DiagnosticInvestigationManager.MaximumRecordsPerSession);
        Complete(calls.Single(), lastTrigger);
        await manager.WaitForFollowUpsAsync(CancellationToken.None);

        for (int index = 0; index < DiagnosticInvestigationManager.MaximumFollowUpsPerSession - 1; index++)
        {
            manager.RecordEvent("connection_error", "active");
            await EventuallyAsync(() => calls.Count == index + 2);
            Complete(calls.Last(), lastTrigger);
            await manager.WaitForFollowUpsAsync(CancellationToken.None);
        }
        manager.RecordEvent("connection_error", "active");
        await Task.Delay(50);

        manager.TryGetSnapshot(session.Id, out DiagnosticInvestigationSnapshot? snapshot).Should().BeTrue();
        snapshot!.FollowUpEvidence.Should().NotBeNull("completed event follow-up evidence must remain exportable");
        snapshot.FollowUpEvidence!["trigger"]!.Value<string>().Should().Be("event:connection_error");
        session.Records.Should().HaveCountLessOrEqualTo(DiagnosticInvestigationManager.MaximumRecordsPerSession);
        calls.Count.Should().Be(DiagnosticInvestigationManager.MaximumFollowUpsPerSession);

        using var stopManager = new DiagnosticInvestigationManager();
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        stopManager.ConfigureFollowUp((_, _, token) =>
        {
            token.Register(() => cancellationObserved.TrySetResult(true));
            return Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => DiagnosticEvidenceBundle.Create("active", "event", DateTimeOffset.UtcNow, Array.Empty<DiagnosticEvidenceObservation>(), false));
        });
        DiagnosticInvestigationSession stopSession = stopManager.Start("active", TimeSpan.FromSeconds(30), new[] { "connection_error" });
        stopManager.RecordEvent("connection_error", "active");
        await EventuallyAsync(() => stopManager.QueuedFollowUpCount == 1);
        stopManager.Stop(stopSession.Id).Should().BeTrue();
        await EventuallyAsync(() => cancellationObserved.Task.IsCompleted);
        stopManager.QueuedFollowUpCount.Should().Be(0);
    }

    [Fact]
    public async Task Session_QuietTtlExpiresWithoutManualExpireOrPollingCleanup()
    {
        using var manager = new DiagnosticInvestigationManager();
        var expiryCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfigureFollowUp((_, _, token) =>
        {
            token.Register(() => expiryCancelled.TrySetResult(true));
            return Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => DiagnosticEvidenceBundle.Create("quiet", "event", DateTimeOffset.UtcNow, Array.Empty<DiagnosticEvidenceObservation>(), false));
        });
        DiagnosticInvestigationSession session = manager.Start("quiet", TimeSpan.FromSeconds(1), new[] { "connection_error" });
        manager.RecordEvent("connection_error", "quiet");

        await EventuallyAsync(() => expiryCancelled.Task.IsCompleted, timeoutMilliseconds: 2000);

        (await expiryCancelled.Task).Should().BeTrue("expiry must actively cancel work even when callers never poll the manager");
        manager.TryGetSnapshot(session.Id, out _).Should().BeFalse("the active expiry timer must remove a quiet session without an explicit cleanup call");
    }

    [Fact]
    public async Task Session_TimerBeforeRecordedExpiryMustScheduleRemainingLifetime()
    {
        using var manager = new DiagnosticInvestigationManager();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfigureFollowUp((_, _, token) =>
        {
            token.Register(() => cancelled.TrySetResult(true));
            return Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => DiagnosticEvidenceBundle.Create("quiet", "event", DateTimeOffset.UtcNow, Array.Empty<DiagnosticEvidenceObservation>(), false));
        });
        // The initial one-second callback necessarily precedes the recorded expiry.
        manager.Start("quiet", TimeSpan.FromSeconds(1), new[] { "connection_error" }, DateTimeOffset.UtcNow.AddSeconds(1));
        manager.RecordEvent("connection_error", "quiet");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(4));

        cancelled.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task FollowUp_DoesNotTargetAnotherSavedCoreOnTheSameAddress_AndRetainsAnHonestGap()
    {
        var selected = Profile();
        selected.Name = "selected";
        selected.Tags["mtdiag_port"] = "4243";
        var otherCore = Profile();
        otherCore.Name = "another-core";
        otherCore.Port = 4243;
        var executor = new ScriptedExecutor(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _ => new JObject { ["unexpected"] = true });
        using var manager = new DiagnosticInvestigationManager();
        _ = new DiagnosticCommand(executor, () => new List<ServerProfile> { selected, otherCore }, manager);
        DiagnosticInvestigationSession session = manager.Start("selected", TimeSpan.FromSeconds(30), new[] { "connection_error" });

        manager.RecordEvent("connection_error", "selected");
        await manager.WaitForFollowUpsAsync(CancellationToken.None);

        executor.Timeouts.Should().BeEmpty("follow-up collection must preserve the same-address saved-core guard as foreground collection");
        manager.TryGetSnapshot(session.Id, out DiagnosticInvestigationSnapshot? snapshot).Should().BeTrue();
        snapshot!.FollowUpEvidence.Should().NotBeNull();
        snapshot.FollowUpEvidence!.ToString(Formatting.None).Should().Contain("gap");
    }

    [Fact]
    public void DispatchObserver_UsesOnlyAllowlistedFacts_ImplicitActiveProfile_AndCannotChangeCommandResult()
    {
        using var manager = new DiagnosticInvestigationManager();
        DiagnosticInvestigationSession session = manager.Start("active-profile", TimeSpan.FromSeconds(30), new[] { "connection_error" });
        var registry = new CommandRegistry { ActiveProfileResolver = () => "active-profile" };
        registry.Register(new ResultCommand("orders", CommandResult.Ok("raw-message-canary", new JObject { ["token"] = "raw-data-canary" })));
        registry.Register(new ResultCommand("diag", CommandResult.Ok("ignored")));
        var observed = new List<DiagnosticDispatchFact>();
        registry.DispatchObserver = fact =>
        {
            observed.Add(fact);
            manager.RecordDispatch(fact);
            throw new InvalidOperationException("observer failure must be isolated");
        };

        CommandResult actionResult = registry.Dispatch("orders cancel secret-argument-canary");
        CommandResult ignoredResult = registry.Dispatch("diag investigation secret-argument-canary");

        actionResult.Success.Should().BeTrue();
        actionResult.Message.Should().Be("raw-message-canary");
        ignoredResult.Success.Should().BeTrue();
        observed.Should().ContainSingle();
        DiagnosticDispatchFact fact = observed.Single();
        fact.Noun.Should().Be("orders");
        fact.Subcommand.Should().Be("cancel");
        fact.Profile.Should().Be("active-profile");
        fact.EndedAt.Should().BeOnOrAfter(fact.StartedAt);
        string factSurface = string.Join("|", fact.Noun, fact.Subcommand, fact.Profile, fact.StartedAt, fact.EndedAt, fact.Success);
        factSurface.Should().NotContain("secret-argument-canary");
        factSurface.Should().NotContain("raw-message-canary");
        factSurface.Should().NotContain("raw-data-canary");
        session.Records.Should().ContainSingle(record => record.Kind == "action" && record.Noun == "orders" && record.Subcommand == "cancel" && record.Success == true);
        var command = new DiagnosticCommand(new ScriptedExecutor(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _ => new JObject()), () => new List<ServerProfile> { Profile() }, manager);
        JObject export = (JObject)command.Execute(new[] { "investigation", "export", session.Id }).Data!;
        JObject action = (JObject)export["records"]!.Single(record => record!["kind"]!.Value<string>() == "action")!;
        action["started_at"]!.Value<string>().Should().NotBeNullOrWhiteSpace();
        action["ended_at"]!.Value<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DispatchObserver_UsesEffectiveExplicitTargets_AndExcludesAllOrAmbiguousActions()
    {
        var registry = new CommandRegistry { ActiveProfileResolver = () => "active" };
        registry.Register(new ResultCommand("orders", CommandResult.Ok("ok")));
        registry.Register(new ResultCommand("algos", CommandResult.Ok("ok")));
        registry.Register(new ResultCommand("disconnect", CommandResult.Ok("ok")));
        var observed = new List<DiagnosticDispatchFact>();
        registry.DispatchObserver = observed.Add;

        registry.Dispatch("orders cancel @orders-target --confirm order-42");
        registry.Dispatch("algos start algo-7 --confirm @algos-target");
        registry.Dispatch("disconnect direct-target");
        registry.Dispatch("disconnect all");
        registry.Dispatch("algos copy algo-7 @source @destination --confirm");

        observed.Select(fact => (fact.Noun, fact.Subcommand, fact.Profile)).Should().BeEquivalentTo(new[]
        {
            ("orders", "cancel", "orders-target"),
            ("algos", "start", "algos-target"),
            ("disconnect", (string?)null, "direct-target")
        });
    }

    [Fact]
    public async Task SessionExport_SanitizesAndBoundsOuterMetadataRecordsAndFollowUp_AndStartDoesNotLeakProfile()
    {
        string unsafeProfile = "/private/session/client_token=session-secret-canary";
        var profile = Profile();
        profile.Name = unsafeProfile;
        using var manager = new DiagnosticInvestigationManager();
        var command = new DiagnosticCommand(new ScriptedExecutor(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _ => new JObject()), () => new List<ServerProfile> { profile }, manager);
        manager.ConfigureFollowUp((_, trigger, _) => Task.FromResult(DiagnosticEvidenceBundle.Create(
            unsafeProfile,
            trigger,
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, 8).Select(index => new DiagnosticEvidenceObservation("source-" + index, "success", null, DateTimeOffset.UtcNow, null, false, false, new JObject { ["payload"] = new string('x', 7000) })),
            includeHashes: true)));

        CommandResult started = command.Execute(new[] { "investigation", "start", unsafeProfile, "--ttl-seconds", "30", "--event-types", "connection_error" });
        string sessionId = ((JObject)started.Data!)["session_id"]!.Value<string>()!;
        started.Data!.ToString()!.Should().NotContain("session-secret-canary");
        started.Data!.ToString()!.Should().NotContain("/private/session");

        manager.RecordDispatch(new DiagnosticDispatchFact("orders", "client_token=record-secret-canary" + new string('r', 7000), unsafeProfile, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true));
        manager.RecordEvent("connection_error", unsafeProfile);
        await manager.WaitForFollowUpsAsync(CancellationToken.None);

        CommandResult result = command.Execute(new[] { "investigation", "export", sessionId });
        string serialized = ((JObject)result.Data!).ToString(Formatting.None);

        result.Success.Should().BeTrue();
        Encoding.UTF8.GetByteCount(serialized).Should().BeLessOrEqualTo(DiagnosticInvestigationExportSerializer.MaximumSerializedBytes);
        serialized.Should().NotContain("session-secret-canary");
        serialized.Should().NotContain("record-secret-canary");
        serialized.Should().NotContain("/private/session");
        ((JObject)result.Data!)["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void Bundle_ExposesDefensiveEvidenceClones_AfterFinalHash()
    {
        var originalEvidence = new JObject { ["safe"] = "before" };
        DiagnosticEvidenceBundle bundle = DiagnosticEvidenceBundle.Create("profile", "one_shot", DateTimeOffset.UtcNow,
            new[] { new DiagnosticEvidenceObservation("health", "success", null, DateTimeOffset.UtcNow, null, false, false, originalEvidence) }, includeHashes: true);
        string serializedBefore = DiagnosticEvidenceBundleSerializer.Serialize(bundle);

        originalEvidence["client_token"] = "input-secret-canary";
        ((JObject)bundle.Observations[0].Evidence!)["client_token"] = "exposed-secret-canary";
        string serializedAfter = DiagnosticEvidenceBundleSerializer.Serialize(bundle);

        serializedAfter.Should().Be(serializedBefore);
        serializedAfter.Should().NotContain("input-secret-canary");
        serializedAfter.Should().NotContain("exposed-secret-canary");
    }

    private static ServerProfile Profile() => new() { Name = "safe-profile", Address = "127.0.0.1", Port = 4242, ClientToken = "profile-token-canary" };

    private static DiagnosticRoute Route() => new("127.0.0.1", 4243, "explicit");

    private static DiagnosticIdentityEvidence Identity() => new("safe-profile", null, new[] { "safe-profile" });

    private static void Complete(TaskCompletionSource<DiagnosticEvidenceBundle> completion, string trigger)
    {
        completion.TrySetResult(DiagnosticEvidenceBundle.Create("active", trigger, DateTimeOffset.UtcNow,
            new[] { new DiagnosticEvidenceObservation("health", "success", null, DateTimeOffset.UtcNow, null, false, false, new JObject { ["ok"] = true }) }, false));
    }

    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("Condition was not met before the unit-test timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class ResultCommand : ICommand
    {
        private readonly CommandResult _result;

        public ResultCommand(string name, CommandResult result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }
        public string Description => "Synthetic diagnostic test command.";
        public string Usage => Name;
        public CommandResult Execute(string[] args) => _result;
    }

    private sealed class ScriptedExecutor : IDiagnosticExecutor
    {
        private readonly long _observedAt;
        private readonly Func<DiagnosticRequest, JToken> _data;

        public ScriptedExecutor(long observedAt, Func<DiagnosticRequest, JToken> data)
        {
            _observedAt = observedAt;
            _data = data;
        }

        public List<TimeSpan> Timeouts { get; } = new();

        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Timeouts.Add(timeout);
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "synthetic", request.Operation.ToString(), profile.Name, new JObject(), true, "success", "companion", _observedAt, _data(request));
            return Task.FromResult(DiagnosticReplyValidator.ValidateNative(reply, profile.Name, route, evidence, profile.ClientToken));
        }
    }
}
