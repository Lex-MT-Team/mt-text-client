using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
namespace MTTextClient.Core;

/// <summary>
/// Loads and saves server profiles from a JSON file.
/// Simplified alternative to MTController's encrypted KV format.
/// </summary>
public static class ProfileManager
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "mt-textclient");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

    public static List<ServerProfile> LoadProfiles()
    {
        if (!File.Exists(ProfilePath))
        {
            return new List<ServerProfile>();
        }

        try
        {
            string? json = File.ReadAllText(ProfilePath);
            return JsonConvert.DeserializeObject<List<ServerProfile>>(json) ?? new List<ServerProfile>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to load profiles: {ex.Message}");
            return new List<ServerProfile>();
        }
    }

    public static void SaveProfiles(List<ServerProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(ProfileDir);
            string? json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
            File.WriteAllText(ProfilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to save profiles: {ex.Message}");
        }
    }

    public static ServerProfile? FindProfile(List<ServerProfile> profiles, string name)
    {
        for (int i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return profiles[i];
            }
        }
        return null;
    }
}
