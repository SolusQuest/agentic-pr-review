using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

    internal static async Task<int> RunProbeAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var count) || count is < 1 or > 512) return 2;
        using var bucket = OpenForScenario(args[0]);
        for (var index = 0; index < count; index++)
            await bucket.ObserveAsync(charged: true, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // FileStream's Unix share lock must not compete with our Darwin
            // whole-file flock. Open that descriptor without a second lock.
            using var stream = OperatingSystem.IsMacOS() ? OpenDarwinStream(path)
                : new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (OperatingSystem.IsMacOS())
            {
                if (Flock(stream.SafeFileHandle, 2 | 4) != 0)
                {
                    if (Marshal.GetLastPInvokeError() != 35)
                        throw new IOException("synthetic_primary_bucket_lock_failed");
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            else
            {
                try { stream.Lock(0, StateBytes); }
                catch (IOException)
                {
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    continue;
                }
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
                if (OperatingSystem.IsMacOS())
                {
                    if (Flock(stream.SafeFileHandle, 8) != 0)
                        throw new IOException("synthetic_primary_bucket_unlock_failed");
                }
                else stream.Unlock(0, StateBytes);
            }
        }
    }

    private static FileStream OpenDarwinStream(string path)
    {
        var descriptor = Open(path, 2 | 4 | 0x20000000 | 0x1000000);
        if (descriptor < 0) throw new IOException("synthetic_primary_bucket_open_failed");
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (DarwinFiles.Stat(descriptor, out var info) != 0 || (info.Mode & 0xf000) != 0x8000 || info.Links != 1)
        {
            handle.Dispose();
            throw new IOException("synthetic_primary_bucket_open_invalid");
        }
        return new FileStream(handle, FileAccess.ReadWrite);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle handle, int operation);

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
