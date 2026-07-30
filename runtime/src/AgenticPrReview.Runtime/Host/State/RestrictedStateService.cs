using System.Collections.Immutable;
using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Host.State;

internal sealed class RestrictedStateService
{
    private readonly IRestrictedStateStore store;
    private readonly IRestrictedStateKeyResolver keyResolver;
    private readonly IRestrictedStateSessionAdmission sessionAdmission;
    private readonly Func<long> unixTimeSeconds;

    internal RestrictedStateService(
        IRestrictedStateStore store,
        IRestrictedStateKeyResolver keyResolver,
        IRestrictedStateSessionAdmission sessionAdmission,
        Func<long> unixTimeSeconds)
    {
        this.store = store;
        this.keyResolver = keyResolver;
        this.sessionAdmission = sessionAdmission;
        this.unixTimeSeconds = unixTimeSeconds;
    }

    internal RestrictedStateEnumerationResult Enumerate(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return EnumerationFailure(RestrictedStateCodes.Cancelled);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return EnumerationFailure(MapReadFailure(read.Failure));
        }

        var snapshot = read.Snapshot!;
        if (!RestrictedStateValidation.IsValidSnapshot(snapshot) ||
            snapshot.Accepted.Any(candidate =>
                !RestrictedStateEnvelope.TryParse(
                    candidate.Envelope,
                    out _)))
        {
            return EnumerationFailure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        return new RestrictedStateEnumerationResult(
            StateResult.Create(
                StateAction.Enumerated,
                RestrictedStateCodes.Enumerated),
            snapshot.Accepted);
    }

    internal RestrictedStatePrepareResult Prepare(
        AuthorizedStateAccess access,
        RestrictedStatePrepareRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PrepareFailure(RestrictedStateCodes.Cancelled);
        }

        if (request is null ||
            request.SessionContext is null)
        {
            return PrepareFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var now = unixTimeSeconds();
        if (now is < 0 or >
            RestrictedStateFormat.MaximumUnixSeconds -
                RestrictedStateFormat.MaximumRetentionSeconds)
        {
            return PrepareFailure(RestrictedStateCodes.LineageMismatch);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return PrepareFailure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return PrepareFailure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        var acceptedCurrent = read.Snapshot!.Accepted.FirstOrDefault();
        if (request.Lineage is not null &&
            acceptedCurrent is null)
        {
            return PrepareFailure(
                RestrictedStateCodes.CurrentMissing);
        }

        if (request.Lineage is not null &&
            request.Lineage.ExpiresAtUnixSeconds <= now)
        {
            return PrepareFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var admitted = Admit(
            access,
            request.Plaintext,
            request.SessionContext);
        if (!admitted.Succeeded)
        {
            return PrepareFailure(
                RestrictedStateCodes.EnvelopeInvalid);
        }

        var session = admitted.Session!;
        if (request.Lineage is not null &&
            (!RestrictedStateValidation.IsValidLineage(
                request.Lineage) ||
                request.Lineage.Scope != access.Scope))
        {
            ZeroSession(session);
            return PrepareFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        if (request.Lineage is not null)
        {
            var currentAuthenticationCode = AuthenticateCandidate(
                access,
                acceptedCurrent!,
                request.SessionContext);
            if (currentAuthenticationCode is not null)
            {
                ZeroSession(session);
                return PrepareFailure(currentAuthenticationCode);
            }
        }

        if (request.Lineage is null)
        {
            if (acceptedCurrent is not null)
            {
                ZeroSession(session);
                return PrepareFailure(RestrictedStateCodes.Conflict);
            }
        }
        else if (!MatchesLineage(acceptedCurrent!, request.Lineage))
        {
            ZeroSession(session);
            return PrepareFailure(
                acceptedCurrent!.Binding.Generation >
                    request.Lineage.Generation
                    ? RestrictedStateCodes.ReplayRejected
                    : RestrictedStateCodes.LineageMismatch);
        }

        if (!TryValidatePreparedTransition(
                access.Scope,
                request.Lineage,
                session,
                out var transitionCode))
        {
            ZeroSession(session);
            return PrepareFailure(transitionCode);
        }

        if (read.Snapshot!.Staging is not null)
        {
            ZeroSession(session);
            return PrepareFailure(RestrictedStateCodes.Conflict);
        }

        var binding = new RestrictedStateBinding(
            access.Scope,
            session.ProducerBaseSha,
            session.ProducerHeadSha,
            session.Generation,
            session.PredecessorEnvelopeSha256,
            now,
            checked(now + RestrictedStateFormat.MaximumRetentionSeconds));
        if (!TryEncrypt(
                access,
                binding,
                session.Plaintext,
                keyResolver,
                out var envelope,
                out var encryptionCode))
        {
            ZeroSession(session);
            return PrepareFailure(encryptionCode);
        }

        try
        {
            var envelopeSha =
                RestrictedStateEnvelope.EnvelopeSha256(envelope!);
            var objectIdentity =
                RestrictedStateEnvelope.ObjectIdentity(
                    binding,
                    session.SessionSha256,
                    envelopeSha);
            var candidate = new RestrictedStateCandidate(
                binding,
                session.SessionSha256,
                envelopeSha,
                objectIdentity,
                envelope!);
            var replacement = read.Snapshot with
            {
                Staging = candidate,
            };
            if (!RestrictedStateValidation.IsValidSnapshot(replacement))
            {
                return PrepareFailure(
                    RestrictedStateCodes.EnumerationInvalid);
            }

            var receipt = new PreparedStateReceipt(
                binding.Generation,
                session.SessionSha256,
                envelopeSha,
                objectIdentity);
            var write = Write(
                access,
                read.Version!,
                replacement,
                cancellationToken);
            if (!write.Committed)
            {
                return PrepareFailure(
                    MapWriteFailure(write.Failure),
                    receipt);
            }

            return new RestrictedStatePrepareResult(
                StateResult.Create(
                    StateAction.Prepared,
                    RestrictedStateCodes.Prepared,
                    binding.Generation,
                    session.SessionSha256,
                    envelopeSha),
                receipt);
        }
        finally
        {
            ZeroSession(session);
        }
    }

    internal RestrictedStateRestoreResult Restore(
        AuthorizedStateAccess access,
        RestrictedStateRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RestoreFailure(RestrictedStateCodes.Cancelled);
        }

        if (request is null ||
            request.SessionContext is null ||
            !Enum.IsDefined(request.LocatorFamily) ||
            !Enum.IsDefined(request.Intent))
        {
            return RestoreFailure(
                RestrictedStateCodes.EnvelopeInvalid);
        }

        if (request.LocatorFamily !=
            RestrictedStateLocatorFamily.Current)
        {
            return MissingRestore(request.Intent, RestrictedStateCodes.Absent);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return RestoreFailure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return RestoreFailure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        if (request.Lineage is null)
        {
            return MissingRestore(request.Intent, RestrictedStateCodes.Absent);
        }

        var lineage = request.Lineage;
        if (!RestrictedStateValidation.IsValidLineage(lineage) ||
            lineage.Scope != access.Scope)
        {
            return RestoreFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var current = read.Snapshot!.Accepted.FirstOrDefault();
        if (current is null)
        {
            return MissingRestore(
                request.Intent,
                RestrictedStateCodes.CurrentMissing);
        }

        if (!StringComparer.Ordinal.Equals(
                current.EnvelopeSha256,
                lineage.EnvelopeSha256) &&
            current.Binding.Generation < lineage.Generation)
        {
            return MissingRestore(
                request.Intent,
                RestrictedStateCodes.CurrentMissing);
        }

        if (current.Binding.ExpiresAtUnixSeconds <= unixTimeSeconds())
        {
            if (!StringComparer.Ordinal.Equals(
                    current.EnvelopeSha256,
                    lineage.EnvelopeSha256) ||
                !MatchesLineage(current, lineage))
            {
                return RestoreFailure(
                    current.Binding.Generation > lineage.Generation
                        ? RestrictedStateCodes.ReplayRejected
                        : RestrictedStateCodes.LineageMismatch);
            }

            var cleanup = Delete(
                access,
                read.Version!,
                cancellationToken);
            if (!cleanup.Committed)
            {
                return RestoreFailure(
                    MapWriteFailure(cleanup.Failure));
            }

            return MissingRestore(
                request.Intent,
                RestrictedStateCodes.Expired);
        }

        if (!TryDecrypt(
                access,
                current.Binding,
                current.Envelope,
                keyResolver,
                out var plaintext,
                out var decryptionCode))
        {
            return RestoreFailure(decryptionCode);
        }

        try
        {
            var agentSessionContext =
                request.SessionContext.SessionContext is null
                    ? null!
                    : request.SessionContext.SessionContext with
                    {
                        EnvelopeSha256 =
                            current.EnvelopeSha256,
                    };
            var admitted = Admit(
                access,
                plaintext!,
                new RestrictedStateSessionAdmissionContext(
                    current.Binding.ProducerBaseSha,
                    current.Binding.ProducerHeadSha,
                    current.Binding.Generation,
                    current.Binding.PredecessorEnvelopeSha256,
                    agentSessionContext));
            if (!admitted.Succeeded ||
                !MatchesCandidate(admitted.Session!, current))
            {
                if (admitted.Session is not null)
                {
                    ZeroSession(admitted.Session);
                }

                return RestoreFailure(
                    RestrictedStateCodes.EnvelopeInvalid);
            }

            if (!StringComparer.Ordinal.Equals(
                    current.EnvelopeSha256,
                    lineage.EnvelopeSha256))
            {
                ZeroSession(admitted.Session!);
                return RestoreFailure(
                    current.Binding.Generation > lineage.Generation
                        ? RestrictedStateCodes.ReplayRejected
                        : RestrictedStateCodes.CurrentMissing);
            }

            if (!MatchesLineage(current, lineage))
            {
                ZeroSession(admitted.Session!);
                return RestoreFailure(
                    current.Binding.Generation < lineage.Generation
                        ? RestrictedStateCodes.ReplayRejected
                        : RestrictedStateCodes.LineageMismatch);
            }

            if (!lineage.TransitionAuthorized)
            {
                ZeroSession(admitted.Session!);
                return RestoreFailure(
                    RestrictedStateCodes.LineageMismatch);
            }

            return new RestrictedStateRestoreResult(
                StateResult.Create(
                    StateAction.Restored,
                    RestrictedStateCodes.Restored,
                    current.Binding.Generation,
                    current.SessionSha256,
                    current.EnvelopeSha256),
                admitted.Session);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext!);
        }
    }

    internal StateResult Accept(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        var now = unixTimeSeconds();
        if (sessionContext is null ||
            !RestrictedStateValidation.IsValidReceipt(receipt))
        {
            return Failure(RestrictedStateCodes.LineageMismatch);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return Failure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return Failure(RestrictedStateCodes.EnumerationInvalid);
        }

        var snapshot = read.Snapshot!;
        var acceptedCurrent = snapshot.Accepted.FirstOrDefault();
        var alreadyAccepted = acceptedCurrent is not null &&
            ReceiptMatches(receipt, acceptedCurrent)
                ? acceptedCurrent
                : null;
        if (alreadyAccepted is not null)
        {
            if (alreadyAccepted.Binding.ExpiresAtUnixSeconds <= now)
            {
                return Failure(RestrictedStateCodes.Expired);
            }

            var authenticationCode = AuthenticateCandidate(
                access,
                alreadyAccepted,
                sessionContext);
            if (authenticationCode is not null)
            {
                return Failure(authenticationCode);
            }

            if (!IsValidAcceptanceLineage(access, lineage, now))
            {
                return Failure(RestrictedStateCodes.LineageMismatch);
            }

            var retryTransitionCode = ValidateAcceptanceTransition(
                lineage,
                alreadyAccepted);
            if (retryTransitionCode is not null)
            {
                return Failure(retryTransitionCode);
            }

            if (lineage is not null)
            {
                var predecessor = snapshot.Accepted
                    .Skip(1)
                    .FirstOrDefault(candidate =>
                        StringComparer.Ordinal.Equals(
                            candidate.EnvelopeSha256,
                            lineage.EnvelopeSha256));
                if (predecessor is null)
                {
                    return Failure(
                        RestrictedStateCodes.CurrentMissing);
                }

                var predecessorAuthenticationCode =
                    AuthenticateCandidate(
                        access,
                        predecessor,
                        sessionContext);
                if (predecessorAuthenticationCode is not null)
                {
                    return Failure(predecessorAuthenticationCode);
                }

                if (!MatchesLineage(predecessor, lineage))
                {
                    return Failure(
                        RestrictedStateCodes.LineageMismatch);
                }
            }

            return Success(
                StateAction.Idempotent,
                RestrictedStateCodes.Idempotent,
                alreadyAccepted);
        }

        var replayedAccepted = snapshot.Accepted.Skip(1).FirstOrDefault(
            candidate => ReceiptMatches(receipt, candidate));
        if (replayedAccepted is not null)
        {
            if (replayedAccepted.Binding.ExpiresAtUnixSeconds <= now)
            {
                return Failure(RestrictedStateCodes.Expired);
            }

            var authenticationCode = AuthenticateCandidate(
                access,
                replayedAccepted,
                sessionContext);
            if (authenticationCode is not null)
            {
                return Failure(authenticationCode);
            }

            return Failure(RestrictedStateCodes.ReplayRejected);
        }

        var staging = snapshot.Staging;
        if (staging is null)
        {
            return Failure(RestrictedStateCodes.CurrentMissing);
        }

        if (staging.Binding.ExpiresAtUnixSeconds <= now)
        {
            return Failure(RestrictedStateCodes.Expired);
        }

        var stagingAuthenticationCode = AuthenticateCandidate(
            access,
            staging,
            sessionContext);
        if (stagingAuthenticationCode is not null)
        {
            return Failure(stagingAuthenticationCode);
        }

        if (!ReceiptMatches(receipt, staging))
        {
            return Failure(
                receipt.Generation < staging.Binding.Generation
                    ? RestrictedStateCodes.ReplayRejected
                    : RestrictedStateCodes.Conflict);
        }

        if (!IsValidAcceptanceLineage(access, lineage, now))
        {
            return Failure(RestrictedStateCodes.LineageMismatch);
        }

        var transitionCode = ValidateAcceptanceTransition(
            lineage,
            staging);
        if (transitionCode is not null)
        {
            return Failure(transitionCode);
        }

        ImmutableArray<RestrictedStateCandidate> accepted;
        if (lineage is null)
        {
            accepted = [staging];
        }
        else
        {
            var previous = snapshot.Accepted.FirstOrDefault(
                candidate => StringComparer.Ordinal.Equals(
                    candidate.EnvelopeSha256,
                    lineage.EnvelopeSha256));
            if (previous is null)
            {
                return Failure(RestrictedStateCodes.CurrentMissing);
            }

            var previousAuthenticationCode = AuthenticateCandidate(
                access,
                previous,
                sessionContext);
            if (previousAuthenticationCode is not null)
            {
                return Failure(previousAuthenticationCode);
            }

            if (!MatchesLineage(previous, lineage))
            {
                return Failure(
                    previous.Binding.Generation < lineage.Generation
                        ? RestrictedStateCodes.ReplayRejected
                        : RestrictedStateCodes.LineageMismatch);
            }

            accepted = [staging, previous];
        }

        var replacement = new RestrictedStateSnapshot(accepted, null);
        if (!RestrictedStateValidation.IsValidSnapshot(replacement))
        {
            return Failure(RestrictedStateCodes.EnumerationInvalid);
        }

        var write = Write(
            access,
            read.Version!,
            replacement,
            cancellationToken);
        if (!write.Committed)
        {
            return Failure(MapWriteFailure(write.Failure));
        }

        return Success(
            StateAction.Accepted,
            RestrictedStateCodes.Accepted,
            staging);
    }

    internal StateResult Reconcile(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        var now = unixTimeSeconds();
        if (sessionContext is null ||
            !RestrictedStateValidation.IsValidReceipt(receipt))
        {
            return Failure(RestrictedStateCodes.Conflict);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return Failure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return Failure(RestrictedStateCodes.EnumerationInvalid);
        }

        var snapshot = read.Snapshot!;
        var acceptedCandidate = snapshot.Accepted.FirstOrDefault(
            candidate => ReceiptMatches(receipt, candidate));
        var candidate = acceptedCandidate is not null
            ? acceptedCandidate
            : snapshot.Staging is not null &&
                ReceiptMatches(receipt, snapshot.Staging)
                    ? snapshot.Staging
                    : null;
        if (candidate is null)
        {
            return Failure(RestrictedStateCodes.Conflict);
        }

        if (candidate.Binding.ExpiresAtUnixSeconds <= now)
        {
            return Failure(RestrictedStateCodes.Expired);
        }

        var authenticationCode = AuthenticateCandidate(
            access,
            candidate,
            sessionContext);
        if (authenticationCode is not null)
        {
            return Failure(authenticationCode);
        }

        if (acceptedCandidate is not null &&
            !ReferenceEquals(
                acceptedCandidate,
                snapshot.Accepted[0]))
        {
            return Failure(RestrictedStateCodes.ReplayRejected);
        }

        if (!IsValidAcceptanceLineage(access, lineage, now))
        {
            return Failure(RestrictedStateCodes.Conflict);
        }

        var transitionCode = ValidateAcceptanceTransition(
            lineage,
            candidate);
        if (transitionCode is not null)
        {
            return Failure(transitionCode);
        }

        if (lineage is not null)
        {
            var predecessor = snapshot.Accepted.FirstOrDefault(
                current => StringComparer.Ordinal.Equals(
                    current.EnvelopeSha256,
                    lineage.EnvelopeSha256));
            if (predecessor is null)
            {
                return Failure(RestrictedStateCodes.CurrentMissing);
            }

            var predecessorAuthenticationCode = AuthenticateCandidate(
                access,
                predecessor,
                sessionContext);
            if (predecessorAuthenticationCode is not null)
            {
                return Failure(predecessorAuthenticationCode);
            }

            if (!MatchesLineage(predecessor, lineage))
            {
                return Failure(RestrictedStateCodes.LineageMismatch);
            }
        }

        return Success(
            StateAction.Idempotent,
            RestrictedStateCodes.Idempotent,
            candidate);
    }

    internal StateResult Reset(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        var read = ReadRawVersion(access, cancellationToken);
        if (!read.Succeeded)
        {
            return Failure(MapReadFailure(read.Failure));
        }

        var write = DeleteRaw(
            access,
            read.Version!,
            cancellationToken);
        if (!write.Committed)
        {
            return Failure(MapWriteFailure(write.Failure));
        }

        return StateResult.Create(
            StateAction.Reset,
            RestrictedStateCodes.Reset);
    }

    internal StateResult CleanupExpired(
        AuthorizedStateAccess access,
        AcceptedLineage lineage,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        if (!RestrictedStateValidation.IsValidLineage(lineage) ||
            lineage.Scope != access.Scope ||
            lineage.ExpiresAtUnixSeconds > unixTimeSeconds())
        {
            return Failure(RestrictedStateCodes.LineageMismatch);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return Failure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return Failure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        var current = read.Snapshot!.Accepted.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(
                candidate.EnvelopeSha256,
                lineage.EnvelopeSha256));
        if (current is null)
        {
            return Failure(RestrictedStateCodes.CurrentMissing);
        }

        if (!MatchesLineage(current, lineage))
        {
            return Failure(RestrictedStateCodes.LineageMismatch);
        }

        RestrictedStateStoreWrite write;
        if (ReferenceEquals(current, read.Snapshot.Accepted[0]))
        {
            write = Delete(
                access,
                read.Version!,
                cancellationToken);
        }
        else
        {
            var replacement = new RestrictedStateSnapshot(
                [read.Snapshot.Accepted[0]],
                read.Snapshot.Staging);
            write = Write(
                access,
                read.Version!,
                replacement,
                cancellationToken);
        }

        if (!write.Committed)
        {
            return Failure(MapWriteFailure(write.Failure));
        }

        return StateResult.Create(
            StateAction.Reset,
            RestrictedStateCodes.Expired);
    }

    internal RestrictedStateHandoffResult PrepareHandoff(
        AuthorizedStateAccess access,
        AcceptedLineage lineage,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return HandoffFailure(RestrictedStateCodes.Cancelled);
        }

        if (sessionContext is null ||
            !RestrictedStateValidation.IsValidLineage(lineage) ||
            lineage.Scope != access.Scope ||
            !lineage.TransitionAuthorized ||
            lineage.ExpiresAtUnixSeconds <= unixTimeSeconds())
        {
            return HandoffFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return HandoffFailure(MapReadFailure(read.Failure));
        }

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return HandoffFailure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        var current = read.Snapshot!.Accepted.FirstOrDefault();
        if (current is null)
        {
            return HandoffFailure(
                RestrictedStateCodes.CurrentMissing);
        }

        var authenticationCode = AuthenticateCandidate(
            access,
            current,
            sessionContext);
        if (authenticationCode is not null)
        {
            return HandoffFailure(authenticationCode);
        }

        if (!StringComparer.Ordinal.Equals(
                current.EnvelopeSha256,
                lineage.EnvelopeSha256) ||
            !MatchesLineage(current, lineage))
        {
            return HandoffFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var receipt = new RestrictedStateHandoffReceipt(
            current.ObjectIdentity,
            read.Version!.Sha256,
            lineage.Generation,
            lineage.EnvelopeSha256);
        return new RestrictedStateHandoffResult(
            StateResult.Create(
                StateAction.HandoffReady,
                RestrictedStateCodes.HandoffReady,
                lineage.Generation,
                lineage.SessionSha256,
                lineage.EnvelopeSha256),
            receipt);
    }

    private RestrictedStateStoreRead Read(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        try
        {
            return store.Read(access, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.Cancelled,
                null,
                null);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.Io,
                null,
                null);
        }
    }

    private RestrictedStateStoreWrite Write(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            return store.CompareExchange(
                access,
                expected,
                replacement,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Cancelled,
                null,
                false);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Io,
                null,
                false);
        }
    }

    private RestrictedStateStoreWrite Delete(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        CancellationToken cancellationToken)
    {
        try
        {
            return store.CompareDelete(
                access,
                expected,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Cancelled,
                null,
                false);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Io,
                null,
                false);
        }
    }

    private RestrictedStateStoreRawRead ReadRawVersion(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        try
        {
            return store.ReadRawVersion(access, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestrictedStateStoreRawRead(
                RestrictedStateStoreFailure.Cancelled,
                null);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return new RestrictedStateStoreRawRead(
                RestrictedStateStoreFailure.Io,
                null);
        }
    }

    private RestrictedStateStoreWrite DeleteRaw(
        AuthorizedStateAccess access,
        RestrictedStateRawVersion expected,
        CancellationToken cancellationToken)
    {
        try
        {
            return store.CompareDeleteRaw(
                access,
                expected,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Cancelled,
                null,
                false);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Io,
                null,
                false);
        }
    }

    private static bool TryValidatePreparedTransition(
        RestrictedStateScope scope,
        AcceptedLineage? lineage,
        RestrictedStateAdmittedSession session,
        out string failureCode)
    {
        failureCode = RestrictedStateCodes.LineageMismatch;
        if (lineage is null)
        {
            if (session.Generation != 0 ||
                session.PredecessorEnvelopeSha256 is not null)
            {
                return false;
            }

            failureCode = string.Empty;
            return true;
        }

        if (lineage.Scope != scope ||
            !lineage.TransitionAuthorized)
        {
            return false;
        }

        if (lineage.Generation == long.MaxValue)
        {
            failureCode = RestrictedStateCodes.Conflict;
            return false;
        }

        if (session.Generation < lineage.Generation)
        {
            failureCode = RestrictedStateCodes.ReplayRejected;
            return false;
        }

        if (session.Generation == lineage.Generation)
        {
            failureCode = RestrictedStateCodes.Conflict;
            return false;
        }

        if (session.Generation != lineage.Generation + 1 ||
            !StringComparer.Ordinal.Equals(
                session.PredecessorEnvelopeSha256,
                lineage.EnvelopeSha256))
        {
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    private static string? ValidateAcceptanceTransition(
        AcceptedLineage? lineage,
        RestrictedStateCandidate staging)
    {
        if (lineage is null)
        {
            return staging.Binding.Generation == 0 &&
                staging.Binding.PredecessorEnvelopeSha256 is null
                    ? null
                    : RestrictedStateCodes.LineageMismatch;
        }

        if (staging.Binding.Generation < lineage.Generation)
        {
            return RestrictedStateCodes.ReplayRejected;
        }

        if (staging.Binding.Generation == lineage.Generation)
        {
            return RestrictedStateCodes.Conflict;
        }

        if (lineage.Generation == long.MaxValue ||
            staging.Binding.Generation != lineage.Generation + 1 ||
            !StringComparer.Ordinal.Equals(
                staging.Binding.PredecessorEnvelopeSha256,
                lineage.EnvelopeSha256))
        {
            return RestrictedStateCodes.LineageMismatch;
        }

        return null;
    }

    private static bool IsValidAcceptanceLineage(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        long now) =>
        lineage is null ||
        (RestrictedStateValidation.IsValidLineage(lineage) &&
            lineage.Scope == access.Scope &&
            lineage.TransitionAuthorized &&
            lineage.ExpiresAtUnixSeconds > now);

    private static bool MatchesLineage(
        RestrictedStateCandidate candidate,
        AcceptedLineage lineage) =>
        candidate.Binding.Scope == lineage.Scope &&
        candidate.Binding.Generation == lineage.Generation &&
        StringComparer.Ordinal.Equals(
            candidate.SessionSha256,
            lineage.SessionSha256) &&
        StringComparer.Ordinal.Equals(
            candidate.EnvelopeSha256,
            lineage.EnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            candidate.Binding.PredecessorEnvelopeSha256,
            lineage.ExpectedPredecessorEnvelopeSha256) &&
        candidate.Binding.AcceptedAtUnixSeconds ==
            lineage.AcceptedAtUnixSeconds &&
        candidate.Binding.ExpiresAtUnixSeconds ==
            lineage.ExpiresAtUnixSeconds;

    private static bool MatchesCandidate(
        RestrictedStateAdmittedSession session,
        RestrictedStateCandidate candidate) =>
        session.Generation == candidate.Binding.Generation &&
        StringComparer.Ordinal.Equals(
            session.SessionSha256,
            candidate.SessionSha256) &&
        StringComparer.Ordinal.Equals(
            session.ProducerBaseSha,
            candidate.Binding.ProducerBaseSha) &&
        StringComparer.Ordinal.Equals(
            session.ProducerHeadSha,
            candidate.Binding.ProducerHeadSha) &&
        StringComparer.Ordinal.Equals(
            session.PredecessorEnvelopeSha256,
            candidate.Binding.PredecessorEnvelopeSha256);

    private string? AuthenticateCandidate(
        AuthorizedStateAccess access,
        RestrictedStateCandidate candidate,
        RestrictedStateSessionAdmissionContext sessionContext)
    {
        if (!TryDecrypt(
                access,
                candidate.Binding,
                candidate.Envelope,
                keyResolver,
                out var plaintext,
                out var decryptionCode))
        {
            return decryptionCode;
        }

        try
        {
            var agentSessionContext =
                sessionContext.SessionContext is null
                    ? null!
                    : sessionContext.SessionContext with
                    {
                        EnvelopeSha256 = candidate.EnvelopeSha256,
                    };
            var candidateContext = new RestrictedStateSessionAdmissionContext(
                candidate.Binding.ProducerBaseSha,
                candidate.Binding.ProducerHeadSha,
                candidate.Binding.Generation,
                candidate.Binding.PredecessorEnvelopeSha256,
                agentSessionContext);
            var admitted = Admit(
                access,
                plaintext!,
                candidateContext);
            if (!admitted.Succeeded)
            {
                return RestrictedStateCodes.EnvelopeInvalid;
            }

            try
            {
                return MatchesCandidate(admitted.Session!, candidate)
                    ? null
                    : RestrictedStateCodes.EnvelopeInvalid;
            }
            finally
            {
                ZeroSession(admitted.Session!);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext!);
        }
    }

    private RestrictedStateSessionAdmissionResult Admit(
        AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext,
        RestrictedStateSessionAdmissionContext context)
    {
        try
        {
            return sessionAdmission.Admit(access, plaintext, context);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            return RestrictedStateSessionAdmissionResult.Failure();
        }
    }

    private static bool TryEncrypt(
        AuthorizedStateAccess access,
        RestrictedStateBinding binding,
        ReadOnlySpan<byte> plaintext,
        IRestrictedStateKeyResolver resolver,
        out byte[]? envelope,
        out string code)
    {
        try
        {
            return RestrictedStateEnvelope.TryEncrypt(
                access,
                binding,
                plaintext,
                resolver,
                out envelope,
                out code);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            envelope = null;
            code = RestrictedStateCodes.KeyUnavailable;
            return false;
        }
    }

    private static bool TryDecrypt(
        AuthorizedStateAccess access,
        RestrictedStateBinding binding,
        ReadOnlySpan<byte> envelope,
        IRestrictedStateKeyResolver resolver,
        out byte[]? plaintext,
        out string code)
    {
        try
        {
            return RestrictedStateEnvelope.TryDecrypt(
                access,
                binding,
                envelope,
                resolver,
                out plaintext,
                out code);
        }
        catch (Exception exception) when (
            IsBoundaryDomainException(exception))
        {
            plaintext = null;
            code = RestrictedStateCodes.KeyUnavailable;
            return false;
        }
    }

    private static bool ReceiptMatches(
        PreparedStateReceipt receipt,
        RestrictedStateCandidate candidate) =>
        receipt.Generation == candidate.Binding.Generation &&
        StringComparer.Ordinal.Equals(
            receipt.SessionSha256,
            candidate.SessionSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.EnvelopeSha256,
            candidate.EnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.ObjectIdentity,
            candidate.ObjectIdentity);

    private static void ZeroSession(
        RestrictedStateAdmittedSession session)
    {
        if (session.Plaintext is not null)
        {
            CryptographicOperations.ZeroMemory(session.Plaintext);
        }
    }

    private static StateResult Success(
        StateAction action,
        string code,
        RestrictedStateCandidate candidate) =>
        StateResult.Create(
            action,
            code,
            candidate.Binding.Generation,
            candidate.SessionSha256,
            candidate.EnvelopeSha256);

    private static StateResult Failure(string code) =>
        StateResult.Create(StateAction.Failed, code);

    private static RestrictedStatePrepareResult PrepareFailure(
        string code,
        PreparedStateReceipt? receipt = null) =>
        new(Failure(code), receipt);

    private static RestrictedStateRestoreResult RestoreFailure(
        string code) =>
        new(Failure(code), null);

    private static RestrictedStateRestoreResult MissingRestore(
        RestrictedStateRestoreIntent intent,
        string code) =>
        new(
            StateResult.Create(
                intent == RestrictedStateRestoreIntent.Automatic
                    ? StateAction.Bootstrap
                    : StateAction.Failed,
                intent == RestrictedStateRestoreIntent.Automatic
                    ? code
                    : code == RestrictedStateCodes.Absent
                        ? RestrictedStateCodes.ExplicitMissing
                        : code),
            null);

    private static RestrictedStateEnumerationResult EnumerationFailure(
        string code) =>
        new(Failure(code), []);

    private static RestrictedStateHandoffResult HandoffFailure(
        string code) =>
        new(Failure(code), null);

    private static string MapReadFailure(
        RestrictedStateStoreFailure failure) =>
        failure switch
        {
            RestrictedStateStoreFailure.Cancelled =>
                RestrictedStateCodes.Cancelled,
            RestrictedStateStoreFailure.Invalid =>
                RestrictedStateCodes.EnumerationInvalid,
            RestrictedStateStoreFailure.Cleanup =>
                RestrictedStateCodes.CleanupFailed,
            _ => RestrictedStateCodes.IoFailed,
        };

    private static string MapWriteFailure(
        RestrictedStateStoreFailure failure) =>
        failure switch
        {
            RestrictedStateStoreFailure.Cancelled =>
                RestrictedStateCodes.Cancelled,
            RestrictedStateStoreFailure.Conflict =>
                RestrictedStateCodes.Conflict,
            RestrictedStateStoreFailure.Cleanup =>
                RestrictedStateCodes.CleanupFailed,
            RestrictedStateStoreFailure.Invalid =>
                RestrictedStateCodes.EnumerationInvalid,
            _ => RestrictedStateCodes.IoFailed,
        };

    private static bool IsBoundaryDomainException(
        Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ObjectDisposedException or
            InvalidOperationException or
            ArgumentException or
            NotSupportedException or
            CryptographicException or
            TimeoutException or
            FormatException or
            OverflowException;
}
