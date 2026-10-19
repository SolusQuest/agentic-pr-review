using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

// The existing encrypted snapshot transaction engine selects this single
// plaintext arm for direct managed review. It never invokes a SESSION reader.
internal sealed class SolusDirectStateAdmission(SolusDirectReviewAuthorization authorization,
    SolusFreshHostContext targets, SolusRestrictedStateCompatibility compatibility)
    : IRestrictedStateSessionAdmission
{
    public RestrictedStateSessionAdmissionResult Admit(AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext, RestrictedStateSessionAdmissionContext context)
    {
        if (!authorization.IsLive || !targets.MatchesDirect(authorization) || access is null ||
            access.Scope != authorization.Root.Access.Scope || context is null ||
            !RestrictedStateValidation.IsLowerHex(context.ProducerBaseSha, 40) ||
            !RestrictedStateValidation.IsLowerHex(context.ProducerHeadSha, 40) ||
            context.Generation is < 0 or > RestrictedStateFormat.MaximumGeneration ||
            (context.Generation == 0 ? context.PredecessorEnvelopeSha256 is not null :
                !RestrictedStateValidation.IsLowerHex(context.PredecessorEnvelopeSha256, 64)) ||
            !SolusRestrictedStateSnapshotCodec.TryRead(plaintext.Span, compatibility, out var record, out _) ||
            record is null)
            return RestrictedStateSessionAdmissionResult.Failure();

        return RestrictedStateSessionAdmissionResult.Success(RestrictedStateAdmittedSession.FromSolus(
            plaintext.ToArray(), SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext.Span),
            context.ProducerBaseSha, context.ProducerHeadSha, context.Generation,
            context.PredecessorEnvelopeSha256, record));
    }
}
