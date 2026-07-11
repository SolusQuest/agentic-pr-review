using System.Runtime.InteropServices;

namespace AgenticPrReview.Runtime;

public sealed record StagedFile(string TempPath, string FinalPath);

public interface IRuntimeFileSystem
{
    bool Exists(string path);
    Task<byte[]> ReadAllBytesAsync(string path);
    Task<StagedFile> StageAsync(string finalPath, byte[] bytes);
    Task CommitNoReplaceAsync(StagedFile stagedFile);
    Task DeleteIfExistsAsync(string path);
}

public sealed class PhysicalRuntimeFileSystem : IRuntimeFileSystem
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public Task<byte[]> ReadAllBytesAsync(string path) => File.ReadAllBytesAsync(path);

    public async Task<StagedFile> StageAsync(string finalPath, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(finalPath);
        if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The destination parent does not exist.");
        }

        var tempPath = Path.Combine(parent, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            return new StagedFile(tempPath, finalPath);
        }
        catch
        {
            try
            {
                await DeleteIfExistsAsync(tempPath);
            }
            catch
            {
                // Cleanup must not replace the staging failure.
            }
            throw;
        }
    }

    public Task CommitNoReplaceAsync(StagedFile stagedFile)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!NativeFileOperations.MoveFile(stagedFile.TempPath, stagedFile.FinalPath))
            {
                throw new IOException("The destination could not be committed without replacement.");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (NativeFileOperations.RenameNoReplace(stagedFile.TempPath, stagedFile.FinalPath) != 0)
            {
                throw new IOException("The destination could not be committed without replacement.");
            }
        }
        else
        {
            throw new PlatformNotSupportedException("This platform has no verified no-replace rename primitive.");
        }

        return Task.CompletedTask;
    }

    public Task DeleteIfExistsAsync(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}

internal static partial class NativeFileOperations
{
    private const int AtFileSystemDirectory = -100;
    private const uint RenameNoReplaceFlag = 1;

    [LibraryImport("kernel32", EntryPoint = "MoveFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFile(string existingFileName, string newFileName);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    internal static int RenameNoReplace(string source, string destination) =>
        RenameAt2(AtFileSystemDirectory, source, AtFileSystemDirectory, destination, RenameNoReplaceFlag);
}
