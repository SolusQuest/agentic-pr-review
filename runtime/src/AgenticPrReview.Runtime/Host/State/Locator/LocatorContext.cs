using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal sealed class LocatorContext : IDisposable
{
    private readonly AuthorizedLocatorAccess authority;
    private LocatorStateKeyRing? keys;
    private readonly string repositoryId;
    private readonly TimeProvider timeProvider;
    private readonly object retirementEvidenceIssuer = new();
    private byte[]? root;
    private readonly bool currentSingletonProven;

    private LocatorContext(
        AuthorizedLocatorAccess authority,
        LocatorStateKeyRing keys,
        string repositoryId,
        ReadOnlySpan<byte> root,
        bool currentSingletonProven,
        TimeProvider timeProvider)
    {
        this.authority = authority;
        this.keys = keys;
        this.repositoryId = repositoryId;
        this.root = root.ToArray();
        this.currentSingletonProven = currentSingletonProven;
        this.timeProvider = timeProvider;
    }

    internal static bool TryCreate(
        AuthorizedLocatorAccess? access,
        LocatorStateKeyRing keys,
        ReadOnlySpan<byte> root,
        bool currentSingletonProven,
        TimeProvider timeProvider,
        out LocatorContext? context)
    {
        context = null;
        if (access is null ||
            root.Length != LocatorRootFormat.RootBytes ||
            !access.TryGetRepositoryId(access, out var repositoryId) ||
            !keys.TryClone(access, out var ownedKeys) ||
            ownedKeys is null)
        {
            return false;
        }

        try
        {
            context = new LocatorContext(
                access,
                ownedKeys,
                repositoryId,
                root,
                currentSingletonProven,
                timeProvider);
            return true;
        }
        catch
        {
            ownedKeys.Dispose();
            throw;
        }
    }

    internal bool TryDeriveOpaqueName(
        AuthorizedLocatorAccess? access,
        string objectClass,
        ReadOnlySpan<byte> canonicalScope,
        out OpaqueStoreName? name)
    {
        name = null;
        var currentRoot = Volatile.Read(ref root);
        var currentKeys = Volatile.Read(ref keys);
        if (currentKeys is null ||
            !currentKeys.Allows(access) ||
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
        var currentKeys = Volatile.Read(ref keys);
        if (!ReferenceEquals(authority, access) ||
            Volatile.Read(ref root) is null ||
            currentKeys is null ||
            !currentKeys.TryGetCurrent(access, out resolved) ||
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
        var currentKeys = Volatile.Read(ref keys);
        if (!ReferenceEquals(authority, access) ||
            Volatile.Read(ref root) is null ||
            currentKeys is null ||
            !currentKeys.TryGetApprovedRead(access, keyId, out resolved) ||
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

    internal bool TryCapturePreviousKeyRetirementEvidence(
        AuthorizedLocatorAccess? access,
        bool enumerationComplete,
        ImmutableArray<LocatorRequiredDependency> requiredDependencies,
        out LocatorPreviousKeyRetirementEvidence? evidence)
    {
        evidence = null;
        var currentKeys = Volatile.Read(ref keys);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (!ReferenceEquals(authority, access) ||
            Volatile.Read(ref root) is null ||
            currentKeys?.PreviousKeyId is null ||
            !currentKeys.Allows(access) ||
            now is < 0 or > RestrictedStateFormat.MaximumUnixSeconds ||
            requiredDependencies.IsDefault ||
            requiredDependencies.Any(dependency =>
                !Enum.IsDefined(dependency.Kind) ||
                !IsKeyId(dependency.KeyId) ||
                dependency.ExpiresAtUnixSeconds is < 0 or >
                    RestrictedStateFormat.MaximumUnixSeconds))
        {
            return false;
        }

        evidence = new LocatorPreviousKeyRetirementEvidence(
            retirementEvidenceIssuer,
            authority,
            repositoryId,
            currentKeys.PreviousKeyId,
            now,
            enumerationComplete,
            requiredDependencies);
        return true;
    }

    internal bool CanRetirePreviousKey(
        AuthorizedLocatorAccess? access,
        LocatorPreviousKeyRetirementEvidence evidence)
    {
        var currentKeys = Volatile.Read(ref keys);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return ReferenceEquals(authority, access) &&
            Volatile.Read(ref root) is not null &&
            currentKeys is not null &&
            currentKeys.Allows(access) &&
            currentKeys.PreviousKeyId is not null &&
            currentSingletonProven &&
            evidence.Allows(
                retirementEvidenceIssuer,
                access,
                repositoryId) &&
            evidence.EnumerationComplete &&
            StringComparer.Ordinal.Equals(
                evidence.PreviousKeyId,
                currentKeys.PreviousKeyId) &&
            evidence.ObservedAtUnixSeconds <= now &&
            now - evidence.ObservedAtUnixSeconds <=
                StateRetentionRequirements.PreStickyBudgetSeconds &&
            !evidence.RequiredDependencies.Any(dependency =>
                StringComparer.Ordinal.Equals(
                    dependency.KeyId,
                    currentKeys.PreviousKeyId) &&
                dependency.ExpiresAtUnixSeconds > now);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref root, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }

        Interlocked.Exchange(ref keys, null)?.Dispose();
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

    private static bool IsKeyId(string? value) =>
        value is { Length: LocatorRootFormat.DigestBytes * 2 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal sealed class LocatorPreviousKeyRetirementEvidence
    {
        private readonly object issuer;
        private readonly AuthorizedLocatorAccess authority;

        internal LocatorPreviousKeyRetirementEvidence(
            object issuer,
            AuthorizedLocatorAccess authority,
            string repositoryId,
            string previousKeyId,
            long observedAtUnixSeconds,
            bool enumerationComplete,
            ImmutableArray<LocatorRequiredDependency> requiredDependencies)
        {
            this.issuer = issuer;
            this.authority = authority;
            RepositoryId = repositoryId;
            PreviousKeyId = previousKeyId;
            ObservedAtUnixSeconds = observedAtUnixSeconds;
            EnumerationComplete = enumerationComplete;
            RequiredDependencies = requiredDependencies;
        }

        internal string RepositoryId { get; }
        internal string PreviousKeyId { get; }
        internal long ObservedAtUnixSeconds { get; }
        internal bool EnumerationComplete { get; }
        internal ImmutableArray<LocatorRequiredDependency>
            RequiredDependencies
        { get; }

        internal bool Allows(
            object expectedIssuer,
            AuthorizedLocatorAccess? access,
            string boundRepositoryId) =>
            ReferenceEquals(issuer, expectedIssuer) &&
            ReferenceEquals(authority, access) &&
            StringComparer.Ordinal.Equals(
                RepositoryId,
                boundRepositoryId) &&
            authority.Allows(access, boundRepositoryId);
    }
}
