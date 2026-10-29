using System.Linq;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.MCP;
using MTTextClient.Tests.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Locks the public diagnostic MCP contract to the command-family lowering.
/// These tests use only the registry and mapper so they never need a profile,
/// a diagnostic endpoint, or an MTCore connection.
/// </summary>
[Trait("Category", TraitCategories.Unit)]
public sealed class DiagnosticMcpParityUnitTests
{
    [Fact]
    public void FleetTool_LowersOnlyExplicitBoundedNativeInputs()
    {
        McpServer.MapToolToCommand("mt_diag_fleet_collect", new JObject
        {
            ["profiles"] = new JArray("diag_alpha", "diag_beta"),
            ["operation"] = "health",
            ["concurrency"] = 16,
            ["deadline_ms"] = 60000,
            ["retry_once"] = true
        }).Should().Be("diag fleet collect --profiles diag_alpha,diag_beta --operation health --concurrency 16 --deadline-ms 60000 --retry-once");
    }

    [Fact]
    public void FleetTool_RejectsImplicitAllInvalidNamesAndUnboundedInputs()
    {
        McpServer.MapToolToCommand("mt_diag_fleet_collect", new JObject { ["operation"] = "health" }).Should().BeNull();
        McpServer.MapToolToCommand("mt_diag_fleet_collect", new JObject { ["profiles"] = new JArray("safe", "bad @other"), ["operation"] = "health" }).Should().BeNull();
        McpServer.MapToolToCommand("mt_diag_fleet_collect", new JObject { ["profiles"] = new JArray("safe"), ["operation"] = "health", ["concurrency"] = 17 }).Should().BeNull();
        McpServer.MapToolToCommand("mt_diag_fleet_collect", new JObject { ["profiles"] = new JArray("safe"), ["operation"] = "health", ["deadline_ms"] = 60001 }).Should().BeNull();
    }

    [Fact]
    public void InvestigationTools_LowerToCanonicalBoundedCommands()
    {
        McpServer.MapToolToCommand("mt_diag_investigation_start", new JObject
        {
            ["profile"] = "diag_alpha",
            ["ttl_seconds"] = 30,
            ["event_types"] = new JArray("connection_error")
        }).Should().Be("diag investigation start diag_alpha --ttl-seconds 30 --event-types connection_error");

        McpServer.MapToolToCommand("mt_diag_investigation_collect", new JObject
        {
            ["profile"] = "diag_alpha",
            ["deadline_ms"] = 15000,
            ["hash"] = true
        }).Should().Be("diag investigation collect diag_alpha --deadline-ms 15000 --hash");

        McpServer.MapToolToCommand("mt_diag_investigation_export", new JObject { ["session_id"] = "0123456789abcdef0123456789abcdef" })
            .Should().Be("diag investigation export 0123456789abcdef0123456789abcdef");
        McpServer.MapToolToCommand("mt_diag_investigation_stop", new JObject { ["session_id"] = "0123456789abcdef0123456789abcdef" })
            .Should().Be("diag investigation stop 0123456789abcdef0123456789abcdef");
    }

    [Theory]
    [InlineData("mt_diag_capabilities", "capabilities", false)]
    [InlineData("mt_diag_health", "health", false)]
    [InlineData("mt_diag_guardian_status", "guardian-status", false)]
    [InlineData("mt_diag_guardian_events", "guardian-events", true)]
    public void DiagnosticTools_LowerToCanonicalReadOnlyCommand(string toolName, string operation, bool requiresLines)
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = 4545,
            ["timeout_ms"] = 1000,
        };
        if (requiresLines) arguments["lines"] = 25;

        string? command = McpServer.MapToolToCommand(toolName, arguments);

        command.Should().Be($"diag {operation} diag_alpha --diag-port 4545 --timeout-ms 1000" + (requiresLines ? " --lines 25" : ""));
    }

    [Fact]
    public void Logs_LowersWithBoundedOptionalArguments()
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = 4545,
            ["timeout_ms"] = 1000,
            ["lines"] = 100,
            ["grep"] = "native packet",
        };

        string? command = McpServer.MapToolToCommand("mt_diag_logs", arguments);

        command.Should().Be("diag logs diag_alpha --diag-port 4545 --timeout-ms 1000 --lines 100 --grep \"native packet\"");
    }

    [Theory]
    [InlineData("diag.alpha", 4545, "diag capabilities diag.alpha --diag-port 4545")]
    [InlineData("diag_alpha@other", 4545, null)]
    [InlineData("diag_alpha\r\norders close-all", 4545, null)]
    [InlineData("diag_alpha", 0, null)]
    [InlineData("diag_alpha", 65536, null)]
    public void DiagnosticTools_RejectInvalidPortsAndSanitiseProfile(string profile, int diagPort, string? expected)
    {
        var arguments = new JObject
        {
            ["profile"] = profile,
            ["diag_port"] = diagPort,
        };

        string? command = McpServer.MapToolToCommand("mt_diag_capabilities", arguments);

        command.Should().Be(expected);
    }

    [Fact]
    public void DiagnosticTools_PreservesTagRouteWhenNoPortOverrideIsProvided()
    {
        var arguments = new JObject { ["profile"] = "diag_alpha" };

        McpServer.MapToolToCommand("mt_diag_capabilities", arguments)
            .Should().Be("diag capabilities diag_alpha");
    }

    [Fact]
    public void DiagnosticTools_RejectsNonStringProfile()
    {
        var arguments = new JObject
        {
            ["profile"] = new JArray("diag_alpha"),
            ["diag_port"] = 4545,
        };

        McpServer.MapToolToCommand("mt_diag_capabilities", arguments).Should().BeNull();
    }

    [Fact]
    public void DiagnosticTools_RejectsOversizedIntegerWithoutThrowing()
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = new JValue(System.Numerics.BigInteger.Parse("999999999999999999999999999999999999")),
        };

        var action = () => McpServer.MapToolToCommand("mt_diag_capabilities", arguments);

        action.Should().NotThrow();
        action().Should().BeNull();
    }

    [Theory]
    [InlineData("diag_port", "4545 @other")]
    [InlineData("timeout_ms", "1000 @other")]
    public void DiagnosticTools_RejectNumericStringInjection(string argumentName, string injectedValue)
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = 4545,
        };
        arguments[argumentName] = injectedValue;

        McpServer.MapToolToCommand("mt_diag_capabilities", arguments).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Logs_RejectsOutOfRangeLineCount(int lines)
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = 4545,
            ["lines"] = lines,
        };

        McpServer.MapToolToCommand("mt_diag_logs", arguments).Should().BeNull();
    }

    [Fact]
    public void Logs_RejectsNumericStringLineInjection()
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["diag_port"] = 4545,
            ["lines"] = "25 @other",
        };

        McpServer.MapToolToCommand("mt_diag_logs", arguments).Should().BeNull();
    }

    [Theory]
    [InlineData("quote\"injection")]
    [InlineData("line\nbreak")]
    public void Logs_RejectsGrepThatCannotBeQuotedSafely(string grep)
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["lines"] = 25,
            ["grep"] = grep,
        };

        McpServer.MapToolToCommand("mt_diag_logs", arguments).Should().BeNull();
    }

    [Fact]
    public void Logs_RejectsGrepBeyondUtf8Limit()
    {
        var arguments = new JObject
        {
            ["profile"] = "diag_alpha",
            ["lines"] = 25,
            ["grep"] = new string('x', 513),
        };

        McpServer.MapToolToCommand("mt_diag_logs", arguments).Should().BeNull();
    }

    [Fact]
    public void DiagnosticCatalog_RequiresOnlyBoundedReadOnlyInputs()
    {
        var tools = ToolRegistry.AllTools()
            .Where(tool => tool["name"]?.Value<string>()?.StartsWith("mt_diag_", System.StringComparison.Ordinal) == true)
            .ToDictionary(tool => tool["name"]!.Value<string>()!, tool => tool["inputSchema"]!);

        tools.Keys.Should().BeEquivalentTo(new[]
        {
            "mt_diag_capabilities",
            "mt_diag_health",
            "mt_diag_logs",
            "mt_diag_guardian_status",
            "mt_diag_guardian_events",
            "mt_diag_fleet_collect",
            "mt_diag_history_page",
            "mt_diag_history_close",
            "mt_diag_history_fleet",
            "mt_diag_investigation_start",
            "mt_diag_investigation_collect",
            "mt_diag_investigation_export",
            "mt_diag_investigation_stop",
        });

        foreach ((string _, JToken schema) in tools.Where(pair => !pair.Key.StartsWith("mt_diag_investigation_", System.StringComparison.Ordinal) && pair.Key != "mt_diag_fleet_collect" && pair.Key != "mt_diag_history_fleet"))
        {
            var required = ((JArray)schema["required"]!).Values<string>();
            required.Should().Contain("profile");
            required.Should().NotContain("diag_port");

            var properties = (JObject)schema["properties"]!;
            properties.Properties().Select(property => property.Name).Should().NotContain(new[]
            {
                "host", "path", "command", "request", "control", "confirm",
            });
            properties["diag_port"]!["minimum"]!.Value<int>().Should().Be(1);
            properties["diag_port"]!["maximum"]!.Value<int>().Should().Be(65535);
        }

        ((JObject)tools["mt_diag_logs"]["properties"]!)["lines"]!["minimum"]!.Value<int>().Should().Be(1);
        ((JObject)tools["mt_diag_logs"]["properties"]!)["lines"]!["maximum"]!.Value<int>().Should().Be(500);
        ((JArray)tools["mt_diag_fleet_collect"]["required"]!).Values<string>().Should().BeEquivalentTo(new[] { "profiles", "operation" });
        ((JObject)tools["mt_diag_fleet_collect"]["properties"]!)["concurrency"]!["maximum"]!.Value<int>().Should().Be(16);
        ((JObject)tools["mt_diag_fleet_collect"]["properties"]!)["deadline_ms"]!["maximum"]!.Value<int>().Should().Be(60000);
        ((JArray)tools["mt_diag_investigation_start"]["required"]!).Values<string>().Should().BeEquivalentTo(new[] { "profile", "ttl_seconds", "event_types" });
        ((JArray)tools["mt_diag_investigation_collect"]["required"]!).Values<string>().Should().BeEquivalentTo(new[] { "profile" });
        ((JArray)tools["mt_diag_investigation_export"]["required"]!).Values<string>().Should().BeEquivalentTo(new[] { "session_id" });
        ((JArray)tools["mt_diag_investigation_stop"]["required"]!).Values<string>().Should().BeEquivalentTo(new[] { "session_id" });
    }
}
