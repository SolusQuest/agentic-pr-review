using System.Buffers.Binary;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

// The synthetic Host, control, and Node transports run in separate processes,
// but GitHub's primary bucket belongs to their one shared token. Keep the tiny
// counter outside the evidence tree and settle each response under an OS file
// lock so every process observes one monotonically decreasing allocation.
internal sealed class FrameworkPrimaryRateLimitBucket : IDisposable
{
    private const int StateBytes = 4;
    private const string FileName = ".apr-r4-synthetic-primary-bucket.bin";
    private readonly string path;
    private readonly bool deleteOnDispose;
    private int disposed;

    private FrameworkPrimaryRateLimitBucket(string path, bool deleteOnDispose)
    {
        this.path = path;
        this.deleteOnDispose = deleteOnDispose;
    }

    internal static FrameworkPrimaryRateLimitBucket Initialize(
        string evidenceRoot,
        int initialRemaining)
    {
        if (!Directory.Exists(evidenceRoot) || initialRemaining < 1)
        {
            throw new InvalidOperationException(
                "synthetic_primary_bucket_initialization_invalid");
        }

        var path = BucketPath(evidenceRoot);
        var state = new byte[StateBytes];
        BinaryPrimitives.WriteInt32LittleEndian(state, initialRemaining);
        using (var stream = new FileStream(path, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            stream.Write(state);
            stream.Flush();
        }

        return new FrameworkPrimaryRateLimitBucket(path, deleteOnDispose: true);
    }

    internal static FrameworkPrimaryRateLimitBucket OpenForScenario(
        string scenarioRoot)
    {
        if (!Directory.Exists(scenarioRoot))
        {
            throw new InvalidOperationException("synthetic_primary_bucket_missing");
        }

        var evidenceRoot = Directory.GetParent(Path.GetFullPath(scenarioRoot))
            ?.FullName;
        if (string.IsNullOrEmpty(evidenceRoot))
        {
            throw new InvalidOperationException("synthetic_primary_bucket_missing");
        }

        var path = BucketPath(evidenceRoot);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("synthetic_primary_bucket_missing");
        }
        return new FrameworkPrimaryRateLimitBucket(path, deleteOnDispose: false);
    }

    internal int ReadRemaining() => ObserveAsync(charged: false,
            CancellationToken.None)
        .AsTask().GetAwaiter().GetResult();

    internal void RestartIndependentWindow(int initialRemaining)
    {
        if (!deleteOnDispose || initialRemaining < 1)
        {
            throw new InvalidOperationException(
                "synthetic_primary_bucket_restart_invalid");
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var state = new byte[StateBytes];
        BinaryPrimitives.WriteInt32LittleEndian(state, initialRemaining);
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        stream.Position = 0;
        stream.Write(state);
        stream.SetLength(StateBytes);
        stream.Flush();
    }

    internal async ValueTask<int> ObserveAsync(
        bool charged,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The trusted-proof synthetic primary bucket requires file locks.");
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite);
            try
            {
                stream.Lock(0, StateBytes);
            }
            catch (IOException)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var state = new byte[StateBytes];
                stream.Position = 0;
                stream.ReadExactly(state);
                if (stream.Length != StateBytes)
                {
                    throw new InvalidOperationException(
                        "synthetic_primary_bucket_invalid");
                }
                var remaining = BinaryPrimitives.ReadInt32LittleEndian(state);
                if (remaining < 0 || (charged && remaining == 0))
                {
                    throw new InvalidOperationException(
                        "synthetic_primary_bucket_invalid");
                }
                if (!charged) return remaining;

                remaining--;
                BinaryPrimitives.WriteInt32LittleEndian(state, remaining);
                stream.Position = 0;
                stream.Write(state);
                stream.SetLength(StateBytes);
                stream.Flush();
                return remaining;
            }
            finally
            {
                stream.Unlock(0, StateBytes);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || !deleteOnDispose)
        {
            return;
        }
        File.Delete(path);
    }

    private static string BucketPath(string evidenceRoot)
    {
        var fullEvidenceRoot = Path.GetFullPath(evidenceRoot);
        var runRoot = Directory.GetParent(fullEvidenceRoot)?.FullName;
        if (string.IsNullOrEmpty(runRoot))
        {
            throw new InvalidOperationException(
                "synthetic_primary_bucket_initialization_invalid");
        }
        return Path.Join(runRoot, FileName);
    }
}
