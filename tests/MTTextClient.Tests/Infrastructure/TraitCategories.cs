namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// String constants for <c>[Trait("Category", ...)]</c> filtering.
/// Keep in sync with <c>.github/workflows/*.yml</c> filter expressions.
/// </summary>
public static class TraitCategories
{
    /// <summary>Source-only assertions. No subprocess, no MTCore. Runs in PR-gate CI.</summary>
    public const string Static = "Static";

    /// <summary>In-process unit tests (no subprocess). Runs in PR-gate CI.</summary>
    public const string Unit = "Unit";

    /// <summary>End-to-end MCP calls against a real local MTCore. Requires MTC_TESTING_ENV=1.</summary>
    public const string Smoke = "Smoke";

    /// <summary>Smoke tests that also place real orders. Requires MTC_TESTING_ENV=1 AND MTC_LIVE_TRADES=1.</summary>
    public const string LiveTrade = "LiveTrade";

    /// <summary>
    /// Integration tests that exercise ALL configured bench profiles
    /// (bench_01–bench_04) end-to-end against real MTCore instances on
    /// different exchanges. Stage 0.4 addition. Gated by
    /// MTC_TESTING_ENV=1 plus the bench-port availability probe in
    /// BenchFixture (any profile whose port is unbound is skipped).
    /// </summary>
    public const string BenchAll = "BenchAll";
}

/// <summary>
/// Trait keys used alongside <see cref="TraitCategories"/> to mark known-broken tools.
/// </summary>
public static class KnownIssue
{
    public const string TraitKey = "KnownIssue";

    public const string McpRetained003 = "MCP-003"; // timeout cluster
    public const string McpRetained005 = "MCP-005"; // import_templates fixed-path
    public const string McpRetained006 = "MCP-006"; // vault auth
    public const string McpRetained009 = "MCP-009"; // core_restart Rosetta crash
    public const string McpRetained010Ext = "MCP-010-ext";          // profile_settings_update missing confirm
    public const string McpRetained010SettingsSet = "MCP-010-set";  // mt_settings_set: parser requires --confirm but schema does not
}
