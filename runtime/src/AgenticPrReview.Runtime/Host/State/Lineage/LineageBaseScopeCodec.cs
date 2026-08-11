using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageBaseScopeCodec
{
    internal static bool TryEncode(
        LineageBaseScope? scope,
        out byte[] canonical)
    {
        canonical = [];
        if (!LineageValidation.IsValid(scope))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.BaseScopeDomain);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteString(scope!.RepositoryId);
        writer.WriteString(scope.TrustedWorkflowIdentity);
        writer.WriteString(scope.TrustedSourceIdentity);
        writer.WriteInt64(scope.PullRequestNumber);
        writer.WriteString(scope.Provider);
        writer.WriteString(scope.Model);
        writer.WriteString(scope.Adapter);
        writer.WriteString(scope.ConfigSha256);
        writer.WriteString(scope.InstructionSha256);
        writer.WriteString(scope.ToolsetSha256);
        writer.WriteString(scope.LimitsSha256);
        writer.WriteString(scope.PayloadBuildIdentity);
        canonical = writer.ToArray();
        if (canonical.Length is < 1 or >
            Locator.LocatorRootFormat.MaximumCanonicalNameInputBytes)
        {
            CryptographicOperations.ZeroMemory(canonical);
            canonical = [];
            return false;
        }

        return true;
    }

    internal static bool TryDigest(
        LineageBaseScope? scope,
        out string digest)
    {
        digest = string.Empty;
        if (!TryEncode(scope, out var canonical))
        {
            return false;
        }

        try
        {
            digest = Convert.ToHexStringLower(SHA256.HashData(canonical));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }
}
