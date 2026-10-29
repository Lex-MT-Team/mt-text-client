using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Defines the bounded companion-only envelope carried by native DEBUG messages.</summary>
public sealed record NativeDiagnosticEnvelope(
    string Domain,
    int Version,
    string RequestId,
    string Operation,
    string Profile,
    JObject Args,
    bool? Ok = null,
    string? Code = null,
    string? Source = null,
    long? ObservedAt = null,
    JToken? Data = null,
    string? Error = null);

/// <summary>Serializes and validates the strict versioned MTDiag envelope before source access.</summary>
public static class NativeDiagnosticProtocol
{
    /// <summary>Companion-owned protocol namespace.</summary>
    public const string Domain = "mtdiag";

    /// <summary>Only supported companion envelope version.</summary>
    public const int Version = 1;

    /// <summary>Maximum UTF-8 request envelope size before native packing.</summary>
    public const int MaximumRequestEnvelopeBytes = 8 * 1024;

    /// <summary>Maximum UTF-8 response envelope size before native packing.</summary>
    public const int MaximumResponseEnvelopeBytes = 60 * 1024;

    /// <summary>Largest allowed native diagnostic envelope.</summary>
    public const int MaximumEnvelopeBytes = MaximumResponseEnvelopeBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates a request using the only supported namespace and version.</summary>
    public static NativeDiagnosticEnvelope CreateRequest(string operation, string profile, string requestId, JObject? args)
    {
        return new NativeDiagnosticEnvelope(Domain, Version, requestId, operation, profile, args?.DeepClone() as JObject ?? new JObject());
    }

    /// <summary>Writes a bounded envelope without allowing peer-controlled JSON formatting.</summary>
    public static bool TrySerialize(NativeDiagnosticEnvelope? envelope, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (envelope is null || !IsBaseEnvelopeValid(envelope) || (envelope.Ok is true && (string.IsNullOrWhiteSpace(envelope.Source) || envelope.ObservedAt is null)))
        {
            return false;
        }

        try
        {
            var json = new JObject
            {
                ["domain"] = envelope.Domain,
                ["version"] = envelope.Version,
                ["request_id"] = envelope.RequestId,
                ["operation"] = envelope.Operation,
                ["profile"] = envelope.Profile,
                ["args"] = envelope.Args.DeepClone()
            };
            if (envelope.Ok is not null)
            {
                json["ok"] = envelope.Ok.Value;
                json["code"] = envelope.Code ?? (envelope.Ok.Value ? "success" : "server-error");
                json["source"] = envelope.Source ?? string.Empty;
                json["observed_at"] = envelope.ObservedAt;
                if (envelope.Ok.Value)
                {
                    json["data"] = envelope.Data?.DeepClone();
                }
                else if (!string.IsNullOrWhiteSpace(envelope.Error))
                {
                    json["error"] = envelope.Error;
                }
            }

            bytes = StrictUtf8.GetBytes(json.ToString(Formatting.None));
            if (bytes.Length > (envelope.Ok is null ? MaximumRequestEnvelopeBytes : MaximumResponseEnvelopeBytes))
            {
                bytes = Array.Empty<byte>();
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException or ArgumentException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>Parses a request only when it matches the active profile and vendor request.</summary>
    public static bool TryParseRequest(byte[]? bytes, string expectedRequestId, string expectedProfile, out NativeDiagnosticEnvelope envelope)
    {
        return TryParse(bytes, expectedRequestId, expectedProfile, requireReply: false, requireSafeProfile: false, out envelope);
    }

    /// <summary>Parses a strict server-bound request before its peer-selected request id is known.</summary>
    public static bool TryParseRequest(byte[]? bytes, string expectedProfile, out NativeDiagnosticEnvelope envelope)
    {
        return TryParse(bytes, expectedRequestId: null, expectedProfile, requireReply: false, requireSafeProfile: false, out envelope);
    }

    /// <summary>Parses a bounded request for a correlated profile-binding refusal without accepting that profile.</summary>
    public static bool TryParseRequestForProfileRefusal(byte[]? bytes, out NativeDiagnosticEnvelope envelope)
    {
        return TryParse(bytes, expectedRequestId: null, expectedProfile: null, requireReply: false, requireSafeProfile: true, out envelope);
    }

    /// <summary>Parses a reply only when it matches the active profile and vendor request.</summary>
    public static bool TryParseReply(byte[]? bytes, string expectedRequestId, string expectedProfile, out NativeDiagnosticEnvelope envelope)
    {
        return TryParse(bytes, expectedRequestId, expectedProfile, requireReply: true, requireSafeProfile: false, out envelope);
    }

    private static bool TryParse(byte[]? bytes, string? expectedRequestId, string? expectedProfile, bool requireReply, bool requireSafeProfile, out NativeDiagnosticEnvelope envelope)
    {
        envelope = new NativeDiagnosticEnvelope(string.Empty, 0, string.Empty, string.Empty, string.Empty, new JObject());
        if (bytes is null || bytes.Length == 0 || bytes.Length > (requireReply ? MaximumResponseEnvelopeBytes : MaximumRequestEnvelopeBytes) || (expectedProfile is not null && string.IsNullOrWhiteSpace(expectedProfile)) || (requireReply && string.IsNullOrWhiteSpace(expectedRequestId)))
        {
            return false;
        }

        try
        {
            string raw = StrictUtf8.GetString(bytes);
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            JObject json = LoadStrictObject(raw, settings);
            string? domain = ExactString(json, "domain");
            string? requestId = ExactString(json, "request_id");
            string? operation = ExactString(json, "operation");
            string? profile = ExactString(json, "profile");
            JObject? args = json["args"] as JObject;
            if (!string.Equals(domain, Domain, StringComparison.Ordinal) || !ExactInteger(json, "version", out int version) || version != Version || string.IsNullOrWhiteSpace(requestId) || (expectedRequestId is not null && !string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)) || (expectedProfile is not null && !string.Equals(profile, expectedProfile, StringComparison.OrdinalIgnoreCase)) || (requireSafeProfile && !IsSafeProfile(profile)) || !IsOperationAllowed(operation) || args is null)
            {
                return false;
            }

            bool? ok = json["ok"]?.Type == JTokenType.Boolean ? json["ok"]!.Value<bool>() : null;
            string? code = ExactString(json, "code");
            string? source = ExactString(json, "source");
            bool validObservedAt = ExactInteger(json, "observed_at", out long observedAt) && observedAt >= 0;
            if (requireReply != ok.HasValue || (requireReply && (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(source) || !validObservedAt)) || (requireReply && !ok!.Value && (json["data"] is not null || json["metadata"] is not null)) || (requireReply && ok!.Value && json["error"] is not null) || (!requireReply && (json["ok"] is not null || json["code"] is not null || json["source"] is not null || json["observed_at"] is not null || json["data"] is not null || json["error"] is not null)))
            {
                return false;
            }

            envelope = new NativeDiagnosticEnvelope(domain!, Version, requestId!, operation!, profile!, (JObject)args.DeepClone(), ok, code, source, requireReply ? observedAt : null, json["data"]?.DeepClone(), ExactString(json, "error"));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static JObject LoadStrictObject(string raw, JsonLoadSettings settings)
    {
        using var text = new StringReader(raw);
        using var reader = new JsonTextReader(text)
        {
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 32,
            SupportMultipleContent = false
        };
        JToken token = JToken.Load(reader, settings);
        if (reader.Read() || token is not JObject obj)
        {
            throw new JsonReaderException("Expected one object.");
        }
        return obj;
    }

    private static string? ExactString(JObject json, string name)
    {
        return json[name]?.Type == JTokenType.String ? json[name]!.Value<string>() : null;
    }

    private static bool ExactInteger(JObject json, string name, out int value)
    {
        value = 0;
        if (json[name]?.Type != JTokenType.Integer)
        {
            return false;
        }
        try { value = json[name]!.Value<int>(); return true; } catch (OverflowException) { return false; }
    }

    private static bool ExactInteger(JObject json, string name, out long value)
    {
        value = 0;
        if (json[name]?.Type != JTokenType.Integer)
        {
            return false;
        }
        try { value = json[name]!.Value<long>(); return true; } catch (OverflowException) { return false; }
    }

    private static bool IsBaseEnvelopeValid(NativeDiagnosticEnvelope envelope)
    {
        return string.Equals(envelope.Domain, Domain, StringComparison.Ordinal) && envelope.Version == Version && !string.IsNullOrWhiteSpace(envelope.RequestId) && !string.IsNullOrWhiteSpace(envelope.Profile) && IsOperationAllowed(envelope.Operation) && envelope.Args is not null;
    }

    private static bool IsSafeProfile(string? value)
    {
        return value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    /// <summary>Returns whether an operation is a fixed read-only diagnostic operation.</summary>
    public static bool IsOperationAllowed(string? operation)
    {
        return operation is "capabilities" or "health" or "logs" or "guardian-status" or "guardian-events" or "history-page" or "history-close";
    }
}
