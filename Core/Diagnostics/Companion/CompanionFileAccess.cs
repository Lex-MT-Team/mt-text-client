using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Opens configured evidence through a nonblocking, no-follow POSIX descriptor
/// and accepts it only after <c>fstat</c> identifies that same descriptor as a
/// regular file. This prevents a FIFO, device, or socket from turning a bounded
/// diagnostic request into an unbounded filesystem wait.
/// </summary>
internal static class CompanionFileAccess
{
    private const int FileTypeMask = 0xf000;
    private const int RegularFile = 0x8000;
    private const int GroupOrOtherPermissions = 0x003f;
    private const int DarwinModeOffset = 4;
    private const int DarwinModifiedSecondsOffset = 48;
    private const int DarwinLengthOffset = 96;
    private const int LinuxX64ModeOffset = 24;
    private const int LinuxArm64ModeOffset = 16;
    private const int LinuxModifiedSecondsOffset = 88;
    private const int LinuxLengthOffset = 48;
    private const int StatBufferLength = 256;
    private const int DarwinOpenFlags = 0x00000004 | 0x00000100 | 0x01000000;
    private const int LinuxX64OpenFlags = 0x00000800 | 0x00020000 | 0x00080000;
    // Linux arm64 defines O_NOFOLLOW as 1 << 15 (x64 uses 1 << 17).
    // The other two flags are O_NONBLOCK and O_CLOEXEC on both ABIs.
    private const int LinuxArm64OpenFlags = 0x00000800 | 0x00008000 | 0x00080000;

    /// <summary>
    /// Opens one regular file for reading without following its final path
    /// component. Unsupported platforms deliberately fail closed.
    /// </summary>
    internal static bool TryOpenRegularFile(string path, bool requireOwnerOnlyPermissions, out FileStream? stream, out CompanionFileMetadata metadata)
    {
        stream = null;
        metadata = default;
        if (string.IsNullOrWhiteSpace(path) || OperatingSystem.IsWindows())
        {
            return false;
        }

        int flags;
        if (OperatingSystem.IsMacOS())
        {
            flags = DarwinOpenFlags;
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            flags = LinuxX64OpenFlags;
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            flags = LinuxArm64OpenFlags;
        }
        else
        {
            return false;
        }

        int descriptor = open(path, flags);
        if (descriptor < 0)
        {
            return false;
        }

        SafeFileHandle? handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (!TryReadMetadata(handle, requireOwnerOnlyPermissions, out metadata))
            {
                return false;
            }

            stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            handle = null;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool TryReadMetadata(SafeFileHandle handle, bool requireOwnerOnlyPermissions, out CompanionFileMetadata metadata)
    {
        metadata = default;
        var stat = new byte[StatBufferLength];
        if (fstat(handle.DangerousGetHandle().ToInt32(), stat) != 0)
        {
            return false;
        }

        int modeOffset;
        int lengthOffset;
        int modifiedSecondsOffset;
        if (OperatingSystem.IsMacOS())
        {
            modeOffset = DarwinModeOffset;
            lengthOffset = DarwinLengthOffset;
            modifiedSecondsOffset = DarwinModifiedSecondsOffset;
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            modeOffset = LinuxX64ModeOffset;
            lengthOffset = LinuxLengthOffset;
            modifiedSecondsOffset = LinuxModifiedSecondsOffset;
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            modeOffset = LinuxArm64ModeOffset;
            lengthOffset = LinuxLengthOffset;
            modifiedSecondsOffset = LinuxModifiedSecondsOffset;
        }
        else
        {
            return false;
        }

        int mode = BitConverter.ToUInt16(stat, modeOffset);
        long length = BitConverter.ToInt64(stat, lengthOffset);
        if ((mode & FileTypeMask) != RegularFile || length < 0 || (requireOwnerOnlyPermissions && (mode & GroupOrOtherPermissions) != 0))
        {
            return false;
        }

        DateTimeOffset? modifiedUtc;
        try
        {
            modifiedUtc = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(stat, modifiedSecondsOffset));
        }
        catch (ArgumentOutOfRangeException)
        {
            modifiedUtc = null;
        }
        metadata = new CompanionFileMetadata(length, modifiedUtc, OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0), BitConverter.ToUInt64(stat, 8), BitConverter.ToInt64(stat, modifiedSecondsOffset + 8), mode & 0xfff, OperatingSystem.IsMacOS() ? BitConverter.ToUInt16(stat, 6) : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? BitConverter.ToUInt32(stat, 20) : BitConverter.ToUInt64(stat, 16), BitConverter.ToInt64(stat, modifiedSecondsOffset + 16), BitConverter.ToInt64(stat, modifiedSecondsOffset + 24));
        return true;
    }

    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fileDescriptor, [Out] byte[] stat);
}

/// <summary>Descriptor-validated metadata for one bounded regular file read.</summary>
internal readonly record struct CompanionFileMetadata(long Length, DateTimeOffset? ModifiedUtc, ulong Device = 0, ulong Inode = 0, long ModifiedNanoseconds = 0, int Permissions = 0, ulong LinkCount = 1, long ChangedSeconds = 0, long ChangedNanoseconds = 0);
