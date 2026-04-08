using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for server profile settings received from Core.
/// Profile settings are key-value string pairs that control Core behavior.
/// 
/// Access pattern: Request → Core responds → store cached.
/// Settings are NOT subscribed (they're on-demand via GET_PROFILE_REQUEST).
/// </summary>
public sealed class ProfileSettingsStore
{
    private readonly ConcurrentDictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);
    private string _profileName = "";
    private DateTime _lastUpdate;
    private bool _hasData;

    /// <summary>Whether we have received settings from Core.</summary>
    public bool HasData => _hasData;

    /// <summary>The profile name these settings belong to.</summary>
    public string ProfileName => _profileName;

    /// <summary>When settings were last received.</summary>
    public DateTime LastUpdate => _lastUpdate;

    /// <summary>Number of settings.</summary>
    public int Count => _settings.Count;

    /// <summary>Event fired when settings are updated.</summary>
    public event Action? OnSettingsUpdated;

    /// <summary>
    /// Update the store with settings received from Core.
    /// </summary>
    public void Update(string profileName, IReadOnlyDictionary<string, string>? settings)
    {
        _profileName = profileName;
        _settings.Clear();

        if (settings != null)
        {
            foreach (KeyValuePair<string, string> kv in settings)
            {
                _settings[kv.Key] = kv.Value;
            }
        }

        _lastUpdate = DateTime.UtcNow;
        _hasData = true;
        OnSettingsUpdated?.Invoke();
    }

    /// <summary>Get a specific setting value.</summary>
    public string? GetValue(string key)
    {
        _settings.TryGetValue(key, out string? value);
        return value;
    }

    /// <summary>Get all settings as a sorted list of key-value pairs.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetAll()
    {
        var list = new List<KeyValuePair<string, string>>(_settings);
        list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Search settings by key or value (case-insensitive).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Search(string query)
    {
        string? q = query.ToLowerInvariant();
        var list = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in _settings)
        {
            if (kv.Key.ToLowerInvariant().Contains(q) ||
                kv.Value.ToLowerInvariant().Contains(q))
            {
                list.Add(kv);
            }
        }
        list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Get settings grouped by prefix (e.g., "algo.", "uds.", "exchange.").</summary>
    public IReadOnlyDictionary<string, List<KeyValuePair<string, string>>> GetGrouped()
    {
        var result = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

        var sortedSettings = new List<KeyValuePair<string, string>>(_settings);
        sortedSettings.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        foreach (KeyValuePair<string, string> kv in sortedSettings)
        {
            int dotIndex = kv.Key.IndexOf('.');
            string prefix = dotIndex > 0 ? kv.Key[..dotIndex] : "general";

            if (!result.TryGetValue(prefix, out List<KeyValuePair<string, string>>? list))
            {
                list = new List<KeyValuePair<string, string>>();
                result[prefix] = list;
            }
            list.Add(kv);
        }

        return result;
    }

    /// <summary>Get a snapshot for display.</summary>
    public ProfileSettingsSnapshot? GetSnapshot()
    {
        if (!_hasData)
        {
            return null;
        }

        return new ProfileSettingsSnapshot
        {
            ProfileName = _profileName,
            SettingCount = _settings.Count,
            LastUpdate = _lastUpdate,
            Settings = GetSettingsEntries()
        };
    }

    private List<SettingEntry> GetSettingsEntries()
    {
        var sorted = new List<KeyValuePair<string, string>>(_settings);
        sorted.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        var entries = new List<SettingEntry>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            entries.Add(new SettingEntry { Key = sorted[i].Key, Value = sorted[i].Value });
        }
        return entries;
    }

    /// <summary>Clear all data (on disconnect).</summary>
    public void Clear()
    {
        _settings.Clear();
        _profileName = "";
        _hasData = false;
    }
}

/// <summary>Snapshot of profile settings for display.</summary>
public sealed class ProfileSettingsSnapshot
{
    public string ProfileName { get; init; } = "";
    public int SettingCount { get; init; }
    public DateTime LastUpdate { get; init; }
    public List<SettingEntry> Settings { get; init; } = new();
}

/// <summary>A single setting key-value pair for display.</summary>
public sealed class SettingEntry
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
}
