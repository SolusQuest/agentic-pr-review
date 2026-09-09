using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Host.State;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

internal sealed class ReplayRejected(ReplayAdmissionCode code) : Exception(code.ToString())
{
    internal ReplayAdmissionCode Code { get; } = code;
}

internal sealed record CapturedReplay(ReplayManifest Manifest, ImmutableDictionary<string, ImmutableArray<byte>> Files);

internal sealed class ReplayDirectoryGuard : IDisposable
{
    private ReplayDirectoryGuard(string path, SafeFileHandle handle, ReviewedStagedFileIdentity identity)
    { Path = path; Handle = handle; Identity = identity; }

    internal string Path { get; }
    internal SafeFileHandle Handle { get; }
    internal ReviewedStagedFileIdentity Identity { get; }
    internal string AnchoredPath => NativeRestrictedStateFiles.AnchoredRoot(Path, Handle);

    internal static ReplayDirectoryGuard Open(string path)
    {
        SafeFileHandle? handle;
        if (OperatingSystem.IsWindows())
        {
            // A metadata-only desired-access handle does not reliably prevent directory rename.
            // Hold read access without delete sharing for the entire capture.
            handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry); }
        }
        else if (NativeRestrictedStateFiles.OpenRootGuardNoFollow(path, out handle) != RestrictedStateOpenResult.Success)
            throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
        try
        {
            if (!NativeRestrictedStateFiles.TryGetIdentity(handle!, true, out var pinned) ||
                !ReviewedStagedFileAccess.TryOpenDirectory(path, out var inspection, out var identity))
                throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
            using (inspection)
            {
                if (pinned.Device != identity.Device || pinned.File != identity.File)
                    throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
            }
            return new(path, handle!, identity);
        }
        catch { handle?.Dispose(); throw; }
    }

    internal bool StillMatches() => ReviewedStagedFileAccess.DirectoryMatches(Path, Identity);
    public void Dispose() => Handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
}

internal static class ReplayDirectory
{
    internal static CapturedReplay Capture(string root, CancellationToken cancellationToken)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) ||
            string.IsNullOrWhiteSpace(root) || root.Length > 4096 || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            root.StartsWith("//", StringComparison.Ordinal) || root.Contains("://", StringComparison.Ordinal))
            throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
        root = System.IO.Path.GetFullPath(root);
        if (OperatingSystem.IsWindows() && new DriveInfo(System.IO.Path.GetPathRoot(root)!).DriveType == DriveType.Network)
            throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
        if (OperatingSystem.IsWindows()) root = "\\\\?\\" + root;
        var guards = new List<ReplayDirectoryGuard>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volume = System.IO.Path.GetPathRoot(root)!;
            var current = ReplayDirectoryGuard.Open(volume);
            guards.Add(current);
            var components = root[volume.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (components.Length > 64) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
            foreach (var component in components)
            {
                current = ReplayDirectoryGuard.Open(System.IO.Path.Join(current.AnchoredPath, component));
                guards.Add(current);
            }
            var bundle = current;
            var manifestBytes = Read(bundle, ReplayLimits.ManifestName, ReplayLimits.ManifestBytes, null, cancellationToken);
            var manifest = ReplayJson.Read(manifestBytes.AsSpan(), ReplayJsonContext.Default.ReplayManifest);
            if (manifest is null || !ReplayAdmission.ValidManifest(manifest))
                throw new ReplayRejected(ReplayAdmissionCode.InvalidManifest);

            var declared = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var expectedFiles = declared.Keys.Append(ReplayLimits.ManifestName).ToHashSet(StringComparer.Ordinal);
            var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in declared.Keys)
            {
                for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
                    expectedDirectories.Add(path[..index]);
            }
            if (expectedDirectories.Overlaps(expectedFiles)) throw new ReplayRejected(ReplayAdmissionCode.InvalidManifest);
            var directories = new Dictionary<string, ReplayDirectoryGuard>(StringComparer.Ordinal) { [""] = bundle };

            HashSet<string> Inventory(bool openDirectories)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                queue.Enqueue("");
                var entries = 0;
                while (queue.TryDequeue(out var relative))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parent = directories[relative];
                    if (!parent.StillMatches()) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(parent.AnchoredPath))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++entries > expectedFiles.Count + expectedDirectories.Count)
                            throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
                        var name = System.IO.Path.GetFileName(entry);
                        var path = relative.Length == 0 ? name : relative + "/" + name;
                        if (!ReplayLimits.Path(path) || !seen.Add(path)) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
                        if (expectedDirectories.Contains(path))
                        {
                            if (openDirectories)
                            {
                                var child = ReplayDirectoryGuard.Open(System.IO.Path.Join(parent.AnchoredPath, name));
                                guards.Add(child);
                                directories.Add(path, child);
                            }
                            else if (!directories[path].StillMatches()) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
                            queue.Enqueue(path);
                        }
                        else if (!expectedFiles.Contains(path)) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
                    }
                }
                if (!seen.SetEquals(expectedFiles.Concat(expectedDirectories))) throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
                return seen;
            }

            _ = Inventory(true);
            var captured = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                var split = file.Path.LastIndexOf('/');
                var parent = directories[split < 0 ? "" : file.Path[..split]];
                var name = file.Path[(split + 1)..];
                captured.Add(file.Path, Read(parent, name, file.Length, file.Sha256, cancellationToken));
            }
            _ = Inventory(false);
            if (!Read(bundle, ReplayLimits.ManifestName, manifestBytes.Length, null, cancellationToken).AsSpan().SequenceEqual(manifestBytes.AsSpan()))
                throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
            foreach (var guard in guards)
                if (!guard.StillMatches()) throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
            return new(manifest, captured.ToImmutable());
        }
        finally
        {
            for (var index = guards.Count - 1; index >= 0; index--) guards[index].Dispose();
        }
    }

    private static ImmutableArray<byte> Read(ReplayDirectoryGuard parent, string name, int limit, string? hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!parent.StillMatches() || NativeRestrictedStateFiles.OpenFileNoFollow(System.IO.Path.Join(parent.AnchoredPath, name), out var handle) != RestrictedStateOpenResult.Success)
            throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
        using (handle)
        {
            if (!ReviewedStagedFileAccess.TryInspectRegular(handle!, out var identity, out var length) || !ReplayFileLinks.IsSingleLink(handle!))
                throw new ReplayRejected(ReplayAdmissionCode.UnsafeEntry);
            if (length > limit || length < 0 || hash is not null && length != limit)
                throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle!, bytes.AsSpan(offset), offset);
                if (read == 0) throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
                offset += read;
            }
            if (!ReviewedStagedFileAccess.TryInspectRegular(handle!, out var after, out var afterLength) ||
                identity != after || afterLength != length || !ReplayFileLinks.IsSingleLink(handle!) || !parent.StillMatches() ||
                hash is not null && AgentCanonical.HashRaw(bytes) != hash)
                throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
            _ = ReplayLimits.Utf8.GetCharCount(bytes);
            return ImmutableArray.CreateRange(bytes);
        }
    }
}
