using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record PinnedEvidenceFile(byte[] Bytes, string Identity);

public sealed class CreatedEvidenceFileReceipt : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly byte[] expectedBytes;
    private readonly string stagingDirectory;
    private readonly string stagingPath;
    private EvidenceFileIdentity identity;
    private bool published;
    private bool retracted;
    private bool disposed;

    private CreatedEvidenceFileReceipt(
        string path,
        string stagingDirectory,
        string stagingPath,
        SafeFileHandle handle,
        ReadOnlySpan<byte> expectedBytes)
    {
        Path = path;
        this.stagingDirectory = stagingDirectory;
        this.stagingPath = stagingPath;
        this.handle = handle;
        this.expectedBytes = expectedBytes.ToArray();
    }

    public string Path { get; }

    public static CreatedEvidenceFileReceipt WriteCreateNew(
        string path,
        ReadOnlySpan<byte> bytes,
        int? failAfterBytesForTest = null,
        Action<string>? beforePublishForTest = null,
        Action<string>? afterPublishForTest = null)
    {
        var finalPath = System.IO.Path.GetFullPath(path);
        var parent = System.IO.Path.GetDirectoryName(finalPath) ??
            throw new InvalidDataException("created_file_parent_invalid");
        var stagingDirectory = System.IO.Path.Join(
            parent,
            $".apr-r4-evidence-{Guid.NewGuid():N}");
        var stagingPath = System.IO.Path.Join(stagingDirectory, "candidate");
        CreatedEvidenceFileReceipt? receipt = null;
        try
        {
            EvidenceFileHandle.CreateDirectoryExclusive(stagingDirectory);
            var handle = EvidenceFileHandle.CreateNewNoFollow(stagingPath);
            try
            {
                var identity = EvidenceFileHandle.Identity(handle);
                if (identity.Links != 1)
                {
                    throw new InvalidDataException("created_file_identity_invalid");
                }
                receipt = new CreatedEvidenceFileReceipt(
                    finalPath,
                    stagingDirectory,
                    stagingPath,
                    handle,
                    bytes);
                if (failAfterBytesForTest is int count)
                {
                    RandomAccess.Write(handle, bytes[..Math.Min(count, bytes.Length)], 0);
                    RandomAccess.FlushToDisk(handle);
                    receipt.identity = EvidenceFileHandle.Identity(handle);
                    throw new IOException("created_file_injected_partial_write");
                }
                RandomAccess.Write(handle, bytes, 0);
                RandomAccess.FlushToDisk(handle);
                receipt.identity = EvidenceFileHandle.Identity(handle);
                receipt.ValidateHandleAndPath(stagingPath);
                receipt.ValidateHandleBytes();
                beforePublishForTest?.Invoke(stagingPath);
                receipt.ValidateHandleAndPath(stagingPath);
                EvidenceFileHandle.MoveNoReplace(stagingPath, finalPath);
                receipt.published = true;
                TryDeleteEmptyDirectory(stagingDirectory);
                afterPublishForTest?.Invoke(finalPath);
                receipt.ValidatePublishedIdentityAndBytes();
            }
            catch
            {
                if (receipt is null)
                {
                    handle.Dispose();
                }
                else
                {
                    try
                    {
                        receipt.identity = EvidenceFileHandle.Identity(handle);
                    }
                    catch (InvalidDataException)
                    {
                        // The original exception remains authoritative.
                    }
                }
                throw;
            }
            return receipt;
        }
        catch
        {
            receipt?.RetractIfOwned();
            receipt?.Dispose();
            if (receipt is null)
            {
                TryDeleteStaging(stagingPath, stagingDirectory);
            }
            throw;
        }
    }

    public bool IsCurrentIdentity()
    {
        try
        {
            using var handle = EvidenceFileHandle.OpenNoFollow(Path);
            var current = EvidenceFileHandle.Identity(handle);
            return SameOwnedIdentity(current, identity);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void ValidatePublished()
    {
        ValidatePublishedIdentityAndBytes();
    }

    public void RetractIfOwned(Action<string>? afterValidationBeforeDispositionForTest = null)
    {
        if (!published)
        {
            DeleteIfOwned();
            return;
        }
        try
        {
            ExactIdentityFileDisposition.Delete(
                Path,
                handle,
                identity,
                expectedBytes,
                afterValidationBeforeDispositionForTest);
            published = false;
            retracted = true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (handle.IsClosed)
            {
                published = false;
                retracted = true;
            }
            // Retraction may remove only the retained process-created identity
            // with its exact expected bytes. A replacement or competitor is never deleted.
        }
    }

    public void DeleteIfOwned()
    {
        if (published)
        {
            return;
        }
        try
        {
            ValidateHandleAndPath(stagingPath);
            File.Delete(stagingPath);
            Directory.Delete(stagingDirectory, recursive: false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The private staging entry is process-owned. Cleanup must never touch the final path.
        }
    }

    private void ValidatePublishedIdentityAndBytes()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!published)
        {
            throw new InvalidDataException("created_file_not_published");
        }
        ValidateHandleAndPath(Path);
        ValidateHandleBytes();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (!published && !retracted)
        {
            DeleteIfOwned();
        }
        CryptographicOperations.ZeroMemory(expectedBytes);
        handle.Dispose();
    }

    private void ValidateHandleAndPath(string currentPath)
    {
        var retained = EvidenceFileHandle.Identity(handle);
        if (!SameOwnedIdentity(retained, identity))
        {
            throw new InvalidDataException("created_file_identity_invalid");
        }
        try
        {
            using var currentHandle = EvidenceFileHandle.OpenNoFollowSharedWrite(currentPath);
            if (!SameOwnedIdentity(EvidenceFileHandle.Identity(currentHandle), identity))
            {
                throw new InvalidDataException("created_file_identity_invalid");
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("created_file_identity_invalid", exception);
        }
    }

    private void ValidateHandleBytes()
    {
        var actual = new byte[expectedBytes.Length];
        try
        {
            var offset = 0;
            while (offset < actual.Length)
            {
                var read = RandomAccess.Read(handle, actual.AsSpan(offset), offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            if (offset != actual.Length ||
                EvidenceFileHandle.Identity(handle).Size != actual.Length ||
                !actual.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new InvalidDataException("created_file_bytes_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static bool SameOwnedIdentity(EvidenceFileIdentity left, EvidenceFileIdentity right) =>
        left.Device == right.Device &&
        left.File == right.File &&
        left.Links == 1 &&
        right.Links == 1 &&
        left.Mode == right.Mode &&
        left.Owner == right.Owner &&
        left.Size == right.Size;

    private static void TryDeleteStaging(string path, string directory)
    {
        try
        {
            File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The private staging entry is process-owned and the final path was never touched.
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Publication is already atomic and authoritative; an empty private staging shell is harmless.
        }
    }
}

public sealed class PinnedEvidenceLease : IDisposable
{
    private readonly string path;
    private readonly SafeFileHandle handle;
    private readonly EvidenceFileIdentity physicalIdentity;
    private bool disposed;

    internal PinnedEvidenceLease(
        string path,
        SafeFileHandle handle,
        EvidenceFileIdentity physicalIdentity,
        byte[] bytes,
        string identity)
    {
        this.path = path;
        this.handle = handle;
        this.physicalIdentity = physicalIdentity;
        Bytes = bytes;
        Identity = identity;
    }

    public byte[] Bytes { get; }
    public string Identity { get; }
    public bool IsDisposed => disposed;

    public void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (EvidenceFileHandle.Identity(handle) != physicalIdentity)
        {
            throw new InvalidDataException("restricted_file_replaced");
        }
        using var current = EvidenceFileHandle.OpenNoFollowSharedWrite(path);
        if (EvidenceFileHandle.Identity(current) != physicalIdentity)
        {
            throw new InvalidDataException("restricted_file_replaced");
        }
    }

    public void ValidateExactBytes()
    {
        Validate();
        var current = new byte[Bytes.Length];
        try
        {
            var offset = 0;
            while (offset < current.Length)
            {
                var read = RandomAccess.Read(handle, current.AsSpan(offset), offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            if (offset != current.Length ||
                EvidenceFileHandle.Identity(handle).Size != current.Length ||
                !current.AsSpan().SequenceEqual(Bytes))
            {
                throw new InvalidDataException("restricted_file_contents_changed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public void DeleteExactIdentity(Action<string>? afterValidationBeforeDispositionForTest = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            ExactIdentityFileDisposition.Delete(
                path,
                handle,
                physicalIdentity,
                Bytes,
                afterValidationBeforeDispositionForTest);
        }
        finally
        {
            if (handle.IsClosed)
            {
                disposed = true;
                CryptographicOperations.ZeroMemory(Bytes);
            }
        }
        if (!disposed)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(Bytes);
            handle.Dispose();
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

internal static class ExactIdentityFileDisposition
{
    internal static void Delete(
        string path,
        SafeFileHandle retainedHandle,
        EvidenceFileIdentity expectedIdentity,
        ReadOnlySpan<byte> expectedBytes,
        Action<string>? afterValidationBeforeDispositionForTest)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        ValidateExact(fullPath, retainedHandle, expectedIdentity, expectedBytes);

        if (OperatingSystem.IsWindows())
        {
            afterValidationBeforeDispositionForTest?.Invoke(fullPath);
            ValidateRetained(retainedHandle, expectedIdentity, expectedBytes);
            EvidenceFileHandle.DeleteRetainedIdentity(retainedHandle);
            retainedHandle.Dispose();
            if (EvidenceFileHandle.PathEntryExists(fullPath))
            {
                throw new InvalidDataException("exact_identity_disposition_competitor_present");
            }
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidDataException("restricted_platform_unsupported");
        }

        var parent = System.IO.Path.GetDirectoryName(fullPath) ??
            throw new InvalidDataException("exact_identity_disposition_parent_invalid");
        var quarantineDirectory = System.IO.Path.Join(
            parent,
            $".apr-r4-disposition-{Guid.NewGuid():N}");
        var quarantinePath = System.IO.Path.Join(quarantineDirectory, "candidate");
        var quarantined = false;
        EvidenceFileHandle.CreateDirectoryExclusive(quarantineDirectory);
        try
        {
            afterValidationBeforeDispositionForTest?.Invoke(fullPath);
            EvidenceFileHandle.MoveNoReplace(fullPath, quarantinePath);
            quarantined = true;
            try
            {
                ValidateExact(
                    quarantinePath,
                    retainedHandle,
                    expectedIdentity,
                    expectedBytes);
            }
            catch
            {
                RestoreWithoutReplace(quarantinePath, fullPath);
                quarantined = false;
                throw;
            }

            File.Delete(quarantinePath);
            quarantined = false;
            if (EvidenceFileHandle.PathEntryExists(quarantinePath) ||
                EvidenceFileHandle.PathEntryExists(fullPath))
            {
                throw new InvalidDataException("exact_identity_disposition_invalid");
            }
        }
        finally
        {
            if (!quarantined)
            {
                TryDeleteEmptyDirectory(quarantineDirectory);
            }
        }
    }

    private static void ValidateExact(
        string path,
        SafeFileHandle retainedHandle,
        EvidenceFileIdentity expectedIdentity,
        ReadOnlySpan<byte> expectedBytes)
    {
        ValidateRetained(retainedHandle, expectedIdentity, expectedBytes);
        using var current = EvidenceFileHandle.OpenNoFollowSharedWrite(path);
        if (EvidenceFileHandle.Identity(current) != expectedIdentity)
        {
            throw new InvalidDataException("exact_identity_disposition_identity_invalid");
        }
    }

    private static void ValidateRetained(
        SafeFileHandle retainedHandle,
        EvidenceFileIdentity expectedIdentity,
        ReadOnlySpan<byte> expectedBytes)
    {
        if (EvidenceFileHandle.Identity(retainedHandle) != expectedIdentity)
        {
            throw new InvalidDataException("exact_identity_disposition_identity_invalid");
        }

        var actual = new byte[expectedBytes.Length];
        try
        {
            var offset = 0;
            while (offset < actual.Length)
            {
                var read = RandomAccess.Read(retainedHandle, actual.AsSpan(offset), offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            if (offset != actual.Length ||
                EvidenceFileHandle.Identity(retainedHandle).Size != actual.Length ||
                !actual.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new InvalidDataException("exact_identity_disposition_bytes_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static void RestoreWithoutReplace(string quarantinePath, string originalPath)
    {
        try
        {
            EvidenceFileHandle.MoveNoReplace(quarantinePath, originalPath);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("exact_identity_disposition_restore_invalid", exception);
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A non-empty quarantine retains the displaced entry; never delete it during recovery.
        }
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

public static partial class EvidenceFileHandle
{
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNoFollowFlag = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
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

    internal static SafeFileHandle OpenNoFollowSharedWrite(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OpenNoFollow(path);
        }
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
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

    internal static SafeFileHandle OpenNoFollowForDisposition(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OpenNoFollow(path);
        }
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            FileShareRead | FileShareDelete,
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

    public static bool PathNamesRetainedHandle(string path, SafeFileHandle retainedHandle)
    {
        var retained = Identity(retainedHandle);
        using var current = OpenNoFollowSharedWrite(path);
        return Identity(current) == retained;
    }

    public static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static SafeFileHandle CreateNewNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                path,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareDelete,
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
                OpenReadWrite | OpenCreate | OpenExclusive | OpenNoFollowFlag | OpenCloseOnExec,
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

    internal static void MoveNoReplace(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFile(source, destination))
            {
                throw new InvalidDataException("created_file_publish_invalid");
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            const int currentDirectory = -100;
            const uint noReplace = 1;
            if (RenameAt2(currentDirectory, source, currentDirectory, destination, noReplace) != 0)
            {
                throw new InvalidDataException("created_file_publish_invalid");
            }
            return;
        }
        throw new InvalidDataException("restricted_platform_unsupported");
    }

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

    internal static void DeleteRetainedIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidDataException("restricted_platform_unsupported");
        }
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new InvalidDataException("exact_identity_disposition_invalid");
        }
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

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFile(string existingFileName, string newFileName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string pathName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    private const int FileDispositionInfo = 4;

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle handle,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenCreateNew(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

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
    private struct FileDispositionInformation { internal int DeleteFile; }

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
