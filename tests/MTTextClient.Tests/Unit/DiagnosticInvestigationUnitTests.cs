using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

public sealed class DiagnosticInvestigationUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void OneShot_BundleSeparatesClaimsAndSanitizesSerializedEvidence()
    {
        DateTimeOffset collectedAt = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var observation = new DiagnosticEvidenceObservation(
            "health",
            "success",
            collectedAt.AddSeconds(-4),
            collectedAt,
            4,
            false,
            false,
            new JObject { ["status"] = "ok", ["token"] = "must-not-leak" });

        DiagnosticEvidenceBundle bundle = DiagnosticEvidenceBundle.Create(
            "demo-profile",
            "one_shot",
            collectedAt,
            new[] { observation },
            includeHashes: true);

        string json = DiagnosticEvidenceBundleSerializer.Serialize(bundle);

        Assert.Equal("mtdiag.evidence", bundle.Schema);
        Assert.Equal("1", bundle.Version);
        Assert.Equal("not_observed", bundle.Acknowledgement);
        Assert.Equal("not_observed", bundle.Outcome);
        Assert.Contains("health", json, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(bundle.Hashes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Session_ExpiresStopsAndRecordsOnlySanitizedMatchingFacts()
    {
        var manager = new DiagnosticInvestigationManager();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        DiagnosticInvestigationSession session = manager.Start("demo-profile", TimeSpan.FromSeconds(2), new[] { "connection_error" }, now);
        manager.RecordDispatch(new DiagnosticDispatchFact("orders", "cancel", "demo-profile", now, now.AddSeconds(1), true));
        manager.RecordDispatch(new DiagnosticDispatchFact("diag", "investigation", "demo-profile", now, now.AddSeconds(1), true));
        manager.RecordEvent("connection_error", "other-profile", now);
        manager.RecordEvent("connection_error", "demo-profile", now.AddSeconds(1));

        Assert.Equal(1, manager.SessionCount);
        Assert.Equal(1, manager.SubscriptionCount);
        Assert.Contains(session.Records, record => record.Kind == "action" && record.Noun == "orders" && record.Success == true);
        Assert.DoesNotContain(session.Records, record => record.Noun == "diag");
        Assert.Contains(session.Records, record => record.Kind == "event" && record.EventType == "connection_error");

        manager.Expire(now.AddSeconds(3));

        Assert.Equal(0, manager.SessionCount);
        Assert.Equal(0, manager.SubscriptionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Bundle_SanitizesAndBoundsTopLevelProfileAndTrigger_BeforeFinalHash()
    {
        string unsafeProfile = "/private/path/client_token=" + new string('p', DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        string unsafeTrigger = "Bearer trigger-secret " + new string('t', DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        DiagnosticEvidenceBundle bundle = DiagnosticEvidenceBundle.Create(
            unsafeProfile,
            unsafeTrigger,
            DateTimeOffset.UtcNow,
            Array.Empty<DiagnosticEvidenceObservation>(),
            includeHashes: true);

        string serialized = DiagnosticEvidenceBundleSerializer.Serialize(bundle);
        JObject portable = JObject.Parse(serialized);
        JObject unhashedPortable = (JObject)portable.DeepClone();
        unhashedPortable.Remove("hashes");
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unhashedPortable.ToString(Formatting.None)))).ToLowerInvariant();

        Assert.True(Encoding.UTF8.GetByteCount(serialized) <= DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        Assert.DoesNotContain("/private/path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger-secret", serialized, StringComparison.Ordinal);
        Assert.True(portable["truncated"]!.Value<bool>());
        Assert.Equal(expectedHash, bundle.Hashes["bundle"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Bundle_BoundsAndSanitizesObservationMetadata_InTheFinalPortableExport()
    {
        string unsafeMetadata = "/private/metadata/client_token=" + new string('x', DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        var observations = Enumerable.Range(0, 8).Select(_ => new DiagnosticEvidenceObservation(
            unsafeMetadata,
            unsafeMetadata,
            null,
            DateTimeOffset.UtcNow,
            null,
            false,
            false,
            null));

        DiagnosticEvidenceBundle bundle = DiagnosticEvidenceBundle.Create("profile", "one_shot", DateTimeOffset.UtcNow, observations, includeHashes: true);
        string serialized = DiagnosticEvidenceBundleSerializer.Serialize(bundle);

        Assert.True(Encoding.UTF8.GetByteCount(serialized) <= DiagnosticEvidenceBundleSerializer.MaximumSerializedBytes);
        Assert.DoesNotContain("/private/metadata", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client_token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.True(JObject.Parse(serialized)["truncated"]!.Value<bool>());
        Assert.All(bundle.Observations, observation => Assert.True(observation.IsTruncated && observation.HasGap));
    }
}
