using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageCryptography
{
    internal static bool TryDeriveInitialEpoch(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string baseScopeDigest,
        out string epoch) =>
        TryDeriveEpoch(
            context,
            access,
            LineageFormat.InitialEpochDomain,
            baseScopeDigest,
            previousHeadIdentity: null,
            transitionEvidenceIdentity: null,
            expiryBoundaryUnixSeconds: null,
            producingRunIdentity: null,
            producingRunAttempt: null,
            out epoch);

    internal static bool TryDeriveResetEpoch(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string baseScopeDigest,
        string previousHeadIdentity,
        string resetRequestIdentity,
        string producingRunIdentity,
        long producingRunAttempt,
        out string epoch) =>
        TryDeriveEpoch(
            context,
            access,
            LineageFormat.ResetEpochDomain,
            baseScopeDigest,
            previousHeadIdentity,
            resetRequestIdentity,
            expiryBoundaryUnixSeconds: null,
            producingRunIdentity,
            producingRunAttempt,
            out epoch);

    internal static bool TryDeriveExpiryEpoch(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string baseScopeDigest,
        string previousHeadIdentity,
        string acceptanceIdentity,
        long expiryBoundaryUnixSeconds,
        out string epoch) =>
        TryDeriveEpoch(
            context,
            access,
            LineageFormat.ExpiryEpochDomain,
            baseScopeDigest,
            previousHeadIdentity,
            acceptanceIdentity,
            expiryBoundaryUnixSeconds,
            producingRunIdentity: null,
            producingRunAttempt: null,
            out epoch);

    internal static bool TryDeriveSessionId(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string baseScopeDigest,
        string epoch,
        out string sessionId)
    {
        sessionId = string.Empty;
        if (!LineageValidation.IsSha256(baseScopeDigest) ||
            !LineageValidation.IsSha256(epoch))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(baseScopeDigest);
        writer.WriteString(epoch);
        var input = writer.ToArray();
        Span<byte> derived = stackalloc byte[LineageFormat.DigestBytes];
        try
        {
            if (!context.TryDeriveRootKeyed(
                    access,
                    LineageFormat.SessionDomain,
                    input,
                    derived))
            {
                return false;
            }

            sessionId = Convert.ToHexStringLower(derived);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    internal static string ObjectIdentity(
        ReadOnlySpan<byte> semanticHeader,
        ReadOnlySpan<byte> payload)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.ObjectIdentityDomain);
        writer.WriteBytes(semanticHeader);
        writer.WriteBytes(SHA256.HashData(payload));
        var framed = writer.ToArray();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(framed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static string CorrelationId(ReadOnlySpan<byte> envelope)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.CorrelationDomain);
        writer.WriteBytes(SHA256.HashData(envelope));
        var framed = writer.ToArray();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(framed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static string InventoryDigest(
        IEnumerable<LineageArtifactEvidence> evidence)
    {
        var writer = new LineageBinaryWriter();
        foreach (var item in evidence
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.ObjectId, StringComparer.Ordinal)
            .ThenBy(value => value.ProducingRunIdentity, StringComparer.Ordinal)
            .ThenBy(value => value.ProducingRunAttempt)
            .ThenBy(value => value.ArchiveSha256, StringComparer.Ordinal)
            .ThenBy(value => value.EncryptedObjectSha256, StringComparer.Ordinal)
            .ThenBy(value => value.ExpiresAtUnixSeconds)
            .ThenBy(value => value.Size))
        {
            writer.WriteString(item.Name);
            writer.WriteString(item.ObjectId);
            writer.WriteString(item.ProducingRunIdentity);
            writer.WriteInt64(item.ProducingRunAttempt);
            writer.WriteString(item.ArchiveSha256);
            writer.WriteString(item.EncryptedObjectSha256);
            writer.WriteInt64(item.ExpiresAtUnixSeconds);
            writer.WriteInt64(item.Size);
        }

        var bytes = writer.ToArray();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryDeriveEpoch(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string domain,
        string baseScopeDigest,
        string? previousHeadIdentity,
        string? transitionEvidenceIdentity,
        long? expiryBoundaryUnixSeconds,
        string? producingRunIdentity,
        long? producingRunAttempt,
        out string epoch)
    {
        epoch = string.Empty;
        if (!LineageValidation.IsSha256(baseScopeDigest) ||
            !LineageValidation.IsOptionalSha256(previousHeadIdentity) ||
            !LineageValidation.IsOptionalSha256(transitionEvidenceIdentity) ||
            (expiryBoundaryUnixSeconds is not null &&
                !LineageValidation.IsTime(expiryBoundaryUnixSeconds.Value)) ||
            (producingRunIdentity is not null &&
                !LineageValidation.IsText(
                    producingRunIdentity,
                    LineageFormat.MaximumRunIdentityBytes)) ||
            producingRunAttempt is < 0)
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(baseScopeDigest);
        writer.WriteOptionalString(previousHeadIdentity);
        writer.WriteOptionalString(transitionEvidenceIdentity);
        writer.WriteByte(expiryBoundaryUnixSeconds is null ? (byte)0 : (byte)1);
        if (expiryBoundaryUnixSeconds is not null)
        {
            writer.WriteInt64(expiryBoundaryUnixSeconds.Value);
        }

        writer.WriteOptionalString(producingRunIdentity);
        writer.WriteByte(producingRunAttempt is null ? (byte)0 : (byte)1);
        if (producingRunAttempt is not null)
        {
            writer.WriteInt64(producingRunAttempt.Value);
        }
        var input = writer.ToArray();
        Span<byte> derived = stackalloc byte[LineageFormat.DigestBytes];
        try
        {
            if (!context.TryDeriveRootKeyed(access, domain, input, derived))
            {
                return false;
            }

            epoch = Convert.ToHexStringLower(derived);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(derived);
        }
    }
}
