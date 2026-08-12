using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed class RetainedStateTransactionService
{
    private readonly object issuer;

    internal RetainedStateTransactionService(object issuer)
    {
        if (!RestrictedStateService.IsRetainedStateIssuer(issuer))
        {
            throw new ArgumentException(
                "The retained-state issuer is not authorized.",
                nameof(issuer));
        }

        this.issuer = issuer;
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStatePreparedCandidate>> PrepareAsync(
        RetainedStateTransactionAuthority authority,
        AgentRunRequest run,
        R4PreparedPublication publication,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var buildCode = RetainedStateTransactionCodes.AccessDenied;
        if (lease is null ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryBuildSuccessor(
                lease,
                run,
                publication,
                out var artifact,
                out var validatedPublication,
                out buildCode) ||
            artifact is null ||
            validatedPublication is null)
        {
            return RetainedStateTransactionResult<
                RetainedStatePreparedCandidate>.Fail(
                    lease is null
                        ? RetainedStateTransactionCodes.AccessDenied
                        : buildCode);
        }

        byte[] stateEnvelope = [];
        byte[] publicationBytes = [];
        byte[] generationBytes = [];
        byte[] outerEnvelope = [];
        var encryptionCode = RetainedStateTransactionCodes.Invalid;
        var envelopeCode = RetainedStateTransactionCodes.Invalid;
        try
        {
            if (!authority.TryReadTrustedTime(lease, out var preparedAt) ||
                !RetainedStateRetention.TryCandidate(
                    preparedAt,
                    out var logicalExpiry,
                    out var requiredPlatformExpiry) ||
                !authority.TryEncryptSession(
                    lease,
                    artifact,
                    preparedAt,
                    logicalExpiry,
                    out stateEnvelope,
                    out encryptionCode) ||
                !AcceptedStatePublicationPayloadCodec.TryEncode(
                    validatedPublication,
                    out publicationBytes))
            {
                return RetainedStateTransactionResult<
                    RetainedStatePreparedCandidate>.Fail(
                        stateEnvelope.Length == 0
                            ? RetainedStateTransactionCodes.Invalid
                            : encryptionCode);
            }

            var generation = new StateGenerationRecordV1(
                ImmutableArray.CreateRange(stateEnvelope),
                RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope),
                artifact.SessionSha256,
                artifact.Document.ProducerBaseSha,
                artifact.Document.ProducerHeadSha,
                artifact.Document.Generation,
                artifact.Document.PredecessorStateSha256,
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
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            return RetainedStateTransactionResult<
                RetainedStatePreparedCandidate>.Fail(
                    RetainedStateTransactionCodes.Invalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(artifact.Plaintext);
            CryptographicOperations.ZeroMemory(stateEnvelope);
            CryptographicOperations.ZeroMemory(publicationBytes);
            CryptographicOperations.ZeroMemory(generationBytes);
            CryptographicOperations.ZeroMemory(outerEnvelope);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStatePersistedCandidate>> PersistCandidateAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePreparedCandidate prepared,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !prepared.IsIssuedBy(authority) ||
            !prepared.TryGetBytes(
                authority,
                out var generationBytes,
                out var envelopeBytes) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var sentinel = await authority.EnsureSentinelCoverageAsync(
                lease,
                prepared.Header.RequiredPlatformExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(sentinel))
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(sentinel);
        }

        var beforeResult = await authority.ObserveAsync(
                lease,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using (var before = beforeResult.Value)
        {
            if (!beforeResult.Succeeded || before is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStatePersistedCandidate>.Fail(
                        beforeResult.Code);
            }

            if (TryFindPersistedPrepared(
                    before,
                    authority,
                    binding,
                    prepared,
                    envelopeBytes,
                    out var existingMetadata,
                    out var existingInventoryDigest))
            {
                return RetainedStateTransactionResult<
                    RetainedStatePersistedCandidate>.Success(
                        RetainedStateTransactionCodes.Persisted,
                        RetainedStatePersistedCandidate.Create(
                            issuer,
                            authority,
                            prepared,
                            existingMetadata!,
                            existingInventoryDigest!));
            }

            if (!CanAppendCandidate(
                    before,
                    authority,
                    binding,
                    expected: null))
            {
                return RetainedStateTransactionResult<
                    RetainedStatePersistedCandidate>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }
        }

        if (!authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null ||
            !authority.TryGetPersistenceContext(
                lease,
                out var locator,
                out var locatorAccess,
                out var baseScope) ||
            locator is null ||
            locatorAccess is null ||
            baseScope is null)
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var persisted = await persistence.UploadAndReconcileAsync(
                locator,
                locatorAccess,
                baseScope,
                binding.SelectedLineage.BaseScopeDigest,
                prepared.Name,
                envelopeBytes,
                prepared.Header,
                generationBytes,
                binding.ProducingRunIdentity,
                binding.ProducingRunAttempt,
                prepared.Header.RequiredPlatformExpiresAtUnixSeconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!persisted.Succeeded || persisted.Metadata is null ||
            persisted.InventoryDigest is null)
        {
            CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(persisted.Code);
        }

        CryptographicOperations.ZeroMemory(persisted.Payload!);
        return RetainedStateTransactionResult<
            RetainedStatePersistedCandidate>.Success(
                RetainedStateTransactionCodes.Persisted,
                RetainedStatePersistedCandidate.Create(
                    issuer,
                    authority,
                    prepared,
                    persisted.Metadata,
                    persisted.InventoryDigest));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOwnership>> RenewOwnershipAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        RetainedStateOwnership? prior,
        ImmutableArray<RetainedStateOpaqueRecord> expectedP5Records,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !candidate.IsIssuedBy(authority) ||
            !candidate.Prepared.IsIssuedBy(authority) ||
            (prior is not null && !prior.TryConsume(authority)) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(candidate.Prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            prior?.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateOwnership>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            !CanAppendCandidate(observed, authority, binding, candidate) ||
            !MatchesOpaqueEvidence(
                observed,
                authority,
                binding,
                expectedP5Records) ||
            !RetainedStateRetention.CoversPreSticky(
                candidate.Metadata.ExpiresAtUnixSeconds,
                trustedNow) ||
            observed.InventoryDigest is not { } inventoryDigest ||
            observed.SelectedHead is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateOwnership>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var selected = new SelectedLineageSnapshot(
            observed.SelectedHead.Header.BaseScopeDigest,
            observed.SelectedHead.Header.Epoch,
            observed.SelectedHead.Header.SessionId,
            observed.SelectedHead.Header.ObjectIdentity,
            observed.SelectedHead.Head.Transition);
        return RetainedStateTransactionResult<RetainedStateOwnership>.Success(
            RetainedStateTransactionCodes.Owned,
            RetainedStateOwnership.Create(
                issuer,
                authority,
                candidate,
                selected,
                inventoryDigest,
                trustedNow));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecord>> PersistOpaqueAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateOwnership ownership,
        RetainedStateOpaqueWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            request is null ||
            request.ObjectClass is not (
                StateObjectClass.PublicationIntent or
                StateObjectClass.PublicationFailure or
                StateObjectClass.Abandonment) ||
            request.Payload.IsDefaultOrEmpty ||
            request.Payload.Length > LineageFormat.MaximumPayloadBytes ||
            !LineageValidation.IsTime(
                request!.SemanticRequiredExpiresAtUnixSeconds) ||
            !StringComparer.Ordinal.Equals(
                request.PredecessorIdentity,
                ownership.Candidate.Prepared.Header.ObjectIdentity) ||
            !ownership.TryConsume(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            request.SemanticRequiredExpiresAtUnixSeconds < trustedNow ||
            !RetainedStateRetention.TryOpaque(
                trustedNow,
                request.SemanticRequiredExpiresAtUnixSeconds,
                out var requiredPlatformExpiry))
        {
            ownership.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var sentinel = await authority.EnsureSentinelCoverageAsync(
                lease,
                requiredPlatformExpiry,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(sentinel))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(sentinel);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                ownership.Candidate.Prepared.Header
                    .LogicalExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            !CanAppendCandidate(
                observed,
                authority,
                binding,
                ownership.Candidate) ||
            !StringComparer.Ordinal.Equals(
                observed.InventoryDigest,
                ownership.InventoryDigest) ||
            observed.Snapshot is not { } snapshot ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == request.ObjectClass) >=
                    LineageFormat.MaximumPhysicalPerClass ||
            snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == request.ObjectClass) ||
            snapshot.Unknown.Any(item =>
                item.Metadata.Reference.Name ==
                    snapshot.Names[request.ObjectClass]))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        byte[] envelope = [];
        var envelopeCode = RetainedStateTransactionCodes.Invalid;
        if (!authority.TryGetPersistenceContext(
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
                request.ObjectClass,
                request.PredecessorIdentity,
                request.SuccessorIdentity,
                binding.ProducingRunIdentity,
                binding.ProducingRunAttempt,
                trustedNow,
                request.SemanticRequiredExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                request.Payload.AsSpan(),
                out var name,
                out envelope,
                out var header,
                out envelopeCode) ||
            name is null ||
            header is null ||
            !authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null)
        {
            CryptographicOperations.ZeroMemory(envelope);
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(envelopeCode);
        }

        try
        {
            var persisted = await persistence.UploadAndReconcileAsync(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage.BaseScopeDigest,
                    name,
                    envelope,
                    header,
                    request.Payload.AsMemory(),
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.Succeeded ||
                persisted.Metadata is null ||
                persisted.Header is null ||
                persisted.Payload is null ||
                persisted.InventoryDigest is null)
            {
                CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueRecord>.Fail(persisted.Code);
            }

            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Success(
                    RetainedStateTransactionCodes.Persisted,
                    RetainedStateOpaqueRecord.Create(
                        issuer,
                        authority,
                        request.ObjectClass,
                        persisted.Metadata,
                        persisted.Header,
                        persisted.Payload,
                        persisted.InventoryDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptancePreparation>> PrepareAcceptanceAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateOwnership ownership,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidate = ownership.Candidate;
        var prepared = candidate.Prepared;
        if (lease is null ||
            receipt is null ||
            !MatchesReceipt(receipt, prepared) ||
            !ownership.TryConsume(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var acceptedAt) ||
            !RetainedStateRetention.TryAcceptance(
                acceptedAt,
                out var logicalExpiry,
                out var receiptPlatformExpiry) ||
            candidate.Metadata.ExpiresAtUnixSeconds < logicalExpiry)
        {
            ownership.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var initialResult = await authority.ObserveAsync(
                lease,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                acceptedAt,
                cancellationToken)
            .ConfigureAwait(false);
        using (var initial = initialResult.Value)
        {
            if (!initialResult.Succeeded ||
                initial is null ||
                !CanAppendCandidate(initial, authority, binding, candidate) ||
                !StringComparer.Ordinal.Equals(
                    initial.InventoryDigest,
                    ownership.InventoryDigest))
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        initialResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : initialResult.Code);
            }
        }

        var sentinel = await authority.EnsureSentinelCoverageAsync(
                lease,
                receiptPlatformExpiry,
                acceptedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(sentinel))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(sentinel);
        }

        var refreshed = await authority.RefreshSelectedHeadAsync(
                lease,
                logicalExpiry,
                acceptedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(refreshed) ||
            !authority.TryGetBinding(lease, out binding) ||
            binding is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(refreshed);
        }

        var predecessorCode = await EnsureImmediatePredecessorAsync(
                authority,
                lease,
                binding,
                candidate,
                acceptedAt,
                logicalExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(predecessorCode))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(predecessorCode);
        }

        var beforeResult = await authority.ObserveAsync(
                lease,
                logicalExpiry,
                acceptedAt,
                cancellationToken)
            .ConfigureAwait(false);
        using var before = beforeResult.Value;
        if (!beforeResult.Succeeded ||
            before is null ||
            !CanAppendCandidate(before, authority, binding, candidate) ||
            !PredecessorCovers(before, binding, logicalExpiry) ||
            before.Snapshot is not { } snapshot ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            HasAcceptanceSuccessor(snapshot, binding) ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance) >=
                    LineageFormat.MaximumPhysicalPerClass ||
            before.InventoryDigest is not { } inventoryDigest ||
            before.SelectedHead is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(
                    beforeResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : beforeResult.Code);
        }

        if (!authority.TryGetPersistenceContext(
                lease,
                out var locator,
                out var locatorAccess,
                out var baseScope) ||
            locator is null ||
            locatorAccess is null ||
            baseScope is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var acceptanceReceipt = new AcceptanceReceiptV1(
            prepared.LogicalGenerationIdentity,
            prepared.Header.ObjectIdentity,
            binding.CurrentLogicalGenerationIdentity,
            binding.CurrentAcceptanceReceiptIdentity,
            prepared.Publication.ReviewedHeadSha,
            receipt.Operation,
            receipt.RepositoryId,
            receipt.PullRequestNumber,
            receipt.CommentId,
            receipt.CommentUrl,
            receipt.ScopeSha256,
            receipt.BodySha256,
            prepared.Generation.PublicationPayloadSha256,
            binding.ProducingRunIdentity,
            binding.ProducingRunAttempt,
            acceptedAt,
            logicalExpiry);
        byte[] receiptBytes = [];
        byte[] acceptanceEnvelope = [];
        byte[] recoveryPayload = [];
        var envelopeCode = RetainedStateTransactionCodes.Invalid;
        RetainedStateAcceptanceAttempt? attempt = null;
        try
        {
            if (!AcceptedStateAcceptanceReceiptCodec.TryEncode(
                    acceptanceReceipt,
                    out receiptBytes) ||
                !RetainedStatePersistence.TryPrepareEnvelope(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage,
                    StateObjectClass.Acceptance,
                    binding.CurrentAcceptanceReceiptIdentity,
                    successorIdentity: null,
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    acceptedAt,
                    logicalExpiry,
                    receiptPlatformExpiry,
                    receiptBytes,
                    out var acceptanceName,
                    out acceptanceEnvelope,
                    out var acceptanceHeader,
                    out envelopeCode) ||
                acceptanceName is null ||
                acceptanceHeader is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(envelopeCode);
            }

            attempt = RetainedStateAcceptanceAttempt.Create(
                issuer,
                acceptedAt,
                logicalExpiry,
                receiptPlatformExpiry,
                acceptanceReceipt,
                acceptanceName,
                acceptanceHeader,
                receiptBytes,
                acceptanceEnvelope);
            receiptBytes = [];
            acceptanceEnvelope = [];
            if (!RetainedStateAcceptanceRecoveryCodec.TryEncode(
                    attempt,
                    out recoveryPayload))
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        RetainedStateTransactionCodes.Invalid);
            }

            var selected = new SelectedLineageSnapshot(
                before.SelectedHead.Header.BaseScopeDigest,
                before.SelectedHead.Header.Epoch,
                before.SelectedHead.Header.SessionId,
                before.SelectedHead.Header.ObjectIdentity,
                before.SelectedHead.Head.Transition);
            var refreshedOwnership = RetainedStateOwnership.Create(
                issuer,
                authority,
                candidate,
                selected,
                inventoryDigest,
                acceptedAt,
                logicalExpiry);
            var preparation = RetainedStateAcceptancePreparation.Create(
                issuer,
                authority,
                candidate,
                receipt,
                refreshedOwnership,
                attempt,
                recoveryPayload);
            attempt = null;
            recoveryPayload = [];
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Success(
                    RetainedStateTransactionCodes.Ready,
                    preparation);
        }
        finally
        {
            attempt?.Dispose();
            CryptographicOperations.ZeroMemory(receiptBytes);
            CryptographicOperations.ZeroMemory(acceptanceEnvelope);
            CryptographicOperations.ZeroMemory(recoveryPayload);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptanceEvidence>> CreateAcceptanceEvidenceAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        RetainedStateOwnership ownership,
        ExactHeadRevalidationResult exactHead,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidate = preparation.Candidate;
        var prepared = candidate.Prepared;
        var attempt = preparation.GetAttempt(authority);
        if (lease is null ||
            !preparation.IsIssuedBy(authority) ||
            attempt is null ||
            !ReferenceEquals(ownership.Candidate, candidate) ||
            exactHead is null ||
            !exactHead.MayMutate ||
            !StringComparer.Ordinal.Equals(
                exactHead.FrozenHeadSha,
                prepared.Publication.ReviewedHeadSha) ||
            !StringComparer.Ordinal.Equals(
                exactHead.ObservedHeadSha,
                prepared.Publication.ReviewedHeadSha) ||
            !MatchesReceipt(preparation.Receipt, prepared) ||
            !ownership.TryConsume(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesAcceptanceAttempt(
                attempt,
                preparation.Receipt,
                binding,
                prepared) ||
            !authority.TryReadTrustedTime(lease, out var observationTime) ||
            observationTime > attempt.LogicalExpiresAtUnixSeconds)
        {
            ownership.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceEvidence>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                attempt.LogicalExpiresAtUnixSeconds,
                observationTime,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            !CanAppendCandidate(observed, authority, binding, candidate) ||
            !PredecessorCovers(
                observed,
                binding,
                attempt.LogicalExpiresAtUnixSeconds) ||
            observed.Snapshot is not { } snapshot ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            HasAcceptanceSuccessor(snapshot, binding) ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance) >=
                    LineageFormat.MaximumPhysicalPerClass ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !StringComparer.Ordinal.Equals(
                inventoryDigest,
                ownership.InventoryDigest))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceEvidence>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var transferred = preparation.TakeAttempt(authority);
        if (!ReferenceEquals(transferred, attempt))
        {
            transferred?.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceEvidence>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        return RetainedStateTransactionResult<
            RetainedStateAcceptanceEvidence>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateAcceptanceEvidence.Create(
                    issuer,
                    authority,
                    candidate,
                    preparation.Receipt,
                    exactHead,
                    inventoryDigest,
                    attempt));
    }

    internal async Task<RetainedStateTransactionResult<
        VerifiedRetainedStateAcceptance>> AcceptAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptanceEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (authority is null || evidence is null)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var cachedAttempt = evidence.GetAttempt(authority);
        if (evidence.IsIssuedBy(authority) &&
            cachedAttempt is not null &&
            authority.TryGetTerminalAcceptance(out var cached) &&
            cached is not null &&
            StringComparer.Ordinal.Equals(
                cached.LogicalGenerationIdentity,
                evidence.Candidate.Prepared.LogicalGenerationIdentity) &&
            StringComparer.Ordinal.Equals(
                cached.AcceptanceReceiptIdentity,
                cachedAttempt.Header.ObjectIdentity))
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Success(
                    RetainedStateTransactionCodes.Accepted,
                    cached);
        }

        using var lease = await authority.EnterAsync(
                cachedAttempt is null
                    ? cancellationToken
                    : CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = evidence.Candidate;
        var prepared = candidate.Prepared;
        if (lease is null ||
            !evidence.IsIssuedBy(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesReceipt(evidence.Receipt, prepared) ||
            !evidence.ExactHead.MayMutate ||
            !StringComparer.Ordinal.Equals(
                evidence.ExactHead.FrozenHeadSha,
                binding.Reviewed.HeadSha) ||
            !StringComparer.Ordinal.Equals(
                evidence.ExactHead.ObservedHeadSha,
                binding.Reviewed.HeadSha))
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var attempt = evidence.GetAttempt(authority);
        if (authority.TryGetTerminalAcceptance(
                lease,
                out var terminalAcceptance))
        {
            return attempt is not null &&
                terminalAcceptance is not null &&
                StringComparer.Ordinal.Equals(
                    terminalAcceptance.LogicalGenerationIdentity,
                    prepared.LogicalGenerationIdentity) &&
                StringComparer.Ordinal.Equals(
                    terminalAcceptance.AcceptanceReceiptIdentity,
                    attempt.Header.ObjectIdentity) &&
                MatchesAcceptanceAttempt(
                    attempt,
                    evidence.Receipt,
                    binding,
                    prepared)
                ? RetainedStateTransactionResult<
                    VerifiedRetainedStateAcceptance>.Success(
                        RetainedStateTransactionCodes.Accepted,
                        terminalAcceptance)
                : RetainedStateTransactionResult<
                    VerifiedRetainedStateAcceptance>.Fail(
                        RetainedStateTransactionCodes.AccessDenied);
        }

        if (attempt is null ||
            !MatchesAcceptanceAttempt(
                attempt,
                evidence.Receipt,
                binding,
                prepared) ||
            !authority.TryReadTrustedTime(lease, out var observedAt) ||
            observedAt > attempt.LogicalExpiresAtUnixSeconds ||
            candidate.Metadata.ExpiresAtUnixSeconds <
                attempt.LogicalExpiresAtUnixSeconds)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var acceptedAt = attempt.AcceptedAtUnixSeconds;
        var logicalExpiry = attempt.LogicalExpiresAtUnixSeconds;
        var receiptPlatformExpiry =
            attempt.RequiredPlatformExpiresAtUnixSeconds;
        var reconciled = await ReconcileFrozenAcceptanceAsync(
                authority,
                lease,
                attempt,
                prepared,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (reconciled.Succeeded)
        {
            return reconciled;
        }

        if (!StringComparer.Ordinal.Equals(
                reconciled.Code,
                RetainedStateTransactionCodes.Stale))
        {
            return reconciled;
        }

        var beforeResult = await authority.ObserveAsync(
                lease,
                logicalExpiry,
                observedAt,
                CancellationToken.None)
            .ConfigureAwait(false);
        using (var before = beforeResult.Value)
        {
            if (!beforeResult.Succeeded ||
                before is null ||
                !CanAppendCandidate(
                    before,
                    authority,
                    binding,
                    candidate) ||
                !PredecessorCovers(before, binding, logicalExpiry) ||
                !StringComparer.Ordinal.Equals(
                    before.InventoryDigest,
                    evidence.InventoryDigest) ||
                before.Snapshot is not { } snapshot ||
                !snapshot.UnderRetained.IsEmpty ||
                !snapshot.Unknown.IsEmpty ||
                HasAcceptanceSuccessor(snapshot, binding) ||
                snapshot.Authenticated.Count(item =>
                    item.Header.ObjectClass == StateObjectClass.Acceptance) >=
                        LineageFormat.MaximumPhysicalPerClass)
            {
                return RetainedStateTransactionResult<
                    VerifiedRetainedStateAcceptance>.Fail(
                        beforeResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : beforeResult.Code);
            }
        }

        if (!authority.TryGetPersistenceContext(
                lease,
                out var locator,
                out var locatorAccess,
                out var baseScope) ||
            locator is null ||
            locatorAccess is null ||
            baseScope is null ||
            !authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        if (!attempt.TryGetBytes(
                out var frozenReceiptBytes,
                out var frozenEnvelopeBytes))
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var persisted = await persistence.UploadAndReconcileAsync(
                locator,
                locatorAccess,
                baseScope,
                binding.SelectedLineage.BaseScopeDigest,
                attempt.Name,
                frozenEnvelopeBytes,
                attempt.Header,
                frozenReceiptBytes,
                attempt.Header.ProducingRunIdentity,
                attempt.Header.ProducingRunAttempt,
                receiptPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
        if (!persisted.Succeeded ||
            persisted.Metadata is null ||
            persisted.InventoryDigest is null)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(persisted.Code);
        }

        var afterResult = await authority.ObserveAsync(
                lease,
                logicalExpiry,
                acceptedAt,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var after = afterResult.Value;
        var selected = after?.AcceptedState.Selection?.Current;
        if (!afterResult.Succeeded ||
            after is null ||
            !after.AcceptedState.Succeeded ||
            selected is null ||
            !StringComparer.Ordinal.Equals(
                selected.LogicalGenerationIdentity,
                prepared.LogicalGenerationIdentity) ||
            !StringComparer.Ordinal.Equals(
                selected.ReceiptPhysical.Header.ObjectIdentity,
                attempt.Header.ObjectIdentity) ||
            selected.ReceiptPhysical.Metadata != persisted.Metadata ||
            selected.Receipt.AcceptedAtUnixSeconds != acceptedAt ||
            selected.Receipt.LogicalExpiresAtUnixSeconds != logicalExpiry ||
            after.InventoryDigest is not { } inventoryDigest)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    afterResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : afterResult.Code);
        }

        var verified = VerifiedRetainedStateAcceptance.Create(
            issuer,
            authority,
            prepared.LogicalGenerationIdentity,
            attempt.Header.ObjectIdentity,
            persisted.Metadata,
            acceptedAt,
            logicalExpiry,
            inventoryDigest);
        return authority.TryMarkTerminalAcceptance(lease, verified)
            ? RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Success(
                    RetainedStateTransactionCodes.Accepted,
                    verified)
            : RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Conflict);
    }

    private async Task<RetainedStateTransactionResult<
        VerifiedRetainedStateAcceptance>> ReconcileFrozenAcceptanceAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAuthorityLease lease,
        RetainedStateAcceptanceAttempt attempt,
        RetainedStatePreparedCandidate prepared,
        CancellationToken cancellationToken)
    {
        if (!attempt.TryGetBytes(
                out var receiptBytes,
                out var envelopeBytes) ||
            !authority.TryReadTrustedTime(lease, out var observedAt) ||
            observedAt > attempt.LogicalExpiresAtUnixSeconds)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                attempt.LogicalExpiresAtUnixSeconds,
                observedAt,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        var selected = observed?.AcceptedState.Selection?.Current;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.OutcomeUnknown
                        : observedResult.Code);
        }

        var expectedEnvelopeDigest = new OpaqueStoreEncryptedObjectDigest(
            OpaqueStoreHash.Sha256(envelopeBytes.Span));
        if (snapshot.Unknown.Any(item =>
                item.Metadata.Reference.Name == attempt.Name) ||
            snapshot.UnderRetained.Any(item =>
                item.Metadata.Reference.Name == attempt.Name))
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.OutcomeUnknown);
        }

        var matches = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                item.Header == attempt.Header &&
                item.Metadata.Reference.Name == attempt.Name &&
                item.Metadata.EncryptedObjectDigest ==
                    expectedEnvelopeDigest &&
                item.Metadata.Size == envelopeBytes.Length &&
                item.Payload.AsSpan().SequenceEqual(receiptBytes.Span))
            .ToArray();
        if (matches.Length == 0)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Stale);
        }

        if (matches.Length != 1 ||
            !observed.AcceptedState.Succeeded ||
            selected is null ||
            !StringComparer.Ordinal.Equals(
                selected.LogicalGenerationIdentity,
                prepared.LogicalGenerationIdentity) ||
            selected.Receipt != attempt.Receipt ||
            selected.ReceiptPhysical.Header != attempt.Header ||
            selected.ReceiptPhysical.Metadata != matches[0].Metadata)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var verified = VerifiedRetainedStateAcceptance.Create(
            issuer,
            authority,
            prepared.LogicalGenerationIdentity,
            attempt.Header.ObjectIdentity,
            selected.ReceiptPhysical.Metadata,
            attempt.AcceptedAtUnixSeconds,
            attempt.LogicalExpiresAtUnixSeconds,
            inventoryDigest);
        return authority.TryMarkTerminalAcceptance(lease, verified)
            ? RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Success(
                    RetainedStateTransactionCodes.Accepted,
                    verified)
            : RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Conflict);
    }

    internal async Task<RetainedStateTransactionResult<
        VerifiedRetainedStateAcceptance>> RecoverVerifiedAcceptanceAsync(
        RetainedStateTransactionAuthority authority,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !authority.TryReadTrustedTime(lease, out var observedAt) ||
            !RetainedStateRetention.TryCandidate(
                observedAt,
                out var requiredLogical,
                out _))
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        if (authority.TryGetTerminalAcceptance(
                lease,
                out var terminal) &&
            terminal is not null)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Success(
                    RetainedStateTransactionCodes.Accepted,
                    terminal);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                requiredLogical,
                observedAt,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        var current = observed?.AcceptedState.Selection?.Current;
        if (!observedResult.Succeeded ||
            observed is null ||
            !observed.AcceptedState.Succeeded ||
            current is null ||
            current.Receipt.LogicalExpiresAtUnixSeconds < observedAt ||
            current.ReceiptPhysical.Metadata.ExpiresAtUnixSeconds <
                current.Receipt.LogicalExpiresAtUnixSeconds ||
            observed.Snapshot is not { } snapshot ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            observed.InventoryDigest is not { } inventoryDigest)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var verified = VerifiedRetainedStateAcceptance.Create(
            issuer,
            authority,
            current.LogicalGenerationIdentity,
            current.ReceiptPhysical.Header.ObjectIdentity,
            current.ReceiptPhysical.Metadata,
            current.Receipt.AcceptedAtUnixSeconds,
            current.Receipt.LogicalExpiresAtUnixSeconds,
            inventoryDigest);
        return authority.TryMarkTerminalAcceptance(lease, verified)
            ? RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Success(
                    RetainedStateTransactionCodes.Accepted,
                    verified)
            : RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Conflict);
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStatePersistedCandidate>> RecoverPendingCandidateAsync(
        RetainedStateTransactionAuthority authority,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            authority.HasTerminalAcceptance ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            !RetainedStateRetention.TryCandidate(
                trustedNow,
                out var requiredLogical,
                out _))
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                requiredLogical,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            !MatchesAcceptedTail(observed.AcceptedState, binding) ||
            snapshot.Authenticated.Any(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                Active(item, binding) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity)))
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var pending = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                Active(item, binding) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity))
            .ToArray();
        if (pending.Length != 1 ||
            !AcceptedStateGenerationRecordCodec.TryDecode(
                pending[0].Payload,
                out var generation) ||
            generation is null ||
            !MatchesRecoveredGeneration(generation, binding) ||
            !AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out var publication) ||
            publication is null ||
            !MatchesRecoveredPublication(publication, generation, binding) ||
            pending[0].Header.ObjectClass != StateObjectClass.Candidate ||
            pending[0].Header.CreatedAtUnixSeconds !=
                generation.PreparedAtUnixSeconds ||
            pending[0].Header.LogicalExpiresAtUnixSeconds !=
                generation.PreparedExpiresAtUnixSeconds ||
            !StringComparer.Ordinal.Equals(
                pending[0].Header.ProducingRunIdentity,
                pending[0].Metadata.ProducingRun.Identity) ||
            pending[0].Header.ProducingRunAttempt !=
                pending[0].Metadata.ProducingRun.Attempt ||
            !authority.TryValidateRecoveredGeneration(
                lease,
                generation,
                out _) ||
            !AcceptedStateIdentity.TryComputeLogicalGeneration(
                pending[0].Payload,
                binding.SelectedLineage.BaseScopeDigest,
                binding.SelectedLineage.Epoch,
                binding.SelectedLineage.SessionId,
                binding.CurrentAcceptanceReceiptIdentity,
                out var logicalIdentity))
        {
            return RetainedStateTransactionResult<
                RetainedStatePersistedCandidate>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var prepared = RetainedStatePreparedCandidate.CreateRecovered(
            issuer,
            authority,
            generation,
            publication,
            pending[0].Payload.ToArray(),
            snapshot.Names[StateObjectClass.Candidate],
            pending[0].Header,
            logicalIdentity);
        return RetainedStateTransactionResult<
            RetainedStatePersistedCandidate>.Success(
                RetainedStateTransactionCodes.Persisted,
                RetainedStatePersistedCandidate.Create(
                    issuer,
                    authority,
                    prepared,
                    pending[0].Metadata,
                    inventoryDigest));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecordSet>> QueryOpaqueAsync(
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            objectClass is not (
                StateObjectClass.PublicationIntent or
                StateObjectClass.PublicationFailure or
                StateObjectClass.Abandonment) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            !RetainedStateRetention.TryCandidate(
                trustedNow,
                out var requiredLogical,
                out _))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecordSet>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                requiredLogical,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            snapshot.Unknown.Any(item =>
                item.Metadata.Reference.Name == snapshot.Names[objectClass]) ||
            snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == objectClass))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecordSet>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var records = snapshot.Authenticated
            .Where(item =>
                item.Header.ObjectClass == objectClass &&
                Active(item, binding))
            .Select(item => RetainedStateOpaqueRecord.Create(
                issuer,
                authority,
                objectClass,
                item.Metadata,
                item.Header,
                item.Payload.ToArray(),
                inventoryDigest))
            .ToImmutableArray();
        return RetainedStateTransactionResult<
            RetainedStateOpaqueRecordSet>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateOpaqueRecordSet.Create(records));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptancePreparation>>
        RecoverAcceptancePreparationAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        RetainedStateOpaqueRecord recoveryRecord,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var prepared = candidate?.Prepared;
        if (lease is null ||
            candidate is null ||
            prepared is null ||
            !candidate.IsIssuedBy(authority) ||
            !prepared.IsIssuedBy(authority) ||
            recoveryRecord is null ||
            recoveryRecord.ObjectClass != StateObjectClass.PublicationIntent ||
            !StringComparer.Ordinal.Equals(
                recoveryRecord.Header.PredecessorIdentity,
                prepared.Header.ObjectIdentity) ||
            !recoveryRecord.TryCopyPayload(authority, out var encoded) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var observedAt))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        OpaqueStoreName? name = null;
        StateControlHeaderV1? header = null;
        AcceptanceReceiptV1? recoveredReceipt = null;
        byte[] receiptBytes = [];
        byte[] envelopeBytes = [];
        byte[] canonicalReceipt = [];
        byte[] expectedEnvelope = [];
        byte[] decryptedPayload = [];
        RetainedStateAcceptanceAttempt? attempt = null;
        byte[] canonicalRecovery = [];
        try
        {
            if (!RetainedStateAcceptanceRecoveryCodec.TryDecode(
                    encoded.AsSpan(),
                    out name,
                    out header,
                    out recoveredReceipt,
                    out receiptBytes,
                    out envelopeBytes) ||
                name is null ||
                header is null ||
                recoveredReceipt is null ||
                observedAt > recoveredReceipt.LogicalExpiresAtUnixSeconds ||
                !RetainedStateRetention.TryAcceptance(
                    recoveredReceipt.AcceptedAtUnixSeconds,
                    out var logicalExpiry,
                    out var platformExpiry) ||
                logicalExpiry != recoveredReceipt.LogicalExpiresAtUnixSeconds ||
                header.RequiredPlatformExpiresAtUnixSeconds !=
                    platformExpiry ||
                recoveryRecord.Header.LogicalExpiresAtUnixSeconds <
                    logicalExpiry ||
                candidate.Metadata.ExpiresAtUnixSeconds < logicalExpiry ||
                !AcceptedStateAcceptanceReceiptCodec.TryEncode(
                    recoveredReceipt,
                    out canonicalReceipt) ||
                !receiptBytes.AsSpan().SequenceEqual(canonicalReceipt) ||
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
                    StateObjectClass.Acceptance,
                    binding.CurrentAcceptanceReceiptIdentity,
                    successorIdentity: null,
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    recoveredReceipt.AcceptedAtUnixSeconds,
                    logicalExpiry,
                    platformExpiry,
                    canonicalReceipt,
                    out var expectedName,
                    out expectedEnvelope,
                    out var expectedHeader,
                    out _) ||
                expectedName != name ||
                expectedHeader != header ||
                !StateControlEnvelopeV1Codec.TryDecrypt(
                    locator,
                    locatorAccess,
                    name,
                    envelopeBytes,
                    out var decryptedHeader,
                    out decryptedPayload,
                    out _) ||
                decryptedHeader != header ||
                !decryptedPayload.AsSpan().SequenceEqual(canonicalReceipt) ||
                !StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
                    recoveredReceipt.PublicationOperation,
                    recoveredReceipt.RepositoryId,
                    recoveredReceipt.PullRequestNumber,
                    recoveredReceipt.CommentId,
                    recoveredReceipt.CommentUrl,
                    recoveredReceipt.ScopeSha256,
                    recoveredReceipt.BodySha256,
                    recoveredReceipt.ReviewedHeadSha,
                    out var sticky) ||
                sticky is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            attempt = RetainedStateAcceptanceAttempt.Create(
                issuer,
                recoveredReceipt.AcceptedAtUnixSeconds,
                logicalExpiry,
                platformExpiry,
                recoveredReceipt,
                name,
                header,
                receiptBytes,
                envelopeBytes);
            receiptBytes = [];
            envelopeBytes = [];
            if (!MatchesReceipt(sticky, prepared) ||
                !MatchesAcceptanceAttempt(
                    attempt,
                    sticky,
                    binding,
                    prepared) ||
                !RetainedStateAcceptanceRecoveryCodec.TryEncode(
                    attempt,
                    out canonicalRecovery) ||
                !encoded.AsSpan().SequenceEqual(canonicalRecovery))
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            var observedResult = await authority.ObserveAsync(
                    lease,
                    logicalExpiry,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            using var observed = observedResult.Value;
            if (!observedResult.Succeeded ||
                observed is null ||
                !CanAppendCandidate(observed, authority, binding, candidate) ||
                !PredecessorCovers(observed, binding, logicalExpiry) ||
                observed.Snapshot is not { } snapshot ||
                !snapshot.UnderRetained.IsEmpty ||
                !snapshot.Unknown.IsEmpty ||
                snapshot.Authenticated.Count(item =>
                    item.Header.ObjectClass == StateObjectClass.Acceptance) >=
                        LineageFormat.MaximumPhysicalPerClass ||
                snapshot.Authenticated.Count(item =>
                    recoveryRecord.MatchesAuthenticated(authority, item)) != 1 ||
                observed.InventoryDigest is not { } inventoryDigest ||
                observed.SelectedHead is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        observedResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : observedResult.Code);
            }

            var selected = new SelectedLineageSnapshot(
                observed.SelectedHead.Header.BaseScopeDigest,
                observed.SelectedHead.Header.Epoch,
                observed.SelectedHead.Header.SessionId,
                observed.SelectedHead.Header.ObjectIdentity,
                observed.SelectedHead.Head.Transition);
            var ownership = RetainedStateOwnership.Create(
                issuer,
                authority,
                candidate,
                selected,
                inventoryDigest,
                observedAt,
                logicalExpiry);
            var preparation = RetainedStateAcceptancePreparation.Create(
                issuer,
                authority,
                candidate,
                sticky,
                ownership,
                attempt,
                canonicalRecovery);
            attempt = null;
            canonicalRecovery = [];
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Success(
                    RetainedStateTransactionCodes.Ready,
                    preparation);
        }
        finally
        {
            attempt?.Dispose();
            CryptographicOperations.ZeroMemory(receiptBytes);
            CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(canonicalReceipt);
            CryptographicOperations.ZeroMemory(expectedEnvelope);
            CryptographicOperations.ZeroMemory(decryptedPayload);
            CryptographicOperations.ZeroMemory(canonicalRecovery);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateKeyDependencyReport>> GetKeyDependenciesAsync(
        RetainedStateTransactionAuthority authority,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            !RetainedStateRetention.TryCandidate(
                trustedNow,
                out var requiredLogical,
                out _))
        {
            return RetainedStateTransactionResult<
                RetainedStateKeyDependencyReport>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                requiredLogical,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !snapshot.Unknown.IsEmpty)
        {
            return RetainedStateTransactionResult<
                RetainedStateKeyDependencyReport>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.OutcomeUnknown
                        : observedResult.Code);
        }

        var dependencies = ImmutableArray.CreateBuilder<
            LocatorRequiredDependency>();
        var selected = observed.AcceptedState.Selection;
        foreach (var item in snapshot.Authenticated.Concat(
            snapshot.UnderRetained))
        {
            var requiredUse = RequiredDependencyHorizon(
                item,
                selected,
                binding);
            dependencies.Add(new LocatorRequiredDependency(
                LocatorDependencyKind.Transaction,
                item.Header.KeyId,
                requiredUse));
            if (item.Header.ObjectClass != StateObjectClass.Candidate ||
                !TryGeneration(item.Payload, out var generation) ||
                generation is null ||
                !RestrictedStateEnvelope.TryParse(
                    generation.EncryptedStateEnvelope.AsSpan(),
                    out var parsed) ||
                parsed is null)
            {
                continue;
            }

            dependencies.Add(new LocatorRequiredDependency(
                LocatorDependencyKind.RestrictedState,
                parsed.KeyId,
                requiredUse));
        }

        var exact = dependencies
            .Distinct()
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.KeyId, StringComparer.Ordinal)
            .ThenBy(item => item.ExpiresAtUnixSeconds)
            .ToImmutableArray();
        if (!authority.TryEvaluatePreviousKeyRetirement(
                lease,
                exact,
                out var mayRetire))
        {
            // No previous key is a valid closed outcome: there is nothing to
            // retire. A malformed/incomplete dependency set was rejected
            // above, so false remains the safe report.
            mayRetire = false;
        }

        return RetainedStateTransactionResult<
            RetainedStateKeyDependencyReport>.Success(
                RetainedStateTransactionCodes.Ready,
                new RetainedStateKeyDependencyReport(
                    exact,
                    mayRetire,
                    inventoryDigest));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateCleanupAuthorization>> PlanCleanupAsync(
        RetainedStateTransactionAuthority authority,
        VerifiedRetainedStateAcceptance acceptance,
        ImmutableArray<RetainedStateOpaqueRecord> completedP5Records,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            acceptance is null ||
            !acceptance.IsIssuedBy(authority) ||
            completedP5Records.IsDefault ||
            completedP5Records.Any(record =>
                record is null ||
                record.ObjectClass is not (
                    StateObjectClass.PublicationIntent or
                    StateObjectClass.PublicationFailure or
                    StateObjectClass.Abandonment)) ||
            completedP5Records.Select(record => record.Metadata)
                .Distinct().Count() != completedP5Records.Length ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                acceptance.LogicalExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !IsTerminal(observed, acceptance) ||
            !snapshot.Unknown.IsEmpty ||
            completedP5Records.Any(record =>
                snapshot.Authenticated.Count(item =>
                    record.MatchesAuthenticated(authority, item)) != 1))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var selected = observed.AcceptedState.Selection!;
        var activeCleanup = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup &&
                Active(item, binding))
            .Select(item => new
            {
                Physical = item,
                Parsed = RetainedStateCleanupRecordCodec.TryDecode(
                    item.Payload,
                    out var parsed)
                    ? parsed
                    : null,
            })
            .ToArray();
        if (snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup) ||
            activeCleanup.Length > 1 ||
            activeCleanup.Any(item =>
                item.Parsed is null ||
                !CleanupRecordMatchesPhysical(
                    item.Parsed,
                    item.Physical) ||
                !StringComparer.Ordinal.Equals(
                    item.Parsed.TerminalAcceptanceIdentity,
                    acceptance.AcceptanceReceiptIdentity)))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var p5Targets = completedP5Records
            .Select(record => record.Metadata)
            .ToHashSet();
        var targets = activeCleanup.Length == 1
            ? activeCleanup[0].Parsed!.Targets
                .Select(item => new RetainedStateCleanupTarget(item))
                .ToImmutableArray()
            : snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Where(item =>
                    p5Targets.Contains(item.Metadata) ||
                    (!IsProtected(item, snapshot, selected) &&
                        item.Header.ObjectClass is (
                            StateObjectClass.Candidate or
                            StateObjectClass.Acceptance) &&
                        !StringComparer.Ordinal.Equals(
                            item.Header.PredecessorIdentity,
                            selected.Current.ReceiptPhysical.Header
                                .ObjectIdentity)))
                .Select(item =>
                    new RetainedStateCleanupTarget(item.Metadata))
                .Distinct()
                .OrderBy(
                    item => item.Metadata.Reference.Name.Value,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.Metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .ToImmutableArray();
        if (!TryValidateCleanupTargets(
                snapshot,
                selected,
                targets,
                out _))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        return RetainedStateTransactionResult<
            RetainedStateCleanupAuthorization>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateCleanupAuthorization.Create(
                    issuer,
                    authority,
                    acceptance.AcceptanceReceiptIdentity,
                    targets,
                    inventoryDigest));
    }

    internal async Task<RetainedStateCleanupResult> CleanupAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateCleanupRequest request,
        CancellationToken cancellationToken)
    {
        var acceptance = request?.Acceptance;
        var authorization = request?.Authorization;
        if (acceptance is null ||
            authorization is null ||
            !acceptance.IsIssuedBy(authority))
        {
            return new RetainedStateCleanupResult(
                acceptance!,
                Completed: false,
                RetainedStateTransactionCodes.AccessDenied);
        }

        var semanticRequiredExpiresAtUnixSeconds =
            request!.SemanticRequiredExpiresAtUnixSeconds;
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !authorization.TryConsume(authority, acceptance) ||
            authorization.Targets.IsDefault ||
            authorization.Targets.Length >
                LineageFormat.MaximumScopedObjects ||
            authorization.Targets.Select(item => item.Metadata)
                .Distinct().Count() != authorization.Targets.Length ||
            !LineageValidation.IsTime(
                semanticRequiredExpiresAtUnixSeconds) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            semanticRequiredExpiresAtUnixSeconds < trustedNow ||
            !RetainedStateRetention.TryOpaque(
                trustedNow,
                semanticRequiredExpiresAtUnixSeconds,
                out var requiredPlatformExpiry))
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                RetainedStateTransactionCodes.AccessDenied);
        }

        var sentinel = await authority.EnsureSentinelCoverageAsync(
                lease,
                requiredPlatformExpiry,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(sentinel))
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                sentinel);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                acceptance.LogicalExpiresAtUnixSeconds,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !StringComparer.Ordinal.Equals(
                inventoryDigest,
                authorization.InventoryDigest) ||
            !IsTerminal(observed, acceptance) ||
            !snapshot.Unknown.IsEmpty ||
            !TryValidateCleanupTargets(
                snapshot,
                observed.AcceptedState.Selection!,
                authorization.Targets,
                out _))
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                observedResult.Succeeded
                    ? RetainedStateTransactionCodes.Conflict
                    : observedResult.Code);
        }

        var pruneResult = await PruneCompletedCleanupRecordsAsync(
                authority,
                lease,
                binding,
                acceptance.AcceptanceReceiptIdentity,
                snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(pruneResult.Code))
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                pruneResult.Code);
        }

        RetainedStateObservation? refreshed = null;
        var refreshedCode = RetainedStateTransactionCodes.Ready;
        if (pruneResult.Pruned)
        {
            var refreshResult = await authority.ObserveAsync(
                    lease,
                    acceptance.LogicalExpiresAtUnixSeconds,
                    trustedNow,
                    CancellationToken.None)
                .ConfigureAwait(false);
            refreshed = refreshResult.Value;
            refreshedCode = refreshResult.Code;
        }

        using var refreshedLifetime = refreshed;
        if (pruneResult.Pruned)
        {
            if (refreshed is null ||
                !StringComparer.Ordinal.Equals(
                    refreshedCode,
                    RetainedStateTransactionCodes.Ready) ||
                refreshed.Snapshot is not { } refreshedSnapshot ||
                refreshed.InventoryDigest is not { } refreshedDigest ||
                !IsTerminal(refreshed, acceptance) ||
                !refreshedSnapshot.Unknown.IsEmpty ||
                !TryValidateCleanupTargets(
                    refreshedSnapshot,
                    refreshed.AcceptedState.Selection!,
                    authorization.Targets,
                    out _))
            {
                return new RetainedStateCleanupResult(
                    acceptance,
                    Completed: false,
                    StringComparer.Ordinal.Equals(
                        refreshedCode,
                        RetainedStateTransactionCodes.Ready)
                        ? RetainedStateTransactionCodes.Conflict
                        : refreshedCode);
            }

            snapshot = refreshedSnapshot;
            inventoryDigest = refreshedDigest;
        }

        if (!RetainedStateCleanupRecordCodec.TryCreate(
                acceptance.AcceptanceReceiptIdentity,
                binding.SelectedLineage.BaseScopeDigest,
                binding.SelectedLineage.Epoch,
                binding.SelectedLineage.SessionId,
                inventoryDigest,
                authorization.Targets.Select(item => item.Metadata)
                    .ToImmutableArray(),
                out var cleanup) ||
            cleanup is null ||
            !RetainedStateCleanupRecordCodec.TryEncode(
                cleanup,
                out var cleanupBytes))
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                RetainedStateTransactionCodes.Invalid);
        }

        OpaqueStoreObjectMetadata? cleanupMetadata = null;
        try
        {
            var activeCleanup = snapshot.Authenticated.Where(item =>
                    item.Header.ObjectClass == StateObjectClass.Cleanup &&
                    Active(item, binding))
                .Select(item => new
                {
                    Physical = item,
                    Parsed = RetainedStateCleanupRecordCodec.TryDecode(
                        item.Payload,
                        out var parsed)
                        ? parsed
                        : null,
                })
                .ToArray();
            if (activeCleanup.Any(item => item.Parsed is null) ||
                activeCleanup.Length > 1)
            {
                return new RetainedStateCleanupResult(
                    acceptance,
                    Completed: false,
                    RetainedStateTransactionCodes.Conflict);
            }

            if (activeCleanup.Length == 1)
            {
                var existing = activeCleanup[0];
                if (!EquivalentCleanup(existing.Parsed!, cleanup))
                {
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        RetainedStateTransactionCodes.Conflict);
                }

                cleanup = existing.Parsed!;
                cleanupMetadata = existing.Physical.Metadata;
            }
            else
            {
                if (snapshot.Authenticated.Count(item =>
                        item.Header.ObjectClass == StateObjectClass.Cleanup) >=
                        LineageFormat.MaximumPhysicalPerClass ||
                    snapshot.UnderRetained.Any(item =>
                        item.Header.ObjectClass == StateObjectClass.Cleanup))
                {
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        RetainedStateTransactionCodes.Conflict);
                }

                byte[] envelope = [];
                var envelopeCode = RetainedStateTransactionCodes.Invalid;
                if (!authority.TryGetPersistenceContext(
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
                        StateObjectClass.Cleanup,
                        acceptance.AcceptanceReceiptIdentity,
                        successorIdentity: null,
                        binding.ProducingRunIdentity,
                        binding.ProducingRunAttempt,
                        trustedNow,
                        semanticRequiredExpiresAtUnixSeconds,
                        requiredPlatformExpiry,
                        cleanupBytes,
                        out var name,
                        out envelope,
                        out var header,
                        out envelopeCode) ||
                    name is null ||
                    header is null ||
                    !authority.TryCreatePersistence(
                        lease,
                        out var persistence) ||
                    persistence is null)
                {
                    CryptographicOperations.ZeroMemory(envelope);
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        envelopeCode);
                }

                try
                {
                    var persisted = await persistence.UploadAndReconcileAsync(
                            locator,
                            locatorAccess,
                            baseScope,
                            binding.SelectedLineage.BaseScopeDigest,
                            name,
                            envelope,
                            header,
                            cleanupBytes,
                            binding.ProducingRunIdentity,
                            binding.ProducingRunAttempt,
                            requiredPlatformExpiry,
                            cancellationToken)
                        .ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(
                        persisted.Payload ?? []);
                    if (!persisted.Succeeded || persisted.Metadata is null)
                    {
                        return new RetainedStateCleanupResult(
                            acceptance,
                            Completed: false,
                            persisted.Code);
                    }

                    cleanupMetadata = persisted.Metadata;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelope);
                }
            }

            if (!authority.TryCreatePersistence(lease, out var deletes) ||
                deletes is null)
            {
                return new RetainedStateCleanupResult(
                    acceptance,
                    Completed: false,
                    RetainedStateTransactionCodes.AccessDenied);
            }

            foreach (var target in cleanup.Targets)
            {
                var freshResult = await authority.ObserveAsync(
                        lease,
                        acceptance.LogicalExpiresAtUnixSeconds,
                        trustedNow,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                using var fresh = freshResult.Value;
                if (!freshResult.Succeeded ||
                    fresh is null ||
                    fresh.Snapshot is not { } freshSnapshot ||
                    !IsTerminal(fresh, acceptance) ||
                    !freshSnapshot.Unknown.IsEmpty ||
                    !HasExactCleanupRecord(
                        freshSnapshot,
                        cleanupMetadata!,
                        cleanup))
                {
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        freshResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : freshResult.Code);
                }

                var physical = freshSnapshot.Authenticated
                    .Concat(freshSnapshot.UnderRetained)
                    .Where(item => item.Metadata.Reference == target.Reference)
                    .ToArray();
                if (physical.Length == 0)
                {
                    continue;
                }

                if (physical.Length != 1 ||
                    physical[0].Metadata != target ||
                    !IsAuthorizedCleanupTarget(
                        physical[0],
                        freshSnapshot,
                        fresh.AcceptedState.Selection!))
                {
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        RetainedStateTransactionCodes.Conflict);
                }

                var deleted = await deletes.DeleteExactAndVerifyAbsentAsync(
                        target,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!Ready(deleted))
                {
                    return new RetainedStateCleanupResult(
                        acceptance,
                        Completed: false,
                        deleted);
                }
            }

            var completedResult = await authority.ObserveAsync(
                    lease,
                    acceptance.LogicalExpiresAtUnixSeconds,
                    trustedNow,
                    CancellationToken.None)
                .ConfigureAwait(false);
            using var completed = completedResult.Value;
            if (!completedResult.Succeeded ||
                completed is null ||
                completed.Snapshot is not { } completedSnapshot ||
                !IsTerminal(completed, acceptance) ||
                !completedSnapshot.Unknown.IsEmpty ||
                !HasExactCleanupRecord(
                    completedSnapshot,
                    cleanupMetadata!,
                    cleanup) ||
                cleanup.Targets.Any(target =>
                    completedSnapshot.Authenticated
                        .Concat(completedSnapshot.UnderRetained)
                        .Any(item => item.Metadata == target)))
            {
                return new RetainedStateCleanupResult(
                    acceptance,
                    Completed: false,
                    completedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : completedResult.Code);
            }

            var selfDeleted = await deletes.DeleteExactAndVerifyAbsentAsync(
                    cleanupMetadata!,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new RetainedStateCleanupResult(
                acceptance,
                Ready(selfDeleted),
                selfDeleted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleanupBytes);
        }
    }

    private async Task<string> EnsureImmediatePredecessorAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAuthorityLease lease,
        RetainedStateTransactionBinding binding,
        RetainedStatePersistedCandidate candidate,
        long copyTime,
        long requiredLogicalExpiry,
        CancellationToken cancellationToken)
    {
        var selected = authority.GetAcceptedSelection(lease);
        if (selected is null)
        {
            return candidate.Prepared.Generation.Generation == 0
                ? RetainedStateTransactionCodes.Ready
                : RetainedStateTransactionCodes.Conflict;
        }

        var current = selected.Current;
        if (current.ReceiptPhysical.Metadata.ExpiresAtUnixSeconds <
            requiredLogicalExpiry)
        {
            return RetainedStateTransactionCodes.RetentionFailed;
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                requiredLogicalExpiry,
                copyTime,
                cancellationToken)
            .ConfigureAwait(false);
        using (var observed = observedResult.Value)
        {
            if (!observedResult.Succeeded ||
                observed is null ||
                observed.Snapshot is not { } snapshot ||
                !snapshot.UnderRetained.IsEmpty ||
                !snapshot.Unknown.IsEmpty)
            {
                return observedResult.Succeeded
                    ? RetainedStateTransactionCodes.Conflict
                    : observedResult.Code;
            }

            if (PredecessorCovers(
                    observed,
                    binding,
                    requiredLogicalExpiry))
            {
                return RetainedStateTransactionCodes.Ready;
            }

            if (snapshot.Authenticated.Count(item =>
                    item.Header.ObjectClass == StateObjectClass.Candidate) >=
                LineageFormat.MaximumPhysicalPerClass)
            {
                return RetainedStateTransactionCodes.CleanupDebt;
            }
        }

        var frozen = authority.GetPredecessorCopyAttempt(lease);
        if (frozen is not null)
        {
            return StringComparer.Ordinal.Equals(
                    frozen.LogicalGenerationIdentity,
                    current.LogicalGenerationIdentity) &&
                frozen.RequiredLogicalExpiresAtUnixSeconds ==
                    requiredLogicalExpiry
                ? await PersistPredecessorCopyAttemptAsync(
                        authority,
                        lease,
                        binding,
                        frozen,
                        dispatchIfUnresolved: false,
                        cancellationToken)
                    .ConfigureAwait(false)
                : RetainedStateTransactionCodes.Conflict;
        }

        if (!AcceptedStateGenerationRecordCodec.TryEncode(
                current.Generation,
                out var generationBytes))
        {
            return RetainedStateTransactionCodes.Invalid;
        }

        var source = Source(current);
        var copy = new AcceptedStatePhysicalCopyV1(
            ImmutableArray.CreateRange(generationBytes),
            current.LogicalGenerationIdentity,
            current.OriginalCandidateObjectIdentity,
            source.Metadata.Reference.ObjectId.Value,
            source.Metadata.ArchiveDigest.Sha256,
            source.Metadata.EncryptedObjectDigest.Sha256);
        if (!AcceptedStatePhysicalCopyCodec.TryEncode(copy, out var copyBytes))
        {
            CryptographicOperations.ZeroMemory(generationBytes);
            return RetainedStateTransactionCodes.Invalid;
        }

        try
        {
            byte[] envelope = [];
            var envelopeCode = RetainedStateTransactionCodes.Invalid;
            if (!RetainedStateRetention.TryOpaque(
                    copyTime,
                    requiredLogicalExpiry,
                    out var requiredPlatformExpiry) ||
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
                    current.Physical.Header.PredecessorIdentity,
                    successorIdentity: null,
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    copyTime,
                    requiredLogicalExpiry,
                    requiredPlatformExpiry,
                    copyBytes,
                    out var name,
                    out envelope,
                    out var header,
                out envelopeCode) ||
                name is null ||
                header is null)
            {
                CryptographicOperations.ZeroMemory(envelope);
                return envelopeCode;
            }

            var created = RetainedStatePredecessorCopyAttempt.Create(
                issuer,
                current.LogicalGenerationIdentity,
                requiredLogicalExpiry,
                requiredPlatformExpiry,
                name,
                header,
                copyBytes,
                envelope);
            copyBytes = [];
            envelope = [];
            if (!authority.TrySetPredecessorCopyAttempt(lease, created))
            {
                created.Dispose();
                return RetainedStateTransactionCodes.Conflict;
            }

            return await PersistPredecessorCopyAttemptAsync(
                    authority,
                    lease,
                    binding,
                    created,
                    dispatchIfUnresolved: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generationBytes);
            CryptographicOperations.ZeroMemory(copyBytes);
        }
    }

    private static async Task<string> PersistPredecessorCopyAttemptAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAuthorityLease lease,
        RetainedStateTransactionBinding binding,
        RetainedStatePredecessorCopyAttempt attempt,
        bool dispatchIfUnresolved,
        CancellationToken cancellationToken)
    {
        if (!attempt.TryGetBytes(out var payload, out var envelope) ||
            !authority.TryGetPersistenceContext(
                lease,
                out var locator,
                out var locatorAccess,
                out var baseScope) ||
            locator is null ||
            locatorAccess is null ||
            baseScope is null ||
            !authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null)
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var persisted = dispatchIfUnresolved
            ? await persistence.UploadAndReconcileAsync(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage.BaseScopeDigest,
                    attempt.Name,
                    envelope,
                    attempt.Header,
                    payload,
                    attempt.Header.ProducingRunIdentity,
                    attempt.Header.ProducingRunAttempt,
                    attempt.RequiredPlatformExpiresAtUnixSeconds,
                    cancellationToken)
                .ConfigureAwait(false)
            : await persistence.ReconcileExistingAsync(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage.BaseScopeDigest,
                    attempt.Name,
                    envelope,
                    attempt.Header,
                    payload,
                    attempt.Header.ProducingRunIdentity,
                    attempt.Header.ProducingRunAttempt,
                    attempt.RequiredPlatformExpiresAtUnixSeconds)
                .ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
        if (!persisted.Succeeded &&
            !persisted.MayHaveCommitted &&
            dispatchIfUnresolved)
        {
            authority.TryClearPredecessorCopyAttempt(lease, attempt);
        }

        return persisted.Succeeded
            ? RetainedStateTransactionCodes.Ready
            : persisted.Code;
    }

    private static AuthenticatedStateObject Source(
        SelectedAcceptedGeneration generation)
    {
        if (AcceptedStatePhysicalCopyCodec.TryDecode(
                generation.Physical.Payload,
                out var copy) &&
            copy is not null)
        {
            return generation.Physical with
            {
                Metadata = new OpaqueStoreObjectMetadata(
                    new OpaqueStoreObjectReference(
                        generation.Physical.Metadata.Reference.Name,
                        new OpaqueStoreObjectId(copy.SourceArtifactId)),
                    generation.Physical.Metadata.ProducingRun,
                    new OpaqueStoreArchiveDigest(copy.SourceArchiveSha256),
                    new OpaqueStoreEncryptedObjectDigest(
                        copy.SourceEncryptedEnvelopeSha256),
                    generation.Physical.Metadata.ExpiresAtUnixSeconds,
                    generation.Physical.Metadata.Size),
            };
        }

        return generation.Physical;
    }

    private static bool CanAppendCandidate(
        RetainedStateObservation observation,
        RetainedStateTransactionAuthority authority,
        RetainedStateTransactionBinding binding,
        RetainedStatePersistedCandidate? expected)
    {
        var snapshot = observation.Snapshot;
        var selectedHead = observation.SelectedHead;
        if (snapshot is null ||
            selectedHead is null ||
            !MatchesSelected(binding.SelectedLineage, selectedHead) ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            !MatchesAcceptedTail(observation.AcceptedState, binding))
        {
            return false;
        }

        var activeCandidates = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    binding.SelectedLineage.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    binding.SelectedLineage.SessionId) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity))
            .ToArray();
        var successors = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    binding.SelectedLineage.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    binding.SelectedLineage.SessionId) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity))
            .ToArray();
        if (successors.Length != 0 ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate) >=
                    LineageFormat.MaximumPhysicalPerClass &&
                expected is null)
        {
            return false;
        }

        if (expected is null)
        {
            return activeCandidates.Length == 0;
        }

        if (!expected.Prepared.TryGetBytes(
                authority,
                out var expectedGenerationBytes,
                out _) ||
            activeCandidates.Length != 1 ||
            activeCandidates[0].Metadata != expected.Metadata ||
            activeCandidates[0].Header != expected.Prepared.Header ||
            !activeCandidates[0].Payload.AsSpan().SequenceEqual(
                expectedGenerationBytes.Span) ||
            !AcceptedStateGenerationRecordCodec.TryDecode(
                activeCandidates[0].Payload,
                out var generation) ||
            generation is null ||
            !AcceptedStateRecordValidation.IsValid(generation) ||
            !AcceptedStateIdentity.TryComputeLogicalGeneration(
                activeCandidates[0].Payload,
                binding.SelectedLineage.BaseScopeDigest,
                binding.SelectedLineage.Epoch,
                binding.SelectedLineage.SessionId,
                binding.CurrentAcceptanceReceiptIdentity,
                out var logicalIdentity))
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(
            logicalIdentity,
            expected.Prepared.LogicalGenerationIdentity);
    }

    private static bool TryFindPersistedPrepared(
        RetainedStateObservation observation,
        RetainedStateTransactionAuthority authority,
        RetainedStateTransactionBinding binding,
        RetainedStatePreparedCandidate prepared,
        ReadOnlyMemory<byte> immutableEnvelope,
        out OpaqueStoreObjectMetadata? metadata,
        out string? inventoryDigest)
    {
        metadata = null;
        inventoryDigest = null;
        var snapshot = observation.Snapshot;
        if (snapshot is null ||
            observation.InventoryDigest is not { } observedDigest ||
            observation.SelectedHead is not { } selected ||
            !MatchesSelected(binding.SelectedLineage, selected) ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            !MatchesAcceptedTail(observation.AcceptedState, binding) ||
            !prepared.TryGetBytes(
                authority,
                out var canonicalGeneration,
                out _) ||
            snapshot.Authenticated.Any(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                Active(item, binding) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity)))
        {
            return false;
        }

        var active = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                Active(item, binding) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    binding.CurrentAcceptanceReceiptIdentity))
            .ToArray();
        if (active.Length != 1 ||
            active[0].Header != prepared.Header ||
            !active[0].Payload.AsSpan().SequenceEqual(
                canonicalGeneration.Span) ||
            active[0].Metadata.EncryptedObjectDigest.Sha256 !=
                OpaqueStoreHash.Sha256(immutableEnvelope.Span) ||
            active[0].Metadata.Size != immutableEnvelope.Length ||
            !StringComparer.Ordinal.Equals(
                active[0].Metadata.ProducingRun.Identity,
                binding.ProducingRunIdentity) ||
            active[0].Metadata.ProducingRun.Attempt !=
                binding.ProducingRunAttempt ||
            active[0].Metadata.ExpiresAtUnixSeconds <
                prepared.Header.RequiredPlatformExpiresAtUnixSeconds)
        {
            return false;
        }

        metadata = active[0].Metadata;
        inventoryDigest = observedDigest;
        return true;
    }

    private static bool MatchesAcceptedTail(
        AcceptedStateSelectionResult selected,
        RetainedStateTransactionBinding binding)
    {
        if (binding.CurrentLogicalGenerationIdentity is null)
        {
            return selected.IsBootstrap && selected.Selection is null;
        }

        return selected.Succeeded &&
            selected.Selection is { } selection &&
            StringComparer.Ordinal.Equals(
                selection.Current.LogicalGenerationIdentity,
                binding.CurrentLogicalGenerationIdentity) &&
            StringComparer.Ordinal.Equals(
                selection.Current.ReceiptPhysical.Header.ObjectIdentity,
                binding.CurrentAcceptanceReceiptIdentity);
    }

    private static bool PredecessorCovers(
        RetainedStateObservation observation,
        RetainedStateTransactionBinding binding,
        long requiredLogicalExpiry)
    {
        if (binding.CurrentLogicalGenerationIdentity is null)
        {
            return true;
        }

        var current = observation.AcceptedState.Selection?.Current;
        if (current is null ||
            current.ReceiptPhysical.Metadata.ExpiresAtUnixSeconds <
                requiredLogicalExpiry)
        {
            return false;
        }

        if (!AcceptedStateGenerationRecordCodec.TryEncode(
                current.Generation,
                out var canonicalGeneration))
        {
            return false;
        }

        var sourceArtifactId = current.Physical.Metadata.Reference
            .ObjectId.Value;
        var sourceArchiveSha256 = current.Physical.Metadata.ArchiveDigest.Sha256;
        var sourceEnvelopeSha256 = current.Physical.Metadata
            .EncryptedObjectDigest.Sha256;
        if (AcceptedStatePhysicalCopyCodec.TryDecode(
                current.Physical.Payload,
                out var currentCopy) &&
            currentCopy is not null)
        {
            sourceArtifactId = currentCopy.SourceArtifactId;
            sourceArchiveSha256 = currentCopy.SourceArchiveSha256;
            sourceEnvelopeSha256 = currentCopy.SourceEncryptedEnvelopeSha256;
        }

        var equivalents = observation.Snapshot!.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    binding.SelectedLineage.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    binding.SelectedLineage.SessionId) &&
                item.Metadata.ExpiresAtUnixSeconds >= requiredLogicalExpiry)
            .Where(item =>
                AcceptedStateGenerationRecordCodec.TryDecode(
                    item.Payload,
                    out var original) &&
                original == current.Generation &&
                StringComparer.Ordinal.Equals(
                    item.Header.ObjectIdentity,
                    current.OriginalCandidateObjectIdentity) ||
                AcceptedStatePhysicalCopyCodec.TryDecode(
                    item.Payload,
                    out var copy) &&
                copy is not null &&
                StringComparer.Ordinal.Equals(
                    copy.LogicalGenerationIdentity,
                    current.LogicalGenerationIdentity) &&
                StringComparer.Ordinal.Equals(
                    copy.OriginalCandidateObjectIdentity,
                    current.OriginalCandidateObjectIdentity) &&
                copy.CanonicalGenerationBytes.AsSpan().SequenceEqual(
                    canonicalGeneration) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceArtifactId,
                    sourceArtifactId) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceArchiveSha256,
                    sourceArchiveSha256) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceEncryptedEnvelopeSha256,
                    sourceEnvelopeSha256))
            .ToArray();
        CryptographicOperations.ZeroMemory(canonicalGeneration);
        return equivalents.Length > 0;
    }

    private static bool MatchesPreparedBinding(
        RetainedStatePreparedCandidate prepared,
        RetainedStateTransactionBinding binding) =>
        StringComparer.Ordinal.Equals(
            prepared.Header.BaseScopeDigest,
            binding.SelectedLineage.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(
            prepared.Header.Epoch,
            binding.SelectedLineage.Epoch) &&
        StringComparer.Ordinal.Equals(
            prepared.Header.SessionId,
            binding.SelectedLineage.SessionId) &&
        StringComparer.Ordinal.Equals(
            prepared.Header.PredecessorIdentity,
            binding.CurrentAcceptanceReceiptIdentity) &&
        StringComparer.Ordinal.Equals(
            prepared.Generation.PreviousLogicalGenerationIdentity,
            binding.CurrentLogicalGenerationIdentity) &&
        StringComparer.Ordinal.Equals(
            prepared.Generation.ProducerHeadSha,
            binding.Reviewed.HeadSha) &&
        (prepared.IsRecovered ||
            (prepared.Header.ProducingRunIdentity ==
                binding.ProducingRunIdentity &&
            prepared.Header.ProducingRunAttempt ==
                binding.ProducingRunAttempt));

    private static bool MatchesRecoveredPublication(
        ValidatedPublicationPayloadV1 publication,
        StateGenerationRecordV1 generation,
        RetainedStateTransactionBinding binding) =>
        R4PublicationIdentityV1.IsValidScope(binding.Publication.Scope) &&
        binding.Publication.Scope.RepositoryId <= long.MaxValue &&
        binding.Publication.Scope.PullRequestNumber <= long.MaxValue &&
        publication.RepositoryId ==
            (long)binding.Publication.Scope.RepositoryId &&
        publication.PullRequestNumber ==
            (long)binding.Publication.Scope.PullRequestNumber &&
        StringComparer.Ordinal.Equals(
            publication.RepositoryName,
            binding.Publication.RepositoryName) &&
        StringComparer.Ordinal.Equals(
            publication.ScopeSha256,
            binding.Publication.ScopeSha256) &&
        StringComparer.Ordinal.Equals(
            publication.ScopeSha256,
            R4PublicationIdentityV1.ComputeScopeSha256(
                binding.Publication.Scope)) &&
        StringComparer.Ordinal.Equals(
            publication.ReviewedHeadSha,
            generation.ProducerHeadSha) &&
        StringComparer.Ordinal.Equals(
            publication.ReviewedHeadSha,
            binding.Reviewed.HeadSha) &&
        StringComparer.Ordinal.Equals(
            publication.PolicyIdentitySha256,
            binding.Policy.PolicyIdentitySha256) &&
        StringComparer.Ordinal.Equals(
            publication.PolicyIdentitySha256,
            binding.Publication.Scope.PolicyIdentitySha256) &&
        StringComparer.Ordinal.Equals(
            publication.PayloadSha256,
            binding.Policy.PayloadSha256) &&
        StringComparer.Ordinal.Equals(
            publication.PayloadSha256,
            binding.Publication.PayloadSha256) &&
        StringComparer.Ordinal.Equals(
            publication.BuildDiscriminator,
            binding.Policy.BuildDiscriminator) &&
        StringComparer.Ordinal.Equals(
            publication.BuildDiscriminator,
            binding.Publication.BuildDiscriminator) &&
        StringComparer.Ordinal.Equals(
            publication.RenderingVersion,
            AcceptedStateFormat.RenderingVersion);

    private static bool MatchesSelected(
        SelectedLineageSnapshot expected,
        LineageHeadCandidate actual) =>
        StringComparer.Ordinal.Equals(
            expected.BaseScopeDigest,
            actual.Header.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(expected.Epoch, actual.Header.Epoch) &&
        StringComparer.Ordinal.Equals(
            expected.SessionId,
            actual.Header.SessionId) &&
        StringComparer.Ordinal.Equals(
            expected.LineageHeadIdentity,
            actual.Header.ObjectIdentity) &&
        expected.Transition == actual.Head.Transition;

    private static bool MatchesReceipt(
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        RetainedStatePreparedCandidate prepared) =>
        receipt.RepositoryId == prepared.Publication.RepositoryId &&
        receipt.PullRequestNumber == prepared.Publication.PullRequestNumber &&
        StringComparer.Ordinal.Equals(
            receipt.ScopeSha256,
            prepared.Publication.ScopeSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.BodySha256,
            prepared.Publication.BodySha256) &&
        StringComparer.Ordinal.Equals(
            receipt.HeadSha,
            prepared.Publication.ReviewedHeadSha) &&
        Uri.TryCreate(receipt.CommentUrl, UriKind.Absolute, out _);

    private static bool MatchesAcceptanceAttempt(
        RetainedStateAcceptanceAttempt attempt,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        RetainedStateTransactionBinding binding,
        RetainedStatePreparedCandidate prepared)
    {
        if (!RetainedStateRetention.TryAcceptance(
                attempt.AcceptedAtUnixSeconds,
                out var logicalExpiry,
                out var platformExpiry) ||
            logicalExpiry != attempt.LogicalExpiresAtUnixSeconds ||
            platformExpiry != attempt.RequiredPlatformExpiresAtUnixSeconds ||
            attempt.Header.ObjectClass != StateObjectClass.Acceptance ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.BaseScopeDigest,
                binding.SelectedLineage.BaseScopeDigest) ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.Epoch,
                binding.SelectedLineage.Epoch) ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.SessionId,
                binding.SelectedLineage.SessionId) ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.PredecessorIdentity,
                binding.CurrentAcceptanceReceiptIdentity) ||
            attempt.Header.SuccessorIdentity is not null ||
            attempt.Header.CreatedAtUnixSeconds !=
                attempt.AcceptedAtUnixSeconds ||
            attempt.Header.LogicalExpiresAtUnixSeconds != logicalExpiry ||
            attempt.Header.RequiredPlatformExpiresAtUnixSeconds !=
                platformExpiry ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.ProducingRunIdentity,
                attempt.Receipt.ProducingRunIdentity) ||
            attempt.Header.ProducingRunAttempt !=
                attempt.Receipt.ProducingRunAttempt)
        {
            return false;
        }

        var expected = new AcceptanceReceiptV1(
            prepared.LogicalGenerationIdentity,
            prepared.Header.ObjectIdentity,
            binding.CurrentLogicalGenerationIdentity,
            binding.CurrentAcceptanceReceiptIdentity,
            prepared.Publication.ReviewedHeadSha,
            receipt.Operation,
            receipt.RepositoryId,
            receipt.PullRequestNumber,
            receipt.CommentId,
            receipt.CommentUrl,
            receipt.ScopeSha256,
            receipt.BodySha256,
            prepared.Generation.PublicationPayloadSha256,
            attempt.Header.ProducingRunIdentity,
            attempt.Header.ProducingRunAttempt,
            attempt.AcceptedAtUnixSeconds,
            logicalExpiry);
        return attempt.Receipt == expected;
    }

    private static bool MatchesOpaqueEvidence(
        RetainedStateObservation observed,
        RetainedStateTransactionAuthority authority,
        RetainedStateTransactionBinding binding,
        ImmutableArray<RetainedStateOpaqueRecord> expected)
    {
        if (expected.IsDefault ||
            observed.Snapshot is not { } snapshot ||
            expected.Any(record => record is null) ||
            expected.Select(record => record.Metadata).Distinct().Count() !=
                expected.Length)
        {
            return false;
        }

        var active = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass is
                    StateObjectClass.PublicationIntent or
                    StateObjectClass.PublicationFailure or
                    StateObjectClass.Abandonment &&
                Active(item, binding))
            .ToArray();
        if (active.Length != expected.Length)
        {
            return false;
        }

        return expected.All(record => active.Count(item =>
            record.MatchesAuthenticated(authority, item)) == 1);
    }

    private static bool Active(
        AuthenticatedStateObject item,
        RetainedStateTransactionBinding binding) =>
        StringComparer.Ordinal.Equals(
            item.Header.Epoch,
            binding.SelectedLineage.Epoch) &&
        StringComparer.Ordinal.Equals(
            item.Header.SessionId,
            binding.SelectedLineage.SessionId);

    private static bool HasAcceptanceSuccessor(
        ScopedStateInventorySnapshot snapshot,
        RetainedStateTransactionBinding binding) =>
        snapshot.Authenticated.Any(item =>
            item.Header.ObjectClass == StateObjectClass.Acceptance &&
            Active(item, binding) &&
            StringComparer.Ordinal.Equals(
                item.Header.PredecessorIdentity,
                binding.CurrentAcceptanceReceiptIdentity));

    private static bool MatchesRecoveredGeneration(
        StateGenerationRecordV1 generation,
        RetainedStateTransactionBinding binding) =>
        generation.Generation ==
            (binding.CurrentGeneration is null
                ? 0
                : binding.CurrentGeneration.Value + 1) &&
        StringComparer.Ordinal.Equals(
            generation.PreviousLogicalGenerationIdentity,
            binding.CurrentLogicalGenerationIdentity) &&
        StringComparer.Ordinal.Equals(
            generation.ProducerBaseSha,
            binding.Reviewed.BaseSha) &&
        StringComparer.Ordinal.Equals(
            generation.ProducerHeadSha,
            binding.Reviewed.HeadSha) &&
        StringComparer.Ordinal.Equals(
            generation.PolicyIdentitySha256,
            binding.Policy.PolicyIdentitySha256) &&
        StringComparer.Ordinal.Equals(
            generation.ConfigSha256,
            binding.Policy.ConfigSha256) &&
        StringComparer.Ordinal.Equals(
            generation.InstructionsSha256,
            binding.Policy.InstructionsSha256) &&
        StringComparer.Ordinal.Equals(
            generation.PayloadSha256,
            binding.Policy.PayloadSha256) &&
        StringComparer.Ordinal.Equals(
            generation.BuildDiscriminator,
            binding.Policy.BuildDiscriminator);

    private static bool TryGeneration(
        ReadOnlySpan<byte> payload,
        out StateGenerationRecordV1? generation)
    {
        generation = null;
        if (AcceptedStateGenerationRecordCodec.TryDecode(
                payload,
                out generation) &&
            generation is not null)
        {
            return true;
        }

        return AcceptedStatePhysicalCopyCodec.TryDecode(
                payload,
                out var copy) &&
            copy is not null &&
            AcceptedStateGenerationRecordCodec.TryDecode(
                copy.CanonicalGenerationBytes.AsSpan(),
                out generation) &&
            generation is not null;
    }

    private static bool SameLogical(
        AuthenticatedStateObject item,
        SelectedAcceptedGeneration selected,
        RetainedStateTransactionBinding binding)
    {
        byte[] canonical;
        if (AcceptedStateGenerationRecordCodec.TryDecode(
                item.Payload,
                out _))
        {
            canonical = item.Payload.ToArray();
        }
        else if (AcceptedStatePhysicalCopyCodec.TryDecode(
                item.Payload,
                out var copy) &&
            copy is not null)
        {
            canonical = copy.CanonicalGenerationBytes.ToArray();
        }
        else
        {
            return false;
        }

        try
        {
            return AcceptedStateIdentity.TryComputeLogicalGeneration(
                    canonical,
                    binding.SelectedLineage.BaseScopeDigest,
                    binding.SelectedLineage.Epoch,
                    binding.SelectedLineage.SessionId,
                    item.Header.PredecessorIdentity,
                    out var logical) &&
                StringComparer.Ordinal.Equals(
                    logical,
                    selected.LogicalGenerationIdentity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static long RequiredDependencyHorizon(
        AuthenticatedStateObject item,
        AcceptedStateSelection? selected,
        RetainedStateTransactionBinding binding)
    {
        if (selected is not null &&
            (SameSelectedPhysical(item, selected.Current) ||
                selected.ImmediatePredecessor is { } predecessor &&
                SameSelectedPhysical(item, predecessor) ||
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                (SameLogical(item, selected.Current, binding) ||
                    selected.ImmediatePredecessor is { } logicalPredecessor &&
                    SameLogical(item, logicalPredecessor, binding))))
        {
            return Math.Max(
                item.Header.LogicalExpiresAtUnixSeconds,
                selected.RequiredCurrentWindowUnixSeconds);
        }

        if (item.Header.ObjectClass == StateObjectClass.Candidate &&
            StringComparer.Ordinal.Equals(
                item.Header.PredecessorIdentity,
                binding.CurrentAcceptanceReceiptIdentity))
        {
            try
            {
                return Math.Max(
                    item.Header.LogicalExpiresAtUnixSeconds,
                    checked(
                        item.Header.CreatedAtUnixSeconds +
                        StateRetentionRequirements.LogicalWindowSeconds +
                        StateRetentionRequirements.PreStickyBudgetSeconds));
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        return item.Header.LogicalExpiresAtUnixSeconds;
    }

    private static bool SameSelectedPhysical(
        AuthenticatedStateObject item,
        SelectedAcceptedGeneration selected) =>
        item.Metadata == selected.Physical.Metadata ||
        item.Metadata == selected.ReceiptPhysical.Metadata;

    private static bool IsTerminal(
        RetainedStateObservation observation,
        VerifiedRetainedStateAcceptance acceptance) =>
        observation.AcceptedState.Succeeded &&
        observation.AcceptedState.Selection?.Current is { } current &&
        StringComparer.Ordinal.Equals(
            current.LogicalGenerationIdentity,
            acceptance.LogicalGenerationIdentity) &&
        StringComparer.Ordinal.Equals(
            current.ReceiptPhysical.Header.ObjectIdentity,
            acceptance.AcceptanceReceiptIdentity) &&
        current.ReceiptPhysical.Metadata == acceptance.ReceiptMetadata;

    private static bool TryValidateCleanupTargets(
        ScopedStateInventorySnapshot snapshot,
        AcceptedStateSelection selected,
        ImmutableArray<RetainedStateCleanupTarget> targets,
        out ImmutableArray<AuthenticatedStateObject> present)
    {
        var builder = ImmutableArray.CreateBuilder<AuthenticatedStateObject>();
        foreach (var target in targets)
        {
            if (target is null ||
                !OpaqueStoreValidation.IsValid(target.Metadata))
            {
                present = [];
                return false;
            }

            var matches = snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Where(item =>
                    item.Metadata.Reference == target.Metadata.Reference)
                .ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            if (matches.Length != 1 ||
                matches[0].Metadata != target.Metadata ||
                !IsAuthorizedCleanupTarget(
                    matches[0],
                    snapshot,
                    selected))
            {
                present = [];
                return false;
            }

            builder.Add(matches[0]);
        }

        present = builder.ToImmutable();
        return true;
    }

    private static bool IsAuthorizedCleanupTarget(
        AuthenticatedStateObject item,
        ScopedStateInventorySnapshot snapshot,
        AcceptedStateSelection selected) =>
        item.Header.ObjectClass is
            StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment ||
        (item.Header.ObjectClass is (
                StateObjectClass.Candidate or
                StateObjectClass.Acceptance) &&
            !IsProtected(item, snapshot, selected));

    private static async Task<(string Code, bool Pruned)>
        PruneCompletedCleanupRecordsAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAuthorityLease lease,
        RetainedStateTransactionBinding binding,
        string terminalAcceptanceIdentity,
        ScopedStateInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var presentReferences = snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Select(item => item.Metadata.Reference)
            .Concat(snapshot.Unknown.Select(item => item.Metadata.Reference))
            .ToHashSet();
        var cleanupPhysical = snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup)
            .Select(item => new
            {
                Physical = item,
                Parsed = RetainedStateCleanupRecordCodec.TryDecode(
                    item.Payload,
                    out var parsed)
                    ? parsed
                    : null,
            })
            .ToArray();
        if (cleanupPhysical.Any(item =>
                item.Parsed is null ||
                !CleanupRecordMatchesPhysical(
                    item.Parsed,
                    item.Physical)))
        {
            return (RetainedStateTransactionCodes.Conflict, false);
        }

        var completed = cleanupPhysical.Where(item =>
                (!Active(item.Physical, binding) ||
                    !StringComparer.Ordinal.Equals(
                        item.Parsed!.TerminalAcceptanceIdentity,
                        terminalAcceptanceIdentity)) &&
                item.Parsed!.Targets.All(target =>
                    !presentReferences.Contains(target.Reference)))
            .ToArray();
        if (completed.Length == 0)
        {
            return (RetainedStateTransactionCodes.Ready, false);
        }

        if (!authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null)
        {
            return (RetainedStateTransactionCodes.AccessDenied, false);
        }

        foreach (var item in completed)
        {
            var deleted = await persistence.DeleteExactAndVerifyAbsentAsync(
                    item.Physical.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!Ready(deleted))
            {
                return (deleted, false);
            }
        }

        return (RetainedStateTransactionCodes.Ready, true);
    }

    private static bool CleanupRecordMatchesPhysical(
        RetainedStateCleanupRecord record,
        AuthenticatedStateObject physical) =>
        StringComparer.Ordinal.Equals(
            record.BaseScopeDigest,
            physical.Header.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(record.Epoch, physical.Header.Epoch) &&
        StringComparer.Ordinal.Equals(
            record.SessionId,
            physical.Header.SessionId) &&
        StringComparer.Ordinal.Equals(
            record.TerminalAcceptanceIdentity,
            physical.Header.PredecessorIdentity) &&
        physical.Header.SuccessorIdentity is null;

    private static bool IsProtected(
        AuthenticatedStateObject item,
        ScopedStateInventorySnapshot snapshot,
        AcceptedStateSelection selected)
    {
        var protectedMetadata = new[]
        {
            selected.Current.Physical.Metadata,
            selected.Current.ReceiptPhysical.Metadata,
            selected.ImmediatePredecessor?.Physical.Metadata,
            selected.ImmediatePredecessor?.ReceiptPhysical.Metadata,
        }.Where(value => value is not null).ToHashSet();
        if (protectedMetadata.Contains(item.Metadata))
        {
            return true;
        }

        if (item.Header.ObjectClass == StateObjectClass.Candidate &&
            StringComparer.Ordinal.Equals(
                item.Header.PredecessorIdentity,
                selected.Current.ReceiptPhysical.Header.ObjectIdentity))
        {
            return true;
        }

        return item.Header.ObjectClass is
            StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment or
            StateObjectClass.Cleanup ||
            snapshot.Unknown.Any(unknown =>
                unknown.Metadata.Reference == item.Metadata.Reference);
    }

    private static bool EquivalentCleanup(
        RetainedStateCleanupRecord left,
        RetainedStateCleanupRecord right) =>
        StringComparer.Ordinal.Equals(
            left.TerminalAcceptanceIdentity,
            right.TerminalAcceptanceIdentity) &&
        StringComparer.Ordinal.Equals(
            left.BaseScopeDigest,
            right.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(left.Epoch, right.Epoch) &&
        StringComparer.Ordinal.Equals(left.SessionId, right.SessionId) &&
        left.Targets.SequenceEqual(right.Targets);

    private static bool HasExactCleanupRecord(
        ScopedStateInventorySnapshot snapshot,
        OpaqueStoreObjectMetadata metadata,
        RetainedStateCleanupRecord expected) =>
        snapshot.Authenticated.Count(item =>
            item.Metadata == metadata &&
            item.Header.ObjectClass == StateObjectClass.Cleanup &&
            CleanupRecordMatchesPhysical(expected, item) &&
            RetainedStateCleanupRecordCodec.TryDecode(
                item.Payload,
                out var parsed) &&
            parsed is not null &&
            EquivalentCleanup(parsed, expected) &&
            StringComparer.Ordinal.Equals(
                parsed.OperationIdentity,
                expected.OperationIdentity)) == 1;

    private static bool Ready(string code) =>
        StringComparer.Ordinal.Equals(
            code,
            RetainedStateTransactionCodes.Ready);
}
