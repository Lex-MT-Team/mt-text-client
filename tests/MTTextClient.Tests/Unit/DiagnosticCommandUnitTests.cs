using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class DiagnosticCommandUnitTests
{
    [Fact]
    public void Execute_InvestigationLifecycle_RequiresBoundedExplicitInputs()
    {
        var executor = new RecordingExecutor();
        var manager = new DiagnosticInvestigationManager();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { Profile() }, manager);

        CommandResult invalid = command.Execute(new[] { "investigation", "start", "saved-profile", "--ttl-seconds", "301", "--event-types", "connection_error" });
        CommandResult started = command.Execute(new[] { "investigation", "start", "saved-profile", "--ttl-seconds", "10", "--event-types", "connection_error" });
        string sessionId = ((JObject)started.Data!)["session_id"]!.Value<string>()!;

        invalid.Success.Should().BeFalse();
        started.Success.Should().BeTrue();
        manager.SessionCount.Should().Be(1);
        CommandResult stopped = command.Execute(new[] { "investigation", "stop", sessionId });
        stopped.Success.Should().BeTrue();
        manager.SessionCount.Should().Be(0);
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_OneShotMode_RefusesPersistentInvestigationStart()
    {
        var executor = new RecordingExecutor();
        var manager = new DiagnosticInvestigationManager();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { Profile() }, manager, supportsPersistentSessions: false);

        CommandResult result = command.Execute(new[] { "investigation", "start", "saved-profile", "--ttl-seconds", "10", "--event-types", "connection_error" });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("REPL");
        manager.SessionCount.Should().Be(0);
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_Logs_UsesSavedAddressExplicitPortAndBoundedArguments()
    {
        var executor = new RecordingExecutor();
        ServerProfile profile = Profile();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { profile });

        CommandResult result = command.Execute(new[] { "logs", "saved-profile", "--diag-port", "4243", "--timeout-ms", "100", "--lines", "20", "--grep", "errors" });

        result.Success.Should().BeTrue();
        executor.Calls.Should().ContainSingle();
        executor.Calls[0].Route.Address.Should().Be(profile.Address);
        executor.Calls[0].Route.Port.Should().Be(4243);
        executor.Calls[0].Route.Source.Should().Be("explicit");
        executor.Calls[0].Request.Should().Be(DiagnosticRequest.Logs(20, "errors"));
        executor.Calls[0].Timeout.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData("health", "saved-profile", "--diag-port", "4242")]
    [InlineData("logs", "saved-profile", "--diag-port", "4243", "--lines", "0")]
    [InlineData("guardian-events", "saved-profile", "--diag-port", "4243", "--lines", "3", "--grep", "nope")]
    [InlineData("health", "saved-profile", "--host", "unsafe")]
    public void Execute_InvalidOrUnsafeArguments_DoNotInvokeExecutor(params string[] args)
    {
        var executor = new RecordingExecutor();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { Profile() });

        CommandResult result = command.Execute(args);

        result.Success.Should().BeFalse();
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_AnotherSavedCorePortOnSameAddress_DoesNotInvokeExecutor()
    {
        var executor = new RecordingExecutor();
        ServerProfile selected = Profile();
        ServerProfile other = Profile();
        other.Name = "other-profile";
        other.Port = 4243;
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { selected, other });

        CommandResult result = command.Execute(new[] { "health", "saved-profile", "--diag-port", "4243" });

        result.Success.Should().BeFalse();
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_UsesCurrentDiagnosticPortTag_WhenExplicitPortIsOmitted()
    {
        var executor = new RecordingExecutor();
        ServerProfile profile = Profile();
        profile.Tags["mtdiag_port"] = "4243";
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { profile });

        CommandResult result = command.Execute(new[] { "guardian-events", "saved-profile", "--lines", "5" });

        result.Success.Should().BeTrue();
        executor.Calls.Single().Route.Source.Should().Be("tag");
        executor.Calls.Single().Route.Port.Should().Be(4243);
    }

    [Fact]
    public void Execute_UnknownProfileAndMissingPort_RemainDistinctWithoutControlState()
    {
        var executor = new RecordingExecutor();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { Profile() });

        CommandResult unknown = command.Execute(new[] { "health", "absent", "--diag-port", "4243" });
        CommandResult missingPort = command.Execute(new[] { "health", "saved-profile" });

        unknown.Success.Should().BeFalse();
        unknown.Message.Should().Contain("not found");
        missingPort.Success.Should().BeFalse();
        missingPort.Message.Should().Contain("diagnostic port");
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_DiagnosticFailure_ReturnsStructuredSanitizedOutcome()
    {
        var executor = new FailingExecutor();
        var command = new DiagnosticCommand(executor, () => new List<ServerProfile> { Profile() });

        CommandResult result = command.Execute(new[] { "health", "saved-profile", "--diag-port", "4243" });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("unsupported-capability");
        JObject data = result.Data.Should().BeOfType<JObject>().Subject;
        data["code"]!.Value<string>().Should().Be("unsupported-capability");
        data["kind"]!.Value<string>().Should().Be(nameof(DiagnosticResultKind.UnsupportedCapability));
    }

    [Fact]
    public void Execute_FleetCoverageSeparatesValidatedRepliesFromSourceAvailability()
    {
        var command = new DiagnosticCommand(new RecordingExecutor(), () => new List<ServerProfile> { Profile() });

        CommandResult result = command.Execute(new[] { "fleet", "collect", "--profiles", "saved-profile", "--operation", "health", "--diag-port", "4243" });

        result.Success.Should().BeTrue();
        JObject coverage = ((JObject)result.Data!)["coverage"]!.Should().BeOfType<JObject>().Subject;
        coverage["validated"]!.Value<int>().Should().Be(1);
        coverage["source_available"]!.Value<int>().Should().Be(1);
        coverage["source_unavailable"]!.Value<int>().Should().Be(0);
        coverage["successful"].Should().BeNull();
    }

    [Fact]
    public void Execute_FleetCoverageDistinguishesReturnedRowsFromAttemptedProfiles()
    {
        var command = new DiagnosticCommand(new RecordingExecutor(), () => new List<ServerProfile> { Profile() });

        CommandResult result = command.Execute(new[] { "fleet", "collect", "--profiles", "saved-profile,unknown-profile", "--operation", "health", "--diag-port", "4243" });

        result.Success.Should().BeFalse();
        JObject data = (JObject)result.Data!;
        data["coverage"]!["requested"]!.Value<int>().Should().Be(2);
        data["coverage"]!["rows_returned"]!.Value<int>().Should().Be(2);
        data["coverage"]!["completed"]!.Value<int>().Should().Be(1);
        data["coverage"]!["not_attempted"]!.Value<int>().Should().Be(1);
        data["coverage"]!["attempted"]!.Value<int>().Should().Be(1);
        data["rows"]![0]!["attempted"]!.Value<bool>().Should().BeTrue();
        data["rows"]![1]!["attempted"]!.Value<bool>().Should().BeFalse();
    }

    private static ServerProfile Profile() => new() { Name = "saved-profile", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-token" };

    [Theory]
    [InlineData(false, DiagnosticResultKind.Timeout, "deadline-exceeded", null, "Silence alone cannot distinguish")]
    [InlineData(true, DiagnosticResultKind.Timeout, "deadline-exceeded", null, "Silence alone cannot distinguish")]
    [InlineData(false, DiagnosticResultKind.TransportFailure, "native-connection-rejected", null, "identity is not authenticated")]
    [InlineData(true, DiagnosticResultKind.TransportFailure, "native-connection-rejected", null, "identity is not authenticated")]
    [InlineData(false, DiagnosticResultKind.ServerError, "server-error", "profile-unbound", "profile name exactly")]
    [InlineData(true, DiagnosticResultKind.ServerError, "server-error", "profile-unbound", "profile name exactly")]
    [InlineData(true, DiagnosticResultKind.ServerError, "server-error", "history-busy", "Back off")]
    [InlineData(false, DiagnosticResultKind.ServerError, "server-error", "history-cursor-capacity", "Back off")]
    [InlineData(true, DiagnosticResultKind.ServerError, "server-error", "history-restart-required", "fresh query")]
    public void Execute_FailureGuidanceIsConsistentForSingleAndFleet(bool fleet, DiagnosticResultKind kind, string code, string? error, string guidance)
    {
        DiagnosticResult failure = (DiagnosticResult)typeof(DiagnosticResult).GetMethod("Failure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { kind, code, error })!;
        var command = new DiagnosticCommand(new FixedExecutor(failure), () => new List<ServerProfile> { Profile() });
        string[] args = fleet
            ? new[] { "fleet", "collect", "--profiles", "saved-profile", "--operation", "health", "--diag-port", "4243" }
            : new[] { "health", "saved-profile", "--diag-port", "4243" };

        CommandResult result = command.Execute(args);

        result.Success.Should().BeFalse();
        JToken data = fleet ? ((JObject)result.Data!)["rows"]![0]! : (JObject)result.Data!;
        data["code"]!.Value<string>().Should().Be(code);
        if (error is not null) data["error"]!.Value<string>().Should().Be(error);
        data["next_action"]!.Value<string>().Should().Contain(guidance);
        data["data"].Should().BeNull();
        data["metadata"].Should().BeNull();
    }

    private sealed class FixedExecutor(DiagnosticResult result) : IDiagnosticExecutor
    {
        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingExecutor : IDiagnosticExecutor
    {
        public List<(DiagnosticRoute Route, DiagnosticRequest Request, TimeSpan Timeout)> Calls { get; } = new();

        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((route, request, timeout));
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "synthetic-request", "health", profile.Name, new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["capabilities"] = new JArray("health", "logs", "guardian-events") });
            return Task.FromResult(DiagnosticReplyValidator.ValidateNative(reply, profile.Name, route, evidence, profile.ClientToken));
        }
    }

    private sealed class FailingExecutor : IDiagnosticExecutor
    {
        public Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "synthetic-request", "health", profile.Name, new JObject(), false, "unsupported-capability", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, "unsupported capability");
            return Task.FromResult(DiagnosticReplyValidator.ValidateNative(reply, profile.Name, route, evidence, profile.ClientToken));
        }
    }
}
