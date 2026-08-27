using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceAssembler;

internal sealed class PublicSurfaceCorpusLease : IDisposable
{
    private const int MaximumFiles = 4096;
    private const long MaximumTotalBytes = EvidenceLimits.MaximumDocumentBytes * 256L;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly HashSet<string> ExcludedDirectories =
        [".git", "node_modules", "bin", "obj"];
    private readonly CorpusRoot[] roots;
    private readonly List<CorpusFileLease> files;
    private bool disposed;

    private PublicSurfaceCorpusLease(CorpusRoot[] roots, List<CorpusFileLease> files)
    {
        this.roots = roots;
        this.files = files;
    }

    internal IReadOnlyList<CorpusFileLease> Files => files;

    internal static PublicSurfaceCorpusLease Open(
        string repositoryRoot,
        string worktreeRoot,
        string logRoot)
    {
        var roots = new List<CorpusRoot> { new("repository", repositoryRoot) };
        if (!PathComparer.Equals(repositoryRoot, worktreeRoot))
        {
            roots.Add(new("worktree", worktreeRoot));
        }
        if (roots.All(item => !PathComparer.Equals(item.Path, logRoot)))
        {
            roots.Add(new("logs", logRoot));
        }

        var files = new List<CorpusFileLease>();
        try
        {
            foreach (var root in roots)
            {
                Enumerate(root, root.Path, files);
            }
            return new PublicSurfaceCorpusLease([.. roots], files);
        }
        catch
        {
            foreach (var file in files)
            {
                file.Dispose();
            }
            throw;
        }
    }

    internal void ValidateComplete(string? publicOutputPath, ReadOnlySpan<byte> publicOutput)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var file in files)
        {
            file.Validate();
        }
        var expected = new Dictionary<string, string>(PathComparer);
        foreach (var file in files)
        {
            AddDigest(expected, file.Path, CanonicalEvidence.Sha256(file.Bytes));
        }
        if (publicOutputPath is not null)
        {
            AddDigest(
                expected,
                Path.GetFullPath(publicOutputPath),
                CanonicalEvidence.Sha256(publicOutput));
        }
        var observed = EnumerateDigests(roots);
        if (observed.Count != expected.Count ||
            expected.Any(item => !observed.TryGetValue(item.Key, out var digest) || digest != item.Value))
        {
            throw new InvalidDataException("public_corpus_changed");
        }
        // EnumerateDigests opens the current pathname with sharing compatible
        // with the retained publication handle. The exact expected digest above
        // already binds the observed bytes without reopening through a weaker
        // default-sharing File.ReadAllBytes call.
    }

    internal void AssertAbsent(
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> categories,
        ReadOnlySpan<byte> publicOutput)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var expected = new[]
        {
            "authorization",
            "state_keys",
            "session_plaintext",
            "provider_content",
            "tool_data",
            "host_evidence",
        };
        if (!categories.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                expected.Order(StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            categories.Any(item => item.Value.Count == 0 || item.Value.Any(value => value.Length < 16)))
        {
            throw new InvalidDataException("public_scan_category_invalid");
        }
        foreach (var category in categories.Values)
        {
            foreach (var protectedValue in category)
            {
                foreach (var file in files)
                {
                    if (file.Bytes.AsSpan().IndexOf(protectedValue) >= 0)
                    {
                        throw new InvalidDataException("public_scan_leak");
                    }
                }
                if (publicOutput.IndexOf(protectedValue) >= 0)
                {
                    throw new InvalidDataException("public_scan_leak");
                }
            }
        }
    }

    internal void AssertExactDocumentAbsent(ReadOnlySpan<byte> protectedDocument, ReadOnlySpan<byte> publicOutput)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (protectedDocument.Length < 16)
        {
            throw new InvalidDataException("public_scan_category_invalid");
        }
        foreach (var file in files)
        {
            if (file.Bytes.AsSpan().IndexOf(protectedDocument) >= 0)
            {
                throw new InvalidDataException("public_scan_leak");
            }
        }
        if (publicOutput.IndexOf(protectedDocument) >= 0)
        {
            throw new InvalidDataException("public_scan_leak");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (var file in files)
        {
            file.Dispose();
        }
    }

    private static void Enumerate(CorpusRoot root, string current, List<CorpusFileLease> result)
    {
        foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("public_corpus_link_invalid");
            }
            if (entry is DirectoryInfo directory)
            {
                if (ExcludedDirectories.Contains(directory.Name) ||
                    (directory.Name == "worktrees" && directory.Parent?.Name == ".codex"))
                {
                    continue;
                }
                Enumerate(root, directory.FullName, result);
            }
            else if (entry is FileInfo file)
            {
                if (result.Count >= MaximumFiles)
                {
                    throw new InvalidDataException("public_corpus_limit_invalid");
                }
                var lease = CorpusFileLease.Open(root, file.FullName);
                if (result.Sum(item => (long)item.Bytes.Length) > MaximumTotalBytes - lease.Bytes.Length)
                {
                    lease.Dispose();
                    throw new InvalidDataException("public_corpus_limit_invalid");
                }
                result.Add(lease);
            }
        }
    }

    private static Dictionary<string, string> EnumerateDigests(IReadOnlyList<CorpusRoot> roots)
    {
        var result = new List<CorpusFileLease>();
        try
        {
            foreach (var root in roots)
            {
                Enumerate(root, root.Path, result);
            }
            var digests = new Dictionary<string, string>(PathComparer);
            foreach (var item in result)
            {
                AddDigest(digests, item.Path, CanonicalEvidence.Sha256(item.Bytes));
            }
            return digests;
        }
        finally
        {
            foreach (var item in result)
            {
                item.Dispose();
            }
        }
    }

    private static void AddDigest(IDictionary<string, string> values, string path, string digest)
    {
        if (values.TryGetValue(path, out var existing) && existing != digest)
        {
            throw new InvalidDataException("public_corpus_changed");
        }
        values[path] = digest;
    }

}

internal sealed record CorpusRoot(string Label, string Path);

internal sealed class CorpusFileLease : IDisposable
{
    private readonly FileStream stream;
    private readonly byte[] digest;
    private bool disposed;

    private CorpusFileLease(string id, string path, FileStream stream, byte[] bytes, byte[] digest)
    {
        Id = id;
        Path = path;
        this.stream = stream;
        Bytes = bytes;
        this.digest = digest;
    }

    internal string Id { get; }
    internal string Path { get; }
    internal byte[] Bytes { get; }

    internal static CorpusFileLease Open(CorpusRoot root, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 0 or > EvidenceLimits.MaximumDocumentBytes * 16L)
        {
            throw new InvalidDataException("public_corpus_file_invalid");
        }
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            stream.Position = 0;
            var relative = System.IO.Path.GetRelativePath(root.Path, path)
                .Replace('\\', '/');
            return new CorpusFileLease(
                $"{root.Label}:{relative}",
                System.IO.Path.GetFullPath(path),
                stream,
                bytes,
                SHA256.HashData(bytes));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!EvidenceFileHandle.PathNamesRetainedHandle(Path, stream.SafeFileHandle))
        {
            throw new InvalidDataException("public_corpus_changed");
        }
        stream.Position = 0;
        var current = SHA256.HashData(stream);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(current, digest))
            {
                throw new InvalidDataException("public_corpus_changed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        CryptographicOperations.ZeroMemory(Bytes);
        CryptographicOperations.ZeroMemory(digest);
        stream.Dispose();
    }
}
