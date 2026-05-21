using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
namespace MTTextClient.Core;

/// <summary>
/// Persists the set of known folder names independently of which profiles
/// reference them. Stored at <c>~/.config/mt-textclient/folders.json</c>;
/// sibling of <see cref="ProfileManager"/>'s <c>profiles.json</c>.
///
/// Folders are a pure client-side concept used for display / dispatch grouping;
/// MTCore is unaware of them. Empty Folder on a profile = root (unfoldered).
/// </summary>
public static class FolderStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "mt-textclient");

    private static readonly string FoldersPath = Path.Combine(Dir, "folders.json");

    public static List<string> LoadFolders()
    {
        if (!File.Exists(FoldersPath)) return new List<string>();
        try
        {
            string text = File.ReadAllText(FoldersPath);
            return JsonConvert.DeserializeObject<List<string>>(text) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void SaveFolders(List<string> folders)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FoldersPath, JsonConvert.SerializeObject(folders, Formatting.Indented));
    }

    public static string FilePath => FoldersPath;
}
