using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Strict, local-only configuration for the optional diagnostic companion.
/// Credential material is intentionally absent: each listener references an
/// existing <c>core.auth</c> file and derives its connection seed transiently.
/// </summary>
public sealed class CompanionConfiguration
{
    private const int MaximumConfigurationBytes = 128 * 1024;
    private const int MaximumProfiles = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private CompanionConfiguration(IReadOnlyList<CompanionProfileConfiguration> profiles)
    {
        Profiles = profiles;
    }

    /// <summary>Gets the explicitly configured listener profiles.</summary>
    public IReadOnlyList<CompanionProfileConfiguration> Profiles { get; }

    /// <summary>Loads and validates a bounded companion configuration file.</summary>
    public static CompanionConfiguration Load(string configurationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configurationPath))
            {
                throw InvalidConfiguration();
            }

            string fullConfigurationPath = Path.GetFullPath(configurationPath);
            string configurationDirectory = Path.GetDirectoryName(fullConfigurationPath) ?? throw InvalidConfiguration();
            byte[] bytes = ReadBoundedFile(fullConfigurationPath, MaximumConfigurationBytes);
            JObject root = ParseObject(StrictUtf8.GetString(bytes));
            AssertAllowedProperties(root, "profiles");
            JArray profilesToken = RequireArray(root, "profiles");
            if (profilesToken.Count is < 1 or > MaximumProfiles)
            {
                throw InvalidConfiguration();
            }

            var profiles = new List<CompanionProfileConfiguration>(profilesToken.Count);
            foreach (JToken token in profilesToken)
            {
                profiles.Add(ParseProfile(token as JObject ?? throw InvalidConfiguration(), configurationDirectory));
            }

            ValidateUniqueBindings(profiles);
            return new CompanionConfiguration(profiles.AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException or JsonException or InvalidOperationException)
        {
            throw InvalidConfiguration();
        }
    }

    private static CompanionProfileConfiguration ParseProfile(JObject profile, string configurationDirectory)
    {
        AssertAllowedProperties(
            profile,
            "name",
            "exchange",
            "corePort",
            "diagnosticPort",
            "coreAuthPath",
            "logRoots",
            "logGlobs",
            "guardianSnapshotPath",
            "guardianEventPath",
            "guardianProcessName", "historyTimeZone", "guardianHistoryRoots", "guardianHistoryGlobs");

        string name = RequireSafeName(profile, "name");
        string exchangeName = RequireSafeName(profile, "exchange");
        if (!Enum.GetNames<ExchangeType>().Any(candidate => string.Equals(candidate, exchangeName, StringComparison.OrdinalIgnoreCase)) || !Enum.TryParse(exchangeName, ignoreCase: true, out ExchangeType exchange))
        {
            throw InvalidConfiguration();
        }

        int corePort = RequirePort(profile, "corePort");
        int diagnosticPort = RequirePort(profile, "diagnosticPort");
        string authPath = ResolveRequiredPath(configurationDirectory, RequireString(profile, "coreAuthPath"));
        if (!string.Equals(Path.GetFileName(authPath), "core.auth", StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }
        ValidateAuthFile(authPath);

        IReadOnlyList<string> roots = RequirePathArray(profile, "logRoots", configurationDirectory, 16);
        IReadOnlyList<string> globs = RequireGlobArray(profile, "logGlobs", 16);
        string? snapshotPath = ResolveOptionalPath(profile, "guardianSnapshotPath", configurationDirectory);
        string? eventPath = ResolveOptionalPath(profile, "guardianEventPath", configurationDirectory);
        if (snapshotPath is not null)
        {
            ValidateNoSymlinkComponents(snapshotPath);
        }
        if (eventPath is not null)
        {
            ValidateNoSymlinkComponents(eventPath);
        }
        string? processName = RequireOptionalSafeName(profile, "guardianProcessName");

        string? zoneName = profile["historyTimeZone"] is null ? null : RequireString(profile, "historyTimeZone");
        TimeZoneInfo? zone = null;
        if (zoneName is not null)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw InvalidConfiguration(); }
        }
        IReadOnlyList<string> historyRoots = profile["guardianHistoryRoots"] is null ? Array.Empty<string>() : RequirePathArray(profile, "guardianHistoryRoots", configurationDirectory, 16);
        IReadOnlyList<string> historyGlobs = profile["guardianHistoryGlobs"] is null ? Array.Empty<string>() : RequireGlobArray(profile, "guardianHistoryGlobs", 16);
        if ((historyRoots.Count == 0) != (historyGlobs.Count == 0)) throw InvalidConfiguration();
        return new CompanionProfileConfiguration(name, exchange, corePort, diagnosticPort, authPath, roots, globs, snapshotPath, eventPath, processName)
        { HistoryTimeZone = zone, GuardianHistoryRoots = historyRoots, GuardianHistoryGlobs = historyGlobs };
    }

    private static void ValidateUniqueBindings(IReadOnlyList<CompanionProfileConfiguration> profiles)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnosticPorts = new HashSet<int>();
        var corePorts = new HashSet<int>(profiles.Select(profile => profile.CorePort));
        foreach (CompanionProfileConfiguration profile in profiles)
        {
            if (!names.Add(profile.Name) || !diagnosticPorts.Add(profile.DiagnosticPort) || corePorts.Contains(profile.DiagnosticPort))
            {
                throw InvalidConfiguration();
            }
        }
    }

    private static JObject ParseObject(string json)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text)
        {
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 16,
            SupportMultipleContent = false
        };
        JToken token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || token is not JObject root)
        {
            throw InvalidConfiguration();
        }
        return root;
    }

    private static void AssertAllowedProperties(JObject source, params string[] allowed)
    {
        var permitted = new HashSet<string>(allowed, StringComparer.Ordinal);
        if (source.Properties().Any(property => !permitted.Contains(property.Name)))
        {
            throw InvalidConfiguration();
        }
    }

    private static string RequireString(JObject source, string propertyName)
    {
        if (source[propertyName]?.Type != JTokenType.String)
        {
            throw InvalidConfiguration();
        }
        string value = source[propertyName]!.Value<string>()!;
        if (string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > 1024)
        {
            throw InvalidConfiguration();
        }
        return value;
    }

    private static string RequireSafeName(JObject source, string propertyName)
    {
        string value = RequireString(source, propertyName);
        return IsSafeName(value) ? value : throw InvalidConfiguration();
    }

    private static string? RequireOptionalSafeName(JObject source, string propertyName)
    {
        if (source[propertyName] is null || source[propertyName]!.Type == JTokenType.Null)
        {
            return null;
        }
        string value = RequireString(source, propertyName);
        return IsSafeName(value) ? value : throw InvalidConfiguration();
    }

    private static bool IsSafeName(string value)
    {
        return value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static int RequirePort(JObject source, string propertyName)
    {
        if (source[propertyName]?.Type != JTokenType.Integer)
        {
            throw InvalidConfiguration();
        }
        try
        {
            long port = source[propertyName]!.Value<long>();
            return port is > 0 and <= 65535 ? (int)port : throw InvalidConfiguration();
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException or ArgumentException)
        {
            throw InvalidConfiguration();
        }
    }

    private static IReadOnlyList<string> RequirePathArray(JObject source, string propertyName, string baseDirectory, int maximumCount)
    {
        JArray values = RequireArray(source, propertyName);
        if (values.Count > maximumCount)
        {
            throw InvalidConfiguration();
        }
        var paths = new List<string>(values.Count);
        foreach (JToken value in values)
        {
            if (value.Type != JTokenType.String)
            {
                throw InvalidConfiguration();
            }
            string resolved = ResolveRequiredPath(baseDirectory, value.Value<string>()!);
            ValidateNoSymlinkComponents(resolved);
            paths.Add(resolved);
        }
        return paths.AsReadOnly();
    }

    private static IReadOnlyList<string> RequireGlobArray(JObject source, string propertyName, int maximumCount)
    {
        JArray values = RequireArray(source, propertyName);
        if (values.Count < 1 || values.Count > maximumCount)
        {
            throw InvalidConfiguration();
        }
        var globs = new List<string>(values.Count);
        foreach (JToken value in values)
        {
            if (value.Type != JTokenType.String)
            {
                throw InvalidConfiguration();
            }
            string glob = value.Value<string>()!;
            if (string.IsNullOrWhiteSpace(glob) || glob.Length > 128 || Path.IsPathRooted(glob) || glob.Contains("..", StringComparison.Ordinal) || glob.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' }) >= 0)
            {
                throw InvalidConfiguration();
            }
            globs.Add(glob);
        }
        return globs.AsReadOnly();
    }

    private static JArray RequireArray(JObject source, string propertyName)
    {
        return source[propertyName] as JArray ?? throw InvalidConfiguration();
    }

    private static string ResolveRequiredPath(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || StrictUtf8.GetByteCount(path) > 1024)
        {
            throw InvalidConfiguration();
        }
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    private static string? ResolveOptionalPath(JObject source, string propertyName, string baseDirectory)
    {
        if (source[propertyName] is null || source[propertyName]!.Type == JTokenType.Null)
        {
            return null;
        }
        return source[propertyName]!.Type == JTokenType.String ? ResolveRequiredPath(baseDirectory, source[propertyName]!.Value<string>()!) : throw InvalidConfiguration();
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, bool requireOwnerOnlyPermissions = false)
    {
        if (CompanionFileRead.HasSymlinkComponent(path) || !CompanionFileAccess.TryOpenRegularFile(path, requireOwnerOnlyPermissions, out FileStream? stream, out CompanionFileMetadata metadata) || stream is null)
        {
            throw InvalidConfiguration();
        }
        using (stream)
        {
            if (metadata.Length > maximumBytes)
            {
                throw InvalidConfiguration();
            }
            var bytes = new byte[checked((int)metadata.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw InvalidConfiguration();
                }
                offset += read;
            }
            return bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? bytes[3..] : bytes;
        }
    }

    private static void ValidateAuthFile(string authPath)
    {
        ValidateNoSymlinkComponents(authPath);
        _ = ReadBoundedFile(authPath, CompanionProfileConfiguration.MaximumAuthBytes, requireOwnerOnlyPermissions: true);
    }

    internal static void ValidateNoSymlinkComponents(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw InvalidConfiguration();
        }
        string[] segments = fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (IsPlatformTemporaryPathAlias(current))
            {
                continue;
            }
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidConfiguration();
            }
        }
    }

    private static bool IsPlatformTemporaryPathAlias(string path)
    {
        string temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string prefix = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return string.Equals(path, temporaryDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || temporaryDirectory.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static InvalidOperationException InvalidConfiguration()
    {
        return new InvalidOperationException("Companion configuration is invalid.");
    }

    internal static byte[] ReadExistingAuthFile(string path)
    {
        return ReadBoundedFile(path, CompanionProfileConfiguration.MaximumAuthBytes, requireOwnerOnlyPermissions: true);
    }
}

internal sealed class CompanionAuthConfigurationException : InvalidOperationException
{
    internal CompanionAuthConfigurationException() : base("Companion auth is invalid.")
    {
    }
}

/// <summary>One explicit companion listener and its local evidence allowlists.</summary>
public sealed class CompanionProfileConfiguration
{
    internal const int MaximumAuthBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal CompanionProfileConfiguration(string name, ExchangeType exchange, int corePort, int diagnosticPort, string coreAuthPath, IReadOnlyList<string> logRoots, IReadOnlyList<string> logGlobs, string? guardianSnapshotPath, string? guardianEventPath, string? guardianProcessName)
    {
        Name = name;
        Exchange = exchange;
        CorePort = corePort;
        DiagnosticPort = diagnosticPort;
        CoreAuthPath = coreAuthPath;
        LogRoots = logRoots;
        LogGlobs = logGlobs;
        GuardianSnapshotPath = guardianSnapshotPath;
        GuardianEventPath = guardianEventPath;
        GuardianProcessName = guardianProcessName;
    }

    /// <summary>Gets the listener's local profile label.</summary>
    public string Name { get; }

    /// <summary>Gets the existing Core exchange used to derive the native seed.</summary>
    public ExchangeType Exchange { get; }

    /// <summary>Gets the configured Core UDP port.</summary>
    public int CorePort { get; }

    /// <summary>Gets the explicit companion listener port.</summary>
    public int DiagnosticPort { get; }

    internal TimeZoneInfo? HistoryTimeZone { get; init; }
    internal IReadOnlyList<string> GuardianHistoryRoots { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> GuardianHistoryGlobs { get; init; } = Array.Empty<string>();
    internal string CoreAuthPath { get; }
    internal IReadOnlyList<string> LogRoots { get; }
    internal IReadOnlyList<string> LogGlobs { get; }
    internal string? GuardianSnapshotPath { get; }
    internal string? GuardianEventPath { get; }
    internal string? GuardianProcessName { get; }

    /// <summary>
    /// Reads one bounded existing auth file and derives a seed without retaining
    /// the decoded token in configuration or a companion credential store.
    /// </summary>
    public string CreateConnectionKeySeed()
    {
        try
        {
            string text = StrictUtf8.GetString(CompanionConfiguration.ReadExistingAuthFile(CoreAuthPath));
            string[] matches = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Where(line => line.StartsWith("CLIENT:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException("Companion auth is invalid.");
            }

            string encoded = matches[0]["CLIENT:".Length..].Trim();
            byte[] decoded = DecodeBase64(encoded);
            if (decoded.Length is < 1 or > 4096)
            {
                throw new InvalidOperationException("Companion auth is invalid.");
            }

            string token = StrictUtf8.GetString(decoded);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Companion auth is invalid.");
            }
            var profile = new ServerProfile { Name = Name, Exchange = Exchange, Port = CorePort, ClientToken = token };
            return profile.GetConnectionKeySeed();
        }
        catch (Exception exception) when (exception is DecoderFallbackException or FormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            throw new CompanionAuthConfigurationException();
        }
    }

    private static byte[] DecodeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsWhiteSpace))
        {
            throw new FormatException();
        }
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int remainder = normalized.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException();
        }
        if (remainder != 0)
        {
            normalized = normalized.PadRight(normalized.Length + 4 - remainder, '=');
        }
        return Convert.FromBase64String(normalized);
    }
}
