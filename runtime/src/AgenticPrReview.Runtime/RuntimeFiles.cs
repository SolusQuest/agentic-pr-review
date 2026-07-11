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
    public bool Exists(string path) => File.Exists(path);

    public Task<byte[]> ReadAllBytesAsync(string path) => File.ReadAllBytesAsync(path);

    public async Task<StagedFile> StageAsync(string finalPath, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(finalPath);
        if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The destination parent does not exist.");
        }

        var tempPath = Path.Combine(parent, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
        return new StagedFile(tempPath, finalPath);
    }

    public Task CommitNoReplaceAsync(StagedFile stagedFile)
    {
        File.Move(stagedFile.TempPath, stagedFile.FinalPath, overwrite: false);
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
