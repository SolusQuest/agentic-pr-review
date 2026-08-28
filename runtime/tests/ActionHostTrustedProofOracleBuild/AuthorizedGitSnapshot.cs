using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild;

internal sealed class AuthorizedGitSnapshot : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly List<SnapshotFileLease> leases;
    private bool disposed;

    private AuthorizedGitSnapshot(string root, List<SnapshotFileLease> leases)
    {
        Root = root;
        this.leases = leases;
    }

    internal string Root { get; }

    internal static AuthorizedGitSnapshot Materialize(
        string git,
        string repositoryRoot,
        string expectedCommit,
        string expectedTree,
        string destination)
    {
        var commit = RunText(git, repositoryRoot, ["rev-parse", "--verify", $"{expectedCommit}^{{commit}}"]);
        var tree = RunText(git, repositoryRoot, ["rev-parse", "--verify", $"{expectedCommit}^{{tree}}"]);
        if (!StringComparer.Ordinal.Equals(commit, expectedCommit) ||
            !StringComparer.Ordinal.Equals(tree, expectedTree))
        {
            throw new InvalidDataException("oracle_build_source_invalid");
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidDataException("oracle_build_snapshot_invalid");
        }
        Directory.CreateDirectory(destination);
        RestrictDirectory(destination);

        var entries = ParseTree(RunBytes(
            git,
            repositoryRoot,
            ["ls-tree", "-rz", "--full-tree", expectedCommit]));
        var leases = new List<SnapshotFileLease>(entries.Count);
        try
        {
            using var batch = StartGit(git, repositoryRoot, ["cat-file", "--batch"]);
            foreach (var entry in entries)
            {
                var target = ResolveSnapshotPath(destination, entry.Path);
                var parent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(parent);
                WriteBatchBlob(batch, entry.ObjectId, target);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target, UnixFileMode.UserRead);
                }
                else
                {
                    File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                }
                leases.Add(SnapshotFileLease.Open(target));
            }
            batch.StandardInput.Close();
            if (!batch.WaitForExit(30_000) || batch.ExitCode != 0)
            {
                throw new InvalidDataException("oracle_build_snapshot_invalid");
            }
            MakeDirectoriesReadOnly(destination);
            return new AuthorizedGitSnapshot(destination, leases);
        }
        catch
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
            if (Directory.Exists(destination))
            {
                RestoreWritable(destination);
            }
            throw;
        }
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var lease in leases)
        {
            lease.Validate();
        }
        var current = EnumerateFiles(Root);
        if (!current.SequenceEqual(
                leases.Select(item => item.Path).Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("oracle_build_snapshot_changed");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
        RestoreWritable(Root);
    }

    private static List<GitTreeEntry> ParseTree(byte[] bytes)
    {
        try
        {
            var result = new List<GitTreeEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var end = Array.IndexOf(bytes, (byte)0, offset);
                if (end < 0)
                {
                    throw new InvalidDataException("oracle_build_tree_invalid");
                }
                var record = StrictUtf8.GetString(bytes, offset, end - offset);
                offset = end + 1;
                var tab = record.IndexOf('\t');
                var header = tab < 0 ? [] : record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = tab < 0 ? "" : record[(tab + 1)..];
                if (header.Length != 3 || header[0] is not ("100644" or "100755") ||
                    header[1] != "blob" || !IsSha(header[2], 40) ||
                    !IsSafeRelativePath(path) || !seen.Add(path))
                {
                    throw new InvalidDataException("oracle_build_tree_invalid");
                }
                result.Add(new GitTreeEntry(path, header[2]));
            }
            if (result.Count == 0)
            {
                throw new InvalidDataException("oracle_build_tree_invalid");
            }
            return result;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("oracle_build_tree_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteBatchBlob(Process batch, string objectId, string target)
    {
        batch.StandardInput.WriteLine(objectId);
        batch.StandardInput.Flush();
        var header = ReadAsciiLine(batch.StandardOutput.BaseStream);
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != objectId || parts[1] != "blob" ||
            !int.TryParse(parts[2], out var length) || length < 0 || length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("oracle_build_blob_invalid");
        }
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[Math.Min(length, 64 * 1024)];
        var gitHeader = Encoding.ASCII.GetBytes($"blob {length}\0");
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        try
        {
            identity.AppendData(gitHeader);
            var remaining = length;
            while (remaining > 0)
            {
                var read = batch.StandardOutput.BaseStream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException("oracle_build_blob_invalid");
                }
                output.Write(buffer, 0, read);
                identity.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (batch.StandardOutput.BaseStream.ReadByte() != '\n')
            {
                throw new InvalidDataException("oracle_build_blob_invalid");
            }
            output.Flush(flushToDisk: true);
            var digest = identity.GetHashAndReset();
            try
            {
                if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(digest), objectId))
                {
                    throw new InvalidDataException("oracle_build_blob_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(gitHeader);
        }
    }

    private static string ReadAsciiLine(Stream stream)
    {
        var bytes = new List<byte>(96);
        while (bytes.Count <= 256)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                break;
            }
            if (value == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]);
            }
            bytes.Add((byte)value);
        }
        throw new InvalidDataException("oracle_build_blob_invalid");
    }

    private static string ResolveSnapshotPath(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts.RestrictedEvidenceRoot.IsWithin(candidate, root) ||
            StringComparer.OrdinalIgnoreCase.Equals(candidate, root))
        {
            throw new InvalidDataException("oracle_build_tree_invalid");
        }
        return candidate;
    }

    internal static bool IsSafeRelativePath(string value) =>
        value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= 1024 &&
        value.IsNormalized(NormalizationForm.FormC) &&
        !Path.IsPathFullyQualified(value) &&
        !value.Contains('\\') &&
        value.Split('/').All(IsSafeSegment);

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(character => character < ' ' || "<>:\"\\|?*".Contains(character)))
        {
            return false;
        }
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$") &&
            !RegexDeviceName(stem);
    }

    private static bool RegexDeviceName(string value) =>
        value.Length == 4 && (value.StartsWith("COM", StringComparison.Ordinal) ||
            value.StartsWith("LPT", StringComparison.Ordinal)) && value[3] is >= '1' and <= '9';

    private static bool IsSha(string value, int length) =>
        value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] RunBytes(string git, string workingDirectory, IReadOnlyList<string> arguments)
    {
        using var process = StartGit(git, workingDirectory, arguments);
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000) || process.ExitCode != 0 || error.Length > 4096 || memory.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("oracle_build_git_invalid");
        }
        return memory.ToArray();
    }

    private static string RunText(string git, string workingDirectory, IReadOnlyList<string> arguments) =>
        Encoding.UTF8.GetString(RunBytes(git, workingDirectory, arguments)).TrimEnd('\r', '\n');

    private static Process StartGit(string git, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var name in start.Environment.Keys
                     .Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            start.Environment.Remove(name);
        }
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return Process.Start(start) ?? throw new InvalidDataException("oracle_build_git_invalid");
    }

    private static IEnumerable<string> EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase);

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void MakeDirectoriesReadOnly(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                SetWindowsAccess(file, isDirectory: false, FileSystemRights.ReadAndExecute |
                    FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes |
                    FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
            }
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(value => value.Length))
            {
                SetWindowsAccess(directory, isDirectory: true, FileSystemRights.ReadAndExecute |
                    FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes |
                    FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions |
                    FileSystemRights.Synchronize);
            }
            SetWindowsAccess(root, isDirectory: true, FileSystemRights.ReadAndExecute |
                FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes |
                FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions |
                FileSystemRights.Synchronize);
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(value => value.Length))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static void RestoreWritable(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsAccess(root, isDirectory: true, FileSystemRights.FullControl, setOwner: false);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                SetWindowsAccess(directory, isDirectory: true, FileSystemRights.FullControl, setOwner: false);
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                SetWindowsAccess(file, isDirectory: false, FileSystemRights.FullControl, setOwner: false);
                File.SetAttributes(file, FileAttributes.Normal);
            }
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsAccess(
        string path,
        bool isDirectory,
        FileSystemRights rights,
        bool setOwner = true)
    {
        var current = WindowsIdentity.GetCurrent().User ??
            throw new InvalidDataException("oracle_build_snapshot_acl_invalid");
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (setOwner)
        {
            security.SetOwner(current);
        }
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (security is DirectorySecurity directorySecurity)
        {
            new DirectoryInfo(path).SetAccessControl(directorySecurity);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    private sealed class SnapshotFileLease : IDisposable
    {
        private readonly FileStream stream;
        private readonly byte[] digest;

        private SnapshotFileLease(string path, FileStream stream, byte[] digest)
        {
            Path = path;
            this.stream = stream;
            this.digest = digest;
        }

        internal string Path { get; }

        internal static SnapshotFileLease Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return new SnapshotFileLease(path, stream, SHA256.HashData(stream));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void Validate()
        {
            stream.Position = 0;
            var current = SHA256.HashData(stream);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(current, digest))
                {
                    throw new InvalidDataException("oracle_build_snapshot_changed");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(digest);
            stream.Dispose();
        }
    }

    private sealed record GitTreeEntry(string Path, string ObjectId);
}
