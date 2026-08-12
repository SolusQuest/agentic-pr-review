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
        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                RetainedStatePreparedCandidate>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

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
                        ? cancellationToken.IsCancellationRequested
                            ? RetainedStateTransactionCodes.Cancelled
                            : RetainedStateTransactionCodes.AccessDenied
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
                    lease is null &&
                        cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
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
        RetainedStateOpaqueWriteAttempt>> PrepareOpaqueAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateOwnership ownership,
        RetainedStateOpaqueWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var operationIdentity = request is not null &&
            !request.Payload.IsDefaultOrEmpty &&
            request.Payload.Length <= LineageFormat.MaximumPayloadBytes
                ? ComputeOpaqueOperationIdentity(request)
                : null;
        if (lease is null ||
            ownership is null ||
            request is null ||
            request.ObjectClass is not (
                StateObjectClass.PublicationIntent or
                StateObjectClass.PublicationFailure or
                StateObjectClass.Abandonment) ||
            request.Payload.IsDefaultOrEmpty ||
            request.Payload.Length > LineageFormat.MaximumPayloadBytes ||
            !LineageValidation.IsSha256(operationIdentity) ||
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
            ownership?.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    lease is null &&
                        cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
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
                RetainedStateOpaqueWriteAttempt>.Fail(sentinel);
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
            observed.InventoryDigest is not { } inventoryDigest ||
            observed.Snapshot is not { } snapshot ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == request.ObjectClass) >=
                    LineageFormat.MaximumPhysicalPerClass ||
            snapshot.Authenticated.Count(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup) >=
                    LineageFormat.MaximumPhysicalPerClass ||
            snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == request.ObjectClass ||
                item.Header.ObjectClass == StateObjectClass.Cleanup) ||
            snapshot.Unknown.Any(item =>
                item.Metadata.Reference.Name ==
                    snapshot.Names[request.ObjectClass] ||
                item.Metadata.Reference.Name ==
                    snapshot.Names[StateObjectClass.Cleanup]))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var activeCleanup = snapshot.Authenticated
            .Where(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup &&
                Active(item, binding))
            .Select(item => new
            {
                IsCleanup = RetainedStateCleanupRecordCodec.TryDecode(
                    item.Payload,
                    out _),
                Anchor = RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                    item.Payload,
                    out var anchor)
                    ? anchor
                    : null,
            })
            .ToArray();
        if (activeCleanup.Any(item =>
                !item.IsCleanup && item.Anchor is null) ||
            activeCleanup.Any(item => item.IsCleanup) ||
            activeCleanup.Any(item =>
                item.Anchor is not null &&
                StringComparer.Ordinal.Equals(
                    item.Anchor.CandidateObjectIdentity,
                    ownership.Candidate.Prepared.Header.ObjectIdentity) &&
                StringComparer.Ordinal.Equals(
                    item.Anchor.OperationIdentity,
                    operationIdentity)))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        byte[] payload = request.Payload.ToArray();
        byte[] envelope = [];
        byte[] recoveryPayload = [];
        byte[] anchorPayload = [];
        byte[] anchorEnvelope = [];
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
                payload,
                out var name,
                out envelope,
                out var header,
                out envelopeCode) ||
            name is null ||
            header is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(envelope);
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(envelopeCode);
        }

        if (!RetainedStateOpaqueWriteRecoveryCodec.TryEncode(
                request.ObjectClass,
                request.SemanticRequiredExpiresAtUnixSeconds,
                ownership.Candidate.Prepared.Header.ObjectIdentity,
                name,
                header,
                envelope,
                out recoveryPayload))
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(recoveryPayload);
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                RetainedStateTransactionCodes.Invalid);
        }

        var anchor = new RetainedStateOpaqueWriteAnchor(
            ownership.Candidate.Prepared.Header.ObjectIdentity,
            operationIdentity!,
            request.ObjectClass,
            request.PredecessorIdentity,
            request.SuccessorIdentity,
            request.SemanticRequiredExpiresAtUnixSeconds,
            requiredPlatformExpiry,
            header.ProducingRunIdentity,
            header.ProducingRunAttempt,
            name,
            header.ObjectIdentity,
            ImmutableArray.CreateRange(envelope),
            OpaqueStoreHash.Sha256(envelope),
            RetainedStateOpaqueWriteAnchorPhase
                .PreparedBeforeTargetDispatch,
            OpaqueStoreHash.Sha256(payload));
        var anchorCode = RetainedStateTransactionCodes.Invalid;
        if (!RetainedStateOpaqueWriteAnchorCodec.TryEncode(
                anchor,
                out anchorPayload) ||
            !RetainedStatePersistence.TryPrepareEnvelope(
                locator,
                locatorAccess,
                baseScope,
                binding.SelectedLineage,
                StateObjectClass.Cleanup,
                ownership.Candidate.Prepared.Header.ObjectIdentity,
                header.ObjectIdentity,
                binding.ProducingRunIdentity,
                binding.ProducingRunAttempt,
                trustedNow,
                request.SemanticRequiredExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                anchorPayload,
                out var anchorName,
                out anchorEnvelope,
                out var anchorHeader,
                out anchorCode) ||
            anchorName is null ||
            anchorHeader is null ||
            !authority.TryCreatePersistence(lease, out var persistence) ||
            persistence is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(recoveryPayload);
            CryptographicOperations.ZeroMemory(anchorPayload);
            CryptographicOperations.ZeroMemory(anchorEnvelope);
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    StringComparer.Ordinal.Equals(
                        anchorCode,
                        RetainedStateTransactionCodes.Ready)
                        ? RetainedStateTransactionCodes.Invalid
                        : anchorCode);
        }

        var persistedAnchor = await persistence.UploadAndReconcileAsync(
                locator,
                locatorAccess,
                baseScope,
                binding.SelectedLineage.BaseScopeDigest,
                anchorName,
                anchorEnvelope,
                anchorHeader,
                anchorPayload,
                binding.ProducingRunIdentity,
                binding.ProducingRunAttempt,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(persistedAnchor.Payload ?? []);
        if (!persistedAnchor.Succeeded ||
            persistedAnchor.Metadata is null ||
            persistedAnchor.InventoryDigest is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(recoveryPayload);
            CryptographicOperations.ZeroMemory(anchorPayload);
            CryptographicOperations.ZeroMemory(anchorEnvelope);
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(persistedAnchor.Code);
        }

        try
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Success(
                    RetainedStateTransactionCodes.Prepared,
                    RetainedStateOpaqueWriteAttempt.Create(
                        issuer,
                        authority,
                        ownership.Candidate,
                        request.ObjectClass,
                        operationIdentity!,
                        request.SemanticRequiredExpiresAtUnixSeconds,
                        name,
                        header,
                        payload,
                        envelope,
                        recoveryPayload,
                        persistedAnchor.Metadata,
                        persistedAnchor.InventoryDigest));
        }
        finally
        {
            payload = [];
            envelope = [];
            recoveryPayload = [];
            CryptographicOperations.ZeroMemory(anchorPayload);
            CryptographicOperations.ZeroMemory(anchorEnvelope);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecord>> PersistOpaqueAttemptAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateOpaqueWriteAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (attempt is null ||
            (!attempt.HasEnteredDispatch &&
                cancellationToken.IsCancellationRequested))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    attempt is null
                        ? RetainedStateTransactionCodes.AccessDenied
                        : RetainedStateTransactionCodes.Cancelled);
        }

        using var lease = await authority.EnterAsync(
                attempt.HasEnteredDispatch
                    ? CancellationToken.None
                    : cancellationToken)
            .ConfigureAwait(false);
        var candidate = attempt.Candidate;
        if (lease is null ||
            !attempt.IsIssuedBy(authority) ||
            !candidate.IsIssuedBy(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(candidate.Prepared, binding) ||
            !MatchesOpaqueWriteAttempt(attempt, binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            trustedNow > attempt.SemanticRequiredExpiresAtUnixSeconds ||
            !attempt.TryGetBytes(
                authority,
                out var payload,
                out var envelope) ||
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
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                trustedNow,
                attempt.HasEnteredDispatch
                    ? CancellationToken.None
                    : cancellationToken)
            .ConfigureAwait(false);
        using (var observed = observedResult.Value)
        {
            if (!observedResult.Succeeded ||
                observed is null ||
                !CanAppendCandidate(
                    observed,
                    authority,
                    binding,
                    candidate) ||
                observed.Snapshot is not { } snapshot ||
                !HasExactOpaqueWriteAnchor(
                    snapshot,
                    authority,
                    attempt) ||
                (!attempt.HasEnteredDispatch &&
                    (!StringComparer.Ordinal.Equals(
                        observed.InventoryDigest,
                        attempt.InventoryDigest) ||
                    snapshot.Authenticated.Count(item =>
                        item.Header.ObjectClass == attempt.ObjectClass) >=
                            LineageFormat.MaximumPhysicalPerClass ||
                    snapshot.UnderRetained.Any(item =>
                        item.Header.ObjectClass == attempt.ObjectClass) ||
                    snapshot.Unknown.Any(item =>
                        item.Metadata.Reference.Name ==
                            snapshot.Names[attempt.ObjectClass]))))
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueRecord>.Fail(
                        observedResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : observedResult.Code);
            }
        }

        var shouldDispatch = attempt.TryBeginDispatch();
        var persisted = shouldDispatch
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
                    attempt.Header.RequiredPlatformExpiresAtUnixSeconds,
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
                    attempt.Header.RequiredPlatformExpiresAtUnixSeconds)
                .ConfigureAwait(false);
        if (!persisted.Succeeded &&
            !persisted.MayHaveCommitted &&
            shouldDispatch)
        {
            attempt.ResetDispatchIfDefinitelyNotCommitted();
        }

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
                    attempt.ObjectClass,
                    persisted.Metadata,
                    persisted.Header,
                    persisted.Payload,
                    persisted.InventoryDigest));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueWriteAttempt>> RecoverOpaqueAttemptAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        ImmutableArray<byte> recoveryPayload,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            candidate is null ||
            !candidate.IsIssuedBy(authority) ||
            !candidate.Prepared.IsIssuedBy(authority) ||
            recoveryPayload.IsDefaultOrEmpty ||
            recoveryPayload.Length > LineageFormat.MaximumPayloadBytes ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(candidate.Prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
        }

        byte[] payload = [];
        byte[] envelope = [];
        byte[] canonicalRecovery = [];
        var encoded = recoveryPayload.ToArray();
        try
        {
            if (!RetainedStateOpaqueWriteRecoveryCodec.TryDecode(
                    encoded,
                    out var objectClass,
                    out var semanticExpiry,
                    out var candidateIdentity,
                    out var name,
                out envelope) ||
                candidateIdentity is null ||
                name is null ||
                !StringComparer.Ordinal.Equals(
                    candidateIdentity,
                    candidate.Prepared.Header.ObjectIdentity) ||
                trustedNow > semanticExpiry ||
                !authority.TryGetPersistenceContext(
                    lease,
                    out var locator,
                    out var locatorAccess,
                    out _) ||
                locator is null ||
                locatorAccess is null ||
                !StateControlEnvelopeV1Codec.TryDecrypt(
                    locator,
                    locatorAccess,
                    name,
                    envelope,
                    out var header,
                    out payload,
                    out _) ||
                header is null ||
                header.ObjectClass != objectClass ||
                !StringComparer.Ordinal.Equals(
                    header.PredecessorIdentity,
                    candidateIdentity) ||
                header.LogicalExpiresAtUnixSeconds != semanticExpiry ||
                !RetainedStateRetention.TryOpaque(
                    header.CreatedAtUnixSeconds,
                    semanticExpiry,
                    out var requiredPlatformExpiry) ||
                requiredPlatformExpiry !=
                    header.RequiredPlatformExpiresAtUnixSeconds ||
                !RetainedStateOpaqueWriteRecoveryCodec.TryEncode(
                    objectClass,
                    semanticExpiry,
                    candidateIdentity,
                    name,
                    header,
                    envelope,
                    out canonicalRecovery) ||
                !encoded.AsSpan().SequenceEqual(canonicalRecovery))
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueWriteAttempt>.Fail(
                        RetainedStateTransactionCodes.Conflict);
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
                !CanAppendCandidate(
                    observed,
                    authority,
                    binding,
                    candidate) ||
                observed.InventoryDigest is not { } inventoryDigest ||
                observed.Snapshot is not { } snapshot)
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueWriteAttempt>.Fail(
                        observedResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
            }

            var anchors = snapshot.Authenticated.Where(item =>
                    item.Header.ObjectClass == StateObjectClass.Cleanup &&
                    item.Header.PredecessorIdentity == candidateIdentity &&
                    item.Header.SuccessorIdentity == header.ObjectIdentity &&
                    RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                        item.Payload,
                        out var anchor) &&
                    anchor is not null &&
                    anchor.CandidateObjectIdentity == candidateIdentity &&
                    anchor.ObjectClass == objectClass &&
                    anchor.PredecessorIdentity ==
                        header.PredecessorIdentity &&
                    anchor.SuccessorIdentity ==
                        header.SuccessorIdentity &&
                    anchor.SemanticRequiredExpiresAtUnixSeconds ==
                        semanticExpiry &&
                    anchor.RequiredPlatformExpiresAtUnixSeconds ==
                        header.RequiredPlatformExpiresAtUnixSeconds &&
                    anchor.ProducingRunIdentity ==
                        header.ProducingRunIdentity &&
                    anchor.ProducingRunAttempt ==
                        header.ProducingRunAttempt &&
                    anchor.TargetName == name &&
                    anchor.TargetObjectIdentity == header.ObjectIdentity &&
                    anchor.TargetEnvelope.AsSpan().SequenceEqual(envelope) &&
                    StringComparer.Ordinal.Equals(
                        anchor.TargetEnvelopeSha256,
                        OpaqueStoreHash.Sha256(envelope)) &&
                    anchor.DispatchPhase ==
                        RetainedStateOpaqueWriteAnchorPhase
                            .PreparedBeforeTargetDispatch &&
                    StringComparer.Ordinal.Equals(
                        anchor.TargetPayloadSha256,
                        OpaqueStoreHash.Sha256(payload)))
                .Select(item => new
                {
                    item.Metadata,
                    Anchor = RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                        item.Payload,
                        out var parsed)
                        ? parsed
                        : null,
                })
                .ToArray();
            if (anchors.Length != 1 || anchors[0].Anchor is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueWriteAttempt>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            var attempt = RetainedStateOpaqueWriteAttempt.Create(
                issuer,
                authority,
                candidate,
                objectClass,
                anchors[0].Anchor!.OperationIdentity,
                semanticExpiry,
                name,
                header,
                payload,
                envelope,
                canonicalRecovery,
                anchors[0].Metadata,
                inventoryDigest,
                reconcileOnly: true);
            payload = [];
            envelope = [];
            canonicalRecovery = [];
            if (!MatchesOpaqueWriteAttempt(attempt, binding))
            {
                attempt.Dispose();
                return RetainedStateTransactionResult<
                    RetainedStateOpaqueWriteAttempt>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttempt>.Success(
                    RetainedStateTransactionCodes.Ready,
                    attempt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(canonicalRecovery);
        }
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueWriteAttemptSet>> RecoverAnchoredOpaqueAttemptsAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            candidate is null ||
            !candidate.IsIssuedBy(authority) ||
            !candidate.Prepared.IsIssuedBy(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(candidate.Prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            !authority.TryGetPersistenceContext(
                lease,
                out var locator,
                out var locatorAccess,
                out _) ||
            locator is null ||
            locatorAccess is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttemptSet>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
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
            !CanAppendCandidate(
                observed,
                authority,
                binding,
                candidate) ||
            observed.InventoryDigest is not { } inventoryDigest ||
            observed.Snapshot is not { } snapshot ||
            snapshot.Unknown.Any(item =>
                item.Metadata.Reference.Name ==
                    snapshot.Names[StateObjectClass.Cleanup]) ||
            snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttemptSet>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var anchors = snapshot.Authenticated
            .Where(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup &&
                Active(item, binding))
            .Select(item => new
            {
                Physical = item,
                IsCleanup = RetainedStateCleanupRecordCodec.TryDecode(
                    item.Payload,
                    out _),
                Anchor = RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                    item.Payload,
                    out var anchor)
                    ? anchor
                    : null,
            })
            .ToArray();
        if (anchors.Any(item => !item.IsCleanup && item.Anchor is null))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueWriteAttemptSet>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var builder = ImmutableArray.CreateBuilder<
            RetainedStateOpaqueWriteAttempt>();
        foreach (var anchored in anchors.Where(item =>
            item.Anchor is not null &&
            StringComparer.Ordinal.Equals(
                item.Anchor.CandidateObjectIdentity,
                candidate.Prepared.Header.ObjectIdentity)))
        {
            var anchor = anchored.Anchor!;
            byte[] payload = [];
            byte[] recoveryPayload = [];
            try
            {
                if (trustedNow >
                        anchor.SemanticRequiredExpiresAtUnixSeconds ||
                    !StateControlEnvelopeV1Codec.TryDecrypt(
                        locator,
                        locatorAccess,
                        anchor.TargetName,
                        anchor.TargetEnvelope.AsSpan(),
                        out var header,
                        out payload,
                        out _) ||
                    header is null ||
                    header.ObjectClass != anchor.ObjectClass ||
                    !StringComparer.Ordinal.Equals(
                        header.PredecessorIdentity,
                        anchor.CandidateObjectIdentity) ||
                    !StringComparer.Ordinal.Equals(
                        header.PredecessorIdentity,
                        anchor.PredecessorIdentity) ||
                    !StringComparer.Ordinal.Equals(
                        header.SuccessorIdentity,
                        anchor.SuccessorIdentity) ||
                    header.LogicalExpiresAtUnixSeconds !=
                        anchor.SemanticRequiredExpiresAtUnixSeconds ||
                    header.RequiredPlatformExpiresAtUnixSeconds !=
                        anchor.RequiredPlatformExpiresAtUnixSeconds ||
                    !StringComparer.Ordinal.Equals(
                        header.ProducingRunIdentity,
                        anchor.ProducingRunIdentity) ||
                    header.ProducingRunAttempt !=
                        anchor.ProducingRunAttempt ||
                    !StringComparer.Ordinal.Equals(
                        header.ObjectIdentity,
                        anchor.TargetObjectIdentity) ||
                    !StringComparer.Ordinal.Equals(
                        anchor.TargetEnvelopeSha256,
                        OpaqueStoreHash.Sha256(
                            anchor.TargetEnvelope.AsSpan())) ||
                    anchor.DispatchPhase !=
                        RetainedStateOpaqueWriteAnchorPhase
                            .PreparedBeforeTargetDispatch ||
                    !StringComparer.Ordinal.Equals(
                        anchor.TargetPayloadSha256,
                        OpaqueStoreHash.Sha256(payload)) ||
                    !RetainedStateRetention.TryOpaque(
                        header.CreatedAtUnixSeconds,
                        anchor.SemanticRequiredExpiresAtUnixSeconds,
                        out var requiredPlatformExpiry) ||
                    requiredPlatformExpiry !=
                        header.RequiredPlatformExpiresAtUnixSeconds ||
                    !RetainedStateOpaqueWriteRecoveryCodec.TryEncode(
                        anchor.ObjectClass,
                        anchor.SemanticRequiredExpiresAtUnixSeconds,
                        anchor.CandidateObjectIdentity,
                        anchor.TargetName,
                        header,
                        anchor.TargetEnvelope.AsSpan(),
                        out recoveryPayload))
                {
                    foreach (var buffered in builder)
                    {
                        buffered.Dispose();
                    }

                    return RetainedStateTransactionResult<
                        RetainedStateOpaqueWriteAttemptSet>.Fail(
                            RetainedStateTransactionCodes.Conflict);
                }

                var attempt = RetainedStateOpaqueWriteAttempt.Create(
                    issuer,
                    authority,
                    candidate,
                    anchor.ObjectClass,
                    anchor.OperationIdentity,
                    anchor.SemanticRequiredExpiresAtUnixSeconds,
                    anchor.TargetName,
                    header,
                    payload,
                    anchor.TargetEnvelope.ToArray(),
                    recoveryPayload,
                    anchored.Physical.Metadata,
                    inventoryDigest,
                    reconcileOnly: true);
                payload = [];
                recoveryPayload = [];
                if (!MatchesOpaqueWriteAttempt(attempt, binding) ||
                    !HasExactOpaqueWriteAnchor(
                        snapshot,
                        authority,
                        attempt))
                {
                    attempt.Dispose();
                    foreach (var existing in builder)
                    {
                        existing.Dispose();
                    }

                    return RetainedStateTransactionResult<
                        RetainedStateOpaqueWriteAttemptSet>.Fail(
                            RetainedStateTransactionCodes.Conflict);
                }

                builder.Add(attempt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(recoveryPayload);
            }
        }

        return RetainedStateTransactionResult<
            RetainedStateOpaqueWriteAttemptSet>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateOpaqueWriteAttemptSet.Create(
                    builder.ToImmutable()));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptancePreparation>> PrepareAcceptanceAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateOwnership ownership,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            ownership.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

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
                    lease is null &&
                        cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
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

        var predecessor = await PrepareImmediatePredecessorAsync(
                authority,
                lease,
                binding,
                candidate,
                acceptedAt,
                logicalExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(predecessor.Code))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Fail(predecessor.Code);
        }

        var predecessorCopy = predecessor.Attempt;

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
            (predecessorCopy is null
                ? !PredecessorCovers(before, binding, logicalExpiry)
                : before.AcceptedState.Selection?.Current is not { } current ||
                    !MatchesPredecessorCopyAttempt(
                        predecessorCopy,
                        current,
                        binding)) ||
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
                    predecessorCopy,
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
                predecessorCopy,
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

    internal async Task<string> ReconcileAcceptancePredecessorAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        RetainedStateAcceptanceRecoveryDurability durability,
        CancellationToken cancellationToken)
    {
        if (preparation is null ||
            durability is null ||
            !durability.TryAuthorizePredecessor(authority, preparation))
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var attempt = preparation?.GetPredecessorCopyAttempt(authority);
        if (cancellationToken.IsCancellationRequested &&
            attempt is not null &&
            !attempt.HasEnteredDispatch)
        {
            return RetainedStateTransactionCodes.Cancelled;
        }

        using var lease = await authority.EnterAsync(
                attempt?.HasEnteredDispatch == true
                    ? CancellationToken.None
                    : cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return cancellationToken.IsCancellationRequested
                ? RetainedStateTransactionCodes.Cancelled
                : RetainedStateTransactionCodes.AccessDenied;
        }

        if (preparation is null ||
            !preparation.IsIssuedBy(authority))
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        if (attempt is null)
        {
            return cancellationToken.IsCancellationRequested
                ? RetainedStateTransactionCodes.Cancelled
                : RetainedStateTransactionCodes.Ready;
        }

        var candidate = preparation.Candidate;
        if (!authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(candidate.Prepared, binding) ||
            !authority.TryReadTrustedTime(lease, out var observedAt) ||
            observedAt > attempt.RequiredLogicalExpiresAtUnixSeconds ||
            authority.GetAcceptedSelection(lease)?.Current is not { } current ||
            !MatchesPredecessorCopyAttempt(attempt, current, binding))
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var persisted = await PersistPredecessorCopyAttemptAsync(
                authority,
                lease,
                binding,
                attempt,
                dispatchIfUnresolved: !attempt.ReconcileOnly,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Ready(persisted))
        {
            return persisted;
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                attempt.RequiredLogicalExpiresAtUnixSeconds,
                observedAt,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            !CanAppendCandidate(observed, authority, binding, candidate) ||
            !PredecessorCovers(
                observed,
                binding,
                attempt.RequiredLogicalExpiresAtUnixSeconds))
        {
            return observedResult.Succeeded
                ? RetainedStateTransactionCodes.Conflict
                : observedResult.Code;
        }

        return RetainedStateTransactionCodes.Ready;
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptanceEvidence>> CreateAcceptanceEvidenceAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        RetainedStateAcceptanceRecoveryDurability durability,
        RetainedStateOwnership ownership,
        ExactHeadRevalidationResult exactHead,
        CancellationToken cancellationToken)
    {
        if (preparation is null || durability is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceEvidence>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

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
            snapshot.Authenticated.Count(item =>
                item.Metadata == durability.RecoveryRecordMetadata) != 1 ||
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

        if (!durability.TryConsumeForEvidence(authority, preparation))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceEvidence>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
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

        if (cachedAttempt is null &&
            cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
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

        if (attempt.ReconcileOnly)
        {
            return RetainedStateTransactionResult<
                VerifiedRetainedStateAcceptance>.Fail(
                    RetainedStateTransactionCodes.OutcomeUnknown);
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

        var shouldDispatch = attempt.TryBeginDispatch();
        var persisted = shouldDispatch
            ? await persistence.UploadAndReconcileAsync(
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
                .ConfigureAwait(false)
            : await persistence.ReconcileExistingAsync(
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
                    receiptPlatformExpiry)
                .ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
        if (!persisted.Succeeded &&
            !persisted.MayHaveCommitted &&
            shouldDispatch)
        {
            attempt.ResetDispatchIfDefinitelyNotCommitted();
        }

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
        RetainedStatePendingCandidateEvidence>> InspectPendingCandidateAsync(
        RetainedStateTransactionAuthority authority,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            authority.HasTerminalAcceptance ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStatePendingCandidateEvidence>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                trustedNow,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            !snapshot.Unknown.IsEmpty ||
            !MatchesAcceptedTail(observed.AcceptedState, binding) ||
            HasAcceptanceSuccessor(snapshot, binding))
        {
            return RetainedStateTransactionResult<
                RetainedStatePendingCandidateEvidence>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var pending = snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item =>
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
            !AcceptedStateRecordValidation.IsValid(generation) ||
            generation.Generation !=
                (binding.CurrentGeneration is null
                    ? 0
                    : binding.CurrentGeneration.Value + 1) ||
            !StringComparer.Ordinal.Equals(
                generation.PreviousLogicalGenerationIdentity,
                binding.CurrentLogicalGenerationIdentity) ||
            !AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out var publication) ||
            publication is null ||
            !StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                generation.ProducerHeadSha) ||
            !R4PublicationIdentityV1.IsValidScope(
                binding.Publication.Scope) ||
            binding.Publication.Scope.RepositoryId > long.MaxValue ||
            binding.Publication.Scope.PullRequestNumber > long.MaxValue ||
            publication.RepositoryId !=
                (long)binding.Publication.Scope.RepositoryId ||
            publication.PullRequestNumber !=
                (long)binding.Publication.Scope.PullRequestNumber ||
            !StringComparer.Ordinal.Equals(
                publication.RepositoryName,
                binding.Publication.RepositoryName) ||
            pending[0].Header.CreatedAtUnixSeconds !=
                generation.PreparedAtUnixSeconds ||
            pending[0].Header.LogicalExpiresAtUnixSeconds !=
                generation.PreparedExpiresAtUnixSeconds ||
            !StringComparer.Ordinal.Equals(
                pending[0].Header.ProducingRunIdentity,
                pending[0].Metadata.ProducingRun.Identity) ||
            pending[0].Header.ProducingRunAttempt !=
                pending[0].Metadata.ProducingRun.Attempt ||
            !AcceptedStateIdentity.TryComputeLogicalGeneration(
                pending[0].Payload,
                binding.SelectedLineage.BaseScopeDigest,
                binding.SelectedLineage.Epoch,
                binding.SelectedLineage.SessionId,
                binding.CurrentAcceptanceReceiptIdentity,
                out var logicalIdentity))
        {
            return RetainedStateTransactionResult<
                RetainedStatePendingCandidateEvidence>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        return RetainedStateTransactionResult<
            RetainedStatePendingCandidateEvidence>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStatePendingCandidateEvidence.Create(
                    issuer,
                    authority,
                    pending[0].Metadata,
                    pending[0].Header,
                    logicalIdentity,
                    generation.Generation,
                    generation.ProducerHeadSha,
                    StringComparer.Ordinal.Equals(
                        generation.ProducerHeadSha,
                        binding.Reviewed.HeadSha),
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
        RetainedStateAcceptanceRecoveryDurability>>
        BindAcceptanceRecoveryDurabilityAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        RetainedStateOpaqueRecord recoveryRecord,
        ImmutableArray<byte> extractedInnerPayload,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            preparation is null ||
            recoveryRecord is null ||
            !preparation.IsIssuedBy(authority) ||
            recoveryRecord.ObjectClass is not (
                StateObjectClass.PublicationIntent or
                StateObjectClass.PublicationFailure or
                StateObjectClass.Abandonment) ||
            extractedInnerPayload.IsDefaultOrEmpty ||
            !preparation.TryCreateRecoveryHandoff(out var handoff) ||
            handoff is null ||
            !extractedInnerPayload.AsSpan().SequenceEqual(
                handoff.OpaqueInnerPayload.AsSpan()) ||
            !StringComparer.Ordinal.Equals(
                recoveryRecord.Header.PredecessorIdentity,
                handoff.CandidateObjectIdentity) ||
            recoveryRecord.Header.LogicalExpiresAtUnixSeconds <
                handoff.MinimumSemanticExpiresAtUnixSeconds ||
            recoveryRecord.Metadata.ExpiresAtUnixSeconds <
                handoff.MinimumSemanticExpiresAtUnixSeconds ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !MatchesPreparedBinding(
                preparation.Candidate.Prepared,
                binding) ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceRecoveryDurability>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                handoff.MinimumSemanticExpiresAtUnixSeconds,
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
                preparation.Candidate) ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            snapshot.Unknown.Any(item =>
                item.Metadata.Reference ==
                    recoveryRecord.Metadata.Reference) ||
            snapshot.Authenticated.Count(item =>
                recoveryRecord.MatchesAuthenticated(authority, item)) != 1)
        {
            return RetainedStateTransactionResult<
                RetainedStateAcceptanceRecoveryDurability>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        return RetainedStateTransactionResult<
            RetainedStateAcceptanceRecoveryDurability>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateAcceptanceRecoveryDurability.Create(
                    issuer,
                    authority,
                    preparation,
                    recoveryRecord.Metadata,
                    inventoryDigest));
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateAcceptancePreparation>>
        RecoverAcceptancePreparationAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        ImmutableArray<byte> recoveryPayload,
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
            recoveryPayload.IsDefaultOrEmpty ||
            recoveryPayload.Length > LineageFormat.MaximumPayloadBytes ||
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
        byte[] copyPayload = [];
        RetainedStateAcceptanceAttempt? attempt = null;
        RetainedStatePredecessorCopyAttempt? predecessorCopyAttempt = null;
        RetainedStateRecoveredPredecessorCopy? recoveredCopy = null;
        byte[] canonicalRecovery = [];
        var encoded = recoveryPayload.ToArray();
        try
        {
            if (!RetainedStateAcceptanceRecoveryCodec.TryDecode(
                    encoded.AsSpan(),
                    out name,
                    out envelopeBytes,
                    out recoveredCopy) ||
                name is null ||
                !authority.TryGetPersistenceContext(
                    lease,
                    out var locator,
                    out var locatorAccess,
                    out var baseScope) ||
                locator is null ||
                locatorAccess is null ||
                baseScope is null ||
                !StateControlEnvelopeV1Codec.TryDecrypt(
                    locator,
                    locatorAccess,
                    name,
                    envelopeBytes,
                    out header,
                    out receiptBytes,
                    out _) ||
                header is null ||
                !AcceptedStateAcceptanceReceiptCodec.TryDecode(
                    receiptBytes,
                    out recoveredReceipt) ||
                recoveredReceipt is null ||
                observedAt > recoveredReceipt.LogicalExpiresAtUnixSeconds ||
                !RetainedStateRetention.TryAcceptance(
                    recoveredReceipt.AcceptedAtUnixSeconds,
                    out var logicalExpiry,
                    out var platformExpiry) ||
                logicalExpiry != recoveredReceipt.LogicalExpiresAtUnixSeconds ||
                header.ObjectClass != StateObjectClass.Acceptance ||
                header.CreatedAtUnixSeconds !=
                    recoveredReceipt.AcceptedAtUnixSeconds ||
                header.LogicalExpiresAtUnixSeconds != logicalExpiry ||
                header.RequiredPlatformExpiresAtUnixSeconds !=
                    platformExpiry ||
                !StringComparer.Ordinal.Equals(
                    header.ProducingRunIdentity,
                    recoveredReceipt.ProducingRunIdentity) ||
                header.ProducingRunAttempt !=
                    recoveredReceipt.ProducingRunAttempt ||
                candidate.Metadata.ExpiresAtUnixSeconds < logicalExpiry ||
                !AcceptedStateAcceptanceReceiptCodec.TryEncode(
                    recoveredReceipt,
                    out canonicalReceipt) ||
                !receiptBytes.AsSpan().SequenceEqual(canonicalReceipt) ||
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
                envelopeBytes,
                reconcileOnly: true);
            receiptBytes = [];
            envelopeBytes = [];

            var acceptedSelection = authority.GetAcceptedSelection(lease);
            if (recoveredCopy is not null)
            {
                if (acceptedSelection?.Current is not { } selectedCurrent ||
                    recoveredCopy.RequiredLogicalExpiresAtUnixSeconds !=
                        logicalExpiry ||
                    !StateControlEnvelopeV1Codec.TryDecrypt(
                        locator,
                        locatorAccess,
                        recoveredCopy.Name,
                        recoveredCopy.Envelope,
                        out var copyHeader,
                        out copyPayload,
                        out _) ||
                    copyHeader is null ||
                    copyHeader.ObjectClass != StateObjectClass.Candidate ||
                    copyHeader.LogicalExpiresAtUnixSeconds != logicalExpiry ||
                    copyHeader.RequiredPlatformExpiresAtUnixSeconds !=
                        recoveredCopy.RequiredPlatformExpiresAtUnixSeconds ||
                    !RetainedStateRetention.TryOpaque(
                        copyHeader.CreatedAtUnixSeconds,
                        recoveredCopy.RequiredLogicalExpiresAtUnixSeconds,
                        out var copyPlatformExpiry) ||
                    copyPlatformExpiry !=
                        recoveredCopy.RequiredPlatformExpiresAtUnixSeconds)
                {
                    return RetainedStateTransactionResult<
                        RetainedStateAcceptancePreparation>.Fail(
                            RetainedStateTransactionCodes.Conflict);
                }

                predecessorCopyAttempt =
                    RetainedStatePredecessorCopyAttempt.Create(
                        issuer,
                        recoveredCopy.LogicalGenerationIdentity,
                        recoveredCopy.RequiredLogicalExpiresAtUnixSeconds,
                        recoveredCopy.RequiredPlatformExpiresAtUnixSeconds,
                        recoveredCopy.Name,
                        copyHeader,
                        copyPayload,
                        recoveredCopy.Envelope.ToArray(),
                        reconcileOnly: true);
                copyPayload = [];
                if (!MatchesPredecessorCopyAttempt(
                        predecessorCopyAttempt,
                        selectedCurrent,
                        binding))
                {
                    return RetainedStateTransactionResult<
                        RetainedStateAcceptancePreparation>.Fail(
                            RetainedStateTransactionCodes.Conflict);
                }
            }

            if (!MatchesReceipt(sticky, prepared) ||
                !MatchesAcceptanceAttempt(
                    attempt,
                    sticky,
                    binding,
                    prepared) ||
                !RetainedStateAcceptanceRecoveryCodec.TryEncode(
                    attempt,
                    predecessorCopyAttempt,
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
                (!CanAppendCandidate(
                        observed,
                        authority,
                        binding,
                        candidate) &&
                    !MatchesVisibleAcceptance(
                        observed,
                        authority,
                        binding,
                        candidate,
                        attempt)) ||
                (predecessorCopyAttempt is null
                    ? !PredecessorCovers(observed, binding, logicalExpiry)
                    : acceptedSelection?.Current is not { } predecessor ||
                        !MatchesPredecessorCopyAttempt(
                            predecessorCopyAttempt,
                            predecessor,
                            binding)) ||
                observed.Snapshot is not { } snapshot ||
                !snapshot.UnderRetained.IsEmpty ||
                !snapshot.Unknown.IsEmpty ||
                observed.InventoryDigest is not { } inventoryDigest ||
                observed.SelectedHead is null)
            {
                return RetainedStateTransactionResult<
                    RetainedStateAcceptancePreparation>.Fail(
                        observedResult.Succeeded
                            ? RetainedStateTransactionCodes.Conflict
                            : observedResult.Code);
            }

            var selectedLineage = new SelectedLineageSnapshot(
                observed.SelectedHead.Header.BaseScopeDigest,
                observed.SelectedHead.Header.Epoch,
                observed.SelectedHead.Header.SessionId,
                observed.SelectedHead.Header.ObjectIdentity,
                observed.SelectedHead.Head.Transition);
            var ownership = RetainedStateOwnership.Create(
                issuer,
                authority,
                candidate,
                selectedLineage,
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
                predecessorCopyAttempt,
                canonicalRecovery);
            attempt = null;
            predecessorCopyAttempt = null;
            canonicalRecovery = [];
            return RetainedStateTransactionResult<
                RetainedStateAcceptancePreparation>.Success(
                    RetainedStateTransactionCodes.Ready,
                    preparation);
        }
        finally
        {
            attempt?.Dispose();
            predecessorCopyAttempt?.Dispose();
            recoveredCopy?.Dispose();
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(receiptBytes);
            CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(canonicalReceipt);
            CryptographicOperations.ZeroMemory(copyPayload);
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
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.Cancelled);
        }

        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            acceptance is null ||
            !acceptance.IsIssuedBy(authority) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    lease is null &&
                        cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
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
            !snapshot.Unknown.IsEmpty)
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var selected = observed.AcceptedState.Selection!;
        var cleanupObjects = snapshot.Authenticated.Where(item =>
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
                IsAnchor = RetainedStateOpaqueWriteAnchorCodec.IsAnchor(
                    item.Payload),
            })
            .ToArray();
        var activeCleanup = cleanupObjects.Where(item =>
                item.Parsed is not null)
            .ToArray();
        if (snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == StateObjectClass.Cleanup &&
                !RetainedStateOpaqueWriteAnchorCodec.IsAnchor(
                    item.Payload)) ||
            cleanupObjects.Any(item =>
                item.Parsed is null && !item.IsAnchor) ||
            activeCleanup.Length > 1 ||
            activeCleanup.Any(item =>
                !CleanupRecordMatchesPhysical(
                    item.Parsed!,
                    item.Physical) ||
                !StringComparer.Ordinal.Equals(
                    item.Parsed!.TerminalAcceptanceIdentity,
                    acceptance.AcceptanceReceiptIdentity)))
        {
            return RetainedStateTransactionResult<
                RetainedStateCleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var targets = activeCleanup.Length == 1
            ? activeCleanup[0].Parsed!.Targets
                .Select(item => new RetainedStateCleanupTarget(item))
                .ToImmutableArray()
            : snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Where(item =>
                    !IsProtected(item, snapshot, selected) &&
                    item.Header.ObjectClass is (
                        StateObjectClass.Candidate or
                        StateObjectClass.Acceptance) &&
                    !StringComparer.Ordinal.Equals(
                        item.Header.PredecessorIdentity,
                        selected.Current.ReceiptPhysical.Header
                            .ObjectIdentity))
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

    internal async Task<RetainedStateTransactionResult<
        RetainedStateP5CleanupAuthorization>> AuthorizeP5CleanupAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateP5CleanupDecision decision,
        RetainedStatePendingCandidateEvidence? pendingCandidate,
        RetainedStateOpaqueRecord? opaqueRecord,
        RetainedStateOpaqueWriteAttempt? opaqueWrite,
        CancellationToken cancellationToken)
    {
        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var sourceCount = (pendingCandidate is null ? 0 : 1) +
            (opaqueRecord is null ? 0 : 1) +
            (opaqueWrite is null ? 0 : 1);
        if (lease is null ||
            decision is null ||
            sourceCount != 1 ||
            !LineageValidation.IsSha256(decision.ClassificationIdentity) ||
            (decision.MarkerEvidenceIdentity is not null &&
                !LineageValidation.IsSha256(
                    decision.MarkerEvidenceIdentity)) ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow))
        {
            return RetainedStateTransactionResult<
                RetainedStateP5CleanupAuthorization>.Fail(
                    lease is null && cancellationToken.IsCancellationRequested
                        ? RetainedStateTransactionCodes.Cancelled
                        : RetainedStateTransactionCodes.AccessDenied);
        }

        OpaqueStoreObjectMetadata target;
        string sourceInventoryDigest;
        var requireSourceInventoryMatch = true;
        if (decision.Classification ==
                RetainedStateP5CleanupClassification
                    .StaleCandidateAbandonment &&
            pendingCandidate is not null &&
            pendingCandidate.IsIssuedBy(authority) &&
            pendingCandidate.Generation == 0 &&
            !pendingCandidate.MatchesCurrentReviewedHead &&
            decision.MarkerEvidenceIdentity is not null)
        {
            target = pendingCandidate.Metadata;
            sourceInventoryDigest = pendingCandidate.InventoryDigest;
        }
        else if (decision.Classification ==
                RetainedStateP5CleanupClassification
                    .CompletedOpaqueRecord &&
            opaqueRecord is not null &&
            opaqueRecord.ObjectClass is (
                StateObjectClass.PublicationIntent or
                StateObjectClass.PublicationFailure or
                StateObjectClass.Abandonment))
        {
            target = opaqueRecord.Metadata;
            sourceInventoryDigest = opaqueRecord.InventoryDigest;
        }
        else if (decision.Classification ==
                RetainedStateP5CleanupClassification
                    .CompletedOpaqueWriteAnchor &&
            opaqueWrite is not null &&
            opaqueWrite.IsIssuedBy(authority))
        {
            target = opaqueWrite.AnchorMetadata;
            sourceInventoryDigest = opaqueWrite.InventoryDigest;
            requireSourceInventoryMatch = false;
        }
        else
        {
            return RetainedStateTransactionResult<
                RetainedStateP5CleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var observedResult = await authority.ObserveAsync(
                lease,
                trustedNow,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var observed = observedResult.Value;
        if (!observedResult.Succeeded ||
            observed is null ||
            observed.Snapshot is not { } snapshot ||
            observed.InventoryDigest is not { } inventoryDigest ||
            (requireSourceInventoryMatch &&
                !StringComparer.Ordinal.Equals(
                    inventoryDigest,
                    sourceInventoryDigest)) ||
            !snapshot.Unknown.IsEmpty)
        {
            return RetainedStateTransactionResult<
                RetainedStateP5CleanupAuthorization>.Fail(
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code);
        }

        var matches = snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item => item.Metadata == target)
            .ToArray();
        var validTarget = matches.Length == 1 &&
            decision.Classification switch
            {
                RetainedStateP5CleanupClassification
                    .StaleCandidateAbandonment =>
                    matches[0].Header.ObjectClass ==
                        StateObjectClass.Candidate &&
                    matches[0].Header.ObjectIdentity ==
                        pendingCandidate!.Header.ObjectIdentity,
                RetainedStateP5CleanupClassification
                    .CompletedOpaqueRecord =>
                    opaqueRecord!.MatchesAuthenticated(
                        authority,
                        matches[0]),
                RetainedStateP5CleanupClassification
                    .CompletedOpaqueWriteAnchor =>
                    matches[0].Header.ObjectClass ==
                        StateObjectClass.Cleanup &&
                    HasExactOpaqueWriteAnchor(
                        snapshot,
                        authority,
                        opaqueWrite!),
                _ => false,
            };
        if (!validTarget)
        {
            return RetainedStateTransactionResult<
                RetainedStateP5CleanupAuthorization>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        return RetainedStateTransactionResult<
            RetainedStateP5CleanupAuthorization>.Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateP5CleanupAuthorization.Create(
                    issuer,
                    authority,
                    decision,
                    target,
                    inventoryDigest));
    }

    internal async Task<RetainedStateCleanupResult> CleanupP5AuthorizedAsync(
        RetainedStateTransactionAuthority authority,
        RetainedStateP5CleanupRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            request.Authorization is null ||
            !request.Authorization.TryConsume(authority))
        {
            return new RetainedStateCleanupResult(
                null,
                Completed: false,
                RetainedStateTransactionCodes.AccessDenied);
        }

        using var lease = await authority.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null ||
            !authority.TryGetBinding(lease, out var binding) ||
            binding is null ||
            !authority.TryReadTrustedTime(lease, out var trustedNow) ||
            request.SemanticRequiredExpiresAtUnixSeconds < trustedNow ||
            !RetainedStateRetention.TryOpaque(
                trustedNow,
                request.SemanticRequiredExpiresAtUnixSeconds,
                out var requiredPlatformExpiry) ||
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
            return new RetainedStateCleanupResult(
                null,
                Completed: false,
                lease is null && cancellationToken.IsCancellationRequested
                    ? RetainedStateTransactionCodes.Cancelled
                    : RetainedStateTransactionCodes.AccessDenied);
        }

        var sentinel = locator.CoversDependentExpiry(
                locatorAccess,
                requiredPlatformExpiry)
            ? RetainedStateTransactionCodes.Ready
            : await authority.EnsureSentinelCoverageAsync(
                    lease,
                    requiredPlatformExpiry,
                    trustedNow,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!Ready(sentinel))
        {
            return new RetainedStateCleanupResult(
                null,
                Completed: false,
                sentinel);
        }

        var authorization = request.Authorization;
        var cleanupPredecessorIdentity =
            binding.CurrentAcceptanceReceiptIdentity ??
            authorization.Target.EncryptedObjectDigest.Sha256;
        var beforeResult = await authority.ObserveAsync(
                lease,
                trustedNow,
                trustedNow,
                cancellationToken)
            .ConfigureAwait(false);
        using var before = beforeResult.Value;
        if (!beforeResult.Succeeded ||
            before is null ||
            before.Snapshot is not { } snapshot ||
            !StringComparer.Ordinal.Equals(
                before.InventoryDigest,
                authorization.InventoryDigest) ||
            !snapshot.Unknown.IsEmpty ||
            snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Count(item => item.Metadata == authorization.Target) != 1)
        {
            return new RetainedStateCleanupResult(
                null,
                Completed: false,
                beforeResult.Succeeded
                    ? RetainedStateTransactionCodes.Conflict
                    : beforeResult.Code);
        }

        if (!RetainedStateCleanupRecordCodec.TryCreate(
                cleanupPredecessorIdentity,
                binding.SelectedLineage.BaseScopeDigest,
                binding.SelectedLineage.Epoch,
                binding.SelectedLineage.SessionId,
                authorization.InventoryDigest,
                [authorization.Target],
                out var cleanup) ||
            cleanup is null ||
            !RetainedStateCleanupRecordCodec.TryEncode(
                cleanup,
                out var cleanupBytes))
        {
            return new RetainedStateCleanupResult(
                null,
                Completed: false,
                RetainedStateTransactionCodes.Invalid);
        }

        try
        {
            if (!RetainedStatePersistence.TryPrepareEnvelope(
                    locator,
                    locatorAccess,
                    baseScope,
                    binding.SelectedLineage,
                    StateObjectClass.Cleanup,
                    cleanupPredecessorIdentity,
                    successorIdentity: null,
                    binding.ProducingRunIdentity,
                    binding.ProducingRunAttempt,
                    trustedNow,
                    request.SemanticRequiredExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    cleanupBytes,
                    out var cleanupName,
                    out var cleanupEnvelope,
                    out var cleanupHeader,
                    out var cleanupCode) ||
                cleanupName is null ||
                cleanupHeader is null)
            {
                return new RetainedStateCleanupResult(
                    null,
                    Completed: false,
                    cleanupCode);
            }

            OpaqueStoreObjectMetadata? cleanupMetadata;
            try
            {
                var persisted = await persistence.UploadAndReconcileAsync(
                        locator,
                        locatorAccess,
                        baseScope,
                        binding.SelectedLineage.BaseScopeDigest,
                        cleanupName,
                        cleanupEnvelope,
                        cleanupHeader,
                        cleanupBytes,
                        binding.ProducingRunIdentity,
                        binding.ProducingRunAttempt,
                        requiredPlatformExpiry,
                        cancellationToken)
                    .ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(persisted.Payload ?? []);
                if (!persisted.Succeeded || persisted.Metadata is null)
                {
                    return new RetainedStateCleanupResult(
                        null,
                        Completed: false,
                        persisted.Code);
                }

                cleanupMetadata = persisted.Metadata;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cleanupEnvelope);
            }

            var freshResult = await authority.ObserveAsync(
                    lease,
                    trustedNow,
                    trustedNow,
                    CancellationToken.None)
                .ConfigureAwait(false);
            using var fresh = freshResult.Value;
            if (!freshResult.Succeeded ||
                fresh is null ||
                fresh.Snapshot is not { } freshSnapshot ||
                !freshSnapshot.Unknown.IsEmpty ||
                !HasExactCleanupRecord(
                    freshSnapshot,
                    cleanupMetadata,
                    cleanup) ||
                freshSnapshot.Authenticated
                    .Concat(freshSnapshot.UnderRetained)
                    .Count(item => item.Metadata == authorization.Target) != 1)
            {
                return new RetainedStateCleanupResult(
                    null,
                    Completed: false,
                    freshResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : freshResult.Code);
            }

            var deleted = await persistence.DeleteExactAndVerifyAbsentAsync(
                    authorization.Target,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!Ready(deleted))
            {
                return new RetainedStateCleanupResult(
                    null,
                    Completed: false,
                    deleted);
            }

            var completedResult = await authority.ObserveAsync(
                    lease,
                    trustedNow,
                    trustedNow,
                    CancellationToken.None)
                .ConfigureAwait(false);
            using var completed = completedResult.Value;
            if (!completedResult.Succeeded ||
                completed is null ||
                completed.Snapshot is not { } completedSnapshot ||
                !completedSnapshot.Unknown.IsEmpty ||
                !HasExactCleanupRecord(
                    completedSnapshot,
                    cleanupMetadata,
                    cleanup) ||
                completedSnapshot.Authenticated
                    .Concat(completedSnapshot.UnderRetained)
                    .Any(item => item.Metadata == authorization.Target))
            {
                return new RetainedStateCleanupResult(
                    null,
                    Completed: false,
                    completedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : completedResult.Code);
            }

            var selfDeleted = await persistence.DeleteExactAndVerifyAbsentAsync(
                    cleanupMetadata,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new RetainedStateCleanupResult(
                null,
                Ready(selfDeleted),
                selfDeleted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleanupBytes);
        }
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

        if (cancellationToken.IsCancellationRequested)
        {
            return new RetainedStateCleanupResult(
                acceptance,
                Completed: false,
                RetainedStateTransactionCodes.Cancelled);
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
                lease is null &&
                    cancellationToken.IsCancellationRequested
                    ? RetainedStateTransactionCodes.Cancelled
                    : RetainedStateTransactionCodes.AccessDenied);
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
            var cleanupObjects = snapshot.Authenticated.Where(item =>
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
                    IsAnchor = RetainedStateOpaqueWriteAnchorCodec.IsAnchor(
                        item.Payload),
                })
                .ToArray();
            var activeCleanup = cleanupObjects.Where(item =>
                    item.Parsed is not null)
                .ToArray();
            if (cleanupObjects.Any(item =>
                    item.Parsed is null && !item.IsAnchor) ||
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

    private async Task<(string Code,
        RetainedStatePredecessorCopyAttempt? Attempt)>
        PrepareImmediatePredecessorAsync(
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
                ? (RetainedStateTransactionCodes.Ready, null)
                : (RetainedStateTransactionCodes.Conflict, null);
        }

        var current = selected.Current;
        if (current.ReceiptPhysical.Metadata.ExpiresAtUnixSeconds <
            requiredLogicalExpiry)
        {
            return (RetainedStateTransactionCodes.RetentionFailed, null);
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
                return (
                    observedResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observedResult.Code,
                    null);
            }

            if (PredecessorCovers(
                    observed,
                    binding,
                    requiredLogicalExpiry))
            {
                return (RetainedStateTransactionCodes.Ready, null);
            }

            if (snapshot.Authenticated.Count(item =>
                    item.Header.ObjectClass == StateObjectClass.Candidate) >=
                LineageFormat.MaximumPhysicalPerClass)
            {
                return (RetainedStateTransactionCodes.CleanupDebt, null);
            }
        }

        var frozen = authority.GetPredecessorCopyAttempt(lease);
        if (frozen is not null)
        {
            if (StringComparer.Ordinal.Equals(
                    frozen.LogicalGenerationIdentity,
                    current.LogicalGenerationIdentity) &&
                frozen.RequiredLogicalExpiresAtUnixSeconds ==
                    requiredLogicalExpiry &&
                MatchesPredecessorCopyAttempt(
                    frozen,
                    current,
                    binding))
            {
                return (RetainedStateTransactionCodes.Ready, frozen);
            }

            if (frozen.HasEnteredDispatch ||
                !authority.TryClearPredecessorCopyAttempt(lease, frozen))
            {
                return (RetainedStateTransactionCodes.Conflict, null);
            }
        }

        if (!AcceptedStateGenerationRecordCodec.TryEncode(
                current.Generation,
                out var generationBytes))
        {
            return (RetainedStateTransactionCodes.Invalid, null);
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
            return (RetainedStateTransactionCodes.Invalid, null);
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
                return (envelopeCode, null);
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
                return (RetainedStateTransactionCodes.Conflict, null);
            }

            return (RetainedStateTransactionCodes.Ready, created);
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

        var shouldDispatch = dispatchIfUnresolved &&
            attempt.TryBeginDispatch();
        var persisted = shouldDispatch
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
            shouldDispatch)
        {
            attempt.ResetDispatchIfDefinitelyNotCommitted();
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

    private static bool MatchesVisibleAcceptance(
        RetainedStateObservation observation,
        RetainedStateTransactionAuthority authority,
        RetainedStateTransactionBinding binding,
        RetainedStatePersistedCandidate candidate,
        RetainedStateAcceptanceAttempt attempt)
    {
        if (observation.Snapshot is not { } snapshot ||
            observation.SelectedHead is not { } selectedHead ||
            !MatchesSelected(binding.SelectedLineage, selectedHead) ||
            !snapshot.UnderRetained.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            observation.AcceptedState.Selection?.Current is not { } current ||
            current.Physical.Metadata != candidate.Metadata ||
            !StringComparer.Ordinal.Equals(
                current.LogicalGenerationIdentity,
                candidate.Prepared.LogicalGenerationIdentity) ||
            !attempt.TryGetBytes(out var receipt, out var envelope))
        {
            return false;
        }

        var physical = current.ReceiptPhysical;
        return physical.Header == attempt.Header &&
            physical.Metadata.Reference.Name == attempt.Name &&
            physical.Metadata.EncryptedObjectDigest.Sha256 ==
                OpaqueStoreHash.Sha256(envelope.Span) &&
            physical.Metadata.Size == envelope.Length &&
            physical.Metadata.ExpiresAtUnixSeconds >=
                attempt.RequiredPlatformExpiresAtUnixSeconds &&
            physical.Payload.AsSpan().SequenceEqual(receipt.Span) &&
            StringComparer.Ordinal.Equals(
                physical.Metadata.ProducingRun.Identity,
                attempt.Header.ProducingRunIdentity) &&
            physical.Metadata.ProducingRun.Attempt ==
                attempt.Header.ProducingRunAttempt;
    }

    private static bool MatchesPredecessorCopyAttempt(
        RetainedStatePredecessorCopyAttempt attempt,
        SelectedAcceptedGeneration current,
        RetainedStateTransactionBinding binding)
    {
        if (!attempt.TryGetBytes(out var payload, out _) ||
            !StringComparer.Ordinal.Equals(
                attempt.LogicalGenerationIdentity,
                current.LogicalGenerationIdentity) ||
            attempt.Header.ObjectClass != StateObjectClass.Candidate ||
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
                current.Physical.Header.PredecessorIdentity) ||
            attempt.Header.SuccessorIdentity is not null ||
            attempt.Header.LogicalExpiresAtUnixSeconds !=
                attempt.RequiredLogicalExpiresAtUnixSeconds ||
            attempt.Header.RequiredPlatformExpiresAtUnixSeconds !=
                attempt.RequiredPlatformExpiresAtUnixSeconds ||
            !RetainedStateRetention.TryOpaque(
                attempt.Header.CreatedAtUnixSeconds,
                attempt.RequiredLogicalExpiresAtUnixSeconds,
                out var platformExpiry) ||
            platformExpiry != attempt.RequiredPlatformExpiresAtUnixSeconds ||
            !AcceptedStatePhysicalCopyCodec.TryDecode(
                payload.Span,
                out var copy) ||
            copy is null ||
            !AcceptedStateGenerationRecordCodec.TryEncode(
                current.Generation,
                out var canonicalGeneration) ||
            !AcceptedStatePhysicalCopyCodec.TryEncode(
                copy,
                out var canonicalCopy))
        {
            return false;
        }

        try
        {
            var source = Source(current);
            return payload.Span.SequenceEqual(canonicalCopy) &&
                copy.CanonicalGenerationBytes.AsSpan().SequenceEqual(
                    canonicalGeneration) &&
                StringComparer.Ordinal.Equals(
                    copy.LogicalGenerationIdentity,
                    current.LogicalGenerationIdentity) &&
                StringComparer.Ordinal.Equals(
                    copy.OriginalCandidateObjectIdentity,
                    current.OriginalCandidateObjectIdentity) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceArtifactId,
                    source.Metadata.Reference.ObjectId.Value) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceArchiveSha256,
                    source.Metadata.ArchiveDigest.Sha256) &&
                StringComparer.Ordinal.Equals(
                    copy.SourceEncryptedEnvelopeSha256,
                    source.Metadata.EncryptedObjectDigest.Sha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalGeneration);
            CryptographicOperations.ZeroMemory(canonicalCopy);
        }
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

    private static bool MatchesOpaqueWriteAttempt(
        RetainedStateOpaqueWriteAttempt attempt,
        RetainedStateTransactionBinding binding) =>
        attempt.ObjectClass is StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment &&
        attempt.Header.ObjectClass == attempt.ObjectClass &&
        StringComparer.Ordinal.Equals(
            attempt.Header.BaseScopeDigest,
            binding.SelectedLineage.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(
            attempt.Header.Epoch,
            binding.SelectedLineage.Epoch) &&
        StringComparer.Ordinal.Equals(
            attempt.Header.SessionId,
            binding.SelectedLineage.SessionId) &&
        StringComparer.Ordinal.Equals(
            attempt.Header.PredecessorIdentity,
            attempt.Candidate.Prepared.Header.ObjectIdentity) &&
        attempt.Header.LogicalExpiresAtUnixSeconds ==
            attempt.SemanticRequiredExpiresAtUnixSeconds &&
        RetainedStateRetention.TryOpaque(
            attempt.Header.CreatedAtUnixSeconds,
            attempt.SemanticRequiredExpiresAtUnixSeconds,
            out var platformExpiry) &&
        platformExpiry ==
            attempt.Header.RequiredPlatformExpiresAtUnixSeconds &&
        OpaqueStoreValidation.IsValid(attempt.Name) &&
        LineageValidation.IsSha256(attempt.OperationIdentity) &&
        OpaqueStoreValidation.IsValid(attempt.AnchorMetadata);

    private static string ComputeOpaqueOperationIdentity(
        RetainedStateOpaqueWriteRequest request)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString("apr.retained-opaque-operation.s6");
        writer.WriteString(StateObjectClasses.ToWireName(
            request.ObjectClass));
        writer.WriteBytes(request.Payload.AsSpan());
        writer.WriteString(request.PredecessorIdentity ?? string.Empty);
        writer.WriteString(request.SuccessorIdentity ?? string.Empty);
        writer.WriteInt64(request.SemanticRequiredExpiresAtUnixSeconds);
        var canonical = writer.ToArray();
        try
        {
            return OpaqueStoreHash.Sha256(canonical);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool HasExactOpaqueWriteAnchor(
        ScopedStateInventorySnapshot snapshot,
        RetainedStateTransactionAuthority authority,
        RetainedStateOpaqueWriteAttempt attempt)
    {
        if (!attempt.TryGetBytes(
                authority,
                out var payload,
                out var envelope))
        {
            return false;
        }

        var matches = snapshot.Authenticated.Where(item =>
                item.Metadata == attempt.AnchorMetadata &&
                item.Header.ObjectClass == StateObjectClass.Cleanup &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    attempt.Candidate.Prepared.Header.ObjectIdentity) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SuccessorIdentity,
                    attempt.Header.ObjectIdentity) &&
                RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                    item.Payload,
                    out var anchor) &&
                anchor is not null &&
                StringComparer.Ordinal.Equals(
                    anchor.CandidateObjectIdentity,
                    attempt.Candidate.Prepared.Header.ObjectIdentity) &&
                StringComparer.Ordinal.Equals(
                    anchor.OperationIdentity,
                    attempt.OperationIdentity) &&
                anchor.ObjectClass == attempt.ObjectClass &&
                StringComparer.Ordinal.Equals(
                    anchor.PredecessorIdentity,
                    attempt.Header.PredecessorIdentity) &&
                StringComparer.Ordinal.Equals(
                    anchor.SuccessorIdentity,
                    attempt.Header.SuccessorIdentity) &&
                anchor.SemanticRequiredExpiresAtUnixSeconds ==
                    attempt.SemanticRequiredExpiresAtUnixSeconds &&
                anchor.RequiredPlatformExpiresAtUnixSeconds ==
                    attempt.Header.RequiredPlatformExpiresAtUnixSeconds &&
                StringComparer.Ordinal.Equals(
                    anchor.ProducingRunIdentity,
                    attempt.Header.ProducingRunIdentity) &&
                anchor.ProducingRunAttempt ==
                    attempt.Header.ProducingRunAttempt &&
                anchor.TargetName == attempt.Name &&
                StringComparer.Ordinal.Equals(
                    anchor.TargetObjectIdentity,
                    attempt.Header.ObjectIdentity) &&
                anchor.TargetEnvelope.AsSpan().SequenceEqual(
                    envelope.Span) &&
                StringComparer.Ordinal.Equals(
                    anchor.TargetEnvelopeSha256,
                    OpaqueStoreHash.Sha256(envelope.Span)) &&
                anchor.DispatchPhase ==
                    RetainedStateOpaqueWriteAnchorPhase
                        .PreparedBeforeTargetDispatch &&
                StringComparer.Ordinal.Equals(
                    anchor.TargetPayloadSha256,
                    OpaqueStoreHash.Sha256(payload.Span)))
            .ToArray();
        return matches.Length == 1;
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
        item.Header.ObjectClass is (
                StateObjectClass.Candidate or
                StateObjectClass.Acceptance) &&
            !IsProtected(item, snapshot, selected);

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
                IsAnchor = RetainedStateOpaqueWriteAnchorCodec.IsAnchor(
                    item.Payload),
            })
            .ToArray();
        if (cleanupPhysical.Any(item =>
                item.Parsed is null && !item.IsAnchor ||
                item.Parsed is not null &&
                    !CleanupRecordMatchesPhysical(
                        item.Parsed,
                        item.Physical)))
        {
            return (RetainedStateTransactionCodes.Conflict, false);
        }

        var completed = cleanupPhysical.Where(item =>
                item.Parsed is not null &&
                (!Active(item.Physical, binding) ||
                    !StringComparer.Ordinal.Equals(
                        item.Parsed.TerminalAcceptanceIdentity,
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
