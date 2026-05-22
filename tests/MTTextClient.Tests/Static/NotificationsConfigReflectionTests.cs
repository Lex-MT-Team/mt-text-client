using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Static;

/// <summary>
/// Parity check that the typed notifications-config reflector returns every
/// MTShared NotificationGroupType / NotificationTarget /
/// SwitchableNotificationDescriptor.  Runs in-process; no subprocess required.
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.Static)]
public sealed class NotificationsConfigReflectionTests
{
    private readonly McpFixture _mcp;
    public NotificationsConfigReflectionTests(McpFixture mcp) => _mcp = mcp;

    [Fact]
    public void Catalog_Matches_MtShared_Reflection_Counts()
    {
        var asm = Assembly.LoadFrom(RepoPaths.MTSharedBuilt);
        var groupType  = asm.GetType("MTShared.Types.NotificationGroupType", throwOnError: true)!;
        // NotificationTarget was removed in MTCore 0.7.23902 — vendor consolidated
        // notification targets into the SwitchableNotificationDescriptor defaults
        // (CLIENT_NOTIFICATIONS_enabled, CLIENT_LOG_enabled, TELEGRAM_enabled).
        // The reflector returns count=0 gracefully when the type is absent.
        var targetType = asm.GetType("MTShared.Types.NotificationTarget", throwOnError: false);
        var descType   = asm.GetType("MTShared.Types.SwitchableNotificationDescriptor", throwOnError: true)!;

        int vendorGroupCount  = Enum.GetValues(groupType).Length;
        int vendorTargetCount = targetType != null ? Enum.GetValues(targetType).Length : 0;
        int vendorDescriptorCount = descType.GetFields(BindingFlags.Public | BindingFlags.Static)
                                            .Count(f => f.FieldType == descType);

        var catalog = MTTextClient.Core.NotificationConfigReflector.CapabilitiesCatalog();

        ((int)catalog["groups"]!["count"]!).Should().Be(vendorGroupCount,
            because: "all NotificationGroupType values must be enumerated");
        ((int)catalog["targets"]!["count"]!).Should().Be(vendorTargetCount,
            because: "all NotificationTarget values must be enumerated (or 0 if the type is absent on this MTShared build)");
        ((int)catalog["descriptors"]!["count"]!).Should().Be(vendorDescriptorCount,
            because: "every static SwitchableNotificationDescriptor field must be reflected");
    }

    [Fact]
    public void Capabilities_Reports_Mutation_Not_Wired_Honestly()
    {
        var catalog = MTTextClient.Core.NotificationConfigReflector.CapabilitiesCatalog();
        ((bool)catalog["mutation_supported"]!).Should().BeFalse(
            because: "the editor is not currently wired through CoreConnection on this build");
        ((string)catalog["mutation_notice"]!)!
            .Should().Contain("notifications_config_mutation_not_wired");
    }

    [Fact]
    public void Descriptors_Include_Per_Target_Default_Booleans()
    {
        var catalog = MTTextClient.Core.NotificationConfigReflector.CapabilitiesCatalog();
        foreach (var d in catalog["descriptors"]!["items"]!)
        {
            d["defaults"].Should().NotBeNull();
            d["defaults"]!["CLIENT_NOTIFICATIONS_enabled"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.Boolean);
            d["defaults"]!["CLIENT_LOG_enabled"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.Boolean);
            d["defaults"]!["TELEGRAM_enabled"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.Boolean);
            d["enum_field"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.String);
            d["group_type"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.String);
        }
    }
}
