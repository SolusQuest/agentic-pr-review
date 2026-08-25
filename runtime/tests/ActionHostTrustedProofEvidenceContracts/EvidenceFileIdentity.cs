using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record PinnedEvidenceFile(byte[] Bytes, string Identity);

internal readonly record struct EvidenceFileIdentity(
    ulong Device,
    ulong File,
    ulong Links,
    long Size,
    uint Mode,
    uint Owner)
{
    internal string Canonical => $"{Device:x16}:{File:x16}:{Links}:{Size}:{Mode:x8}:{Owner}";
}

internal static partial class EvidenceFileHandle
{
    private const int OpenReadOnly = 0;
    private const int OpenNoFollowFlag = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;

    internal static SafeFileHandle OpenNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                path,
                GenericRead,
                FileShareRead,
                0,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagRandomAccess,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidDataException("restricted_file_open_invalid");
            }
            return handle;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = Open(path, OpenReadOnly | OpenNoFollowFlag | OpenCloseOnExec);
            if (descriptor < 0)
            {
                throw new InvalidDataException("restricted_file_open_invalid");
            }
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        throw new InvalidDataException("restricted_platform_unsupported");
    }

    internal static EvidenceFileIdentity Identity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.FileAttributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("restricted_file_identity_invalid");
            }
            return new EvidenceFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                info.NumberOfLinks,
                checked((long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow)),
                0,
                0);
        }

        var descriptor = checked((int)handle.DangerousGetHandle());
        if (FStat(descriptor, out var stat) != 0 || (stat.Mode & FileTypeMask) != RegularFile)
        {
            throw new InvalidDataException("restricted_file_identity_invalid");
        }
        return new EvidenceFileIdentity(
            stat.Device,
            stat.Inode,
            stat.HardLinkCount,
            stat.Size,
            stat.Mode,
            stat.UserId);
    }

    internal static uint EffectiveUserId() => OperatingSystem.IsLinux() ? GetEffectiveUserId() : 0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(int fileDescriptor, out LinuxFileInformation information);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low; internal uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxFileInformation
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong HardLinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong DeviceId;
        internal long Size;
        internal long BlockSize;
        internal long BlockCount;
        internal LinuxTimestamp AccessTime;
        internal LinuxTimestamp ModificationTime;
        internal LinuxTimestamp ChangeTime;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimestamp { internal long Seconds; internal long Nanoseconds; }
}
