using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed partial class RetainedStateTransactionService
{
    internal async Task<RetainedStateTransactionResult<RetainedStatePreparedCandidate>> PrepareSolusAsync(
        RetainedStateTransactionAuthority authority, SolusRestrictedStateRecord record,
        R4ValidatedPublicationReview review, CancellationToken cancellationToken, SolusConsumedContext? consumed = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.Cancelled);
        using var lease = await authority.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null || !authority.TryGetBinding(lease, out var binding) || binding is null)
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(
                cancellationToken.IsCancellationRequested ? RetainedStateTransactionCodes.Cancelled : RetainedStateTransactionCodes.Invalid);
        RetainedStateObservation? observation = null;
        if (consumed is not null)
        {
            if (!authority.TryReadTrustedTime(lease, out var now) ||
                !RetainedStateRetention.TryCandidate(now, out var expiry, out _))
                return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.Invalid);
            var observed = await authority.ObserveAsync(lease, expiry, now, cancellationToken).ConfigureAwait(false);
            if (!observed.Succeeded || observed.Value is null)
                return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(observed.Code);
            observation = observed.Value;
        }
        using var admittedObservation = observation;
        SolusRetainedStateMaterial? material = null;
        var built = consumed is null
            ? authority.TryBuildSolusSuccessor(lease, record, review, out material)
            : observation is not null && authority.TryBuildRestoredSolusSuccessor(lease, consumed, record,
                review, observation, out material);
        if (!built || material is null)
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(
                cancellationToken.IsCancellationRequested ? RetainedStateTransactionCodes.Cancelled : RetainedStateTransactionCodes.Invalid);
        byte[] publicationBytes = [];
        try
        {
            if (!AcceptedStatePublicationPayloadCodec.TryEncode(material.Publication, out publicationBytes))
                return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.Invalid);
            return PrepareGeneration(authority, lease, binding, null, material.Envelope, material.RecordSha256,
                binding.Reviewed.BaseSha, binding.Reviewed.HeadSha, material.Generation,
                material.PredecessorEnvelopeSha256, material.Publication,
                publicationBytes, material.Projection, material.PreparedAt, material.LogicalExpiry, material.RequiredPlatformExpiry);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            OverflowException or CryptographicException)
        { return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.Invalid); }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Envelope);
            CryptographicOperations.ZeroMemory(publicationBytes);
        }
    }

    // Native SESSION and S1 Solus material share exactly the same generation,
    // authenticated control envelope, identity and live-candidate issuance.
    private RetainedStateTransactionResult<RetainedStatePreparedCandidate> PrepareGeneration(
        RetainedStateTransactionAuthority authority, RetainedStateAuthorityLease lease,
        RetainedStateTransactionBinding binding, AgentRunRequest? run, byte[] stateEnvelope,
        string stateDigest, string producerBase, string producerHead, long generationNumber,
        string? predecessorEnvelope, ValidatedPublicationPayloadV1 validatedPublication, byte[] publicationBytes,
        RetainedStateCurrentReviewProjection currentReview, long preparedAt, long logicalExpiry, long requiredPlatformExpiry)
    {
        byte[] generationBytes = [], outerEnvelope = [];
        var envelopeCode = RetainedStateTransactionCodes.Invalid;
        try
        {
            var generation = new StateGenerationRecordV1(
                ImmutableArray.CreateRange(stateEnvelope),
                RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope),
                stateDigest,
                producerBase,
                producerHead,
                generationNumber,
                predecessorEnvelope,
                binding.CurrentLogicalGenerationIdentity,
                preparedAt,
                logicalExpiry,
                ImmutableArray.CreateRange(publicationBytes),
                AcceptedStateRecordValidation.Sha256(publicationBytes),
                binding.Policy.PolicyIdentitySha256,
                binding.Policy.ConfigSha256,
                binding.Policy.InstructionsSha256,
                binding.Policy.PayloadSha256,
                binding.Policy.BuildDiscriminator);
            if (!AcceptedStateGenerationRecordCodec.TryEncode(
                    generation,
                    out generationBytes) ||
                !AcceptedStateIdentity.TryComputeLogicalGeneration(
                    generationBytes,
                    binding.SelectedLineage.BaseScopeDigest,
                    binding.SelectedLineage.Epoch,
                    binding.SelectedLineage.SessionId,
                    binding.CurrentAcceptanceReceiptIdentity,
                    out var logicalIdentity) ||
                !authority.TryGetPersistenceContext(
                    lease,
                    out var locator,
                    out var locatorAccess,
                    out var baseScope) ||
                locator is null ||
                locatorAccess is null ||
                baseScope is null ||
                !RetainedStatePersistence.TryPrepareEnvelope(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage,
                    StateObjectClass.Candidate,
                    binding.CurrentAcceptanceReceiptIdentity,
                    successorIdentity: null,
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    preparedAt,
                    logicalExpiry,
                    requiredPlatformExpiry,
                    generationBytes,
                    out var name,
                    out outerEnvelope,
                    out var header,
                    out envelopeCode) ||
                name is null ||
                header is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStatePreparedCandidate>.Fail(
                        outerEnvelope.Length == 0
                            ? RetainedStateTransactionCodes.RetentionFailed
                            : envelopeCode);
            }

            if (!authority.TryBindCurrentReviewProjection(
                    lease,
                    logicalIdentity,
                    currentReview))
            {
                return RetainedStateTransactionResult<
                    RetainedStatePreparedCandidate>.Fail(
                        RetainedStateTransactionCodes.Invalid);
            }

            var prepared = RetainedStatePreparedCandidate.Create(
                issuer,
                authority,
                run,
                generation,
                validatedPublication,
                generationBytes,
                name,
                outerEnvelope,
                header,
                logicalIdentity);
            generationBytes = [];
            outerEnvelope = [];
            return RetainedStateTransactionResult<
                RetainedStatePreparedCandidate>.Success(
                    RetainedStateTransactionCodes.Prepared,
                    prepared);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generationBytes);
            CryptographicOperations.ZeroMemory(outerEnvelope);
        }
    }
}
