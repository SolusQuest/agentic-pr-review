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

        if (!RestrictedStateValidation.IsValidSnapshot(read.Snapshot!))
        {
            return EnumerationFailure(
                RestrictedStateCodes.EnumerationInvalid);
        }

        return new RestrictedStateEnumerationResult(
            StateResult.Create(
                StateAction.Enumerated,
                RestrictedStateCodes.Enumerated),
            read.Snapshot!.Accepted);
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
            request.SessionContext is null ||
            !RestrictedStateValidation.IsValidLifetime(
                request.AcceptedAtUnixSeconds,
                request.ExpiresAtUnixSeconds) ||
            request.ExpiresAtUnixSeconds <= unixTimeSeconds())
        {
            return PrepareFailure(
                RestrictedStateCodes.LineageMismatch);
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

        var admitted = sessionAdmission.Admit(
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
            request.AcceptedAtUnixSeconds,
            request.ExpiresAtUnixSeconds);
        if (!RestrictedStateEnvelope.TryEncrypt(
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
                    access.Scope,
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

            var write = Write(
                access,
                read.Version!,
                replacement,
                cancellationToken);
            if (!write.Succeeded)
            {
                return PrepareFailure(MapWriteFailure(write.Failure));
            }

            var receipt = new PreparedStateReceipt(
                binding.Generation,
                session.SessionSha256,
                envelopeSha,
                objectIdentity);
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
            request.SessionContext is null)
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

        var current = read.Snapshot!.Accepted.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(
                candidate.EnvelopeSha256,
                lineage.EnvelopeSha256));
        if (current is null)
        {
            return MissingRestore(
                request.Intent,
                RestrictedStateCodes.CurrentMissing);
        }

        if (lineage.ExpiresAtUnixSeconds <= unixTimeSeconds())
        {
            var cleanup = Write(
                access,
                read.Version!,
                RestrictedStateSnapshot.Empty,
                cancellationToken);
            if (!cleanup.Succeeded)
            {
                return RestoreFailure(
                    RestrictedStateCodes.CleanupFailed);
            }

            return MissingRestore(
                request.Intent,
                RestrictedStateCodes.Expired);
        }

        if (!MatchesLineage(current, lineage))
        {
            return RestoreFailure(
                current.Binding.Generation < lineage.Generation
                    ? RestrictedStateCodes.ReplayRejected
                    : RestrictedStateCodes.LineageMismatch);
        }

        if (!RestrictedStateEnvelope.TryDecrypt(
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
            var admitted = sessionAdmission.Admit(
                access,
                plaintext!,
                request.SessionContext);
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
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        if (!RestrictedStateValidation.IsValidReceipt(receipt) ||
            (lineage is not null &&
                (!RestrictedStateValidation.IsValidLineage(lineage) ||
                    lineage.Scope != access.Scope ||
                    !lineage.TransitionAuthorized)))
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
        var alreadyAccepted = snapshot.Accepted.FirstOrDefault(
            candidate => ReceiptMatches(receipt, candidate));
        if (alreadyAccepted is not null)
        {
            return Success(
                StateAction.Idempotent,
                RestrictedStateCodes.Idempotent,
                alreadyAccepted);
        }

        var staging = snapshot.Staging;
        if (staging is null)
        {
            return Failure(RestrictedStateCodes.CurrentMissing);
        }

        if (!ReceiptMatches(receipt, staging))
        {
            if (receipt.Generation < staging.Binding.Generation)
            {
                return Failure(RestrictedStateCodes.ReplayRejected);
            }

            return Failure(RestrictedStateCodes.Conflict);
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
        if (!write.Succeeded)
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
        PreparedStateReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(RestrictedStateCodes.Cancelled);
        }

        if (!RestrictedStateValidation.IsValidReceipt(receipt))
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

        var candidate = read.Snapshot!.Accepted
            .Append(read.Snapshot.Staging)
            .FirstOrDefault(current =>
                current is not null &&
                ReceiptMatches(receipt, current));
        return candidate is null
            ? Failure(RestrictedStateCodes.Conflict)
            : Success(
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

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return Failure(MapReadFailure(read.Failure));
        }

        var write = Write(
            access,
            read.Version!,
            RestrictedStateSnapshot.Empty,
            cancellationToken);
        if (!write.Succeeded)
        {
            return Failure(RestrictedStateCodes.CleanupFailed);
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

        var current = read.Snapshot!.Accepted.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(
                candidate.EnvelopeSha256,
                lineage.EnvelopeSha256));
        if (current is null)
        {
            return Failure(RestrictedStateCodes.CurrentMissing);
        }

        var write = Write(
            access,
            read.Version!,
            RestrictedStateSnapshot.Empty,
            cancellationToken);
        if (!write.Succeeded)
        {
            return Failure(RestrictedStateCodes.CleanupFailed);
        }

        return StateResult.Create(
            StateAction.Reset,
            RestrictedStateCodes.Expired);
    }

    internal RestrictedStateHandoffResult PrepareHandoff(
        AuthorizedStateAccess access,
        AcceptedLineage lineage,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return HandoffFailure(RestrictedStateCodes.Cancelled);
        }

        if (!RestrictedStateValidation.IsValidLineage(lineage) ||
            lineage.Scope != access.Scope ||
            !lineage.TransitionAuthorized)
        {
            return HandoffFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var read = Read(access, cancellationToken);
        if (!read.Succeeded)
        {
            return HandoffFailure(MapReadFailure(read.Failure));
        }

        var current = read.Snapshot!.Accepted.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(
                candidate.EnvelopeSha256,
                lineage.EnvelopeSha256));
        if (current is null)
        {
            return HandoffFailure(
                RestrictedStateCodes.CurrentMissing);
        }

        if (!MatchesLineage(current, lineage))
        {
            return HandoffFailure(
                RestrictedStateCodes.LineageMismatch);
        }

        var receipt = new RestrictedStateHandoffReceipt(
            RestrictedStateEnvelope.ObjectIdentity(
                access.Scope,
                lineage.EnvelopeSha256),
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
        catch (Exception exception) when (IsIoDomainException(exception))
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
        catch (Exception exception) when (IsIoDomainException(exception))
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
        string code) =>
        new(Failure(code), null);

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

    private static bool IsIoDomainException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ObjectDisposedException;
}
