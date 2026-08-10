using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class ArtifactBridgeStaging
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private readonly string root;
    private readonly string csharpRoot;
    private readonly SafeFileHandle rootGuard;
    private readonly SafeFileHandle csharpRootGuard;
    private readonly RestrictedStateFileIdentity rootIdentity;
    private readonly RestrictedStateFileIdentity csharpRootIdentity;
    private readonly string anchoredCsharpRoot;

    internal ArtifactBridgeStaging(string explicitBridgeOwnedRoot)
    {
        if (string.IsNullOrWhiteSpace(explicitBridgeOwnedRoot))
        {
            throw new ArgumentException(
                "artifact_bridge_staging_root_required",
                nameof(explicitBridgeOwnedRoot));
        }
        root = Path.GetFullPath(explicitBridgeOwnedRoot);
        if (!Directory.Exists(root) || !IsSafeDirectory(root))
        {
            throw new IOException("artifact_bridge_staging_root_invalid");
        }
        rootGuard = OpenDirectoryGuard(root);
        rootIdentity = RequireIdentity(rootGuard, expectDirectory: true);
        var anchoredRoot = NativeRestrictedStateFiles.AnchoredRoot(
            root,
            rootGuard);
        csharpRoot = Path.Join(root, "csharp");
        var anchoredCsharpPath = Path.Join(anchoredRoot, "csharp");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(anchoredCsharpPath);
        }
        else
        {
            Directory.CreateDirectory(
                anchoredCsharpPath,
                PrivateDirectoryMode);
        }
        csharpRootGuard = OpenDirectoryGuard(anchoredCsharpPath);
        csharpRootIdentity = RequireIdentity(
            csharpRootGuard,
            expectDirectory: true);
        anchoredCsharpRoot = NativeRestrictedStateFiles.AnchoredRoot(
            csharpRoot,
            csharpRootGuard);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                anchoredCsharpRoot,
                PrivateDirectoryMode);
        }
        if (!IsSafeDirectory(root) ||
            !PathMatchesIdentity(root, rootIdentity, expectDirectory: true) ||
            !IsSafeDirectory(csharpRoot) ||
            !PathMatchesIdentity(
                csharpRoot,
                csharpRootIdentity,
                expectDirectory: true))
        {
            throw new IOException("artifact_bridge_staging_root_invalid");
        }
    }

    internal static string DeriveRoot(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            var digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(endpoint)))
                .ToLowerInvariant();
            return Path.Join(
                Path.GetTempPath(),
                "apr-artifact-bridge",
                digest);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(endpoint));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("artifact_bridge_endpoint_invalid");
        }
        return Path.Join(directory, "artifact-staging");
    }

    internal async Task<ArtifactBridgeStagingScope> StageUploadAsync(
        ReadOnlyMemory<byte> encryptedBytes,
        CancellationToken cancellationToken)
    {
        if (encryptedBytes.Length is < 1 or >
            ArtifactBridgeLimits.MaximumEncryptedObjectBytes)
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        var scope = CreateScope(upload: true);
        try
        {
            await scope.ObjectStream.WriteAsync(
                    encryptedBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await scope.ObjectStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            if (scope.ObjectStream.Length != encryptedBytes.Length)
            {
                throw new IOException("artifact_bridge_staging_invalid");
            }
            scope.ObjectStream.Position = 0;
            scope.Seal();
            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ArtifactBridgeStagingScope PrepareDownload()
    {
        var scope = CreateScope(upload: false);
        scope.Seal();
        return scope;
    }

    internal async Task<byte[]> ReadDownloadAsync(
        ArtifactBridgeStagingScope scope,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or >
                ArtifactBridgeLimits.MaximumEncryptedObjectBytes ||
            !scope.IsOwnedBy(csharpRoot))
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        if (!scope.ValidatePhysicalIdentity())
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        var handle = scope.ObjectStream.SafeFileHandle;
        var attributes = File.GetAttributes(handle);
        var length = RandomAccess.GetLength(handle);
        if ((attributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0 ||
            length is < 1 or >
                ArtifactBridgeLimits.MaximumEncryptedObjectBytes ||
            length > maximumBytes)
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                    handle,
                    bytes.AsMemory(offset),
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("artifact_bridge_staging_invalid");
            }
            offset += read;
        }
        if (RandomAccess.GetLength(handle) != length ||
            !scope.ValidatePhysicalIdentity())
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        return bytes;
    }

    private ArtifactBridgeStagingScope CreateScope(bool upload)
    {
        AssertRoots();
        var operationName = $"op-{Guid.NewGuid():N}";
        var directory = Path.Join(csharpRoot, operationName);
        var anchoredDirectory = Path.Join(
            anchoredCsharpRoot,
            operationName);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(anchoredDirectory);
        }
        else
        {
            Directory.CreateDirectory(
                anchoredDirectory,
                PrivateDirectoryMode);
        }
        SafeFileHandle? operationGuard = null;
        FileStream? objectStream = null;
        FileStream? envelopeStream = null;
        RestrictedStateFileIdentity? operationIdentity = null;
        RestrictedStateFileIdentity? objectIdentity = null;
        RestrictedStateFileIdentity? envelopeIdentity = null;
        try
        {
            operationGuard = OpenDirectoryGuard(anchoredDirectory);
            operationIdentity = RequireIdentity(
                operationGuard,
                expectDirectory: true);
            var anchoredOperation =
                NativeRestrictedStateFiles.AnchoredRoot(
                    directory,
                    operationGuard);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    anchoredOperation,
                    PrivateDirectoryMode);
            }
            var fullPath = Path.Join(directory, "object.bin");
            var anchoredFullPath = Path.Join(
                anchoredOperation,
                "object.bin");
            objectStream = new FileStream(
                anchoredFullPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                upload ? FileShare.Read : FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            objectIdentity = RequireIdentity(
                objectStream.SafeFileHandle,
                expectDirectory: false);
            var envelopePath = Path.Join(
                directory,
                "artifact-envelope.json");
            if (upload)
            {
                envelopeStream = new FileStream(
                    Path.Join(
                        anchoredOperation,
                        "artifact-envelope.json"),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            envelopeIdentity = envelopeStream is null
                ? (RestrictedStateFileIdentity?)null
                : RequireIdentity(
                    envelopeStream.SafeFileHandle,
                    expectDirectory: false);
            var relative = Path.GetRelativePath(root, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (Encoding.UTF8.GetByteCount(relative) >
                ArtifactBridgeLimits.MaximumRelativePathBytes)
            {
                throw new IOException("artifact_bridge_staging_invalid");
            }
            return new ArtifactBridgeStagingScope(
                csharpRoot,
                anchoredCsharpRoot,
                directory,
                operationName,
                operationGuard,
                operationIdentity.Value,
                fullPath,
                objectStream,
                objectIdentity.Value,
                envelopePath,
                envelopeStream,
                envelopeIdentity,
                relative);
        }
        catch
        {
            envelopeStream?.Dispose();
            objectStream?.Dispose();
            operationGuard?.Dispose();
            DeleteFailedScope(
                anchoredDirectory,
                operationIdentity,
                objectIdentity,
                envelopeIdentity);
            throw;
        }
    }

    private static void DeleteFailedScope(
        string anchoredDirectory,
        RestrictedStateFileIdentity? operationIdentity,
        RestrictedStateFileIdentity? objectIdentity,
        RestrictedStateFileIdentity? envelopeIdentity)
    {
        try
        {
            if (operationIdentity is null ||
                !PathMatchesIdentity(
                    anchoredDirectory,
                    operationIdentity.Value,
                    expectDirectory: true))
            {
                return;
            }
            if (envelopeIdentity is not null)
            {
                DeleteExactFile(
                    Path.Join(
                        anchoredDirectory,
                        "artifact-envelope.json"),
                    envelopeIdentity.Value);
            }
            if (objectIdentity is not null)
            {
                DeleteExactFile(
                    Path.Join(anchoredDirectory, "object.bin"),
                    objectIdentity.Value);
            }
            Directory.Delete(anchoredDirectory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return;
        }
    }

    private static void DeleteExactFile(
        string path,
        RestrictedStateFileIdentity expectedIdentity)
    {
        if (!PathMatchesIdentity(
                path,
                expectedIdentity,
                expectDirectory: false))
        {
            throw new IOException("artifact_bridge_cleanup_identity_changed");
        }
        File.Delete(path);
    }

    private void AssertRoots()
    {
        if (!IsSafeDirectory(root) ||
            !IsSafeDirectory(csharpRoot) ||
            !PathMatchesIdentity(root, rootIdentity, expectDirectory: true) ||
            !PathMatchesIdentity(
                csharpRoot,
                csharpRootIdentity,
                expectDirectory: true))
        {
            throw new IOException("artifact_bridge_staging_root_invalid");
        }
    }

    internal static SafeFileHandle OpenDirectoryGuard(string path)
    {
        var result = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            path,
            out var handle);
        return result == RestrictedStateOpenResult.Success &&
            handle is not null
            ? handle
            : throw new IOException("artifact_bridge_staging_invalid");
    }

    internal static RestrictedStateFileIdentity RequireIdentity(
        SafeFileHandle handle,
        bool expectDirectory)
    {
        var attributes = File.GetAttributes(handle);
        var forbidden = FileAttributes.ReparsePoint |
            FileAttributes.Device;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & forbidden) != 0 ||
            isDirectory != expectDirectory ||
            !NativeRestrictedStateFiles.TryGetIdentity(
                handle,
                expectDirectory,
                out var identity))
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        return identity;
    }

    internal static bool PathMatchesIdentity(
        string path,
        RestrictedStateFileIdentity expected,
        bool expectDirectory)
    {
        var result = expectDirectory
            ? NativeRestrictedStateFiles.OpenDirectoryNoFollow(
                path,
                out var handle)
            : NativeRestrictedStateFiles.OpenFileNoFollow(
                path,
                out handle);
        using (handle)
        {
            try
            {
                return result == RestrictedStateOpenResult.Success &&
                    handle is not null &&
                    RequireIdentity(handle, expectDirectory) == expected;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    internal static bool IsSafeDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists ||
                (info.Attributes & (FileAttributes.ReparsePoint |
                    FileAttributes.Device)) != 0)
            {
                return false;
            }
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode forbidden =
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite |
                    UnixFileMode.OtherExecute;
                if ((mode & forbidden) != 0)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class ArtifactBridgeStagingScope(
    string ownerRoot,
    string anchoredOwnerRoot,
    string operationDirectory,
    string operationName,
    SafeFileHandle operationGuard,
    RestrictedStateFileIdentity operationIdentity,
    string fullPath,
    FileStream objectStream,
    RestrictedStateFileIdentity objectIdentity,
    string envelopePath,
    FileStream? envelopeStream,
    RestrictedStateFileIdentity? envelopeIdentity,
    string relativePath) : IAsyncDisposable
{
    private bool disposed;

    internal string FullPath { get; } = fullPath;

    internal string RelativePath { get; } = relativePath;

    internal FileStream ObjectStream { get; } = objectStream;

    internal bool CleanupSucceeded { get; private set; } = true;

    internal bool IsOwnedBy(string expectedOwnerRoot) =>
        StringComparer.Ordinal.Equals(ownerRoot, expectedOwnerRoot) &&
        StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(operationDirectory),
            expectedOwnerRoot) &&
        StringComparer.Ordinal.Equals(
            Path.GetFileName(operationDirectory),
            operationName) &&
        operationName.StartsWith("op-", StringComparison.Ordinal);

    internal void Seal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(
            FullPath,
            envelopeStream is null
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead);
        if (envelopeStream is not null)
        {
            File.SetUnixFileMode(
                envelopePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.SetUnixFileMode(
            operationDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    internal bool ValidatePhysicalIdentity()
    {
        try
        {
            return !disposed &&
                IsOwnedBy(ownerRoot) &&
                ArtifactBridgeStaging.IsSafeDirectory(ownerRoot) &&
                ArtifactBridgeStaging.IsSafeDirectory(operationDirectory) &&
                ArtifactBridgeStaging.PathMatchesIdentity(
                    operationDirectory,
                    operationIdentity,
                    expectDirectory: true) &&
                ArtifactBridgeStaging.RequireIdentity(
                    ObjectStream.SafeFileHandle,
                    expectDirectory: false) == objectIdentity &&
                ArtifactBridgeStaging.PathMatchesIdentity(
                    FullPath,
                    objectIdentity,
                    expectDirectory: false) &&
                (envelopeStream is null ||
                    envelopeIdentity is not null &&
                    ArtifactBridgeStaging.RequireIdentity(
                        envelopeStream.SafeFileHandle,
                        expectDirectory: false) == envelopeIdentity.Value &&
                    ArtifactBridgeStaging.PathMatchesIdentity(
                        envelopePath,
                        envelopeIdentity.Value,
                        expectDirectory: false));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ObjectDisposedException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        var physicalIdentityValid = ValidateBeforeDisposal();
        envelopeStream?.Dispose();
        ObjectStream.Dispose();
        operationGuard.Dispose();
        if (!physicalIdentityValid)
        {
            CleanupSucceeded = false;
            return ValueTask.CompletedTask;
        }
        try
        {
            var tombstoneName = $"cleanup-{Guid.NewGuid():N}";
            var tombstone = Path.Join(ownerRoot, tombstoneName);
            var anchoredTombstone = Path.Join(
                anchoredOwnerRoot,
                tombstoneName);
            Directory.Move(
                Path.Join(anchoredOwnerRoot, operationName),
                anchoredTombstone);
            var tombstoneGuard =
                ArtifactBridgeStaging.OpenDirectoryGuard(tombstone);
            if (ArtifactBridgeStaging.RequireIdentity(
                    tombstoneGuard,
                    expectDirectory: true) != operationIdentity)
            {
                tombstoneGuard.Dispose();
                CleanupSucceeded = false;
                return ValueTask.CompletedTask;
            }
            _ = Task.Run(() => DeleteIsolated(
                anchoredTombstone,
                tombstoneGuard,
                operationIdentity,
                objectIdentity,
                envelopeIdentity));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            CleanupSucceeded = false;
        }
        return ValueTask.CompletedTask;
    }

    private bool ValidateBeforeDisposal()
    {
        disposed = false;
        try
        {
            return ValidatePhysicalIdentity();
        }
        finally
        {
            disposed = true;
        }
    }

    private static void DeleteIsolated(
        string anchoredTombstone,
        SafeFileHandle tombstoneGuard,
        RestrictedStateFileIdentity expectedDirectoryIdentity,
        RestrictedStateFileIdentity expectedObjectIdentity,
        RestrictedStateFileIdentity? expectedEnvelopeIdentity)
    {
        try
        {
            using (tombstoneGuard)
            {
                if (ArtifactBridgeStaging.RequireIdentity(
                        tombstoneGuard,
                        expectDirectory: true) != expectedDirectoryIdentity)
                {
                    return;
                }
                var expectedNames = expectedEnvelopeIdentity is null
                    ? new HashSet<string>(
                        ["object.bin"],
                        StringComparer.Ordinal)
                    : new HashSet<string>(
                        ["object.bin", "artifact-envelope.json"],
                        StringComparer.Ordinal);
                var entries = Directory.EnumerateFileSystemEntries(
                        anchoredTombstone)
                    .Select(Path.GetFileName)
                    .ToArray();
                if (entries.Any(entry =>
                        entry is null || !expectedNames.Remove(entry)) ||
                    expectedNames.Count != 0)
                {
                    return;
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        anchoredTombstone,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                }
                DeleteExactFile(
                    Path.Join(anchoredTombstone, "object.bin"),
                    expectedObjectIdentity);
                if (expectedEnvelopeIdentity is not null)
                {
                    DeleteExactFile(
                        Path.Join(
                            anchoredTombstone,
                            "artifact-envelope.json"),
                        expectedEnvelopeIdentity.Value);
                }
            }
            Directory.Delete(anchoredTombstone, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ObjectDisposedException)
        {
            return;
        }
    }

    private static void DeleteExactFile(
        string path,
        RestrictedStateFileIdentity expectedIdentity)
    {
        if (!ArtifactBridgeStaging.PathMatchesIdentity(
                path,
                expectedIdentity,
                expectDirectory: false))
        {
            throw new IOException("artifact_bridge_cleanup_identity_changed");
        }
        File.Delete(path);
    }
}
