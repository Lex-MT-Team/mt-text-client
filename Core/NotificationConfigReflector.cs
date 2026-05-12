namespace MTTextClient.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

/// <summary>
/// Stage 6.2 — typed notification-config introspector.
///
/// MTShared exposes the notification surface across three primary types:
///   * MTShared.Types.NotificationGroupType — broad group (TRADE, SYSTEM, …).
///   * MTShared.Types.NotificationTarget — destination channel (client UI,
///     local log, telegram).
///   * MTShared.Types.SwitchableNotificationDescriptor — the individual,
///     toggleable notification (ORDER_FILLED, MARGIN_INSUFFICIENT, …) with a
///     groupType and a 3-tuple of per-target default-enabled bools.
///
/// MTShared's mutation surface (NotificationSettingsEditor) requires a
/// ProfileManager + per-profile CommonProfileSettings — neither of which is
/// currently exposed via CoreConnection on this build.  Stage 6.2 therefore
/// ships the *typed read* of this catalog (defaults + grouping + per-target
/// flags) and documents the mutation gap honestly in the capabilities tool.
/// </summary>
public static class NotificationConfigReflector
{
    private static readonly Lazy<JObject> _catalog = new(BuildCatalog);

    public static JObject GroupsCatalog() => (JObject)_catalog.Value["groups"]!.DeepClone();
    public static JObject DescriptorsCatalog() => (JObject)_catalog.Value["descriptors"]!.DeepClone();
    public static JObject TargetsCatalog() => (JObject)_catalog.Value["targets"]!.DeepClone();
    public static JObject CapabilitiesCatalog() => (JObject)_catalog.Value.DeepClone();

    private static JObject BuildCatalog()
    {
        var asm = TryFindMtSharedAssembly();
        if (asm == null)
        {
            return new JObject
            {
                ["error"] = "MTShared assembly not found in current AppDomain",
                ["groups"]      = new JObject(),
                ["descriptors"] = new JObject(),
                ["targets"]     = new JObject(),
            };
        }

        var groupType  = asm.GetType("MTShared.Types.NotificationGroupType");
        var targetType = asm.GetType("MTShared.Types.NotificationTarget");
        var descriptorType = asm.GetType("MTShared.Types.SwitchableNotificationDescriptor");

        var groups = new JArray();
        if (groupType != null && groupType.IsEnum)
        {
            var ut = Enum.GetUnderlyingType(groupType);
            foreach (var v in Enum.GetValues(groupType))
                groups.Add(new JObject
                {
                    ["name"]  = v!.ToString(),
                    ["value"] = JToken.FromObject(Convert.ChangeType(v, ut)),
                });
        }

        var targets = new JArray();
        if (targetType != null && targetType.IsEnum)
        {
            var ut = Enum.GetUnderlyingType(targetType);
            foreach (var v in Enum.GetValues(targetType))
                targets.Add(new JObject
                {
                    ["name"]  = v!.ToString(),
                    ["value"] = JToken.FromObject(Convert.ChangeType(v, ut)),
                });
        }

        var descriptors = new JArray();
        if (descriptorType != null)
        {
            // Each static field of type SwitchableNotificationDescriptor is a
            // declared notification.  Read the private 'groupType' and
            // 'defaults' fields + the public Id property + DataType property.
            var groupTypeF = descriptorType.GetField("groupType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var defaultsF  = descriptorType.GetField("defaults",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var idP        = descriptorType.GetProperty("Id");
            var dataTypeP  = descriptorType.GetProperty("DataType");

            foreach (var f in descriptorType.GetFields(BindingFlags.Public | BindingFlags.Static)
                                            .Where(x => x.FieldType == descriptorType)
                                            .OrderBy(x => x.Name))
            {
                var inst   = f.GetValue(null);
                var groupV = groupTypeF?.GetValue(inst);
                var defV   = defaultsF?.GetValue(inst);
                var (showClient, showLog, sendTelegram) = ExtractDefaults(defV);

                descriptors.Add(new JObject
                {
                    ["enum_field"] = f.Name,
                    ["id"]         = idP?.GetValue(inst)?.ToString(),
                    ["group_type"] = groupV?.ToString(),
                    ["data_type"]  = (dataTypeP?.GetValue(inst) as Type)?.FullName,
                    ["defaults"] = new JObject
                    {
                        ["CLIENT_NOTIFICATIONS_enabled"] = showClient,
                        ["CLIENT_LOG_enabled"]           = showLog,
                        ["TELEGRAM_enabled"]             = sendTelegram,
                    },
                });
            }
        }

        return new JObject
        {
            ["groups"]      = new JObject { ["count"] = groups.Count,      ["items"] = groups },
            ["descriptors"] = new JObject { ["count"] = descriptors.Count, ["items"] = descriptors },
            ["targets"]     = new JObject { ["count"] = targets.Count,     ["items"] = targets },
            ["mutation_supported"] = false,
            ["mutation_notice"] =
                "notifications_config_mutation_not_wired: MTShared exposes " +
                "NotificationSettingsEditor (SetUIGroupState, SetUINotificationState, " +
                "SaveChanges, ResetToDefaults) but constructing it requires a " +
                "ProfileManager + per-profile CommonProfileSettings — neither is " +
                "exposed via CoreConnection on this build. Stage 6.2 ships the " +
                "typed read catalog only; mutation is a follow-up workstream.",
        };
    }

    private static (bool client, bool log, bool tg) ExtractDefaults(object? defaults)
    {
        if (defaults == null) return (false, false, false);
        // Tuple<bool, bool, bool>: Item1, Item2, Item3 fields.
        var t = defaults.GetType();
        bool Get(string name) =>
            (t.GetField(name) is { } f && f.GetValue(defaults) is bool b) ? b : false;
        return (Get("Item1"), Get("Item2"), Get("Item3"));
    }

    private static Assembly? TryFindMtSharedAssembly()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "MTShared");
        if (loaded != null) return loaded;

        // Reflection-only fallback: when the calling AppDomain hasn't yet
        // resolved any MTShared type (e.g. in Static unit tests that exercise
        // the reflector in isolation), look for MTShared.dll next to the
        // currently-loaded MTTextClient assembly and load it.
        try
        {
            var cur = typeof(NotificationConfigReflector).Assembly.Location;
            if (!string.IsNullOrEmpty(cur))
            {
                var dir = System.IO.Path.GetDirectoryName(cur)!;
                var candidate = System.IO.Path.Combine(dir, "MTShared.dll");
                if (System.IO.File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
        }
        catch { /* fall through */ }
        return null;
    }
}
