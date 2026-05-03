using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
namespace MTTextClient.Core;

/// <summary>
/// Loads and saves server profiles from a JSON file.
/// Simplified alternative to MTController's encrypted KV format.
///
/// Security note (EN review #2 short-term mitigation):
///   profiles.json contains plaintext ClientToken material that is
///   used to derive the per-connection session key. The file MUST be
///   readable only by the owner. This module:
///     * creates the parent directory with mode 0700,
///     * writes the file atomically (temp + rename) with mode 0600,
///     * warns at load time if the file mode is wider than 0600 on
///       POSIX systems.
///   On Windows the mode bits don't apply; ACLs are inherited from
///   the user-profile directory.
/// </summary>
public static class ProfileManager
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "mt-textclient");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

    private const UnixFileMode FileMode0600 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode DirMode0700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode LooseMask =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static List<ServerProfile> LoadProfiles()
    {
        if (!File.Exists(ProfilePath))
        {
            return new List<ServerProfile>();
        }

        WarnIfWorldReadable(ProfilePath);

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
            EnsureProfileDir();
            string? json = JsonConvert.SerializeObject(profiles, Formatting.Indented);

            // Atomic write: write to a sibling temp file (created with
            // mode 0600), fsync-equivalent close, then rename over the
            // real path. Prevents readers from observing a half-written
            // file and prevents a window where the file exists with
            // default (umask-driven, often 0644) permissions.
            string tmpPath = ProfilePath + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var fs = new FileStream(
                        tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 4096, options: FileOptions.WriteThrough);
                    File.SetUnixFileMode(tmpPath, FileMode0600);
                    using var sw = new StreamWriter(fs);
                    sw.Write(json);
                }
                else
                {
                    File.WriteAllText(tmpPath, json);
                }

                File.Move(tmpPath, ProfilePath, overwrite: true);

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // After Move the inode may be the original one — re-stamp.
                    try { File.SetUnixFileMode(ProfilePath, FileMode0600); }
                    catch { /* best-effort; file system may not support it */ }
                }
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* ignore */ }
                }
            }
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

    private static void EnsureProfileDir()
    {
        Directory.CreateDirectory(ProfileDir);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(ProfileDir, DirMode0700); }
            catch { /* best-effort */ }
        }
    }

    private static void WarnIfWorldReadable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & LooseMask) != 0)
            {
                Console.Error.WriteLine(
                    "[WARN] " + path + " has loose permissions (mode=" +
                    Convert.ToString((int)mode, 8) +
                    "); profiles.json contains client tokens. " +
                    "Tightening to 0600. Run: chmod 600 \"" + path + "\"");
                try { File.SetUnixFileMode(path, FileMode0600); }
                catch { /* best-effort */ }
            }
        }
        catch
        {
            /* file may have been deleted between Exists and stat — ignore */
        }
    }
}
