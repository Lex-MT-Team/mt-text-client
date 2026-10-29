using System.Text;
using FluentAssertions;
using MTTextClient.Core.Diagnostics;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class DiagnosticReplyValidatorUnitTests
{
    [Theory]
    [InlineData("at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.GetResult(TResult result)")]
    [InlineData("Couldn't POST v5/order/create")]
    [InlineData("request api/v5/account/balance")]
    [InlineData("api/v5/trade/order")]
    [InlineData("Failed REST API GET v5/market/tickers")]
    public void SanitizeEvidence_PreservesGenericFramesAndRelativeApiPaths(string text)
    {
        DiagnosticReplyValidator.SanitizeEvidence(text).Should().Be(text);
    }

    [Theory]
    [InlineData("POST /home/synthetic/private/file")]
    [InlineData("POST v5/../../private/file")]
    [InlineData("api/v5/../../private/file")]
    [InlineData("/home/operator/api/v5/private/file")]
    [InlineData("api/v5/trade/order?signature=synthetic-private-value")]
    [InlineData("POST v5/order/create?signature=synthetic-private-value")]
    [InlineData("POST v5/order/create#synthetic-private-value")]
    [InlineData("Internal Core Heap Data: #CL#synthetic-private-value+payload/opaque/end")]
    public void SanitizeEvidence_HidesPathsQueriesAndCompleteOpaqueHeapPayloads(string text)
    {
        string sanitized = DiagnosticReplyValidator.SanitizeEvidence(text);
        sanitized.Should().NotContain("synthetic").And.NotContain("payload").And.NotContain("../../");
    }

    [Fact]
    public void SanitizeEvidence_HeapMaskStopsAtNewlineAndKeepsFollowingDiagnostic()
    {
        DiagnosticReplyValidator.SanitizeEvidence("Internal Core Heap Data: #CL#opaque\r\nWS.State Closed")
            .Should().Be("Internal Core Heap Data: [redacted]\r\nWS.State Closed");
        DiagnosticReplyValidator.SanitizeEvidence(new Newtonsoft.Json.Linq.JObject { ["Internal Core Heap Data"] = "#CL#opaque" })
            .ToString().Should().NotContain("opaque");
    }

    [Fact]
    public void SanitizeEvidence_ApiRouteExemptionNeverPreservesQueryOrKnownSecret()
    {
        DiagnosticReplyValidator.SanitizeEvidence("POST v5/order/create?signature=synthetic-value")
            .Should().Be("POST v5/order/create[redacted]");
        DiagnosticReplyValidator.SanitizeEvidence("POST v5/order/synthetic-value", "synthetic-value")
            .Should().NotContain("synthetic-value");
        DiagnosticReplyValidator.SanitizeEvidence("Type`1.Secret", "Secret")
            .Should().NotContain("Secret");
    }

    [Theory]
    [InlineData("WS.State became: Closed")]
    [InlineData("at System.Net.Security.SslStream.ReadAsyncInternal()")]
    [InlineData("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder failed")]
    [InlineData("   at synthetic.worker.run()")]
    public void SanitizeEvidence_PreservesDottedDiagnosticIdentifiers(string text)
    {
        DiagnosticReplyValidator.SanitizeEvidence(text).Should().Be(text);
    }

    [Theory]
    [InlineData("https://Node.Example.Test/path", "Node.Example.Test")]
    [InlineData("endpoint=Node.Example.Test", "Node.Example.Test")]
    [InlineData("hostname: Node.Example.Test", "Node.Example.Test")]
    [InlineData("Node.Example.Test:4343", "Node.Example.Test")]
    [InlineData("connect api.example.test", "api.example.test")]
    [InlineData("WS.State token=synthetic-secret", "synthetic-secret")]
    public void SanitizeEvidence_StillRedactsExplicitNetworkLocationsAndSecrets(string text, string hidden)
    {
        DiagnosticReplyValidator.SanitizeEvidence(text).Should().NotContain(hidden);
    }

    [Fact]
    public void SanitizeEvidence_IdentifierExemptionNeverOverridesKnownSecretsOrEndpointFields()
    {
        DiagnosticReplyValidator.SanitizeEvidence("WS.State", "WS.State").Should().Be("[redacted]");
        var evidence = new Newtonsoft.Json.Linq.JObject { ["hostname"] = "Node.Example.Test", ["message"] = "WS.State" };
        string sanitized = DiagnosticReplyValidator.SanitizeEvidence(evidence).ToString();
        sanitized.Should().Contain("WS.State").And.NotContain("Node.Example.Test");
    }

    [Fact]
    public void Validate_AcceptedReply_RecursivelyRedactsSensitiveDataAndMetadata()
    {
        const string token = "synthetic-validator-token";
        var evidence = new DiagnosticIdentityEvidence("synthetic-profile", null, new[] { "synthetic-profile" });
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"SYNTHETIC-PROFILE\",\"source\":\"sidecar\",\"data\":{\"nested\":{\"authorization\":\"Bearer " + token + "\",\"message\":\"useful\"}},\"metadata\":{\"endpoint\":\"10.0.0.1:9000\"}}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", evidence, token);

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        result.Data!.ToString().Should().Contain("useful");
        result.Data!.ToString().Should().NotContain(token);
        result.Metadata!.ToString().Should().NotContain("10.0.0.1:9000");
    }

    [Theory]
    [InlineData("{\"id\":\"request-1\",\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\"}")]
    [InlineData("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\"} {\"extra\":true}")]
    public void Validate_StrictJsonRefusal_NeverCarriesPeerData(string json)
    {
        DiagnosticResult result = DiagnosticReplyValidator.Validate(Encoding.UTF8.GetBytes(json), "request-1", Evidence(), "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Validate_CurrentRegistryContradictionInNestedLegacyServerStrings_OverridesExactProfileAndWithholdsPeerMaterial()
    {
        var evidence = new DiagnosticIdentityEvidence("synthetic-profile", "ephemeral", new[] { "synthetic-profile", "other-current-profile" });
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":[{\"server\":\"synthetic-profile (remote-profile / venue)\"},\"{\\\"server\\\":\\\"other-current-profile (remote-profile / venue)\\\"}\"],\"metadata\":{\"endpoint\":\"10.0.0.1:9000\"}}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", evidence, "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Code.Should().Be("registry-contradiction");
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Validate_ServerError_IsPayloadFreeAndClassifiesOnlyNarrowUnsupportedForm()
    {
        byte[] unsupported = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":false,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"error\":\"unsupported command\",\"data\":{\"secret\":\"peer-only\"}}");
        byte[] ordinary = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":false,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"error\":\"unavailable at 10.0.0.1:9000\",\"data\":{\"secret\":\"peer-only\"}}");

        DiagnosticResult unsupportedResult = DiagnosticReplyValidator.Validate(unsupported, "request-1", Evidence(), "synthetic-secret");
        DiagnosticResult ordinaryResult = DiagnosticReplyValidator.Validate(ordinary, "request-1", Evidence(), "synthetic-secret");

        unsupportedResult.Kind.Should().Be(DiagnosticResultKind.UnsupportedCapability);
        ordinaryResult.Kind.Should().Be(DiagnosticResultKind.ServerError);
        unsupportedResult.Data.Should().BeNull();
        ordinaryResult.Data.Should().BeNull();
        ordinaryResult.Error.Should().NotContain("10.0.0.1:9000");
    }

    [Fact]
    public void Validate_AcceptedText_RedactsLabelledSecretsCredentialUrlsAndIpv6Endpoints()
    {
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":\"password=another-secret api_key=key-value https://user:pass@example.test:9000/path [2001:db8::1]:9000\"}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", Evidence(), "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        string output = result.Data!.ToObject<string>()!;
        output.Should().NotContain("another-secret");
        output.Should().NotContain("key-value");
        output.Should().NotContain("user:pass@");
        output.Should().NotContain("2001:db8::1");
    }

    [Fact]
    public void SanitizeEvidence_RedactsIpv6WithoutTreatingClockTimeAsAnEndpoint()
    {
        string output = DiagnosticReplyValidator.SanitizeEvidence("observed at 12:34:56 from [2001:db8::1]:9000");

        output.Should().Contain("12:34:56");
        output.Should().NotContain("2001:db8::1");
    }

    [Fact]
    public void SanitizeEvidence_RedactsCompressedBracketedAndScopedIpv6Endpoints()
    {
        string output = DiagnosticReplyValidator.SanitizeEvidence("at 12:34:56 from 2001:db8::1, ::1, fe80::1%7, and [fe80::1%7]:9000");

        output.Should().Contain("12:34:56");
        output.Should().NotContain("2001:db8::1");
        output.Should().NotContain("::1");
        output.Should().NotContain("fe80::1%7");
    }

    [Fact]
    public void Validate_AcceptedJsonLogText_RedactsQuotedSecretsIncludingValuesWithSpaces()
    {
        const string password = "synthetic-other-secret with spaces";
        const string apiKey = "synthetic-api-key with spaces";
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":\"log {\\\"password\\\":\\\"" + password + "\\\",\\\"api_key\\\":\\\"" + apiKey + "\\\"}\",\"metadata\":{\"detail\":\"authorization: Bearer synthetic-metadata-secret\"}}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", Evidence(), "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        string visible = string.Concat(result.Data, result.Metadata, result.Error, result.Profile, result.Source, result.Code);
        visible.Should().NotContain(password);
        visible.Should().NotContain(apiKey);
        visible.Should().NotContain("synthetic-metadata-secret");
    }

    [Fact]
    public void Validate_WhitespacePrefixedEmbeddedJsonWithDuplicateServerFields_FailsClosed()
    {
        var evidence = new DiagnosticIdentityEvidence("synthetic-profile", "ephemeral", new[] { "synthetic-profile", "other-current-profile" });
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":\"  {\\\"server\\\":\\\"other-current-profile (remote-profile / venue)\\\",\\\"server\\\":\\\"synthetic-profile (remote-profile / venue)\\\"}\"}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", evidence, "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.ValidationFailure);
        result.Data.Should().BeNull();
        result.Metadata.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("[2026-10-26 14:30:00] normal core log")]
    [InlineData("[INFO] normal core log")]
    public void Validate_AcceptedBracketPrefixedLogText_IsNotTreatedAsEmbeddedJson(string logLine)
    {
        byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":\"" + logLine + "\"}");

        DiagnosticResult result = DiagnosticReplyValidator.Validate(reply, "request-1", Evidence(), "synthetic-secret");

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        result.Data!.ToObject<string>().Should().Contain("normal core log");
    }

    [Fact]
    public void Validate_AcceptedJsonlString_SanitizesDecodedEscapedNamesAndNestedTokenValues()
    {
        const string hiddenValue = "synthetic-hidden-value";
        const string sensitiveToken = "synthetic-validator-token";
        const string jsonl = "{\"pa\\u0073sword\":\"synthetic\\u002dhidden\\u002dvalue\",\"nested\":\"{\\\"client\\u005ftoken\\\":\\\"synthetic\\u002dvalidator\\u002dtoken\\\"}\"}";
        string reply = "{\"id\":\"request-1\",\"ok\":true,\"profile\":\"synthetic-profile\",\"source\":\"sidecar\",\"data\":{\"lines\":" + Newtonsoft.Json.JsonConvert.ToString(jsonl) + "}}";

        DiagnosticResult result = DiagnosticReplyValidator.Validate(Encoding.UTF8.GetBytes(reply), "request-1", Evidence(), sensitiveToken);

        result.Kind.Should().Be(DiagnosticResultKind.Success);
        string sanitizedJsonl = result.Data!["lines"]!.ToObject<string>()!;
        string visible = Newtonsoft.Json.Linq.JToken.Parse(sanitizedJsonl).ToString();
        visible.Should().NotContain(hiddenValue);
        visible.Should().NotContain(sensitiveToken);
        visible.Should().NotContain("password");
        visible.Should().Contain("[redacted]");
    }

    [Fact]
    public void SanitizeEvidence_StringOverload_UsesRecursiveEmbeddedJsonRedaction()
    {
        const string token = "synthetic-validator-token";
        string jsonl = "{\"pa\\u0073sword\":\"synthetic\\u002dhidden\\u002dvalue\",\"nested\":\"{\\\"client\\u005ftoken\\\":\\\"synthetic\\u002dvalidator\\u002dtoken\\\"}\"}";

        string sanitized = DiagnosticReplyValidator.SanitizeEvidence(jsonl, token);

        sanitized.Should().NotContain("synthetic-hidden-value");
        sanitized.Should().NotContain(token);
        sanitized.Should().Contain("[redacted]");
    }

    [Fact]
    public void SanitizeEvidence_EmptySensitiveToken_PreservesBenignPropertiesAndRedactsCredentialPathCanaries()
    {
        var evidence = new Newtonsoft.Json.Linq.JObject
        {
            ["status"] = "healthy",
            ["CLIENT"] = "synthetic-base64-token",
            ["connectionKeySeed"] = "synthetic-key-seed",
            ["path"] = "/private/synthetic/core.auth"
        };

        Newtonsoft.Json.Linq.JToken sanitized = DiagnosticReplyValidator.SanitizeEvidence(evidence);

        sanitized["status"]!.ToObject<string>().Should().Be("healthy");
        sanitized.ToString().Should().NotContain("synthetic-base64-token");
        sanitized.ToString().Should().NotContain("synthetic-key-seed");
        sanitized.ToString().Should().NotContain("/private/synthetic/core.auth");
    }

    private static DiagnosticIdentityEvidence Evidence() => new("synthetic-profile", null, new[] { "synthetic-profile" });
}
