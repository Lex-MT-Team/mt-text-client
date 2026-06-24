using FluentAssertions;
using MTShared.Network;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Unit coverage for CoreStatusStore license mapping, including the 0.7.24554
/// Risk Limit license fields and the build-version handshake guard.
/// </summary>
public sealed class CoreStatusStoreUnitTests
{
    private static CoreStatusData InitialStatus(string build) => new()
    {
        isInitialUpdate = true,
        licenseID = 42,
        licenseName = "bench",
        buildVersion = build,
        licenseUserManualOrderLimits = new System.Collections.Generic.List<int> { 5, 10 },
        licenseUserAlgoOrderLimits = new System.Collections.Generic.List<int> { 3 },
        licenseUserBalanceLimitInfo = new BalanceLimitInfo { percentLimit = 25, fixedLimit = 1000, asset = "usdt" },
        exchangeUID = "uid-123",
    };

    [Fact]
    [Trait("Category", "Unit")]
    public void License_maps_risk_limit_fields()
    {
        var store = new CoreStatusStore();
        store.ProcessData(NetworkMessageType.CORE_STATUS_RESULT, InitialStatus(CoreStatusStore.ExpectedCoreBuild));

        var lic = store.GetLicense();
        lic.Should().NotBeNull();
        lic!.ManualOrderLimits.Should().Equal(5, 10);
        lic.AlgoOrderLimits.Should().Equal(3);
        lic.BalanceLimitPercent.Should().Be(25);
        lic.BalanceLimitFixed.Should().Be(1000);
        lic.BalanceLimitAsset.Should().Be("usdt");
        lic.ExchangeUID.Should().Be("uid-123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Build_version_handshake_matches_expected_and_flags_skew()
    {
        var ok = new CoreStatusStore();
        ok.ProcessData(NetworkMessageType.CORE_STATUS_RESULT, InitialStatus(CoreStatusStore.ExpectedCoreBuild));
        ok.BuildVersionMatches.Should().BeTrue();

        var skew = new CoreStatusStore();
        skew.ProcessData(NetworkMessageType.CORE_STATUS_RESULT, InitialStatus("0.7.23902"));
        skew.BuildVersionMatches.Should().BeFalse();
    }
}
