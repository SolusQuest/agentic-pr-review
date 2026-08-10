using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class ArtifactBridgeStaging
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private readonly string root;
    private readonly string csharpRoot;

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
        csharpRoot = Path.Join(root, "csharp");
        Directory.CreateDirectory(csharpRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                csharpRoot,
                PrivateDirectoryMode);
        }
        if (!IsSafeDirectory(csharpRoot))
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
        var scope = CreateScope();
        try
        {
            await using var stream = new FileStream(
                scope.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(encryptedBytes, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            if (stream.Length != encryptedBytes.Length)
            {
                throw new IOException("artifact_bridge_staging_invalid");
            }
            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ArtifactBridgeStagingScope PrepareDownload() => CreateScope();

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
        var pathAttributes = File.GetAttributes(scope.FullPath);
        if ((pathAttributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0)
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        using var handle = File.OpenHandle(
            scope.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var attributes = File.GetAttributes(handle);
        var length = RandomAccess.GetLength(handle);
        var namedAttributes = File.GetAttributes(scope.FullPath);
        var namedLength = new FileInfo(scope.FullPath).Length;
        if ((attributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0 ||
            (namedAttributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0 ||
            namedLength != length ||
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
        namedAttributes = File.GetAttributes(scope.FullPath);
        namedLength = new FileInfo(scope.FullPath).Length;
        if (RandomAccess.GetLength(handle) != length ||
            (namedAttributes & (FileAttributes.Directory |
                FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0 ||
            namedLength != length)
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        return bytes;
    }

    private ArtifactBridgeStagingScope CreateScope()
    {
        if (!IsSafeDirectory(root) || !IsSafeDirectory(csharpRoot))
        {
            throw new IOException("artifact_bridge_staging_root_invalid");
        }
        var directory = Path.Join(csharpRoot, $"op-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, PrivateDirectoryMode);
        }
        if (!IsSafeDirectory(directory))
        {
            throw new IOException("artifact_bridge_staging_invalid");
        }
        var fullPath = Path.Join(directory, "object.bin");
        var relative = Path.GetRelativePath(root, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Encoding.UTF8.GetByteCount(relative) >
            ArtifactBridgeLimits.MaximumRelativePathBytes)
        {
            Directory.Delete(directory);
            throw new IOException("artifact_bridge_staging_invalid");
        }
        return new ArtifactBridgeStagingScope(
            csharpRoot,
            directory,
            fullPath,
            relative);
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
    string operationDirectory,
    string fullPath,
    string relativePath) : IAsyncDisposable
{
    private bool disposed;

    internal string FullPath { get; } = fullPath;

    internal string RelativePath { get; } = relativePath;

    internal bool CleanupSucceeded { get; private set; } = true;

    internal bool IsOwnedBy(string expectedOwnerRoot) =>
        StringComparer.Ordinal.Equals(ownerRoot, expectedOwnerRoot) &&
        StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(operationDirectory),
            expectedOwnerRoot) &&
        Path.GetFileName(operationDirectory).StartsWith(
            "op-",
            StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        try
        {
            if (!IsOwnedBy(ownerRoot))
            {
                CleanupSucceeded = false;
            }
            else if (Directory.Exists(operationDirectory) &&
                ArtifactBridgeStaging.IsSafeDirectory(ownerRoot) &&
                ArtifactBridgeStaging.IsSafeDirectory(operationDirectory))
            {
                Directory.Delete(operationDirectory, recursive: true);
            }
            else if (Directory.Exists(operationDirectory))
            {
                CleanupSucceeded = false;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            CleanupSucceeded = false;
        }
        return ValueTask.CompletedTask;
    }
}
