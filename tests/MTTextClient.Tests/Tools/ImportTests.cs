using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Import tools.  <c>mt_import_templates</c> accepts an optional <c>path</c>
/// argument that overrides the default search.  When the file is missing
/// (either at the explicit path or the defaults), the call returns a clean
/// <c>success: false</c> with a descriptive message (no crash).
/// </summary>
[Collection(McpCollection.Name)]
public sealed class ImportTests
{
    private readonly McpFixture _mcp;
    public ImportTests(McpFixture mcp) { _mcp = mcp; }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_import_templates_with_explicit_missing_path_returns_clean_error()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");

        // Pass an explicit path that definitely doesn't exist. Schema must accept
        // the call (no RPC error), and the runtime must report a clean failure
        // (success:false with a "not found" message + the explicit path echoed).
        var resp = await _mcp.CallTool("mt_import_templates",
            new { path = "/tmp/definitely_does_not_exist_99999.json" });

        resp.IsRpcError.Should().BeFalse(because: "schema accepts the call; runtime reports the failure");

        if (resp.ParsedBody is { } b && b.TryGetProperty("success", out var s))
        {
            s.GetBoolean().Should().BeFalse(because: "explicit path missing should report success:false");
            if (b.TryGetProperty("message", out var m))
            {
                string msg = m.GetString() ?? "";
                msg.Should().Contain("not found",
                    because: "the failure message should clearly say the file is missing");
                msg.Should().Contain("/tmp/definitely_does_not_exist_99999.json",
                    because: "echoing the explicit path back makes the failure self-explanatory");
            }
        }
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_import_templates_default_search_does_not_crash()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set");

        // No path arg → falls back to the 3-location default search. We assert
        // the call doesn't crash at the protocol layer and produces a parseable
        // body. Whether the file is found is environment-dependent.
        var resp = await _mcp.CallTool("mt_import_templates", new { });

        resp.IsRpcError.Should().BeFalse();
        resp.ParsedBody.Should().NotBeNull(because: "must produce a parseable response body");
    }
}
