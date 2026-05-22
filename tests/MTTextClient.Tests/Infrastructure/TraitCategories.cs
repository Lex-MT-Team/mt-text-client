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
    /// different exchanges.  Gated by MTC_TESTING_ENV=1 plus the
    /// bench-port availability probe in BenchFixture (any profile whose
    /// port is unbound is skipped).
    /// </summary>
    public const string BenchAll = "BenchAll";
}
