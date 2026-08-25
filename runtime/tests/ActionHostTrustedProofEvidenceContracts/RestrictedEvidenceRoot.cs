using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record RestrictedRootMarker(
    string Kind,
    string DestinationIdentitySha256);

public sealed class RestrictedEvidenceRoot
{
    public const string MarkerName = ".apr-r4-e3-restricted-root.json";
    public const string MarkerKind = "apr-r4-e3-maintainer-approved-restricted-root-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private RestrictedEvidenceRoot(
        string path,
        string destinationIdentitySha256,
        ulong device)
    {
        Path = path;
        DestinationIdentitySha256 = destinationIdentitySha256;
        Device = device;
    }

    public string Path { get; }
    public string DestinationIdentitySha256 { get; }
    private ulong Device { get; }

    public static RestrictedEvidenceRoot Open(
        string rootPath,
        string expectedDestinationIdentitySha256,
        IEnumerable<string> prohibitedRoots)
    {
        if (!System.IO.Path.IsPathFullyQualified(rootPath) ||
            !IsSha256(expectedDestinationIdentitySha256))
        {
            throw new InvalidDataException("restricted_root_invalid");
        }

        var full = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(rootPath));
        var root = new DirectoryInfo(full);
        if (!root.Exists || IsLinkOrReparse(root))
        {
            throw new InvalidDataException("restricted_root_invalid");
        }

        foreach (var prohibited in prohibitedRoots.Append(System.IO.Path.GetTempPath()))
        {
            if (string.IsNullOrWhiteSpace(prohibited))
            {
                continue;
            }

            var blocked = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(prohibited));
            if (IsWithin(full, blocked) || IsWithin(blocked, full))
            {
                throw new InvalidDataException("restricted_root_prohibited");
            }
        }

        for (var current = root; current is not null; current = current.Parent)
        {
            if (IsLinkOrReparse(current))
            {
                throw new InvalidDataException("restricted_root_reparse");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode forbidden =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(full) & forbidden) != 0)
            {
                throw new InvalidDataException("restricted_root_permissions_invalid");
            }
        }
        else
        {
            ValidateWindowsOwnerAccess(root);
        }

        var markerPath = System.IO.Path.Join(full, MarkerName);
        var markerFile = new FileInfo(markerPath);
        if (!markerFile.Exists || IsLinkOrReparse(markerFile) ||
            markerFile.Length is < 1 or > 4_096)
        {
            throw new InvalidDataException("restricted_root_marker_invalid");
        }

        byte[] markerBytes;
        EvidenceFileIdentity markerPhysicalIdentity;
        using (var markerHandle = EvidenceFileHandle.OpenNoFollow(markerPath))
        {
            var markerIdentity = EvidenceFileHandle.Identity(markerHandle);
            markerPhysicalIdentity = markerIdentity;
            if (markerIdentity.Links != 1 || markerIdentity.Size is < 1 or > 4_096)
            {
                throw new InvalidDataException("restricted_root_marker_invalid");
            }
            using var markerStream = new FileStream(
                markerHandle,
                FileAccess.Read,
                bufferSize: 4_096,
                isAsync: false);
            markerBytes = new byte[checked((int)markerIdentity.Size)];
            markerStream.ReadExactly(markerBytes);
            if (markerStream.ReadByte() != -1 ||
                EvidenceFileHandle.Identity(markerStream.SafeFileHandle) != markerIdentity)
            {
                CryptographicOperations.ZeroMemory(markerBytes);
                throw new InvalidDataException("restricted_root_marker_invalid");
            }
        }
        try
        {
            var marker = JsonSerializer.Deserialize<RestrictedRootMarker>(
                markerBytes,
                EvidenceJson.Options);
            var canonical = CanonicalEvidence.Encode(marker, EvidenceJson.Options);
            try
            {
                if (!markerBytes.AsSpan().SequenceEqual(canonical))
                {
                    throw new InvalidDataException("restricted_root_marker_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            if (marker is null ||
                !StringComparer.Ordinal.Equals(marker.Kind, MarkerKind) ||
                !StringComparer.Ordinal.Equals(
                    marker.DestinationIdentitySha256,
                    expectedDestinationIdentitySha256))
            {
                throw new InvalidDataException("restricted_root_marker_invalid");
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("restricted_root_marker_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(markerBytes);
        }

        return new RestrictedEvidenceRoot(full, expectedDestinationIdentitySha256, markerPhysicalIdentity.Device);
    }

    public string ResolveExistingFile(string relativePath, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            System.IO.Path.IsPathRooted(relativePath) ||
            Encoding.UTF8.GetByteCount(relativePath) > EvidenceLimits.MaximumRelativePathBytes)
        {
            throw new InvalidDataException("restricted_file_invalid");
        }

        var candidate = System.IO.Path.GetFullPath(
            System.IO.Path.Join(Path, relativePath));
        if (!IsWithin(candidate, Path) ||
            StringComparer.OrdinalIgnoreCase.Equals(candidate, Path))
        {
            throw new InvalidDataException("restricted_file_escape");
        }

        var file = new FileInfo(candidate);
        if (!file.Exists || IsLinkOrReparse(file) ||
            file.Length is < 1 || file.Length > maximumBytes)
        {
            throw new InvalidDataException("restricted_file_invalid");
        }

        for (var current = file.Directory; current is not null && IsWithin(current.FullName, Path); current = current.Parent)
        {
            if (IsLinkOrReparse(current))
            {
                throw new InvalidDataException("restricted_file_reparse");
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(current.FullName, Path))
            {
                break;
            }
        }

        return candidate;
    }

    public byte[] ReadCredentialFile(string relativePath, bool base64Key)
    {
        var pinned = ReadPinnedFile(relativePath, EvidenceLimits.MaximumCredentialBytes);
        var bytes = pinned.Bytes;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            if (text.Length == 0 ||
                text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                throw new InvalidDataException("credential_file_invalid");
            }

            if (!base64Key)
            {
                return bytes.ToArray();
            }

            if (text.Length != 44)
            {
                throw new InvalidDataException("credential_file_invalid");
            }

            var decoded = Convert.FromBase64String(text);
            if (decoded.Length != 32 ||
                !StringComparer.Ordinal.Equals(Convert.ToBase64String(decoded), text))
            {
                CryptographicOperations.ZeroMemory(decoded);
                throw new InvalidDataException("credential_file_invalid");
            }

            return decoded;
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException or FormatException)
        {
            throw new InvalidDataException("credential_file_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public PinnedEvidenceFile ReadPinnedFile(string relativePath, int maximumBytes)
    {
        var path = ResolveExistingFile(relativePath, maximumBytes);
        using var handle = EvidenceFileHandle.OpenNoFollow(path);
        var before = EvidenceFileHandle.Identity(handle);
        if (before.Device != Device || before.Links != 1 || before.Size is < 1 || before.Size > maximumBytes)
        {
            throw new InvalidDataException("restricted_file_identity_invalid");
        }
        if (!OperatingSystem.IsWindows())
        {
            const uint groupOrOtherMask = 0x3f;
            if ((before.Mode & groupOrOtherMask) != 0 ||
                before.Owner != EvidenceFileHandle.EffectiveUserId())
            {
                throw new InvalidDataException("restricted_file_permissions_invalid");
            }
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4_096, isAsync: false);
        var bytes = new byte[checked((int)before.Size)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || EvidenceFileHandle.Identity(stream.SafeFileHandle) != before)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("restricted_file_replaced");
        }
        var identity = CanonicalEvidence.Sha256(Encoding.UTF8.GetBytes(before.Canonical));
        return new PinnedEvidenceFile(bytes, identity);
    }

    public void RemoveCredentialFile(string relativePath)
    {
        var path = ResolveExistingFile(relativePath, EvidenceLimits.MaximumCredentialBytes);
        File.Delete(path);
        if (File.Exists(path))
        {
            throw new InvalidDataException("credential_file_removal_invalid");
        }
    }

    public static bool IsWithin(string candidate, string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, candidate);
        return !System.IO.Path.IsPathFullyQualified(relative) &&
            !StringComparer.Ordinal.Equals(relative, "..") &&
            !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public static bool IsSinglePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !StringComparer.Ordinal.Equals(value, value.Trim()) ||
            StringComparer.Ordinal.Equals(value, ".") ||
            StringComparer.Ordinal.Equals(value, "..") ||
            value.EndsWith(".", StringComparison.Ordinal) ||
            System.IO.Path.IsPathFullyQualified(value) ||
            value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            value.Any(character => char.IsControl(character)))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= EvidenceLimits.MaximumNameBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static string ResolveChildPath(string parent, string child)
    {
        if (!System.IO.Path.IsPathFullyQualified(parent) || !IsSinglePathSegment(child))
        {
            throw new InvalidDataException("restricted_child_path_invalid");
        }

        var fullParent = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(parent));
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Join(fullParent, child));
        if (!IsWithin(candidate, fullParent) ||
            StringComparer.OrdinalIgnoreCase.Equals(candidate, fullParent))
        {
            throw new InvalidDataException("restricted_child_path_invalid");
        }

        return candidate;
    }

    private static bool IsLinkOrReparse(FileSystemInfo value) =>
        value.LinkTarget is not null ||
        (value.Attributes & FileAttributes.ReparsePoint) != 0;

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsOwnerAccess(DirectoryInfo root)
    {
        var current = WindowsIdentity.GetCurrent().User ??
            throw new InvalidDataException("restricted_root_owner_invalid");
        var security = root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != current)
        {
            throw new InvalidDataException("restricted_root_owner_invalid");
        }
        var allowedPrincipals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            current.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        };
        const FileSystemRights mutationRights =
            FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;
        foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & mutationRights) != 0 &&
                rule.IdentityReference is SecurityIdentifier sid &&
                !allowedPrincipals.Contains(sid.Value)))
        {
            throw new InvalidDataException("restricted_root_permissions_invalid");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
