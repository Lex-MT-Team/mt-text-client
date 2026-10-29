using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class MtDiagClientUnitTests
{
    [Fact]
    public async Task HistorySessionDeadlineIncludesQueueWaitAndReleasesUncertainConnections()
    {
        using var endpoint = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var profile = new ServerProfile { Name = "synthetic", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-token" };
        var route = new DiagnosticRoute(profile.Address, ((System.Net.IPEndPoint)endpoint.Client.LocalEndPoint!).Port, "explicit");
        var query = new DiagnosticHistoryQuery("logs", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-02T00:00:00Z"), 10, Guid.NewGuid().ToString("N"));
        var evidence = new DiagnosticIdentityEvidence(profile.Name, null, new[] { profile.Name }); using var client = new MtDiagClient();
        var first = client.ExecuteAsync(profile, route, DiagnosticRequest.HistoryRequest(query), evidence, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        var second = client.ExecuteAsync(profile, route, DiagnosticRequest.HistoryRequest(query), evidence, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        (await second).Kind.Should().Be(DiagnosticResultKind.Timeout);
        first.IsCompleted.Should().BeFalse("the queued timeout must not cancel the active exchange");
        client.Dispose(); (await first).Kind.Should().Be(DiagnosticResultKind.Cancelled);
        var sessions = (System.Collections.IDictionary)typeof(MtDiagClient).GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        sessions.Count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidRouteOrRequest_RefusesBeforeOpeningNativeSession()
    {
        var profile = new ServerProfile { Name = "synthetic", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-token" };
        var client = new MtDiagClient();
        DiagnosticIdentityEvidence evidence = new(profile.Name, null, new[] { profile.Name });

        DiagnosticResult corePort = await client.ExecuteAsync(profile, new DiagnosticRoute(profile.Address, profile.Port, "explicit"), DiagnosticRequest.Health(), evidence, TimeSpan.FromSeconds(1), CancellationToken.None);
        DiagnosticResult invalidLines = await client.ExecuteAsync(profile, new DiagnosticRoute(profile.Address, 4243, "explicit"), DiagnosticRequest.Logs(0, null), evidence, TimeSpan.FromSeconds(1), CancellationToken.None);
        DiagnosticResult invalidSource = await client.ExecuteAsync(profile, new DiagnosticRoute(profile.Address, 4243, "derived"), DiagnosticRequest.Health(), evidence, TimeSpan.FromSeconds(1), CancellationToken.None);

        corePort.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        invalidLines.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        invalidSource.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
    }

    [Fact]
    public async Task ExecuteAsync_PreCancelledRequest_ReturnsCancelledWithoutContactingPeer()
    {
        var profile = new ServerProfile { Name = "synthetic", Address = "127.0.0.1", Port = 4242, ClientToken = "synthetic-token" };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        DiagnosticResult result = await new MtDiagClient().ExecuteAsync(profile, new DiagnosticRoute(profile.Address, 4243, "explicit"), DiagnosticRequest.Capabilities(), new DiagnosticIdentityEvidence(profile.Name, null, new[] { profile.Name }), TimeSpan.FromSeconds(1), cancelled.Token);

        result.Kind.Should().Be(DiagnosticResultKind.Cancelled);
        result.Code.Should().Be("caller-cancelled");
    }

    [Fact]
    public void ValidateNative_ContradictoryOrSecretPeerEvidence_IsRefusedOrRedacted()
    {
        const string token = "synthetic-token";
        var evidence = new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic", "other-profile" });
        var route = new DiagnosticRoute("127.0.0.1", 4243, "explicit");
        var contradiction = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["server"] = "other-profile (loopback / venue)" });
        var success = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["token"] = token, ["message"] = "useful" });

        DiagnosticResult rejected = DiagnosticReplyValidator.ValidateNative(contradiction, "synthetic", route, evidence, token);
        DiagnosticResult redacted = DiagnosticReplyValidator.ValidateNative(success, "synthetic", route, new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic" }), token);

        rejected.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        rejected.Data.Should().BeNull();
        redacted.Kind.Should().Be(DiagnosticResultKind.Success);
        redacted.Data!.ToString().Should().Contain("useful");
        redacted.Data!.ToString().Should().NotContain(token);
        redacted.Metadata!["route"]!.Value<string>().Should().Be("explicit");
    }

    [Fact]
    public void ValidateNative_ServerError_RecursivelyRedactsEscapedJsonSecrets()
    {
        const string token = "synthetic-token";
        var route = new DiagnosticRoute("127.0.0.1", 4243, "explicit");
        var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), false, "server-error", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, "peer {\"password\":\"escaped-secret\",\"client_token\":\"synthetic-token\"}");

        DiagnosticResult result = DiagnosticReplyValidator.ValidateNative(reply, "synthetic", route, new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic" }), token);

        result.Kind.Should().Be(DiagnosticResultKind.ServerError);
        result.Error.Should().NotContain("escaped-secret");
        result.Error.Should().NotContain(token);
        result.Error.Should().Contain("[redacted]");
    }

    [Fact]
    public void ValidateNative_MaliciousReplyCannotEchoTokenOrDerivedConnectionSeed()
    {
        const string token = "synthetic-token";
        const string seed = "BINANCE|DERIVED-CONNECTION-SEED";
        var route = new DiagnosticRoute("127.0.0.1", 4243, "explicit");
        var success = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), true, "success", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["message"] = token + " " + seed }, null);
        var failure = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), false, "server-error", "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, "failure " + token + " " + seed);

        DiagnosticResult accepted = DiagnosticReplyValidator.ValidateNative(success, "synthetic", route, new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic" }), token, seed);
        DiagnosticResult rejected = DiagnosticReplyValidator.ValidateNative(failure, "synthetic", route, new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic" }), token, seed);

        accepted.Data!.ToString().Should().NotContain(token).And.NotContain(seed);
        rejected.Error.Should().NotContain(token).And.NotContain(seed);
    }

    [Fact]
    public void ValidateNative_SuccessCodeCanary_IsRefusedWithoutPeerData()
    {
        const string token = "synthetic-token";
        var route = new DiagnosticRoute("127.0.0.1", 4243, "explicit");
        var reply = new NativeDiagnosticEnvelope(NativeDiagnosticProtocol.Domain, NativeDiagnosticProtocol.Version, "request", "health", "synthetic", new JObject(), true, token, "companion", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new JObject { ["message"] = "peer-data" });

        DiagnosticResult result = DiagnosticReplyValidator.ValidateNative(reply, "synthetic", route, new DiagnosticIdentityEvidence("synthetic", null, new[] { "synthetic" }), token);

        result.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Code.Should().Be("invalid-success-code");
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
        result.Error.Should().BeNull();
    }
}
