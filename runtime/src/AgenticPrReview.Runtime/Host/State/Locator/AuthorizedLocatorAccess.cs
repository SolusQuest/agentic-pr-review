using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal sealed class AuthorizedLocatorAccess : IDisposable
{
    private byte[]? identity;
    private readonly string repositoryId;

    private AuthorizedLocatorAccess(string repositoryId)
    {
        if (!IsValidRepositoryId(repositoryId))
        {
            throw new ArgumentException(
                "A bounded repository identity is required.",
                nameof(repositoryId));
        }

        this.repositoryId = repositoryId;
        identity = RandomNumberGenerator.GetBytes(LocatorRootFormat.KeyBytes);
    }

    internal bool Allows(
        AuthorizedLocatorAccess? candidate,
        string expectedRepositoryId) =>
        ReferenceEquals(this, candidate) &&
        Volatile.Read(ref identity) is not null &&
        StringComparer.Ordinal.Equals(
            repositoryId,
            expectedRepositoryId);

    internal bool TryGetRepositoryId(
        AuthorizedLocatorAccess? candidate,
        out string value)
    {
        value = string.Empty;
        if (!Allows(candidate, repositoryId))
        {
            return false;
        }

        value = repositoryId;
        return true;
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref identity, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private static bool IsValidRepositoryId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Encoding.UTF8.GetByteCount(value) is > 0 and <= 512;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
