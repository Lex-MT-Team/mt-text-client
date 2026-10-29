extern alias companion;

using System.Text;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;
using BoundedLogReader = companion::MTTextClient.Core.Diagnostics.Companion.BoundedLogReader;
using CompanionConfiguration = companion::MTTextClient.Core.Diagnostics.Companion.CompanionConfiguration;
using CompanionProfileConfiguration = companion::MTTextClient.Core.Diagnostics.Companion.CompanionProfileConfiguration;
using GuardianEvidenceReader = companion::MTTextClient.Core.Diagnostics.Companion.GuardianEvidenceReader;

namespace MTTextClient.Tests.Unit;

[Trait("Category", TraitCategories.Unit)]
public sealed class CompanionReaderUnitTests
{
    [SkippableFact]
    public void Read_FiltersEntriesBeforeTheGlobalTraversalBudget()
    {
        using var fixture = new ReaderFixture();
        for (int index = 0; index < 160; index++)
        {
            File.WriteAllText(Path.Combine(fixture.LogDirectory, $"noise-{index:D3}.txt"), "noise");
        }
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "wanted.log"), "wanted marker");

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None);

        evidence["status"]!.Value<string>().Should().Be("active");
        evidence["lines"]!.ToString().Should().Contain("wanted marker");
        evidence["traversalTruncated"]!.Value<bool>().Should().BeFalse();
    }

    [SkippableFact]
    public void Load_RejectsAuthPathsWhoseParentIsASymlink()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file guards are not supported on Windows.");

        using var fixture = new ReaderFixture();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        string outsideAuth = Path.Combine(outside, "core.auth");
        File.Move(fixture.AuthPath, outsideAuth);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "auth-link"), outside);
        fixture.SetAuthPath("auth-link/core.auth");

        Action load = () => fixture.Load();

        load.Should().Throw<InvalidOperationException>().WithMessage("Companion configuration is invalid.");
    }

    [SkippableFact]
    public void Load_RejectsLogRootsThatAreSymlinks()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file guards are not supported on Windows.");

        using var fixture = new ReaderFixture();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(fixture.LogDirectory);
        Directory.CreateSymbolicLink(fixture.LogDirectory, outside);

        Action load = () => fixture.Load();

        load.Should().Throw<InvalidOperationException>().WithMessage("Companion configuration is invalid.");
    }

    [SkippableFact]
    public void Read_RejectsLogRootReplacedByASymlinkAfterConfigurationLoad()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file guards are not supported on Windows.");

        using var fixture = new ReaderFixture();
        CompanionProfileConfiguration profile = fixture.Load();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "service.log"), "outside evidence");
        Directory.Delete(fixture.LogDirectory);
        Directory.CreateSymbolicLink(fixture.LogDirectory, outside);

        JObject evidence = new BoundedLogReader(profile).Read(CancellationToken.None);

        evidence["lines"]!.ToString().Should().NotContain("outside evidence");
    }

    [SkippableFact]
    public void Load_AcceptsAbsoluteRegularAllowlistedInputsOutsideTheConfigurationDirectory()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "mtdiag-absolute-path-unit-" + Guid.NewGuid().ToString("N"));
        string configurationDirectory = Path.Combine(root, "config");
        string evidenceDirectory = Path.Combine(root, "evidence");
        Directory.CreateDirectory(configurationDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string authPath = Path.Combine(evidenceDirectory, "core.auth");
        string logDirectory = Path.Combine(evidenceDirectory, "logs");
        string snapshotPath = Path.Combine(evidenceDirectory, "guardian.snapshot");
        string eventPath = Path.Combine(evidenceDirectory, "guardian.events");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(authPath, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-auth-token")) + Environment.NewLine);
        File.WriteAllText(snapshotPath, "synthetic snapshot");
        File.WriteAllText(eventPath, "synthetic event");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        string configurationPath = Path.Combine(configurationDirectory, "companion.json");
        File.WriteAllText(configurationPath, new JObject
        {
            ["profiles"] = new JArray(new JObject
            {
                ["name"] = "reader",
                ["exchange"] = "BINANCE",
                ["corePort"] = 4242,
                ["diagnosticPort"] = 52123,
                ["coreAuthPath"] = authPath,
                ["logRoots"] = new JArray(logDirectory),
                ["logGlobs"] = new JArray("*.log"),
                ["guardianSnapshotPath"] = snapshotPath,
                ["guardianEventPath"] = eventPath
            })
        }.ToString());

        try
        {
            Action load = () => CompanionConfiguration.Load(configurationPath);

            load.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void Load_RejectsIntegersOutsideThePortRangeWithoutLeakingConversionFailures()
    {
        using var fixture = new ReaderFixture();
        fixture.WriteRawConfig("""
        {"profiles":[{"name":"reader","exchange":"BINANCE","corePort":999999999999999999999999999999,"diagnosticPort":52123,"coreAuthPath":"core.auth","logRoots":["logs"],"logGlobs":["*.log"]}]}
        """);

        Action load = () => fixture.LoadRaw();

        load.Should().Throw<InvalidOperationException>().WithMessage("Companion configuration is invalid.");
    }

    [SkippableFact]
    public void Load_RejectsNumericExchangeStrings()
    {
        using var fixture = new ReaderFixture { Exchange = "1" };

        Action load = () => fixture.Load();

        load.Should().Throw<InvalidOperationException>().WithMessage("Companion configuration is invalid.");
    }

    [SkippableFact]
    public void Read_DoesNotExposeEscapedJsonSecretsInLogEvidence()
    {
        using var fixture = new ReaderFixture();
        const string secret = "synthetic-escaped-secret";
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), "{\"pa\\u0073sword\":\"" + secret + "\",\"nested\":\"{\\\"client_token\\\":\\\"hidden\\\"}\"}");

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None);

        evidence.ToString().Should().NotContain(secret);
        evidence.ToString().Should().NotContain("hidden");
        evidence.ToString().Should().Contain("[redacted]");
    }

    [SkippableFact]
    public void Read_ResponseCapRetainsNewestLogLines()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), string.Join(Environment.NewLine, Enumerable.Range(0, 10).Select(index => $"entry-{index:D2} {new string('x', 7000)}")));

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, maximumLines: 10);

        string returned = evidence["lines"]!.ToString();
        evidence["responseTruncated"]!.Value<bool>().Should().BeTrue();
        returned.Should().Contain("entry-09");
        returned.Should().NotContain("entry-00");
    }

    [SkippableFact]
    public void Read_GrepSearchesTheBoundedFileBeforeApplyingTheLineTail()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), string.Join(Environment.NewLine, new[] { "match-oldest", "ordinary-1", "ordinary-2", "ordinary-3", "ordinary-4" }));

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, maximumLines: 2, grep: "match");

        evidence["lines"]!.ToString().Should().Contain("match-oldest");
        evidence["searchTruncated"]!.Value<bool>().Should().BeFalse();
    }

    [SkippableFact]
    public void Read_GrepWithExactlyTheRequestedMatchCountIsNotSearchTruncated()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), string.Join(Environment.NewLine, new[] { "match-one", "match-two" }));

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, maximumLines: 2, grep: "match");

        evidence["lines"]!.ToString().Should().Contain("match-one").And.Contain("match-two");
        evidence["searchTruncated"]!.Value<bool>().Should().BeFalse();
        evidence["lineTruncated"]!.Value<bool>().Should().BeFalse();
        evidence["truncated"]!.Value<bool>().Should().BeFalse();
    }

    [SkippableFact]
    public void Read_GrepWithMoreMatchesThanRequestedRemainsTruncated()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), string.Join(Environment.NewLine, new[] { "match-one", "match-two", "match-three" }));

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, maximumLines: 2, grep: "match");

        evidence["searchTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_GrepDeadlineReportsIncompleteSearchAsTruncated()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), "match");

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, grep: "match", deadline: TimeSpan.FromTicks(1));

        evidence["inputTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["searchTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_GrepInputLimitReportsIncompleteSearchAsTruncated()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(Path.Combine(fixture.LogDirectory, "service.log"), new string('x', BoundedLogReader.MaximumInputBytes + 1024));

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, maximumLines: 1, grep: "x");

        evidence["inputTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["searchTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_GrepUnsearchedCandidateFilesRemainTruncated()
    {
        using var fixture = new ReaderFixture();
        for (int index = 0; index <= 128; index++)
        {
            File.WriteAllText(Path.Combine(fixture.LogDirectory, $"service-{index:D3}.log"), "match");
        }

        JObject evidence = new BoundedLogReader(fixture.Load()).Read(CancellationToken.None, grep: "match");

        evidence["inputTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["searchTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_BoundsGuardianEvidenceAndSeparatesObservationFromFileModification()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(fixture.SnapshotPath, new string('x', 80 * 1024));
        File.WriteAllText(fixture.EventPath, "2026-10-26T00:00:00Z synthetic event");

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None);

        Encoding.UTF8.GetByteCount(evidence.ToString()).Should().BeLessOrEqualTo(48 * 1024);
        evidence["snapshot"]!["fileModifiedAtUtc"].Should().NotBeNull();
        evidence["snapshot"]!["observedAtUtc"].Should().NotBeNull();
        evidence["snapshot"]!["capturedAtUtc"].Should().BeNull();
        evidence["eventHistory"]!["lastEventUtc"].Should().NotBeNull();
    }

    [SkippableFact]
    public void Read_GuardianResponseBudgetRetainsNewestEvents()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(fixture.EventPath, string.Join(Environment.NewLine, Enumerable.Range(0, 10).Select(index => $"2026-10-26T00:00:{index:D2}Z event-{index:D2} {new string('x', 7000)}")));

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None, maximumEventEntries: 10);

        string returned = evidence["eventHistory"]!["entries"]!.ToString();
        evidence["responseTruncated"]!.Value<bool>().Should().BeTrue();
        returned.Should().Contain("event-09");
        returned.Should().NotContain("event-00");
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(500)]
    public void Read_GuardianEventsHonorTheAdvertisedRangeAndDiscloseTheTailLimit(int count)
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(fixture.EventPath, string.Join('\n', Enumerable.Range(0, 600).Select(index => $"event-{index:D3}")));

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None, maximumEventEntries: count);

        JArray entries = (JArray)evidence["eventHistory"]!["entries"]!;
        entries.Should().HaveCount(count);
        entries.First!.Value<string>().Should().Be($"event-{600 - count:D3}");
        entries.Last!.Value<string>().Should().Be("event-599");
        evidence["eventHistory"]!["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_FiveHundredLargeGuardianEventsRemainBoundedAndKeepTheNewest()
    {
        using var fixture = new ReaderFixture();
        File.WriteAllText(fixture.EventPath, string.Join('\n', Enumerable.Range(0, 500).Select(index => $"event-{index:D3} {new string('x', 200)}")));

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None, maximumEventEntries: 500);

        Encoding.UTF8.GetByteCount(evidence.ToString(Newtonsoft.Json.Formatting.None)).Should().BeLessOrEqualTo(48 * 1024);
        evidence["responseTruncated"]!.Value<bool>().Should().BeTrue();
        evidence["eventHistory"]!["entries"]!.ToString().Should().Contain("event-499").And.NotContain("event-000");
    }

    [SkippableFact]
    public void Read_GuardianSnapshotOnlyResponseIsBoundedWithoutThrowing()
    {
        using var fixture = new ReaderFixture { IncludeGuardianEventPath = false };
        File.WriteAllText(fixture.SnapshotPath, new string('x', 80 * 1024));

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None);

        Encoding.UTF8.GetByteCount(evidence.ToString()).Should().BeLessOrEqualTo(48 * 1024);
        evidence["snapshot"]!["truncated"]!.Value<bool>().Should().BeTrue();
        evidence["responseTruncated"]!.Value<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public void Read_ProcessNameWithoutAMatchIsOnlyANegativeObservation()
    {
        using var fixture = new ReaderFixture { GuardianProcessName = "mtdiag-absent-process" };

        JObject evidence = new GuardianEvidenceReader(fixture.Load()).Read(CancellationToken.None);

        evidence["process"]!["status"]!.Value<string>().Should().Be("not_observed");
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _configPath;

        internal ReaderFixture()
        {
            Skip.If(OperatingSystem.IsWindows(), "Local companion file reads require a supported POSIX host.");
            Root = Path.Combine(Path.GetTempPath(), "mtdiag-reader-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            LogDirectory = Path.Combine(Root, "logs");
            Directory.CreateDirectory(LogDirectory);
            AuthPath = Path.Combine(Root, "core.auth");
            SnapshotPath = Path.Combine(Root, "guardian.snapshot");
            EventPath = Path.Combine(Root, "guardian.events");
            _configPath = Path.Combine(Root, "companion.json");
            File.WriteAllText(AuthPath, "CLIENT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-auth-token")) + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(AuthPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        internal string Root { get; }
        internal string LogDirectory { get; }
        internal string AuthPath { get; private set; }
        internal string SnapshotPath { get; }
        internal string EventPath { get; }
        internal string Exchange { get; set; } = "BINANCE";
        internal string? GuardianProcessName { get; set; }
        internal bool IncludeGuardianEventPath { get; set; } = true;

        internal CompanionProfileConfiguration Load()
        {
            WriteConfig();
            return LoadRaw().Profiles.Single();
        }

        internal CompanionConfiguration LoadRaw()
        {
            return CompanionConfiguration.Load(_configPath);
        }

        internal void SetAuthPath(string relativePath)
        {
            AuthPath = Path.Combine(Root, relativePath);
        }

        internal void WriteRawConfig(string json)
        {
            File.WriteAllText(_configPath, json);
        }

        private void WriteConfig()
        {
            string auth = Path.GetRelativePath(Root, AuthPath);
            var profile = new JObject
            {
                ["name"] = "reader",
                ["exchange"] = Exchange,
                ["corePort"] = 4242,
                ["diagnosticPort"] = 52123,
                ["coreAuthPath"] = auth,
                ["logRoots"] = new JArray("logs"),
                ["logGlobs"] = new JArray("*.log"),
                ["guardianSnapshotPath"] = "guardian.snapshot"
            };
            if (IncludeGuardianEventPath)
            {
                profile["guardianEventPath"] = "guardian.events";
            }
            if (GuardianProcessName is not null)
            {
                profile["guardianProcessName"] = GuardianProcessName;
            }
            File.WriteAllText(_configPath, new JObject { ["profiles"] = new JArray(profile) }.ToString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
