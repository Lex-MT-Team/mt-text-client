using System;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Read-only companion diagnostic checks. They run only when a caller
/// explicitly supplies both the normal test-environment gate and a companion
/// port; they never connect to MTCore as a fallback.
/// </summary>
[Collection(BenchCollection.Name)]
public sealed class DiagnosticToolsTests
{
    private const string DiagnosticPortVariable = "MTC_DIAGNOSTIC_PORT";
    private readonly McpFixture _mcp;

    public DiagnosticToolsTests(McpFixture mcp)
    {
        _mcp = mcp;
    }

    [SkippableTheory]
    [Trait("Category", TraitCategories.Smoke)]
    [InlineData("mt_diag_capabilities", false)]
    [InlineData("mt_diag_health", false)]
    [InlineData("mt_diag_logs", true)]
    [InlineData("mt_diag_guardian_status", false)]
    [InlineData("mt_diag_guardian_events", true)]
    public async System.Threading.Tasks.Task DiagnosticTools_UseOnlyAnExplicitCompanionEndpoint(string toolName, bool requiresLines)
    {
        int diagnosticPort = RequireDiagnosticPort();
        object arguments = requiresLines
            ? new { profile = EnvFlags.DefaultBenchProfile, diag_port = diagnosticPort, timeout_ms = 5000, lines = 25 }
            : new { profile = EnvFlags.DefaultBenchProfile, diag_port = diagnosticPort, timeout_ms = 5000 };

        var response = await _mcp.CallTool(toolName, arguments);

        response.InnerSuccess.Should().BeTrue(
            because: "the explicitly configured diagnostic companion must serve the selected read-only operation");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async System.Threading.Tasks.Task DiagnosticFleetTool_UsesOneExplicitProfileAndCompanionEndpoint()
    {
        int diagnosticPort = RequireDiagnosticPort();

        var response = await _mcp.CallTool("mt_diag_fleet_collect", new
        {
            profiles = new[] { EnvFlags.DefaultBenchProfile },
            operation = "health",
            diag_port = diagnosticPort,
            concurrency = 1,
            deadline_ms = 5000,
            retry_once = false
        });

        response.InnerSuccess.Should().BeTrue(
            because: "fleet collection requires an explicit one-profile allowlist and an explicit companion endpoint");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async System.Threading.Tasks.Task HistoryTools_PageFleetAndCloseUseExplicitCompanion()
    {
        int port = RequireDiagnosticPort();
        await CheckHistoryTools(_mcp, EnvFlags.DefaultBenchProfile, port);
    }

    internal static async System.Threading.Tasks.Task CheckHistoryTools(McpFixture mcp, string profile, int port)
    {
        string since = DateTimeOffset.UtcNow.AddHours(-48).ToString("O"), until = DateTimeOffset.UtcNow.ToString("O"), queryId = Guid.NewGuid().ToString("N");
        var response = await mcp.CallTool("mt_diag_history_page", new { profile, source = "logs", since, until, query_id = queryId, page_lines = 2, diag_port = port });
        response.InnerSuccess.Should().BeTrue();
        string cursor = response.ParsedBody!.Value.GetProperty("data").GetProperty("data").GetProperty("cursor").GetString()!;
        try
        {
            var fleet = await mcp.CallTool("mt_diag_history_fleet", new { requests = new[] { new { profile, source = "logs", since, until, query_id = queryId, page_lines = 2, cursor, sequence = 0 } }, diag_port = port, concurrency = 1, deadline_ms = 5000 });
            fleet.InnerSuccess.Should().BeTrue("the same first page must be replayable through fleet collection");
        }
        finally
        {
            var closed = await mcp.CallTool("mt_diag_history_close", new { profile, source = "logs", since, until, query_id = queryId, page_lines = 2, cursor, sequence = 0, diag_port = port });
            closed.InnerSuccess.Should().BeTrue();
        }
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
