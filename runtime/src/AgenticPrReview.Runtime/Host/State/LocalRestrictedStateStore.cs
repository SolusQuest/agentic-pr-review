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

internal sealed class LocalRestrictedStateStore : IRestrictedStateStore
{
    private static readonly ConcurrentDictionary<string, object> ScopeLocks =
        new(StringComparer.Ordinal);
    private readonly string configuredRoot;

    internal LocalRestrictedStateStore(string explicitTestOwnedRoot)
    {
        if (string.IsNullOrWhiteSpace(explicitTestOwnedRoot))
        {
            throw new ArgumentException(
                "An explicit test-owned state root is required.",
                nameof(explicitTestOwnedRoot));
        }

        configuredRoot = explicitTestOwnedRoot;
    }

    public RestrictedStateStoreRead Read(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path) ||
            !TryValidateRoot(root))
        {
            return ReadFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadUnderLock(access, path, cancellationToken);
        }
    }

    public RestrictedStateStoreWrite CompareExchange(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected is null ||
            replacement is null ||
            !RestrictedStateValidation.IsValidSnapshot(replacement) ||
            !TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path) ||
            !TryValidateRoot(root))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadUnderLock(
                access,
                path,
                cancellationToken);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Conflict);
            }

            if (!RestrictedStateSnapshotCodec.TryWrite(
                    replacement,
                    out var bytes))
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            var temporaryPath = Path.Combine(
                root,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(temporaryPath);
                if ((attributes &
                        (FileAttributes.Directory |
                            FileAttributes.ReparsePoint)) != 0)
                {
                    return WriteFailure(
                        RestrictedStateStoreFailure.Invalid);
                }

                if (expected.Exists)
                {
                    File.Move(
                        temporaryPath,
                        path,
                        overwrite: true);
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        path,
                        overwrite: false);
                }

                committed = true;
                NativeRestrictedStateFiles.TrySyncDirectory(root);
                var sha = SnapshotSha256(bytes);
                return new RestrictedStateStoreWrite(
                    RestrictedStateStoreFailure.None,
                    new RestrictedStateSnapshotVersion(sha, true),
                    Committed: true);
            }
            catch (OperationCanceledException) when (!committed)
            {
                throw;
            }
            catch (IOException)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Io,
                    committed);
            }
            catch (UnauthorizedAccessException)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Io,
                    committed);
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception exception) when (
                        exception is IOException or
                            UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    private RestrictedStateStoreRead ReadUnderLock(
        AuthorizedStateAccess access,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                RestrictedStateSnapshot.Empty,
                RestrictedStateSnapshotVersion.Absent);
        }

        SafeFileHandle? firstHandle = null;
        try
        {
            if (!TryOpenRead(path, out firstHandle) ||
                firstHandle is null ||
                !TryInspectRegular(
                    firstHandle,
                    out var firstIdentity,
                    out var length) ||
                length is < 1 or >
                    RestrictedStateSnapshotCodec.MaximumSnapshotBytes)
            {
                return ReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            var bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)length));
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(
                    firstHandle,
                    bytes.AsSpan(offset),
                    offset);
                if (read == 0)
                {
                    return ReadFailure(
                        RestrictedStateStoreFailure.Invalid);
                }

                offset += read;
            }

            if (!TryInspectRegular(
                    firstHandle,
                    out var afterReadIdentity,
                    out var afterReadLength) ||
                afterReadIdentity != firstIdentity ||
                afterReadLength != length ||
                !TryPathStillNamesIdentity(path, firstIdentity) ||
                !RestrictedStateSnapshotCodec.TryRead(
                    bytes,
                    access.Scope,
                    out var snapshot))
            {
                return ReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                snapshot,
                new RestrictedStateSnapshotVersion(
                    SnapshotSha256(bytes),
                    true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return ReadFailure(RestrictedStateStoreFailure.Io);
        }
        catch (UnauthorizedAccessException)
        {
            return ReadFailure(RestrictedStateStoreFailure.Io);
        }
        finally
        {
            firstHandle?.Dispose();
        }
    }

    private bool TryResolveRoot(
        AuthorizedStateAccess access,
        out string root)
    {
        root = string.Empty;
        if (access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope))
        {
            return false;
        }

        try
        {
            root = Path.GetFullPath(configuredRoot);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryResolveScopePath(
        AuthorizedStateAccess access,
        string root,
        out string path)
    {
        path = string.Empty;
        var scopeBytes =
            RestrictedStateSnapshotCodec.WriteScopeIdentity(access.Scope);
        var scopeSha = AgentCanonical.HashDomain(
            "apr.state-scope.r2",
            scopeBytes);
        path = Path.Combine(root, $"scope-{scopeSha}.aprstate");
        return StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(path),
            root);
    }

    private static bool TryValidateRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        var current = new DirectoryInfo(root);
        while (current is not null)
        {
            FileAttributes attributes;
            try
            {
                attributes = current.Attributes;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    private static bool TryPathStillNamesIdentity(
        string path,
        RestrictedStateFileIdentity expected)
    {
        SafeFileHandle? secondHandle = null;
        try
        {
            return TryOpenRead(path, out secondHandle) &&
                secondHandle is not null &&
                TryInspectRegular(
                    secondHandle,
                    out var actual,
                    out _) &&
                actual == expected;
        }
        finally
        {
            secondHandle?.Dispose();
        }
    }

    private static bool TryOpenRead(
        string path,
        out SafeFileHandle? handle)
    {
        handle = null;
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsWindows())
        {
            return NativeRestrictedStateFiles.TryOpenNoFollow(
                path,
                out handle);
        }

        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryInspectRegular(
        SafeFileHandle handle,
        out RestrictedStateFileIdentity identity,
        out long length)
    {
        identity = default;
        length = 0;
        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes &
                    (FileAttributes.Directory |
                        FileAttributes.ReparsePoint)) != 0 ||
                !NativeRestrictedStateFiles.TryGetIdentity(
                    handle,
                    out identity))
            {
                return false;
            }

            length = RandomAccess.GetLength(handle);
            return length >= 0;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SnapshotSha256(ReadOnlySpan<byte> bytes) =>
        AgentCanonical.HashDomain(
            "apr.state-snapshot.r2",
            bytes);

    private static RestrictedStateStoreRead ReadFailure(
        RestrictedStateStoreFailure failure) =>
        new(failure, null, null);

    private static RestrictedStateStoreWrite WriteFailure(
        RestrictedStateStoreFailure failure,
        bool committed = false) =>
        new(failure, null, committed);
}

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

    private static void WriteCandidate(
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
        WriteUInt32(writer, checked((uint)candidate.Envelope.Length));
        Write(writer, candidate.Envelope);
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

        if (!StringComparer.Ordinal.Equals(
                envelopeSha,
                RestrictedStateEnvelope.EnvelopeSha256(envelope)) ||
            !StringComparer.Ordinal.Equals(
                objectIdentity,
                RestrictedStateEnvelope.ObjectIdentity(
                    scope!,
                    envelopeSha!)))
        {
            return false;
        }

        candidate = new RestrictedStateCandidate(
            new RestrictedStateBinding(
                scope!,
                producerBase!,
                producerHead!,
                generation,
                predecessor,
                acceptedAt,
                expiresAt),
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

internal static partial class NativeRestrictedStateFiles
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenDirectory = 0x10000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;

    internal static bool TryOpenNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
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
                handle = null;
                return false;
            }

            return true;
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
            return false;
        }

        handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return true;
    }

    internal static bool TryGetIdentity(
        SafeFileHandle handle,
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

        if (OperatingSystem.IsLinux())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (FStat(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != RegularFile)
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

    internal static bool TrySyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var descriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        if (descriptor < 0)
        {
            return false;
        }

        try
        {
            return FSync(descriptor) == 0;
        }
        finally
        {
            Close(descriptor);
        }
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

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(
        int fileDescriptor,
        out LinuxFileInformation information);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);

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
