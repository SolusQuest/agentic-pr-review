using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

// This is authenticated storage data, never a capability or proof of admission.
internal sealed record SolusContextClaimRecord(
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    string SelectedHeadIdentity,
    string AcceptanceIdentity,
    string LogicalGenerationIdentity,
    string EnvelopeSha256,
    string RecordSha256,
    SolusRestrictedStateCheckpoint Source,
    Guid GrantId,
    Guid ExecutionId,
    Guid RoundId,
    long SourceExpiresAtUnixSeconds)
{
    internal bool ConflictsWith(SolusContextClaimRecord other) =>
        BaseScopeDigest == other.BaseScopeDigest && Epoch == other.Epoch &&
        SessionId == other.SessionId &&
        (GrantId == other.GrantId ||
            Source.LogicalWorkId == other.Source.LogicalWorkId &&
            Source.RoundId == other.Source.RoundId);
}

internal static class SolusContextClaimRecordCodec
{
    internal const int MaximumBytes = 4096;
    private const string Magic = "APRCCL01";
    private const string GrantDomain = "apr.solus-context-grant.v1";

    internal static Guid DeriveGrantId(string baseScopeDigest, string epoch,
        string sessionId, string logicalGenerationIdentity, Guid logicalWorkId,
        Guid sourceRoundId, Guid executionId, Guid roundId)
    {
        var writer = new LineageBinaryWriter(MaximumBytes);
        writer.WriteString(GrantDomain);
        writer.WriteString(baseScopeDigest);
        writer.WriteString(epoch);
        writer.WriteString(sessionId);
        writer.WriteString(logicalGenerationIdentity);
        WriteGuid(writer, logicalWorkId);
        WriteGuid(writer, sourceRoundId);
        WriteGuid(writer, executionId);
        WriteGuid(writer, roundId);
        var canonical = writer.ToArray();
        try
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(canonical, digest);
            // Specify byte order rather than relying on Guid's mixed-endian constructor.
            return new Guid(digest[..16], bigEndian: true);
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    internal static bool IsValid(SolusContextClaimRecord? value) =>
        value is not null &&
        new[] { value.BaseScopeDigest, value.Epoch, value.SessionId,
            value.SelectedHeadIdentity, value.AcceptanceIdentity,
            value.LogicalGenerationIdentity, value.EnvelopeSha256, value.RecordSha256 }
            .All(LineageValidation.IsSha256) &&
        SolusRestrictedStateAdmission.IsValidCheckpoint(value.Source) &&
        value.ExecutionId != Guid.Empty && value.RoundId != Guid.Empty &&
        value.ExecutionId != value.Source.ExecutionId &&
        value.RoundId != value.Source.RoundId && value.GrantId != Guid.Empty &&
        LineageValidation.IsTime(value.SourceExpiresAtUnixSeconds) &&
        value.GrantId == DeriveGrantId(value.BaseScopeDigest, value.Epoch,
            value.SessionId, value.LogicalGenerationIdentity, value.Source.LogicalWorkId,
            value.Source.RoundId, value.ExecutionId, value.RoundId);

    internal static bool MatchesHeader(SolusContextClaimRecord value,
        StateControlHeaderV1 header) =>
        header.ObjectClass == StateObjectClass.ContextClaim &&
        header.BaseScopeDigest == value.BaseScopeDigest && header.Epoch == value.Epoch &&
        header.SessionId == value.SessionId &&
        header.PredecessorIdentity == value.AcceptanceIdentity &&
        header.SuccessorIdentity == value.LogicalGenerationIdentity &&
        header.CreatedAtUnixSeconds < value.SourceExpiresAtUnixSeconds &&
        header.LogicalExpiresAtUnixSeconds >= value.SourceExpiresAtUnixSeconds;

    internal static bool TryEncode(SolusContextClaimRecord? value, out byte[] bytes)
    {
        bytes = [];
        if (!IsValid(value)) return false;
        var writer = new LineageBinaryWriter(MaximumBytes);
        writer.WriteString(Magic);
        writer.WriteUInt16(1);
        foreach (var hash in new[] { value!.BaseScopeDigest, value.Epoch, value.SessionId,
            value.SelectedHeadIdentity, value.AcceptanceIdentity, value.LogicalGenerationIdentity,
            value.EnvelopeSha256, value.RecordSha256 }) writer.WriteString(hash);
        foreach (var id in new[] { value.Source.CheckpointId, value.Source.LogicalWorkId,
            value.Source.RoundId, value.Source.ExecutionId, value.GrantId,
            value.ExecutionId, value.RoundId }) WriteGuid(writer, id);
        writer.WriteInt64(value.SourceExpiresAtUnixSeconds);
        bytes = writer.ToArray();
        return true;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out SolusContextClaimRecord? value)
    {
        value = null;
        if (bytes.Length is < 1 or > MaximumBytes) return false;
        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(8, out var magic) || magic != Magic ||
            !reader.TryReadUInt16(out var version) || version != 1) return false;
        var hashes = new string[8];
        for (var i = 0; i < hashes.Length; i++)
            if (!reader.TryReadString(64, out hashes[i]) ||
                !LineageValidation.IsSha256(hashes[i])) return false;
        var ids = new Guid[7];
        for (var i = 0; i < ids.Length; i++)
            if (!reader.TryReadString(32, out var text) ||
                !Guid.TryParseExact(text, "N", out ids[i]) ||
                text != ids[i].ToString("N")) return false;
        if (!reader.TryReadInt64(out var expires) || !reader.IsComplete) return false;
        var candidate = new SolusContextClaimRecord(hashes[0], hashes[1], hashes[2],
            hashes[3], hashes[4], hashes[5], hashes[6], hashes[7],
            new(ids[0], ids[1], ids[2], ids[3]), ids[4], ids[5], ids[6], expires);
        if (!IsValid(candidate)) return false;
        value = candidate;
        return true;
    }

    private static void WriteGuid(LineageBinaryWriter writer, Guid value) =>
        writer.WriteString(value.ToString("N"));
}
