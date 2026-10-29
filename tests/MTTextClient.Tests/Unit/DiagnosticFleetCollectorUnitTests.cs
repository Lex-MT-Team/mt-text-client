using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class DiagnosticFleetCollectorUnitTests
{
    [Fact]
    public async Task CollectAsync_PreservesRequestedRowsAndSkipsDuplicateOrUnknownRoutes()
    {
        var executor = new RecordingExecutor();
        var collector = new DiagnosticFleetCollector(executor);
        List<ServerProfile> profiles = new() { Profile("one", 5101), Profile("two", 5102) };

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one", "missing", "one", "two" }, DiagnosticRequest.Health(), null, 8, TimeSpan.FromSeconds(15), false),
            profiles,
            CancellationToken.None);

        result.Rows.Select(row => row.RequestedProfile).Should().Equal("one", "missing", "one", "two");
        result.Rows.Select(row => row.Code).Should().Equal("success", "not_configured", "duplicate", "success");
        executor.Calls.Should().Be(2);
        result.Coverage.Requested.Should().Be(4);
        result.Coverage.Completed.Should().Be(2);
    }

    [Fact]
    public async Task CollectAsync_RefusesAnySavedCoreEndpointBeforeNetworkAccess()
    {
        var executor = new RecordingExecutor();
        var collector = new DiagnosticFleetCollector(executor);
        List<ServerProfile> profiles = new() { Profile("one", 5101), Profile("core-at-route", 6101) };

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), 6101, 8, TimeSpan.FromSeconds(15), false),
            profiles,
            CancellationToken.None);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Code.Should().Be("refused");
        executor.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CollectAsync_DoesNotRetryAnExplicitNativeConnectionRejection()
    {
        DiagnosticResult rejection = (DiagnosticResult)typeof(DiagnosticResult).GetMethod("Failure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { DiagnosticResultKind.TransportFailure, "native-connection-rejected", null })!;
        var executor = new RecordingExecutor(rejection);
        var collector = new DiagnosticFleetCollector(executor);

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromSeconds(1), true),
            new[] { Profile("one", 5101) }, CancellationToken.None);

        executor.Calls.Should().Be(1);
        result.Rows[0].Code.Should().Be("native-connection-rejected");
        result.Rows[0].Recovered.Should().BeFalse();
        result.Coverage.NotAttempted.Should().Be(0);
    }

    [Fact]
    public async Task CollectAsync_DoesNotRetryThrownExecutorExceptions()
    {
        var executor = new ThrowingThenSuccessExecutor();
        var collector = new DiagnosticFleetCollector(executor);

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), null, 16, TimeSpan.FromSeconds(15), true),
            new List<ServerProfile> { Profile("one", 5101) },
            CancellationToken.None);

        result.Rows[0].Attempts.Should().ContainSingle();
        result.Rows[0].Recovered.Should().BeFalse();
        result.Rows[0].Code.Should().Be("native-transport-failure");
        executor.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CollectAsync_RetriesReturnedTransportFailureAndMarksRecoveryAfterValidatedSuccess()
    {
        var executor = new ReturnedTransportFailureThenSuccessExecutor();
        var collector = new DiagnosticFleetCollector(executor);

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), null, 16, TimeSpan.FromSeconds(15), true),
            new List<ServerProfile> { Profile("one", 5101) },
            CancellationToken.None);

        result.Rows[0].Attempts.Should().HaveCount(2);
        result.Rows[0].Recovered.Should().BeTrue();
        result.Rows[0].Code.Should().Be("success");
        executor.Calls.Should().Be(2);
        result.Coverage.Completed.Should().Be(1);
        result.Coverage.Attempted.Should().Be(2);
    }

    [Fact]
    public async Task CollectAsync_DistinguishesCallerCancellationFromTheSharedDeadline()
    {
        var collector = new DiagnosticFleetCollector(new DelayingExecutor());
        List<ServerProfile> profiles = new() { Profile("one", 5101), Profile("two", 5102) };

        DiagnosticFleetResult timedOut = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one", "two" }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromMilliseconds(20), false), profiles, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        DiagnosticFleetResult callerCancelled = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), null, 1, TimeSpan.FromSeconds(1), false), profiles, cancelled.Token);

        timedOut.Rows.Should().OnlyContain(row => row.Kind == DiagnosticResultKind.Timeout);
        timedOut.Coverage.Completed.Should().Be(timedOut.Rows.Count(row => row.Attempts.Count > 0));
        timedOut.Coverage.Completed.Should().BeLessThan(2);
        callerCancelled.Rows.Should().OnlyContain(row => row.Kind == DiagnosticResultKind.Cancelled);
        callerCancelled.Coverage.Completed.Should().Be(0);
    }

    [Fact]
    public async Task CollectAsync_DeadlineCountsOnlyAttemptedProfilesAsCompleted()
    {
        var collector = new DiagnosticFleetCollector(new DelayingExecutor());
        List<ServerProfile> profiles = Enumerable.Range(0, 77).Select(index => Profile("synthetic-" + index, 10000 + index)).ToList();

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(profiles.Select(profile => profile.Name).ToArray(), DiagnosticRequest.Health(), null, 1, TimeSpan.FromMilliseconds(100), false),
            profiles, CancellationToken.None);

        result.Rows.Should().HaveCount(77);
        result.Coverage.Requested.Should().Be(77);
        result.Coverage.Completed.Should().Be(1);
        result.Coverage.Attempted.Should().Be(1);
        result.Rows.Count(row => row.Attempts.Count == 0).Should().Be(76);
        result.Coverage.Validated.Should().Be(0);
    }

    [Fact]
    public async Task CollectAsync_RefusesAmbiguousSavedIdentityAndTreatsMissingTagAsNotConfigured()
    {
        var executor = new RecordingExecutor();
        var collector = new DiagnosticFleetCollector(executor);
        ServerProfile missingTag = Profile("missing-route", 5101);
        missingTag.Tags.Clear();

        DiagnosticFleetResult result = await collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "ambiguous", "missing-route" }, DiagnosticRequest.Health(), null, 8, TimeSpan.FromSeconds(15), false),
            new List<ServerProfile> { Profile("ambiguous", 5101), Profile("ambiguous", 5102), missingTag },
            CancellationToken.None);

        result.Rows.Select(row => row.Code).Should().Equal("refused", "not_configured");
        executor.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task CollectAsync_RejectsInvalidConcurrency(int concurrency)
    {
        var collector = new DiagnosticFleetCollector(new RecordingExecutor());

        Func<Task> action = () => collector.CollectAsync(
            new DiagnosticFleetRequest(new[] { "one" }, DiagnosticRequest.Health(), null, concurrency, TimeSpan.FromSeconds(15), false),
            new List<ServerProfile> { Profile("one", 5101) },
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static ServerProfile Profile(string name, int diagnosticPort) => new()
    {
        Name = name,
        Address = "127.0.0.1",
        Port = diagnosticPort + 1000,
        ClientToken = name + "-token",
        Exchange = ExchangeType.BINANCE,
        Tags = new Dictionary<string, string> { ["mtdiag_port"] = diagnosticPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };

    private static DiagnosticResult Success(string profile) => DiagnosticReplyValidator.ValidateNative(
        new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "id", "health", profile, new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["profile"] = profile }, null),
        profile,
        new DiagnosticRoute("127.0.0.1", 5101, "tag"),
        new DiagnosticIdentityEvidence(profile, null, new[] { profile }),
        profile + "-token");

    private sealed class RecordingExecutor : IDiagnosticExecutor
    {
        private readonly Queue<DiagnosticResult> _results;

        internal RecordingExecutor(params DiagnosticResult[] results)
        {
            _results = new Queue<DiagnosticResult>(results.Length == 0 ? new[] { Success("one"), Success("two") } : results);
        }

        internal int Calls { get; private set; }

        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Success(profile.Name));
        }
    }

    private sealed class ThrowingThenSuccessExecutor : IDiagnosticExecutor
    {
        private int _calls;

        internal int Calls => _calls;

        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                throw new InvalidOperationException("synthetic transient transport failure");
            }
            return Task.FromResult(Success(profile.Name));
        }
    }

    private sealed class ReturnedTransportFailureThenSuccessExecutor : IDiagnosticExecutor
    {
        private int _calls;

        internal int Calls => _calls;

        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _calls++;
            return Task.FromResult(_calls == 1
                ? ReturnedTransportFailure()
                : Success(profile.Name));
        }
    }

    private static DiagnosticResult ReturnedTransportFailure() =>
        (DiagnosticResult)typeof(DiagnosticResult).GetMethod("Failure", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { DiagnosticResultKind.TransportFailure, "synthetic-transport-failure", null })!;

    private sealed class DelayingExecutor : IDiagnosticExecutor
    {
        public async Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return Success(profile.Name);
        }
    }
}
