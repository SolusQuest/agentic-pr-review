using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

// Issued only by the authorized filesystem/Host entrypoints. The borrowed
// lifetime stays live across selection, durable consumption and runtime handoff.
internal sealed partial class SolusContextExclusion : IDisposable
{
    private SafeFileHandle? handle;
    private SafeFileHandle? parent;
    private string? invocationScope;
    private readonly string path;
    private readonly RestrictedStateFileIdentity identity;
    private readonly Func<bool> authorityIsCurrent;

    private SolusContextExclusion(SafeFileHandle handle, string path,
        RestrictedStateFileIdentity identity, Func<bool> authorityIsCurrent)
    {
        this.handle = handle;
        this.path = path;
        this.identity = identity;
        this.authorityIsCurrent = authorityIsCurrent;
    }

    internal bool IsLive
    {
        get
        {
            var current = Volatile.Read(ref handle);
            if (current is null || current.IsClosed) return false;
            try
            {
                if (!authorityIsCurrent()) return false;
                if (!NativeRestrictedStateFiles.TryGetIdentity(current, true, out var held) ||
                    held != identity || NativeRestrictedStateFiles.OpenRootGuardNoFollow(path,
                        out var observed) != RestrictedStateOpenResult.Success || observed is null)
                    return false;
                using (observed)
                    return NativeRestrictedStateFiles.TryGetIdentity(observed, true, out var found) &&
                        found == identity;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ObjectDisposedException) { return false; }
        }
    }

    internal bool MatchesInvocation(ArtifactStateRestoreRequest request) =>
        invocationScope is not null && IsLive &&
        AcceptedStateProductionAuthorization.TryAuthorize(request, out _) &&
        invocationScope == InvocationScope(request);

    internal static async Task<SolusContextExclusion?> AcquireInvocationAsync(
        ArtifactStateRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() ||
            !AcceptedStateProductionAuthorization.TryAuthorize(request, out _)) return null;
        SafeFileHandle? root = null;
        try
        {
            var scope = InvocationScope(request);
            var parentPath = "/tmp/apr-context-" + GetEffectiveUserId().ToString(CultureInfo.InvariantCulture);
            Directory.CreateDirectory(parentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            if (NativeRestrictedStateFiles.OpenRootGuardNoFollow(parentPath, out root) !=
                    RestrictedStateOpenResult.Success || root is null || !IsPrivateDirectory(root) ||
                !NativeRestrictedStateFiles.TryGetIdentity(root, true, out var rootIdentity)) return null;
            var anchored = Path.Join(NativeRestrictedStateFiles.AnchoredRoot(parentPath, root), scope);
            Directory.CreateDirectory(anchored, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            var ownedParent = root;
            bool Current()
            {
                if (ownedParent.IsClosed || !IsPrivateDirectory(ownedParent) ||
                    NativeRestrictedStateFiles.OpenRootGuardNoFollow(parentPath, out var observed) !=
                        RestrictedStateOpenResult.Success || observed is null) return false;
                using (observed)
                    return NativeRestrictedStateFiles.TryGetIdentity(observed, true, out var found) &&
                        found == rootIdentity;
            }
            var lease = await AcquireAuthorizedDirectoryAsync(Path.Join(parentPath, scope),
                Current, cancellationToken).ConfigureAwait(false);
            if (lease is null) return null;
            if (lease.handle is null || !IsPrivateDirectory(lease.handle))
            {
                lease.Dispose();
                return null;
            }
            lease.invocationScope = scope;
            lease.parent = root;
            root = null;
            return lease;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException) { return null; }
        finally { root?.Dispose(); }
    }

    private static string InvocationScope(ArtifactStateRestoreRequest request)
    {
        var writer = new LineageBinaryWriter(4096);
        writer.WriteString("apr.solus-invocation-exclusion.v1");
        writer.WriteInt64(request.Launch.RepositoryId);
        writer.WriteInt64(request.Invocation.PullRequest.Number);
        writer.WriteString(request.Invocation.WorkflowPath);
        writer.WriteInt64(request.Launch.RunId);
        writer.WriteInt64(request.Launch.RunAttempt);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    private static bool IsPrivateDirectory(SafeFileHandle directory) =>
        Statx(directory, "", 0x1000, 0x7ff, out var stat) == 0 &&
        (stat.Mode & 0xf000) == 0x4000 && (stat.Mode & 0x1ff) == 0x1c0 &&
        stat.UserId == GetEffectiveUserId();

    internal static async Task<SolusContextExclusion?> AcquireAuthorizedDirectoryAsync(
        string path, Func<bool> authorityIsCurrent, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !authorityIsCurrent() ||
            cancellationToken.IsCancellationRequested) return null;
        SafeFileHandle? opened = null;
        try
        {
            if (NativeRestrictedStateFiles.OpenRootGuardNoFollow(path, out opened) !=
                    RestrictedStateOpenResult.Success || opened is null ||
                !NativeRestrictedStateFiles.TryGetIdentity(opened, true, out var identity)) return null;
            var lease = new SolusContextExclusion(opened, path, identity, authorityIsCurrent);
            while (lease.IsLive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Flock(opened, 2 | 4) == 0)
                {
                    if (!lease.IsLive) return null;
                    opened = null; // ownership passes only after lock and identity revalidation
                    return lease;
                }
                if (Marshal.GetLastPInvokeError() != 11) return null;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            OperationCanceledException or ObjectDisposedException) { return null; }
        finally { opened?.Dispose(); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref handle, null)?.Dispose();
        Interlocked.Exchange(ref parent, null)?.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle descriptor, int operation);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct DirectoryStat
    {
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(SafeFileHandle directory, string path, int flags,
        uint mask, out DirectoryStat stat);
}
