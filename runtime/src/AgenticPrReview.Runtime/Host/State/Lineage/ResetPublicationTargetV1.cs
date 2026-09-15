namespace AgenticPrReview.Runtime.Host.State.Lineage;

// Data only. Authority comes from the authenticated selected reset head, never this record alone.
internal sealed record ResetPublicationTargetV1(
    byte Operation,
    long RepositoryId,
    long PullRequestNumber,
    long CommentId,
    string CommentUrl,
    string ScopeSha256,
    string BodySha256,
    string HeadSha,
    string SourceAcceptanceIdentity,
    string SourceEpoch,
    long ExpiresAtUnixSeconds)
{
    internal static bool IsValid(ResetPublicationTargetV1? target) =>
        target is not null &&
        target.Operation is >= 1 and <= 3 &&
        target.RepositoryId > 0 && target.PullRequestNumber > 0 && target.CommentId > 0 &&
        LineageValidation.IsText(target.CommentUrl, LineageFormat.MaximumTextBytes) &&
        LineageValidation.IsSha256(target.ScopeSha256) && LineageValidation.IsSha256(target.BodySha256) &&
        target.HeadSha is { Length: 40 } && LineageValidation.IsGitSha(target.HeadSha) &&
        LineageValidation.IsSha256(target.SourceAcceptanceIdentity) &&
        LineageValidation.IsSha256(target.SourceEpoch) &&
        LineageValidation.IsTime(target.ExpiresAtUnixSeconds);

    public override string ToString() => nameof(ResetPublicationTargetV1);
}

internal static class ResetPublicationTargetCodec
{
    // An absent suffix has exactly the pre-existing encoding. A present suffix has one version.
    internal static void Write(LineageBinaryWriter writer, ResetPublicationTargetV1? target)
    {
        if (target is null) return;
        writer.WriteByte(1);
        writer.WriteByte((byte)target.Operation);
        writer.WriteInt64(target.RepositoryId);
        writer.WriteInt64(target.PullRequestNumber);
        writer.WriteInt64(target.CommentId);
        writer.WriteString(target.CommentUrl);
        writer.WriteString(target.ScopeSha256);
        writer.WriteString(target.BodySha256);
        writer.WriteString(target.HeadSha);
        writer.WriteString(target.SourceAcceptanceIdentity);
        writer.WriteString(target.SourceEpoch);
        writer.WriteInt64(target.ExpiresAtUnixSeconds);
    }

    internal static bool TryRead(ref LineageBinaryReader reader, out ResetPublicationTargetV1? target)
    {
        target = null;
        if (reader.IsComplete) return true;
        if (!reader.TryReadByte(out var version) || version != 1 ||
            !reader.TryReadByte(out var operation) ||
            !reader.TryReadInt64(out var repository) ||
            !reader.TryReadInt64(out var pullRequest) ||
            !reader.TryReadInt64(out var comment) ||
            !reader.TryReadString(LineageFormat.MaximumTextBytes, out var url) ||
            !reader.TryReadString(64, out var scope) ||
            !reader.TryReadString(64, out var body) ||
            !reader.TryReadString(40, out var head) ||
            !reader.TryReadString(64, out var acceptance) ||
            !reader.TryReadString(64, out var epoch) ||
            !reader.TryReadInt64(out var expiry) || !reader.IsComplete) return false;
        var value = new ResetPublicationTargetV1(operation,
            repository, pullRequest, comment, url, scope, body, head, acceptance, epoch, expiry);
        if (!ResetPublicationTargetV1.IsValid(value)) return false;
        target = value;
        return true;
    }
}
