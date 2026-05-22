namespace MTTextClient.Tests.Infrastructure;

/// <summary>
/// Environment variable gates that decide whether Smoke / LiveTrade tests
/// run.  The PR-gate CI workflow leaves these unset, so Smoke / LiveTrade
/// tests are silently skipped.  The testing-environment workflow sets them
/// explicitly.
/// </summary>
public static class EnvFlags
{
    public const string TestingEnvVar = "MTC_TESTING_ENV";
    public const string LiveTradesVar = "MTC_LIVE_TRADES";

    /// <summary>True if MTCore is expected to be reachable on the bench ports.</summary>
    public static bool TestingEnv => Environment.GetEnvironmentVariable(TestingEnvVar) == "1";

    /// <summary>True if real-trade tests are explicitly authorised.</summary>
    public static bool LiveTrades => Environment.GetEnvironmentVariable(LiveTradesVar) == "1";

    /// <summary>The bench profile name Smoke tests target by default.</summary>
    public const string DefaultBenchProfile = "bench_01";

    /// <summary>The MTCore process running this profile is expected to listen here.</summary>
    public const int DefaultBenchPort = 4242;

    /// <summary>
    /// Single source of truth for the four configured bench profiles.
    /// Each tuple is (mt-text-client-side profile name, UDP port,
    /// exchange label).  Matches the configuration in
    /// <c>~/.config/mt-textclient/profiles.json</c> and the bench's
    /// configured UDP port.
    /// </summary>
    public static readonly (string Profile, int Port, string Exchange)[] AllBenches = new[]
    {
        ("bench_01", 4242, "BYBIT"),
        ("bench_02", 4243, "BINANCE"),
        ("bench_03", 4244, "HYPERLIQUID"),
        ("bench_04", 4245, "OKX"),
    };
}
