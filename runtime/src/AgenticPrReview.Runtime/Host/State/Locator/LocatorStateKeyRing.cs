using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal sealed class LocatorStateKey : IDisposable
{
    private byte[]? material;

    internal LocatorStateKey(string keyId, ReadOnlySpan<byte> material)
    {
        KeyId = keyId;
        this.material = material.ToArray();
    }

    internal string KeyId { get; }

    internal bool TryCopyMaterial(Span<byte> destination)
    {
        var current = Volatile.Read(ref material);
        if (current is null || destination.Length != current.Length)
        {
            return false;
        }

        current.CopyTo(destination);
        return true;
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref material, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }
}

internal sealed class LocatorStateKeyRing : IDisposable
{
    private readonly AuthorizedLocatorAccess authority;
    private readonly string repositoryId;
    private byte[]? current;
    private byte[]? previous;

    private LocatorStateKeyRing(
        AuthorizedLocatorAccess authority,
        string repositoryId,
        byte[] current,
        byte[]? previous)
    {
        this.authority = authority;
        this.repositoryId = repositoryId;
        this.current = current;
        this.previous = previous;
        CurrentKeyId = LocatorCryptography.KeyId(current);
        PreviousKeyId = previous is null
            ? null
            : LocatorCryptography.KeyId(previous);
    }

    internal string CurrentKeyId { get; }
    internal string? PreviousKeyId { get; }
    internal bool HasPrevious => PreviousKeyId is not null;

    internal static bool TryCreate(
        AuthorizedLocatorAccess? access,
        string repositoryId,
        string currentBase64,
        string? previousBase64,
        out LocatorStateKeyRing? keyRing,
        out string failureCode)
    {
        keyRing = null;
        failureCode = LocatorCodes.AccessDenied;
        if (access is null || !access.Allows(access, repositoryId))
        {
            return false;
        }

        failureCode = LocatorCodes.KeyUnavailable;
        if (!TryDecodeCanonical(currentBase64, out var current))
        {
            return false;
        }

        byte[]? previous = null;
        try
        {
            if (previousBase64 is not null &&
                !TryDecodeCanonical(previousBase64, out previous))
            {
                return false;
            }

            if (previous is not null &&
                CryptographicOperations.FixedTimeEquals(current, previous))
            {
                CryptographicOperations.ZeroMemory(previous);
                previous = null;
            }

            keyRing = new LocatorStateKeyRing(
                access,
                repositoryId,
                current,
                previous);
            current = [];
            previous = null;
            failureCode = string.Empty;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }
    }

    internal bool Allows(AuthorizedLocatorAccess? access) =>
        authority.Allows(access, repositoryId) &&
        Volatile.Read(ref current) is not null;

    internal bool TryGetCurrent(
        AuthorizedLocatorAccess? access,
        out LocatorStateKey? key)
    {
        key = null;
        var material = Volatile.Read(ref current);
        if (!Allows(access) || material is null)
        {
            return false;
        }

        key = new LocatorStateKey(CurrentKeyId, material);
        return true;
    }

    internal bool TryGetApprovedRead(
        AuthorizedLocatorAccess? access,
        string keyId,
        out LocatorStateKey? key)
    {
        key = null;
        if (!Allows(access))
        {
            return false;
        }

        if (StringComparer.Ordinal.Equals(keyId, CurrentKeyId))
        {
            return TryGetCurrent(access, out key);
        }

        var material = Volatile.Read(ref previous);
        if (material is null ||
            !StringComparer.Ordinal.Equals(keyId, PreviousKeyId))
        {
            return false;
        }

        key = new LocatorStateKey(keyId, material);
        return true;
    }

    internal bool TryDeriveInitialRoot(
        AuthorizedLocatorAccess? access,
        out byte[] root)
    {
        root = [];
        if (!TryGetCurrent(access, out var resolved) || resolved is null)
        {
            return false;
        }

        using (resolved)
        {
            Span<byte> key = stackalloc byte[LocatorRootFormat.KeyBytes];
            try
            {
                if (!resolved.TryCopyMaterial(key))
                {
                    return false;
                }

                root = LocatorCryptography.InitialRoot(key, repositoryId);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public void Dispose()
    {
        var currentMaterial = Interlocked.Exchange(ref current, null);
        var previousMaterial = Interlocked.Exchange(ref previous, null);
        if (currentMaterial is not null)
        {
            CryptographicOperations.ZeroMemory(currentMaterial);
        }

        if (previousMaterial is not null)
        {
            CryptographicOperations.ZeroMemory(previousMaterial);
        }
    }

    private static bool TryDecodeCanonical(
        string? encoded,
        out byte[] decoded)
    {
        decoded = [];
        if (encoded is null || encoded.Length != 44)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[LocatorRootFormat.KeyBytes];
        try
        {
            if (!Convert.TryFromBase64String(
                    encoded,
                    buffer,
                    out var written) ||
                written != LocatorRootFormat.KeyBytes ||
                !StringComparer.Ordinal.Equals(
                    Convert.ToBase64String(buffer),
                    encoded))
            {
                return false;
            }

            decoded = buffer.ToArray();
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
