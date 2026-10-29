using System;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>Gated checks for the read-only investigation collection surface.</summary>
[Collection(BenchCollection.Name)]
public sealed class DiagnosticInvestigationToolsTests
{
    private const string DiagnosticPortVariable = "MTC_DIAGNOSTIC_PORT";
    private readonly McpFixture _mcp;

    public DiagnosticInvestigationToolsTests(McpFixture mcp)
    {
        _mcp = mcp;
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async System.Threading.Tasks.Task InvestigationCollect_UsesOnlyAnExplicitConfiguredCompanion()
    {
        int port = RequireDiagnosticPort();

        var response = await _mcp.CallTool("mt_diag_investigation_collect", new
        {
            profile = EnvFlags.DefaultBenchProfile,
            diag_port = port,
            deadline_ms = 15000,
            hash = true
        });

        response.InnerSuccess.Should().BeTrue();
    }

    private static int RequireDiagnosticPort()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");
        string? configured = Environment.GetEnvironmentVariable(DiagnosticPortVariable);
        Skip.If(!int.TryParse(configured, out int port) || port is < 1 or > 65535,
            $"{DiagnosticPortVariable} must name an explicitly configured companion port.");
        return port;
    }
}
