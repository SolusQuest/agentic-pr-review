using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal sealed class LocatorContext : IDisposable
{
    private readonly AuthorizedLocatorAccess authority;
    private readonly LocatorStateKeyRing keys;
    private byte[]? root;
    private readonly bool currentSingletonProven;

    internal LocatorContext(
        AuthorizedLocatorAccess authority,
        LocatorStateKeyRing keys,
        ReadOnlySpan<byte> root,
        bool currentSingletonProven)
    {
        this.authority = authority;
        this.keys = keys;
        this.root = root.ToArray();
        this.currentSingletonProven = currentSingletonProven;
    }

    internal bool TryDeriveOpaqueName(
        AuthorizedLocatorAccess? access,
        string objectClass,
        ReadOnlySpan<byte> canonicalScope,
        out OpaqueStoreName? name)
    {
        name = null;
        var currentRoot = Volatile.Read(ref root);
        if (!keys.Allows(access) ||
            !ReferenceEquals(authority, access) ||
            currentRoot is null ||
            !IsValidObjectClass(objectClass) ||
            canonicalScope.Length is < 1 or >
                LocatorRootFormat.MaximumCanonicalNameInputBytes)
        {
            return false;
        }

        var value = LocatorCryptography.OpaqueName(
            currentRoot,
            objectClass,
            canonicalScope);
        var candidate = new OpaqueStoreName(value);
        if (!OpaqueStoreValidation.IsValid(candidate))
        {
            return false;
        }

        name = candidate;
        return true;
    }

    internal bool TryCopyCurrentStateKey(
        AuthorizedLocatorAccess? access,
        Span<byte> destination,
        out string keyId)
    {
        keyId = string.Empty;
        LocatorStateKey? resolved = null;
        if (!ReferenceEquals(authority, access) ||
            Volatile.Read(ref root) is null ||
            !keys.TryGetCurrent(access, out resolved) ||
            resolved is null)
        {
            resolved?.Dispose();
            return false;
        }

        using (resolved)
        {
            if (!resolved.TryCopyMaterial(destination))
            {
                return false;
            }

            keyId = resolved.KeyId;
            return true;
        }
    }

    internal bool TryCopyApprovedReadKey(
        AuthorizedLocatorAccess? access,
        string keyId,
        Span<byte> destination)
    {
        LocatorStateKey? resolved = null;
        if (!ReferenceEquals(authority, access) ||
            Volatile.Read(ref root) is null ||
            !keys.TryGetApprovedRead(access, keyId, out resolved) ||
            resolved is null)
        {
            resolved?.Dispose();
            return false;
        }

        using (resolved)
        {
            return resolved.TryCopyMaterial(destination);
        }
    }

    internal bool CanRetirePreviousKey(
        AuthorizedLocatorAccess? access,
        LocatorPreviousKeyRetirementEvidence evidence) =>
        ReferenceEquals(authority, access) &&
        Volatile.Read(ref root) is not null &&
        keys.Allows(access) &&
        keys.HasPrevious &&
        currentSingletonProven &&
        evidence is
        {
            EnumerationComplete: true,
            NoLiveRestrictedStateDependencies: true,
            NoLiveTransactionDependencies: true,
        };

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref root, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private static bool IsValidObjectClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Encoding.UTF8.GetByteCount(value) is > 0 and <= 128;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
