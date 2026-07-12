using System.Reflection;
using FluentAssertions;
using MTTextClient.MCP;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// The concurrency-gate classifier decides how each request is serialized:
/// per-profile requests run in parallel across profiles but serialize within a
/// profile; fleet / profile-less connection tools are exclusive; in-process
/// tools are ungated. This pins that routing.
/// </summary>
public sealed class DispatchGateUnitTests
{
    // ResolveGateScope((JObject)) -> (GateKind, string?) is private static; call
    // it by reflection and read the returned tuple's kind (as a string) + key.
    private static (string Kind, string? Key) Classify(string json)
    {
        MethodInfo m = typeof(McpServer).GetMethod(
            "ResolveGateScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = m.Invoke(null, new object[] { JObject.Parse(json) })!;
        var t = result.GetType();
        string kind = t.GetField("Item1")!.GetValue(result)!.ToString()!;
        string? key = (string?)t.GetField("Item2")!.GetValue(result);
        return (kind, key);
    }

    private static string Call(string tool, string? profile) =>
        profile == null
            ? $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{{}}}}}}"
            : $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{{\"profile\":\"{profile}\"}}}}}}";

    [Fact]
    [Trait("Category", "Unit")]
    public void Profile_scoped_tool_gates_on_its_profile()
    {
        var (kind, key) = Classify(Call("mt_algos_list", "benchProfile"));
        kind.Should().Be("Profile");
        key.Should().Be("benchProfile", because: "different profiles must be able to run in parallel, keyed by name");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Fleet_tool_is_exclusive()
    {
        // Fleet fans out over every connection → exclusive against per-profile ops.
        Classify(Call("mt_fleet_balances", null)).Kind.Should().Be("Fleet");
        // A profile-less connection tool is conservatively exclusive too.
        Classify(Call("mt_algos_list_all", null)).Kind.Should().Be("Fleet");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void In_process_tools_and_non_tool_methods_are_ungated()
    {
        Classify(Call("mt_events_poll", null)).Kind.Should().Be("Ungated");
        Classify(Call("mt_metrics_get", null)).Kind.Should().Be("Ungated");
        Classify("{\"method\":\"tools/list\"}").Kind.Should().Be("Ungated");
        Classify("{\"method\":\"initialize\",\"id\":1}").Kind.Should().Be("Ungated");
    }
}
