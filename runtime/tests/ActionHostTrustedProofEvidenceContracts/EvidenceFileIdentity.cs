using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record PinnedEvidenceFile(byte[] Bytes, string Identity);

public sealed class CreatedEvidenceFileReceipt
{
    private readonly EvidenceFileIdentity identity;

    private CreatedEvidenceFileReceipt(string path, EvidenceFileIdentity identity)
    {
        Path = path;
        this.identity = identity;
    }

    public string Path { get; }

    public static CreatedEvidenceFileReceipt WriteCreateNew(
        string path,
        ReadOnlySpan<byte> bytes,
        int? failAfterBytesForTest = null)
    {
        CreatedEvidenceFileReceipt? receipt = null;
        try
        {
            using (var handle = EvidenceFileHandle.CreateNewNoFollow(path))
            {
                var identity = EvidenceFileHandle.Identity(handle);
                if (identity.Links != 1)
                {
                    throw new InvalidDataException("created_file_identity_invalid");
                }
                receipt = new CreatedEvidenceFileReceipt(System.IO.Path.GetFullPath(path), identity);
                using var stream = new FileStream(handle, FileAccess.Write);
                if (failAfterBytesForTest is int count)
                {
                    stream.Write(bytes[..Math.Min(count, bytes.Length)]);
                    stream.Flush(flushToDisk: true);
                    throw new IOException("created_file_injected_partial_write");
                }
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!receipt.IsCurrentIdentity())
            {
                throw new InvalidDataException("created_file_identity_invalid");
            }
            return receipt;
        }
        catch
        {
            receipt?.DeleteIfOwned();
            throw;
        }
    }

    public bool IsCurrentIdentity()
    {
        try
        {
            using var handle = EvidenceFileHandle.OpenNoFollow(Path);
            var current = EvidenceFileHandle.Identity(handle);
            return current.Device == identity.Device &&
                current.File == identity.File &&
                current.Links == 1 &&
                current.Mode == identity.Mode &&
                current.Owner == identity.Owner;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void DeleteIfOwned()
    {
        try
        {
            if (!IsCurrentIdentity())
            {
                return;
            }
            File.Delete(Path);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Failure cleanup must never replace the original stable error or delete another identity.
        }
    }
}

public sealed class PinnedEvidenceLease : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly EvidenceFileIdentity physicalIdentity;
    private bool disposed;

    internal PinnedEvidenceLease(
        SafeFileHandle handle,
        EvidenceFileIdentity physicalIdentity,
        byte[] bytes,
        string identity)
    {
        this.handle = handle;
        this.physicalIdentity = physicalIdentity;
        Bytes = bytes;
        Identity = identity;
    }

    public byte[] Bytes { get; }
    public string Identity { get; }

    public void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (EvidenceFileHandle.Identity(handle) != physicalIdentity)
        {
            throw new InvalidDataException("restricted_file_replaced");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        CryptographicOperations.ZeroMemory(Bytes);
        handle.Dispose();
    }
}

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
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNoFollowFlag = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint CreateNew = 1;
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

    internal static SafeFileHandle CreateNewNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                path,
                GenericWrite,
                0,
                0,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagRandomAccess,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidDataException("restricted_file_create_invalid");
            }
            return handle;
        }

        if (OperatingSystem.IsLinux())
        {
            const uint ownerReadWrite = 0x180;
            var descriptor = OpenCreateNew(
                path,
                OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollowFlag | OpenCloseOnExec,
                ownerReadWrite);
            if (descriptor < 0)
            {
                throw new InvalidDataException("restricted_file_create_invalid");
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

    internal static void CreateDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectory(path, 0))
            {
                throw new InvalidDataException("restricted_directory_create_invalid");
            }
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            const uint ownerReadWriteExecute = 0x1c0;
            if (MakeDirectory(path, ownerReadWriteExecute) != 0)
            {
                throw new InvalidDataException("restricted_directory_create_invalid");
            }
            return;
        }

        throw new InvalidDataException("restricted_platform_unsupported");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string pathName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenCreateNew(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(int fileDescriptor, out LinuxFileInformation information);

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectory(string path, uint mode);

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
