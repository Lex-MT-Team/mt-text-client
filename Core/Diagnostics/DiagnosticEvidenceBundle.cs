using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>One bounded, portable observation collected during an investigation.</summary>
public sealed record DiagnosticEvidenceObservation(
    string Source,
    string Status,
    DateTimeOffset? ObservedAt,
    DateTimeOffset CollectedAt,
    long? AgeSeconds,
    bool HasGap,
    bool IsTruncated,
    JToken? Evidence);

/// <summary>Immutable, sanitized evidence returned by an explicit diagnostic request.</summary>
public sealed class DiagnosticEvidenceBundle
{
    private const int MaximumObservations = 8;
    private const int MaximumTopLevelTextBytes = 512;
    private const int MaximumObservationIdentifierBytes = 128;
    private readonly IReadOnlyList<DiagnosticEvidenceObservation> _observations;
    private readonly IReadOnlyDictionary<string, string> _hashes;
    private DiagnosticEvidenceBundle(string profile, string trigger, DateTimeOffset collectedAt, IReadOnlyList<DiagnosticEvidenceObservation> observations, IReadOnlyDictionary<string, string> hashes, bool isTruncated)
    {
        Schema = "mtdiag.evidence";
        Version = "1";
        Profile = profile;
        Trigger = trigger;
        CollectedAt = collectedAt;
        Acknowledgement = "not_observed";
        Outcome = "not_observed";
        _observations = observations.Select(CloneObservation).ToArray();
        _hashes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(hashes, StringComparer.Ordinal));
        IsTruncated = isTruncated;
    }

    /// <summary>Gets the stable portable schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Gets the stable evidence schema version.</summary>
    public string Version { get; }

    /// <summary>Gets the saved profile name selected by the caller.</summary>
    public string Profile { get; }

    /// <summary>Gets the explicit local reason collection was requested.</summary>
    public string Trigger { get; }

    /// <summary>Gets when the bundle was finalized locally.</summary>
    public DateTimeOffset CollectedAt { get; }

    /// <summary>Gets an acknowledgement claim, never inferred from dispatch success.</summary>
    public string Acknowledgement { get; }

    /// <summary>Gets an outcome claim, never inferred from a local diagnostic reply.</summary>
    public string Outcome { get; }

    /// <summary>Gets separately timed evidence observations.</summary>
    public IReadOnlyList<DiagnosticEvidenceObservation> Observations => _observations.Select(CloneObservation).ToArray();

    /// <summary>Gets hashes calculated after recursive sanitation.</summary>
    public IReadOnlyDictionary<string, string> Hashes => _hashes;

    internal IReadOnlyList<DiagnosticEvidenceObservation> SerializedObservations => _observations;

    /// <summary>Gets whether top-level metadata or observations were bounded before export.</summary>
    public bool IsTruncated { get; }

    /// <summary>Builds a bounded, sanitized evidence bundle without writing a file.</summary>
    public static DiagnosticEvidenceBundle Create(string profile, string trigger, DateTimeOffset collectedAt, IEnumerable<DiagnosticEvidenceObservation> observations, bool includeHashes)
    {
        if (string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("A profile is required.", nameof(profile));
        if (string.IsNullOrWhiteSpace(trigger)) throw new ArgumentException("A trigger is required.", nameof(trigger));
        if (observations is null) throw new ArgumentNullException(nameof(observations));

        DiagnosticText sanitizedProfile = SanitizeTopLevelText(profile);
        DiagnosticText sanitizedTrigger = SanitizeTopLevelText(trigger);
        DiagnosticEvidenceObservation[] inputObservations = observations.Take(MaximumObservations + 1).ToArray();
        IReadOnlyList<DiagnosticEvidenceObservation> cleanObservations = inputObservations.Take(MaximumObservations).Select(SanitizeObservation).ToArray();
        bool truncated = sanitizedProfile.IsTruncated || sanitizedTrigger.IsTruncated || inputObservations.Length > MaximumObservations || cleanObservations.Any(observation => observation.IsTruncated);
        var unhashed = new DiagnosticEvidenceBundle(sanitizedProfile.Value, sanitizedTrigger.Value, collectedAt, cleanObservations, new Dictionary<string, string>(), truncated);
        string canonicalPortable = JObject.Parse(DiagnosticEvidenceBundleSerializer.ToJson(unhashed, includeHashes: false).ToString(Formatting.None)).ToString(Formatting.None);
        IReadOnlyDictionary<string, string> hashes = includeHashes
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["bundle"] = ComputeHash(canonicalPortable) }
            : new Dictionary<string, string>();
        return new DiagnosticEvidenceBundle(sanitizedProfile.Value, sanitizedTrigger.Value, collectedAt, cleanObservations, hashes, truncated);
    }

    private static DiagnosticEvidenceObservation SanitizeObservation(DiagnosticEvidenceObservation observation)
    {
        DiagnosticText source = SanitizeIdentifier(observation.Source);
        DiagnosticText status = SanitizeIdentifier(observation.Status);
        return observation with
        {
            Source = source.Value,
            Status = status.Value,
            HasGap = observation.HasGap || source.IsTruncated || status.IsTruncated,
            IsTruncated = observation.IsTruncated || source.IsTruncated || status.IsTruncated,
            Evidence = observation.Evidence is null ? null : DiagnosticReplyValidator.SanitizeEvidence(observation.Evidence)
        };
    }

    private static DiagnosticEvidenceObservation CloneObservation(DiagnosticEvidenceObservation observation) => observation with
    {
        Evidence = observation.Evidence?.DeepClone()
    };

    private static DiagnosticText SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new DiagnosticText("unknown", false);
        string sanitized = DiagnosticReplyValidator.SanitizeEvidence(value);
        string identifier = new(sanitized.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
        if (string.IsNullOrEmpty(identifier)) identifier = "unknown";
        DiagnosticText bounded = BoundUtf8(identifier, MaximumObservationIdentifierBytes);
        return bounded with { IsTruncated = bounded.IsTruncated || !string.Equals(sanitized, value, StringComparison.Ordinal) };
    }

    private static DiagnosticText SanitizeTopLevelText(string value)
    {
        string sanitized = DiagnosticReplyValidator.SanitizeEvidence(value);
        DiagnosticText bounded = BoundUtf8(sanitized, MaximumTopLevelTextBytes);
        return bounded with { IsTruncated = bounded.IsTruncated || !string.Equals(sanitized, value, StringComparison.Ordinal) };
    }

    private static DiagnosticText BoundUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return new DiagnosticText(value, false);
        int length = Math.Min(value.Length, maximumBytes);
        while (length > 0 && (char.IsHighSurrogate(value[length - 1]) || Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maximumBytes)) length--;
        return new DiagnosticText(value[..length], true);
    }

    private static string ComputeHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record DiagnosticText(string Value, bool IsTruncated);
}

/// <summary>Serializes only sanitized portable evidence in a deterministic JSON form.</summary>
public static class DiagnosticEvidenceBundleSerializer
{
    /// <summary>Maximum UTF-8 bytes returned for one evidence bundle.</summary>
    public const int MaximumSerializedBytes = 65536;
    /// <summary>Serializes the complete portable bundle.</summary>
    public static string Serialize(DiagnosticEvidenceBundle bundle) => ToJson(bundle, includeHashes: true).ToString(Formatting.None);

    internal static JObject ToJson(DiagnosticEvidenceBundle bundle, bool includeHashes)
    {
        var result = new JObject
        {
            ["schema"] = bundle.Schema,
            ["version"] = bundle.Version,
            ["profile"] = bundle.Profile,
            ["trigger"] = bundle.Trigger,
            ["collected_at"] = bundle.CollectedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["acknowledgement"] = bundle.Acknowledgement,
            ["outcome"] = bundle.Outcome,
            ["observations"] = new JArray(bundle.SerializedObservations.Select(observation => new JObject
            {
                ["source"] = observation.Source,
                ["status"] = observation.Status,
                ["observed_at"] = observation.ObservedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["collected_at"] = observation.CollectedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["age_seconds"] = observation.AgeSeconds,
                ["gap"] = observation.HasGap,
                ["truncated"] = observation.IsTruncated,
                ["evidence"] = observation.Evidence
            }))
        };
        if (bundle.IsTruncated) result["truncated"] = true;
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > MaximumSerializedBytes - 512)
        {
            result["observations"] = new JArray(bundle.SerializedObservations.Select(observation => new JObject
            {
                ["source"] = observation.Source,
                ["status"] = observation.Status,
                ["observed_at"] = observation.ObservedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["collected_at"] = observation.CollectedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["age_seconds"] = observation.AgeSeconds,
                ["gap"] = observation.HasGap,
                ["truncated"] = true
            }));
            result["truncated"] = true;
        }
        if (includeHashes && bundle.Hashes.Count > 0) result["hashes"] = JObject.FromObject(bundle.Hashes);
        return result;
    }
}
