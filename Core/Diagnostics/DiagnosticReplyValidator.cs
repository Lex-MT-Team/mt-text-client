using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Validates authenticated reply JSON before peer material becomes evidence.</summary>
public static class DiagnosticReplyValidator
{
    private const int MaximumEmbeddedJsonBytes = 8 * 1024;
    private const int MaximumEmbeddedJsonDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> KnownSources = new(StringComparer.OrdinalIgnoreCase) { "companion", "sidecar", "mtguardianx", "auto", "file", "files", "docker", "journal" };
    private static readonly Regex AuthorizationValue = CreateRegex("\\b(?:bearer|basic)\\s+[^\\s\\\"']+");
    private static readonly Regex EndpointValue = CreateRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b|\b[a-z0-9.-]+\.[a-z]{2,}(?::\d{1,5})?\b");
    private static readonly Regex NetworkUrl = CreateRegex(@"\b[a-z][a-z0-9+.-]*://[^/\s]+");
    private static readonly Regex LabelledEndpoint = CreateRegex("\\b(?:host(?:name)?|endpoint|remote[_-]?address|address|url|uri)\\b\\s*[\\\"']?\\s*[:=]\\s*(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'(?:\\\\.|[^'])*'|\\S+)");
    private static readonly Regex LabelledSecret = CreateRegex("\\b(?:password|token|secret|api[_-]?key|client(?:[_-]?token)?|connection[_-]?key(?:seed)?|crypto[_-]?key|authorization|auth)\\b\\s*[\\\"']?\\s*[:=]\\s*(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'(?:\\\\.|[^'])*'|\\S+)");
    private static readonly Regex CredentialUrl = CreateRegex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@]+(?::[^@\s/]+)?@[^/\s]+");
    private static readonly Regex Ipv6Candidate = CreateRegex(@"(?:(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?:%[a-z0-9._-]+)?|\[[0-9a-f:.%_-]+\](?::\d{1,5})?)");
    private static readonly Regex LocalPath = CreateRegex("(?:[a-z]:\\\\|/(?:[^\\s/]+/)+)[^\\s\\\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeApiPrefix = CreateRegex(@"\b(?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+v[0-9]+$");
    private static readonly Regex RelativeApiNamespace = CreateRegex(@"(?:^|[\s|])api$");
    private static readonly Regex VersionedApiSuffix = CreateRegex(@"^/v[0-9]+/");
    private static readonly Regex HeapPayload = CreateRegex(@"(\bInternal Core Heap Data\s*:[ \t]*)[^\r\n]*");
    private static readonly Regex ServerString = CreateRegex(@"^\s*(\S+)\s*\(\s*([^\)\s/]+)\s*(?:/\s*([^\)\s]+?)\s*)?\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>Recursively redacts untrusted structured evidence before it leaves the diagnostic boundary.</summary>
    public static JToken SanitizeEvidence(JToken token, params string?[] sensitiveTokens)
    {
        ArgumentNullException.ThrowIfNull(token);
        JToken sanitized = SanitizeToken(token, string.Empty);
        foreach (string? secret in sensitiveTokens.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            sanitized = SanitizeToken(sanitized, secret!);
        }
        return sanitized;
    }

    /// <summary>Redacts untrusted free-form evidence before it leaves the diagnostic boundary.</summary>
    public static string SanitizeEvidence(string value, params string?[] sensitiveTokens)
    {
        ArgumentNullException.ThrowIfNull(value);
        string sanitized = SanitizeStringValue(value, string.Empty, 0).Value<string>()!;
        foreach (string? secret in sensitiveTokens.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            sanitized = SanitizeStringValue(sanitized, secret!, 0).Value<string>()!;
        }
        return sanitized;
    }

    /// <summary>Validates transport-independent reply evidence retained for redaction regression coverage.</summary>
    public static DiagnosticResult Validate(byte[] plaintext, string expectedRequestId, DiagnosticIdentityEvidence evidence, string sensitiveToken)
    {
        if (plaintext is null || plaintext.Length > NativeDiagnosticProtocol.MaximumResponseEnvelopeBytes || string.IsNullOrWhiteSpace(expectedRequestId) || evidence is null || string.IsNullOrWhiteSpace(evidence.SelectedProfile) || string.IsNullOrEmpty(sensitiveToken))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-validation-input");
        }

        JObject reply;
        try
        {
            reply = LoadStrictJson(StrictUtf8.GetString(plaintext)) as JObject ?? throw new JsonReaderException("Expected object.");
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or ArgumentException)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-json");
        }

        if (!TryRequiredString(reply, "id", out string id) || !string.Equals(id, expectedRequestId, StringComparison.Ordinal))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "id-correlated");
        }
        if (reply["ok"]?.Type != JTokenType.Boolean)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "ok-typed");
        }
        if (!TryRequiredString(reply, "source", out string source) || !KnownSources.Contains(source))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "source-known");
        }
        if (!TryRequiredString(reply, "profile", out string profile))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "profile-asserted");
        }
        if (!TryHasRegistryContradiction(reply["data"], evidence, out bool dataContradiction) || !TryHasRegistryContradiction(reply["metadata"], evidence, out bool metadataContradiction))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "embedded-json");
        }
        if (ContradictsCurrentRegistry(profile, evidence) || dataContradiction || metadataContradiction)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "registry-contradiction");
        }

        bool exactProfile = string.Equals(profile, evidence.SelectedProfile, StringComparison.OrdinalIgnoreCase);
        bool ephemeralAlias = !string.IsNullOrWhiteSpace(evidence.EphemeralAlias) && string.Equals(profile, evidence.EphemeralAlias, StringComparison.OrdinalIgnoreCase);
        if (!exactProfile && !ephemeralAlias)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "profile-unbound");
        }

        if (!reply["ok"]!.Value<bool>())
        {
            string? error = reply["error"]?.Type == JTokenType.String ? SanitizeText(reply["error"]!.Value<string>()!, sensitiveToken) : null;
            bool unsupported = error is not null && (error.Equals("unsupported command", StringComparison.OrdinalIgnoreCase) || error.Equals("unsupported capability", StringComparison.OrdinalIgnoreCase));
            return DiagnosticResult.Failure(unsupported ? DiagnosticResultKind.UnsupportedCapability : DiagnosticResultKind.ServerError, unsupported ? "unsupported-capability" : "server-error", error);
        }
        return DiagnosticResult.Success(SanitizeText(profile, sensitiveToken), SanitizeText(source, sensitiveToken), reply["data"] is null ? null : SanitizeToken(reply["data"]!, sensitiveToken), reply["metadata"] is null ? null : SanitizeToken(reply["metadata"]!, sensitiveToken));
    }

    /// <summary>Validates one already-correlated native envelope and removes unsafe peer evidence.</summary>
    public static DiagnosticResult ValidateNative(NativeDiagnosticEnvelope reply, string expectedProfile, DiagnosticRoute route, DiagnosticIdentityEvidence evidence, string sensitiveToken, string? connectionKeySeed = null)
    {
        if (reply is null || string.IsNullOrWhiteSpace(expectedProfile) || route is null || evidence is null || string.IsNullOrEmpty(sensitiveToken) || !string.Equals(reply.Domain, NativeDiagnosticProtocol.Domain, StringComparison.Ordinal) || reply.Version != NativeDiagnosticProtocol.Version || !string.Equals(reply.Profile, expectedProfile, StringComparison.OrdinalIgnoreCase) || !string.Equals(evidence.SelectedProfile, expectedProfile, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(reply.Source) || !KnownSources.Contains(reply.Source) || reply.ObservedAt is null || reply.Ok is null)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "reply-validation-failed");
        }
        if (!TryHasRegistryContradiction(reply.Data, evidence, out bool contradiction) || contradiction)
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "registry-contradiction");
        }

        if (reply.Ok.Value && !string.Equals(reply.Code, "success", StringComparison.Ordinal))
        {
            return DiagnosticResult.Failure(DiagnosticResultKind.ValidationFailure, "invalid-success-code");
        }

        if (!reply.Ok.Value)
        {
            bool unsupported = string.Equals(reply.Code, "unsupported-capability", StringComparison.Ordinal);
            return DiagnosticResult.Failure(unsupported ? DiagnosticResultKind.UnsupportedCapability : DiagnosticResultKind.ServerError, unsupported ? "unsupported-capability" : "server-error", reply.Error is null ? null : SanitizeEvidence(reply.Error, sensitiveToken, connectionKeySeed));
        }

        var metadata = new JObject
        {
            ["profile"] = expectedProfile,
            ["route"] = route.Source,
            ["domain"] = NativeDiagnosticProtocol.Domain,
            ["version"] = NativeDiagnosticProtocol.Version,
            ["request_id"] = reply.RequestId,
            ["observed_at"] = reply.ObservedAt.Value
        };
        return DiagnosticResult.Success(expectedProfile, SanitizeEvidence(reply.Source, sensitiveToken, connectionKeySeed), reply.Data is null ? null : SanitizeEvidence(reply.Data, sensitiveToken, connectionKeySeed), metadata);
    }

    private static Regex CreateRegex(string pattern, RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    {
        return new Regex(pattern, options | RegexOptions.NonBacktracking, RegexTimeout);
    }

    private static JToken LoadStrictJson(string json)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text)
        {
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 32,
            SupportMultipleContent = false
        };
        JToken token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read())
        {
            throw new JsonReaderException("Trailing JSON content.");
        }
        return token;
    }

    private static bool TryRequiredString(JObject reply, string name, out string value)
    {
        value = string.Empty;
        if (reply[name]?.Type != JTokenType.String)
        {
            return false;
        }
        value = reply[name]!.Value<string>()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool ContradictsCurrentRegistry(string? claimedIdentity, DiagnosticIdentityEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(claimedIdentity) || evidence.CurrentRegistryNames is null)
        {
            return false;
        }
        return evidence.CurrentRegistryNames.Any(name => string.Equals(name, claimedIdentity, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, evidence.SelectedProfile, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryHasRegistryContradiction(JToken? token, DiagnosticIdentityEvidence evidence, out bool contradiction)
    {
        contradiction = false;
        return token is null || TryInspectServerRegistryNames(token, evidence, 0, ref contradiction);
    }

    private static bool TryInspectServerRegistryNames(JToken token, DiagnosticIdentityEvidence evidence, int embeddedDepth, ref bool contradiction)
    {
        if (embeddedDepth > MaximumEmbeddedJsonDepth)
        {
            return false;
        }
        if (token is JObject obj)
        {
            if (obj["server"]?.Type == JTokenType.String)
            {
                if (!TryServerRegistryName(obj["server"]!.Value<string>()!, out string name, out bool matchTimedOut))
                {
                    if (matchTimedOut)
                    {
                        return false;
                    }
                }
                else if (ContradictsCurrentRegistry(name, evidence))
                {
                    contradiction = true;
                }
            }
            foreach (JProperty property in obj.Properties())
            {
                if (!TryInspectServerRegistryNames(property.Value, evidence, embeddedDepth, ref contradiction))
                {
                    return false;
                }
            }
            return true;
        }

        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                if (!TryInspectServerRegistryNames(item, evidence, embeddedDepth, ref contradiction))
                {
                    return false;
                }
            }
            return true;
        }

        if (token.Type == JTokenType.String)
        {
            string candidate = token.Value<string>()!;
            if (!TryServerRegistryName(candidate, out string direct, out bool directMatchTimedOut))
            {
                if (directMatchTimedOut)
                {
                    return false;
                }
            }
            else if (ContradictsCurrentRegistry(direct, evidence))
            {
                contradiction = true;
            }
            if (TryGetEmbeddedJson(candidate, out string embeddedJson))
            {
                try
                {
                    if (StrictUtf8.GetByteCount(embeddedJson) > MaximumEmbeddedJsonBytes)
                    {
                        return false;
                    }
                    JToken embedded = LoadStrictJson(embeddedJson);
                    return TryInspectServerRegistryNames(embedded, evidence, embeddedDepth + 1, ref contradiction);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or EncoderFallbackException)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryGetEmbeddedJson(string candidate, out string embeddedJson)
    {
        embeddedJson = candidate.TrimStart();
        if (embeddedJson.StartsWith("{", StringComparison.Ordinal))
        {
            return true;
        }
        if (!embeddedJson.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }
        int index = 1;
        while (index < embeddedJson.Length && char.IsWhiteSpace(embeddedJson[index]))
        {
            index++;
        }
        return index == embeddedJson.Length || embeddedJson[index] is '{' or '[' or '"' or ']';
    }

    private static bool TryServerRegistryName(string candidate, out string registryName, out bool matchTimedOut)
    {
        try
        {
            Match match = ServerString.Match(candidate);
            registryName = match.Success ? match.Groups[1].Value : string.Empty;
            matchTimedOut = false;
            return match.Success;
        }
        catch (RegexMatchTimeoutException)
        {
            registryName = string.Empty;
            matchTimedOut = true;
            return false;
        }
    }

    private static JToken SanitizeToken(JToken token, string sensitiveToken)
    {
        return SanitizeToken(token, sensitiveToken, 0);
    }

    private static JToken SanitizeToken(JToken token, string sensitiveToken, int embeddedDepth)
    {
        return token switch
        {
            JObject source => SanitizeObject(source, sensitiveToken, embeddedDepth),
            JArray source => new JArray(source.Select(item => SanitizeToken(item, sensitiveToken, embeddedDepth))),
            JValue value when value.Value is null => JValue.CreateNull(),
            JValue value when value.Type == JTokenType.String => SanitizeStringValue(value.Value<string>()!, sensitiveToken, embeddedDepth),
            _ => token.DeepClone()
        };
    }

    private static JToken SanitizeStringValue(string value, string sensitiveToken, int embeddedDepth)
    {
        if (!TryGetEmbeddedJson(value, out string embeddedJson))
        {
            return new JValue(SanitizeText(value, sensitiveToken));
        }
        if (embeddedDepth >= MaximumEmbeddedJsonDepth)
        {
            return new JValue("[redacted]");
        }
        try
        {
            if (StrictUtf8.GetByteCount(embeddedJson) > MaximumEmbeddedJsonBytes)
            {
                return new JValue("[redacted]");
            }
            JToken embedded = LoadStrictJson(embeddedJson);
            return new JValue(SanitizeToken(embedded, sensitiveToken, embeddedDepth + 1).ToString(Formatting.None));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or EncoderFallbackException)
        {
            return new JValue("[redacted]");
        }
    }

    private static JObject SanitizeObject(JObject source, string sensitiveToken, int embeddedDepth)
    {
        var sanitized = new JObject();
        foreach (JProperty property in source.Properties())
        {
            bool sensitiveName = IsSensitiveName(property.Name, sensitiveToken);
            string name = sensitiveName ? "redacted" : SanitizeText(property.Name, sensitiveToken);
            int suffix = 2;
            while (sanitized.Property(name, StringComparison.Ordinal) is not null)
            {
                name = "redacted_" + suffix++;
            }
            sanitized.Add(name, sensitiveName ? new JValue("[redacted]") : SanitizeToken(property.Value, sensitiveToken, embeddedDepth));
        }
        return sanitized;
    }

    private static bool IsSensitiveName(string value, string sensitiveToken)
    {
        string lower = value.ToLowerInvariant();
        return (!string.IsNullOrEmpty(sensitiveToken) && value.Contains(sensitiveToken, StringComparison.Ordinal)) || lower is "client" or "host" or "hostname" or "address" or "url" or "uri" or "internal core heap data" || lower.Contains("token") || lower.Contains("secret") || lower.Contains("password") || lower.Contains("credential") || lower.Contains("connectionkey") || lower.Contains("connection_key") || lower.Contains("crypto") || lower.Contains("authorization") || lower.Contains("api_key") || lower.Contains("api-key") || lower.Contains("apikey") || lower.Contains("auth") || lower.Contains("endpoint");
    }

    private static string SanitizeText(string value, string sensitiveToken)
    {
        try
        {
            string sanitized = string.IsNullOrEmpty(sensitiveToken) ? value : value.Replace(sensitiveToken, "[redacted]", StringComparison.Ordinal);
            sanitized = HeapPayload.Replace(sanitized, "$1[redacted]");
            sanitized = CredentialUrl.Replace(sanitized, "[redacted]");
            sanitized = AuthorizationValue.Replace(sanitized, "[redacted]");
            sanitized = LabelledSecret.Replace(sanitized, "[redacted]");
            sanitized = NetworkUrl.Replace(sanitized, "[redacted]");
            sanitized = LabelledEndpoint.Replace(sanitized, "[redacted]");
            string endpointInput = sanitized;
            sanitized = EndpointValue.Replace(endpointInput, match => IsDottedDiagnosticIdentifier(match.Value) || IsStackFrameSymbol(endpointInput, match) || IsGenericAritySegment(endpointInput, match) ? match.Value : "[redacted]");
            sanitized = RedactIpv6Endpoints(sanitized);
            string pathInput = sanitized;
            return LocalPath.Replace(pathInput, match => SanitizePath(pathInput, match));
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted]";
        }
    }

    private static bool IsDottedDiagnosticIdentifier(string value)
    {
        // Bare PascalCase identifiers are common in logger names and stack frames.
        // Explicit URLs, labelled endpoints and host:port values never get this exemption.
        string[] parts = value.Split('.');
        return parts.Length > 1 && parts.All(part => part.Length > 0 && part[0] is >= 'A' and <= 'Z' &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
    }

    private static bool IsStackFrameSymbol(string value, Match match)
    {
        int lineStart = match.Index == 0 ? 0 : value.LastIndexOf('\n', match.Index - 1) + 1;
        return value.AsSpan(lineStart, match.Index - lineStart).Trim().SequenceEqual("at".AsSpan()) &&
            value.AsSpan(match.Index + match.Length).TrimStart().StartsWith("(".AsSpan(), StringComparison.Ordinal) &&
            match.Value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.');
    }

    private static bool IsGenericAritySegment(string value, Match match)
    {
        if (match.Index == 0 || value[match.Index - 1] != '`') return false;
        int dot = match.Value.IndexOf('.');
        return dot > 0 && match.Value.AsSpan(0, dot).IndexOfAnyExceptInRange('0', '9') < 0 &&
            IsDottedDiagnosticIdentifier("Type." + match.Value[(dot + 1)..]);
    }

    private static string SanitizePath(string value, Match match)
    {
        // Only a versioned relative API route in HTTP-method context is code evidence.
        // Absolute filesystem paths and URL authorities have no such exemption.
        int prefixStart = Math.Max(0, match.Index - 64);
        string prefix = value.Substring(prefixStart, match.Index - prefixStart);
        if (!RelativeApiPrefix.IsMatch(prefix) && !(RelativeApiNamespace.IsMatch(prefix) && VersionedApiSuffix.IsMatch(match.Value))) return "[redacted]";
        int suffix = match.Value.IndexOfAny(new[] { '?', '#' });
        string path = suffix < 0 ? match.Value : match.Value[..suffix];
        if (path.Contains("..", StringComparison.Ordinal)) return "[redacted]";
        return suffix < 0 ? path : path + "[redacted]";
    }

    private static string RedactIpv6Endpoints(string value)
    {
        return Ipv6Candidate.Replace(value, static match =>
        {
            string candidate = match.Value;
            string host = candidate;
            if (candidate[0] == '[')
            {
                int closingBracket = candidate.IndexOf(']');
                if (closingBracket <= 1)
                {
                    return candidate;
                }
                host = candidate.Substring(1, closingBracket - 1);
            }
            return IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? "[redacted]"
                : candidate;
        });
    }
}
