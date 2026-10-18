using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State;

internal static class RestrictedStateSnapshotCodec
{
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("APRLOC01");
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);
    internal const ushort Version = 1;
    internal const int FixedFramingAllowance = 4_096;
    internal const int MaximumSnapshotBytes =
        AgentLimits.StateScopeTotalBytes +
        AgentLimits.CandidateMetadataBytes +
        FixedFramingAllowance;

    internal static bool TryWrite(
        RestrictedStateSnapshot snapshot,
        out byte[] bytes)
    {
        bytes = [];
        if (!RestrictedStateValidation.IsValidSnapshot(snapshot))
        {
            return false;
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>(
                Math.Min(MaximumSnapshotBytes, 16 * 1024));
            Write(writer, Magic);
            WriteUInt16(writer, Version);
            WriteByte(writer, checked((byte)snapshot.Accepted.Length));
            foreach (var candidate in snapshot.Accepted)
            {
                WriteCandidate(writer, candidate);
            }

            WriteByte(writer, snapshot.Staging is null ? (byte)0 : (byte)1);
            if (snapshot.Staging is not null)
            {
                WriteCandidate(writer, snapshot.Staging);
            }

            if (writer.WrittenCount > MaximumSnapshotBytes)
            {
                return false;
            }

            bytes = writer.WrittenMemory.ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                EncoderFallbackException or
                FormatException or
                OverflowException)
        {
            return false;
        }
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        RestrictedStateScope expectedScope,
        out RestrictedStateSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length is < 1 or > MaximumSnapshotBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(bytes, ref offset, Magic.Length, out var magic) ||
            !magic.SequenceEqual(Magic) ||
            !TryReadUInt16(bytes, ref offset, out var version) ||
            version != Version ||
            !TryReadByte(bytes, ref offset, out var acceptedCount) ||
            acceptedCount > AgentLimits.AcceptedCandidates)
        {
            return false;
        }

        var accepted = ImmutableArray.CreateBuilder<
            RestrictedStateCandidate>(acceptedCount);
        for (var index = 0; index < acceptedCount; index++)
        {
            if (!TryReadCandidate(
                    bytes,
                    ref offset,
                    expectedScope,
                    out var candidate))
            {
                return false;
            }

            accepted.Add(candidate!);
        }

        if (!TryReadByte(bytes, ref offset, out var hasStaging) ||
            hasStaging > 1)
        {
            return false;
        }

        RestrictedStateCandidate? staging = null;
        if (hasStaging == 1 &&
            !TryReadCandidate(
                bytes,
                ref offset,
                expectedScope,
                out staging))
        {
            return false;
        }

        if (offset != bytes.Length)
        {
            return false;
        }

        var result = new RestrictedStateSnapshot(
            accepted.MoveToImmutable(),
            staging);
        if (!RestrictedStateValidation.IsValidSnapshot(result))
        {
            return false;
        }

        snapshot = result;
        return true;
    }

    internal static byte[] WriteScopeIdentity(
        RestrictedStateScope scope)
    {
        var writer = new ArrayBufferWriter<byte>(512);
        WriteScope(writer, scope);
        return writer.WrittenMemory.ToArray();
    }

    internal static int CandidateMetadataBytes(
        RestrictedStateCandidate candidate)
    {
        var writer = new ArrayBufferWriter<byte>(512);
        WriteCandidateMetadata(writer, candidate);
        WriteUInt32(writer, checked((uint)candidate.Envelope.Length));
        return writer.WrittenCount;
    }

    private static void WriteCandidate(
        IBufferWriter<byte> writer,
        RestrictedStateCandidate candidate)
    {
        WriteCandidateMetadata(writer, candidate);
        WriteUInt32(writer, checked((uint)candidate.Envelope.Length));
        Write(writer, candidate.Envelope);
    }

    private static void WriteCandidateMetadata(
        IBufferWriter<byte> writer,
        RestrictedStateCandidate candidate)
    {
        WriteScope(writer, candidate.Binding.Scope);
        WriteHex(writer, candidate.Binding.ProducerBaseSha, 20);
        WriteHex(writer, candidate.Binding.ProducerHeadSha, 20);
        WriteInt64(writer, candidate.Binding.Generation);
        WriteOptionalHash(
            writer,
            candidate.Binding.PredecessorEnvelopeSha256);
        WriteInt64(writer, candidate.Binding.AcceptedAtUnixSeconds);
        WriteInt64(writer, candidate.Binding.ExpiresAtUnixSeconds);
        WriteHex(writer, candidate.SessionSha256, 32);
        WriteHex(writer, candidate.EnvelopeSha256, 32);
        WriteHex(writer, candidate.ObjectIdentity, 32);
    }

    private static bool TryReadCandidate(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        RestrictedStateScope expectedScope,
        out RestrictedStateCandidate? candidate)
    {
        candidate = null;
        if (!TryReadScope(bytes, ref offset, out var scope) ||
            scope != expectedScope ||
            !TryReadHex(bytes, ref offset, 20, out var producerBase) ||
            !TryReadHex(bytes, ref offset, 20, out var producerHead) ||
            !TryReadInt64(bytes, ref offset, out var generation) ||
            !TryReadOptionalHash(
                bytes,
                ref offset,
                out var predecessor) ||
            !TryReadInt64(bytes, ref offset, out var acceptedAt) ||
            !TryReadInt64(bytes, ref offset, out var expiresAt) ||
            !TryReadHex(bytes, ref offset, 32, out var sessionSha) ||
            !TryReadHex(bytes, ref offset, 32, out var envelopeSha) ||
            !TryReadHex(bytes, ref offset, 32, out var objectIdentity) ||
            !TryReadUInt32(
                bytes,
                ref offset,
                out var envelopeLength) ||
            envelopeLength is 0 or > AgentLimits.StateEnvelopeBytes ||
            !TryReadBytes(
                bytes,
                ref offset,
                checked((int)envelopeLength),
                out var envelope))
        {
            return false;
        }

        var binding = new RestrictedStateBinding(
            scope!,
            producerBase!,
            producerHead!,
            generation,
            predecessor,
            acceptedAt,
            expiresAt);
        if (!StringComparer.Ordinal.Equals(
                envelopeSha,
                RestrictedStateEnvelope.EnvelopeSha256(envelope)) ||
            !StringComparer.Ordinal.Equals(
                objectIdentity,
                RestrictedStateEnvelope.ObjectIdentity(
                    binding,
                    sessionSha!,
                    envelopeSha!)))
        {
            return false;
        }

        candidate = new RestrictedStateCandidate(
            binding,
            sessionSha!,
            envelopeSha!,
            objectIdentity!,
            envelope.ToArray());
        return RestrictedStateValidation.IsValidCandidate(candidate);
    }

    private static void WriteScope(
        IBufferWriter<byte> writer,
        RestrictedStateScope scope)
    {
        WriteUtf8(writer, scope.RepositoryId);
        WriteUtf8(writer, scope.WorkflowIdentity);
        WriteInt64(writer, scope.ReviewTarget);
        WriteAscii(writer, scope.SessionId);
        WriteUtf8(writer, scope.ProviderId);
        WriteUtf8(writer, scope.ModelId);
        WriteUtf8(writer, scope.AdapterId);
        WriteHex(writer, scope.PolicySha256, 32);
        WriteHex(writer, scope.LimitsSha256, 32);
        WriteHex(writer, scope.ToolsetSha256, 32);
        WriteUtf8(writer, scope.BuildId);
    }

    private static bool TryReadScope(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out RestrictedStateScope? scope)
    {
        scope = null;
        if (!TryReadUtf8(bytes, ref offset, 1, 128, out var repository) ||
            !TryReadUtf8(bytes, ref offset, 1, 256, out var workflow) ||
            !TryReadInt64(bytes, ref offset, out var reviewTarget) ||
            !TryReadAscii(bytes, ref offset, 1, 64, out var session) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var provider) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var model) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var adapter) ||
            !TryReadHex(bytes, ref offset, 32, out var policySha) ||
            !TryReadHex(bytes, ref offset, 32, out var limitsSha) ||
            !TryReadHex(bytes, ref offset, 32, out var toolsetSha) ||
            !TryReadUtf8(bytes, ref offset, 1, 256, out var build))
        {
            return false;
        }

        scope = new RestrictedStateScope(
            repository!,
            workflow!,
            reviewTarget,
            session!,
            provider!,
            model!,
            adapter!,
            policySha!,
            limitsSha!,
            toolsetSha!,
            build!);
        return RestrictedStateValidation.IsValidScope(scope);
    }

    private static void WriteOptionalHash(
        IBufferWriter<byte> writer,
        string? value)
    {
        if (value is null)
        {
            WriteByte(writer, 0);
        }
        else
        {
            WriteByte(writer, 1);
            WriteHex(writer, value, 32);
        }
    }

    private static bool TryReadOptionalHash(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out string? value)
    {
        value = null;
        if (!TryReadByte(bytes, ref offset, out var present) ||
            present > 1)
        {
            return false;
        }

        return present == 0 ||
            TryReadHex(bytes, ref offset, 32, out value);
    }

    private static void WriteUtf8(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteAscii(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteHex(
        IBufferWriter<byte> writer,
        string value,
        int bytes)
    {
        var decoded = Convert.FromHexString(value);
        if (decoded.Length != bytes)
        {
            throw new FormatException("Invalid fixed hash.");
        }

        Write(writer, decoded);
    }

    private static void WriteByte(
        IBufferWriter<byte> writer,
        byte value)
    {
        var destination = writer.GetSpan(1);
        destination[0] = value;
        writer.Advance(1);
    }

    private static void WriteUInt16(
        IBufferWriter<byte> writer,
        ushort value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteUInt32(
        IBufferWriter<byte> writer,
        uint value)
    {
        var destination = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        writer.Advance(sizeof(uint));
    }

    private static void WriteInt64(
        IBufferWriter<byte> writer,
        long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }

    private static void Write(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> bytes)
    {
        var destination = writer.GetSpan(bytes.Length);
        bytes.CopyTo(destination);
        writer.Advance(bytes.Length);
    }

    private static bool TryReadByte(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out byte value)
    {
        value = 0;
        if (bytes.Length - offset < 1)
        {
            return false;
        }

        value = bytes[offset++];
        return true;
    }

    private static bool TryReadUInt16(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out ushort value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(ushort))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out uint value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt64(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out long value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(long))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
        offset += sizeof(long);
        return true;
    }

    private static bool TryReadBytes(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int length,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (length < 0 || bytes.Length - offset < length)
        {
            return false;
        }

        value = bytes.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadUtf8(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(bytes, ref offset, out var length) ||
            length < minimum ||
            length > maximum ||
            !TryReadBytes(bytes, ref offset, length, out var encoded))
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(encoded);
            return StrictUtf8.GetByteCount(value) == length;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadAscii(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(bytes, ref offset, out var length) ||
            length < minimum ||
            length > maximum ||
            !TryReadBytes(bytes, ref offset, length, out var encoded))
        {
            return false;
        }

        foreach (var current in encoded)
        {
            if (current > 0x7f)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(encoded);
        return true;
    }

    private static bool TryReadHex(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int length,
        out string? value)
    {
        value = null;
        if (!TryReadBytes(bytes, ref offset, length, out var decoded))
        {
            return false;
        }

        value = Convert.ToHexStringLower(decoded);
        return true;
    }
}

internal readonly record struct RestrictedStateFileIdentity(
    ulong Device,
    ulong File);

internal readonly record struct RestrictedStateRootEntry(
    string Path,
    RestrictedStateFileIdentity Identity);

internal enum RestrictedStateOpenResult
{
    Success,
    NotFound,
    Unsafe,
    Io,
}

internal static partial class NativeRestrictedStateFiles
{
    private const int OpenReadOnly = 0;
    private static readonly int OpenNonBlocking = OperatingSystem.IsMacOS() ? DarwinFiles.NonBlocking : 0x800;
    private static readonly int OpenNoFollow = OperatingSystem.IsMacOS() ? DarwinFiles.NoFollowAny : 0x20000;
    private static readonly int OpenCloseOnExec = OperatingSystem.IsMacOS() ? DarwinFiles.CloseOnExec : 0x80000;
    private static readonly int OpenDirectory = OperatingSystem.IsMacOS() ? DarwinFiles.Directory : 0x10000;
    private const int DuplicateCloseOnExec = 1030;
    private const string LinuxDescriptorPrefix = "/proc/self/fd/";
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryFile = 0x4000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int LinuxNoEntry = 2;
    private const int LinuxSymbolicLinkLoop = 40;

    internal static RestrictedStateOpenResult OpenFileNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics |
                    FileFlagOpenReparsePoint |
                    FileFlagRandomAccess,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            return RestrictedStateOpenResult.Success;
        }

        var descriptor = Open(
            path,
            OpenReadOnly |
                OpenNonBlocking |
                OpenNoFollow |
                OpenCloseOnExec);
        if (descriptor < 0)
        {
            handle = null;
            return MapOpenError(Marshal.GetLastPInvokeError());
        }

        handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return RestrictedStateOpenResult.Success;
    }

    internal static RestrictedStateOpenResult OpenDirectoryNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            return RestrictedStateOpenResult.Success;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (OperatingSystem.IsLinux() && TryParseLinuxDescriptor(path, out var existingDescriptor))
            {
                var duplicate = Fcntl(
                    existingDescriptor,
                    DuplicateCloseOnExec,
                    0);
                if (duplicate < 0 ||
                    ReadInformation(duplicate, out var duplicateInfo) != 0 ||
                    (duplicateInfo.Mode & FileTypeMask) != DirectoryFile)
                {
                    if (duplicate >= 0)
                    {
                        new SafeFileHandle(
                            (nint)duplicate,
                            ownsHandle: true).Dispose();
                    }

                    handle = null;
                    return RestrictedStateOpenResult.Unsafe;
                }

                handle = new SafeFileHandle(
                    (nint)duplicate,
                    ownsHandle: true);
                return RestrictedStateOpenResult.Success;
            }

            var descriptor = Open(
                path,
                OpenReadOnly |
                    OpenDirectory |
                    OpenNoFollow |
                    OpenCloseOnExec);
            if (descriptor < 0)
            {
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            handle = new SafeFileHandle(
                (nint)descriptor,
                ownsHandle: true);
            return RestrictedStateOpenResult.Success;
        }

        handle = null;
        return RestrictedStateOpenResult.Unsafe;
    }

    private static bool TryParseLinuxDescriptor(
        string path,
        out int descriptor)
    {
        descriptor = -1;
        if (!path.StartsWith(
                LinuxDescriptorPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var value = path.AsSpan(LinuxDescriptorPrefix.Length);
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(value, out descriptor) && descriptor >= 0;
    }

    internal static bool IsLinuxAnchoredRoot(string path) =>
        OperatingSystem.IsLinux() &&
        TryParseLinuxDescriptor(path, out _);

    internal static RestrictedStateOpenResult OpenRootGuardNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OpenDirectoryNoFollow(path, out handle);
        }

        handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = null;
            return MapOpenError(Marshal.GetLastPInvokeError());
        }

        return RestrictedStateOpenResult.Success;
    }

    internal static string AnchoredRoot(
        string configuredRoot,
        SafeFileHandle rootGuard) =>
        OperatingSystem.IsLinux()
            ? $"/proc/self/fd/{checked((int)rootGuard.DangerousGetHandle())}"
            : configuredRoot;

    internal static bool TryGetIdentity(
        SafeFileHandle handle,
        bool expectDirectory,
        out RestrictedStateFileIdentity identity)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                identity = default;
                return false;
            }

            identity = new RestrictedStateFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            var expectedType = expectDirectory
                ? DirectoryFile
                : RegularFile;
            if (ReadInformation(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != expectedType)
            {
                identity = default;
                return false;
            }

            identity = new RestrictedStateFileIdentity(
                info.Device,
                info.Inode);
            return true;
        }

        identity = default;
        return false;
    }

    private static RestrictedStateOpenResult MapOpenError(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound
            ? RestrictedStateOpenResult.NotFound
            : error == (OperatingSystem.IsMacOS() ? 62 : LinuxSymbolicLinkLoop)
                ? RestrictedStateOpenResult.Unsafe
                : RestrictedStateOpenResult.Io;

    internal static bool TrySyncDirectory(SafeFileHandle rootGuard)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return true;
        }

        var descriptor = checked(
            (int)rootGuard.DangerousGetHandle());
        return FSync(descriptor) == 0;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    private static int ReadInformation(int descriptor, out LinuxFileInformation information)
    {
        if (!OperatingSystem.IsMacOS()) return FStat(descriptor, out information);
        var result = DarwinFiles.Stat(descriptor, out var native);
        information = new LinuxFileInformation
        {
            Device = native.Device, Inode = native.Inode, Mode = native.Mode,
            HardLinkCount = native.Links, UserId = native.UserId, Size = native.Size,
        };
        return result;
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(
        int fileDescriptor,
        out LinuxFileInformation information);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(
        int fileDescriptor,
        int command,
        int argument);

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
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LinuxFileInformation
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
        internal fixed long Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimestamp
    {
        internal long Seconds;
        internal long Nanoseconds;
    }
}

// The existing snapshot and outer crypto formats remain unchanged. This is the
// separate protected Solus plaintext record and its independent admission seam.
internal static class SolusRestrictedStateSnapshotCodec
{
    internal const int FramingBytes = 242;
    internal const int MaximumRecordBytes =
        FramingBytes + SolusRestrictedStateFormat.MaximumPayloadBytes;
    // Maximum APRAST01 header (64-byte key ID), nonce and tag, without padding.
    internal const int MaximumEnvelopeBytes = MaximumRecordBytes + 161;
    private static readonly byte[] Prefix = BuildPrefix();

    internal static bool TryWrite(SolusRestrictedStateRecord? record, out byte[] bytes)
    {
        bytes = [];
        if (record is null ||
            record.PayloadByteCount is < 1 or > SolusRestrictedStateFormat.MaximumPayloadBytes ||
            !SolusRestrictedStateAdmission.IsSupported(record.Compatibility) ||
            !SolusRestrictedStateAdmission.IsValidCheckpoint(record.Checkpoint))
        {
            return false;
        }

        bytes = new byte[FramingBytes + record.PayloadByteCount];
        Prefix.CopyTo(bytes, 0);
        var offset = Prefix.Length;
        var value = record.Compatibility;
        bytes[offset++] = value.Mode;
        WriteGuid(bytes, ref offset, value.ImplementationId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value.FormatVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value.CompatibilityVersion);
        offset += 4;
        foreach (var hash in new[] { value.PackageClosureSha256,
                     value.RuntimeConfigurationSha256, value.ProviderConfigurationSha256 })
        {
            Convert.FromHexString(hash).CopyTo(bytes, offset);
            offset += 32;
        }

        var source = record.Checkpoint;
        WriteGuid(bytes, ref offset, source.CheckpointId);
        WriteGuid(bytes, ref offset, source.LogicalWorkId);
        WriteGuid(bytes, ref offset, source.RoundId);
        WriteGuid(bytes, ref offset, source.ExecutionId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)record.PayloadByteCount);
        offset += 4;
        record.RestrictedPayload.CopyTo(bytes.AsSpan(offset));
        return true;
    }

    // Structural decoding alone confers no authority. Call TryRestore for
    // independently selected, authenticated state.
    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        SolusRestrictedStateCompatibility? expected,
        out SolusRestrictedStateRecord? record,
        out SolusRestrictedStateFailure failure)
    {
        record = null;
        failure = SolusRestrictedStateFailure.Malformed;
        if (bytes.Length > MaximumRecordBytes)
        {
            failure = SolusRestrictedStateFailure.Oversized;
            return false;
        }

        if (!SolusRestrictedStateAdmission.IsSupported(expected))
        {
            failure = SolusRestrictedStateFailure.Incompatible;
            return false;
        }

        if (bytes.Length < FramingBytes + 1)
        {
            return false;
        }

        if (!bytes[..Prefix.Length].SequenceEqual(Prefix))
        {
            failure = SolusRestrictedStateFailure.Incompatible;
            return false;
        }

        // Validate length/EOF before even allocating fixed metadata objects.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(FramingBytes - 4)..]);
        if (length is 0 or > SolusRestrictedStateFormat.MaximumPayloadBytes ||
            bytes.Length - FramingBytes != length)
        {
            return false;
        }

        var offset = Prefix.Length;
        var mode = bytes[offset++];
        var implementation = ReadGuid(bytes, ref offset);
        var format = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        offset += 4;
        var compatibility = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
        offset += 4;
        var packageHash = ReadHash(bytes, ref offset);
        var runtimeHash = ReadHash(bytes, ref offset);
        var providerHash = ReadHash(bytes, ref offset);
        var actual = new SolusRestrictedStateCompatibility(
            SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
            mode, implementation, format, compatibility, packageHash, runtimeHash, providerHash);
        if (actual != expected)
        {
            failure = SolusRestrictedStateFailure.Incompatible;
            return false;
        }

        var checkpoint = new SolusRestrictedStateCheckpoint(
            ReadGuid(bytes, ref offset), ReadGuid(bytes, ref offset),
            ReadGuid(bytes, ref offset), ReadGuid(bytes, ref offset));
        if (!SolusRestrictedStateRecord.TryCreate(actual, checkpoint, bytes[FramingBytes..], out record))
        {
            return false;
        }

        failure = SolusRestrictedStateFailure.None;
        return true;
    }

    internal static string RecordSha256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumRecordBytes)
            throw new ArgumentException("APR_SOLUS_RECORD_INVALID");
        return AgentCanonical.HashDomain(SolusRestrictedStateFormat.RecordDomain, bytes);
    }

    internal static bool TrySeal(
        AuthorizedStateAccess? access,
        RestrictedStateBinding? binding,
        SolusRestrictedStateRecord? record,
        IRestrictedStateKeyResolver? keys,
        out byte[]? envelope,
        out string? recordSha256,
        out SolusRestrictedStateFailure failure)
    {
        envelope = null;
        recordSha256 = null;
        failure = SolusRestrictedStateFailure.AccessDenied;
        if (access is null || binding is null || binding.Scope != access.Scope || keys is null)
        {
            return false;
        }

        failure = SolusRestrictedStateFailure.Malformed;
        if (!RestrictedStateValidation.IsValidBinding(binding) || !TryWrite(record, out var plaintext))
        {
            return false;
        }

        try
        {
            if (!RestrictedStateEnvelope.TryEncrypt(access, binding, plaintext, keys,
                    out envelope, out var code))
            {
                failure = CryptoFailure(code);
                return false;
            }

            recordSha256 = RecordSha256(plaintext);
            failure = SolusRestrictedStateFailure.None;
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            envelope = null;
            failure = SolusRestrictedStateFailure.KeyUnavailable;
            return false;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static bool TryRestoreCandidate(
        AuthorizedStateAccess? access,
        RestrictedStateBinding? trustedBinding,
        AcceptedLineage? lineage,
        RestrictedStateCandidate? candidate,
        SolusRestrictedStateCompatibility? expected,
        long nowUnixSeconds,
        IRestrictedStateKeyResolver? keys,
        out SolusRestrictedStateRecord? record,
        out SolusRestrictedStateFailure failure)
    {
        record = null;
        failure = SolusRestrictedStateFailure.AccessDenied;
        if (access is null || trustedBinding is null || keys is null ||
            trustedBinding.Scope != access.Scope)
        {
            return false;
        }

        failure = SolusRestrictedStateFailure.LineageMismatch;
        // Bound before the existing candidate validator hashes/copies any bytes.
        if (candidate?.Envelope is null || candidate.Envelope.Length > MaximumEnvelopeBytes ||
            candidate.Binding != trustedBinding || lineage is null ||
            candidate.SessionSha256 != lineage.SessionSha256 ||
            candidate.EnvelopeSha256 != lineage.EnvelopeSha256 ||
            !RestrictedStateValidation.IsValidCandidate(candidate))
        {
            return false;
        }

        return TryRestore(access, trustedBinding, lineage, candidate.Envelope,
            expected, nowUnixSeconds, keys, out record, out failure);
    }

    internal static bool TryRestore(
        AuthorizedStateAccess? access,
        RestrictedStateBinding? trustedBinding,
        AcceptedLineage? lineage,
        ReadOnlySpan<byte> envelope,
        SolusRestrictedStateCompatibility? expected,
        long nowUnixSeconds,
        IRestrictedStateKeyResolver? keys,
        out SolusRestrictedStateRecord? record,
        out SolusRestrictedStateFailure failure)
    {
        record = null;
        failure = SolusRestrictedStateFailure.AccessDenied;
        if (access is null || trustedBinding is null || keys is null ||
            trustedBinding.Scope != access.Scope)
        {
            return false;
        }

        if (envelope.Length > MaximumEnvelopeBytes)
        {
            failure = SolusRestrictedStateFailure.Oversized;
            return false;
        }

        failure = SolusRestrictedStateFailure.Incompatible;
        if (!SolusRestrictedStateAdmission.IsSupported(expected))
        {
            return false;
        }

        failure = SolusRestrictedStateFailure.LineageMismatch;
        if (lineage is null || !RestrictedStateValidation.IsValidBinding(trustedBinding) ||
            !RestrictedStateValidation.IsValidLineage(lineage) || !lineage.TransitionAuthorized ||
            lineage.Scope != access.Scope || lineage.Generation != trustedBinding.Generation ||
            lineage.ExpectedPredecessorEnvelopeSha256 != trustedBinding.PredecessorEnvelopeSha256 ||
            lineage.AcceptedAtUnixSeconds != trustedBinding.AcceptedAtUnixSeconds ||
            lineage.ExpiresAtUnixSeconds != trustedBinding.ExpiresAtUnixSeconds ||
            nowUnixSeconds < 0 || nowUnixSeconds > RestrictedStateFormat.MaximumUnixSeconds ||
            nowUnixSeconds < lineage.AcceptedAtUnixSeconds ||
            RestrictedStateEnvelope.EnvelopeSha256(envelope) != lineage.EnvelopeSha256)
        {
            return false;
        }

        if (nowUnixSeconds >= lineage.ExpiresAtUnixSeconds)
        {
            failure = SolusRestrictedStateFailure.Expired;
            return false;
        }

        if (!HasBoundedCiphertext(envelope, out failure))
        {
            return false;
        }

        byte[]? plaintext = null;
        try
        {
            if (!RestrictedStateEnvelope.TryDecrypt(access, trustedBinding, envelope, keys,
                    out plaintext, out var code))
            {
                failure = CryptoFailure(code);
                return false;
            }

            if (plaintext!.Length > MaximumRecordBytes)
            {
                failure = SolusRestrictedStateFailure.Oversized;
                return false;
            }

            if (RecordSha256(plaintext) != lineage.SessionSha256)
            {
                failure = SolusRestrictedStateFailure.LineageMismatch;
                return false;
            }

            // The selected authenticated record is the checkpoint source. No
            // extra caller-selected checkpoint can replace its provenance.
            return TryRead(plaintext, expected, out record, out failure);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            record = null;
            failure = SolusRestrictedStateFailure.KeyUnavailable;
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static SolusRestrictedStateFailure CryptoFailure(string code) => code switch
    {
        RestrictedStateCodes.AuthenticationFailed => SolusRestrictedStateFailure.AuthenticationFailed,
        RestrictedStateCodes.KeyUnavailable => SolusRestrictedStateFailure.KeyUnavailable,
        _ => SolusRestrictedStateFailure.Malformed,
    };

    // Inspect only bounded APRAST01 length fields before the existing crypto
    // parser allocates ciphertext/decrypted buffers. Crypto validates contents.
    private static bool HasBoundedCiphertext(ReadOnlySpan<byte> bytes,
        out SolusRestrictedStateFailure failure)
    {
        failure = SolusRestrictedStateFailure.Malformed;
        var offset = 12; // magic, version, algorithm
        foreach (var maximum in new[] { 128, 64, 64, RestrictedStateFormat.NonceBytes })
        {
            if (bytes.Length - offset < 2) return false;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            offset += 2;
            if (length == 0 || length > maximum || bytes.Length - offset < length) return false;
            offset += length;
        }

        if (bytes.Length - offset < 4) return false;
        var ciphertextLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (ciphertextLength > MaximumRecordBytes)
        {
            failure = SolusRestrictedStateFailure.Oversized;
            return false;
        }

        return ciphertextLength > 0;
    }

    private static byte[] BuildPrefix()
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(Encoding.ASCII.GetBytes(SolusRestrictedStateFormat.Magic));
        Span<byte> number = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(number, SolusRestrictedStateFormat.Version);
        writer.Write(number);
        foreach (var text in new[] { SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(number, (ushort)text.Length);
            writer.Write(number);
            writer.Write(Encoding.ASCII.GetBytes(text));
        }

        return writer.WrittenSpan.ToArray();
    }

    private static void WriteGuid(Span<byte> bytes, ref int offset, Guid value)
    {
        value.TryWriteBytes(bytes.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = new Guid(bytes.Slice(offset, 16), bigEndian: true);
        offset += 16;
        return value;
    }

    private static string ReadHash(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = Convert.ToHexStringLower(bytes.Slice(offset, 32));
        offset += 32;
        return value;
    }
}
