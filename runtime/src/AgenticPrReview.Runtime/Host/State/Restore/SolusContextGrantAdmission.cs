using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State.Restore;

// A compiled Host construction capability, never reconstructed from saved state.
internal sealed class SolusFreshHostContext : IDisposable
{
    private readonly ArtifactStateRestoreRequest request;
    private readonly BoundedReviewedSnapshotLease snapshot;
    private readonly RestrictedStateFileIdentity rootIdentity;
    private SafeFileHandle? root;
    private int constructed;

    private SolusFreshHostContext(ArtifactStateRestoreRequest request,
        BoundedReviewedSnapshotLease snapshot, SolusContextExclusion exclusion,
        SafeFileHandle root, RestrictedStateFileIdentity rootIdentity)
    {
        this.request = request;
        this.snapshot = snapshot;
        Exclusion = exclusion;
        this.root = root;
        this.rootIdentity = rootIdentity;
    }

    internal ActionHostTrustedPolicy Policy => request.TrustedPolicy;
    internal ReviewedSnapshot Snapshot => snapshot.Snapshot;
    internal SolusContextExclusion Exclusion { get; }
    internal bool TryBeginConstruction() => IsLive &&
        Interlocked.CompareExchange(ref constructed, 1, 0) == 0;
    internal bool IsLive
    {
        get
        {
            var held = Volatile.Read(ref root);
            if (held is null || held.IsClosed || !Exclusion.IsLive) return false;
            if (NativeRestrictedStateFiles.OpenRootGuardNoFollow(Snapshot.AbsoluteRoot,
                    out var observed) != RestrictedStateOpenResult.Success || observed is null) return false;
            using (observed)
                return NativeRestrictedStateFiles.TryGetIdentity(observed, true, out var identity) &&
                    identity == rootIdentity;
        }
    }

    internal bool Matches(ArtifactStateRestoreRequest candidate) => IsLive &&
        ReferenceEquals(candidate.Launch, request.Launch) &&
        ReferenceEquals(candidate.Invocation, request.Invocation) &&
        ReferenceEquals(candidate.TrustedPolicy, request.TrustedPolicy) &&
        Exclusion.MatchesInvocation(candidate);

    internal static SolusFreshHostContext? TryCreate(ArtifactStateRestoreRequest request,
        BoundedReviewedSnapshotLease snapshot, SolusContextExclusion exclusion)
    {
        if (!AcceptedStateProductionAuthorization.TryAuthorize(request, out _) ||
            !exclusion.MatchesInvocation(request) || snapshot is null ||
            snapshot.Identities.RepositoryId != request.Launch.RepositoryId ||
            snapshot.Identities.PullRequestNumber != request.Invocation.PullRequest.Number ||
            snapshot.Identities.BaseSha != request.Invocation.PullRequest.BaseSha ||
            snapshot.Identities.HeadSha != request.Invocation.PullRequest.HeadSha ||
            snapshot.Snapshot.Identity.RepositoryId != request.Launch.RepositoryId.ToString(CultureInfo.InvariantCulture) ||
            snapshot.Snapshot.Identity.BaseSha != snapshot.Identities.BaseSha ||
            snapshot.Snapshot.Identity.HeadSha != snapshot.Identities.HeadSha) return null;
        if (NativeRestrictedStateFiles.OpenRootGuardNoFollow(snapshot.Snapshot.AbsoluteRoot,
                out var root) != RestrictedStateOpenResult.Success || root is null) return null;
        if (!NativeRestrictedStateFiles.TryGetIdentity(root, true, out var identity))
        {
            root.Dispose();
            return null;
        }
        return new(request, snapshot, exclusion, root, identity);
    }

    public void Dispose() => Interlocked.Exchange(ref root, null)?.Dispose();
}

internal sealed record SolusSourceSelectionRequest(
    SolusFreshHostContext Targets, SolusRestrictedStateCompatibility Compatibility);

// Owns the original authorized locator lifetime. Only the genuine composer
// issuer can construct this value; S1 structural records cannot create it.
internal sealed class SolusContextSource : IDisposable
{
    private readonly object issuer;
    private readonly LocatorStateKeyRing keys;
    private readonly IRestrictedStateStore store;
    private readonly LineageResolveRequest lineageRequest;
    private readonly TimeProvider clock;
    private readonly IStateReconciliationDiagnosticSink? diagnostics;
    private readonly AuthorizedStateAccess stateAccess;
    private readonly RestrictedStateBinding binding;
    private readonly AcceptedLineage lineage;
    private readonly byte[] envelope;
    private int disposed;
    private int preparing;

    internal SolusContextSource(object issuer, LocatorStateKeyRing keys,
        LocatorContext locator, IRestrictedStateStore store, LineageResolveRequest lineageRequest,
        AcceptedStateSelection selection, SolusSourceSelectionRequest request,
        AuthorizedStateAccess stateAccess, TimeProvider clock,
        IStateReconciliationDiagnosticSink? diagnostics)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        this.issuer = issuer;
        this.keys = keys;
        Locator = locator;
        this.store = store;
        this.lineageRequest = lineageRequest;
        Selection = selection;
        Request = request;
        this.stateAccess = stateAccess;
        this.clock = clock;
        this.diagnostics = diagnostics;
        var generation = selection.Current.Generation;
        binding = new(stateAccess.Scope, generation.ProducerBaseSha, generation.ProducerHeadSha,
            generation.Generation, generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds);
        lineage = new(stateAccess.Scope, generation.Generation, generation.SessionSha256,
            generation.StateEnvelopeSha256, generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds, true);
        envelope = generation.EncryptedStateEnvelope.ToArray();
    }

    internal LocatorContext Locator { get; }
    internal AcceptedStateSelection Selection { get; }
    internal SolusSourceSelectionRequest Request { get; }
    internal bool IsLive => Volatile.Read(ref disposed) == 0 && Request.Targets.IsLive;

    internal async Task<SolusConsumedContext?> PrepareAsync(Guid executionId, Guid roundId,
        CancellationToken cancellationToken)
    {
        if (!IsLive || executionId == Guid.Empty || roundId == Guid.Empty ||
            Interlocked.CompareExchange(ref preparing, 1, 0) != 0) return null;
        // One preparation attempt per live source capability, including unknown outcomes.
        try
        {
            using var preparation = await Request.Targets.Exclusion.EnterPreparationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (preparation is null || !IsLive) return null;
            var observed = await new LineageService(store, clock, diagnostics)
                .ObserveReadOnlyAsync(Locator, lineageRequest, cancellationToken).ConfigureAwait(false);
            using var observation = observed.Context;
            if (!IsLive || !observed.Succeeded || observation?.Snapshot is not { } inventory)
                return null;
            var current = new AcceptedStateSelector(clock).Select(observation, lineageRequest);
            if (!current.Succeeded || current.Selection is not { } selected ||
                !LineageService.HasNoPendingTransition(observation, lineageRequest) ||
                !RetainedStateTransactionService.HasNoPendingPublicationWork(inventory, selected,
                    clock.GetUtcNow().ToUnixTimeSeconds()) ||
                selected.LineageHead.Header.ObjectIdentity != Selection.LineageHead.Header.ObjectIdentity ||
                selected.Current.LogicalGenerationIdentity != Selection.Current.LogicalGenerationIdentity ||
                selected.Current.ReceiptPhysical.Header.ObjectIdentity != Selection.Current.ReceiptPhysical.Header.ObjectIdentity ||
                !inventory.Unknown.IsEmpty || inventory.UnderRetained.Any(item =>
                    item.Header.ObjectClass == StateObjectClass.ContextClaim)) return null;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (!SolusRestrictedStateSnapshotCodec.TryRestore(stateAccess, binding, lineage,
                    envelope, Request.Compatibility, now,
                    new LocatorKeys(stateAccess, lineageRequest.Access, Locator),
                    out var record, out _) || record is null ||
                executionId == record.Checkpoint.ExecutionId || roundId == record.Checkpoint.RoundId)
                return null;
            var head = selected.LineageHead.Header;
            var grantId = SolusContextClaimRecordCodec.DeriveGrantId(head.BaseScopeDigest,
                head.Epoch, head.SessionId, selected.Current.LogicalGenerationIdentity,
                record.Checkpoint.LogicalWorkId, record.Checkpoint.RoundId, executionId, roundId);
            var claim = new SolusContextClaimRecord(head.BaseScopeDigest, head.Epoch, head.SessionId,
                head.ObjectIdentity, selected.Current.ReceiptPhysical.Header.ObjectIdentity,
                selected.Current.LogicalGenerationIdentity, lineage.EnvelopeSha256, lineage.SessionSha256,
                record.Checkpoint, grantId, executionId, roundId, binding.ExpiresAtUnixSeconds);
            var claims = inventory.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.ContextClaim).ToArray();
            if (claims.Length >= LineageFormat.MaximumPhysicalPerClass ||
                inventory.PhysicalCount >= LineageFormat.MaximumScopedObjects) return null;
            foreach (var physical in claims)
            {
                if (!SolusContextClaimRecordCodec.TryDecode(physical.Payload, out var prior) ||
                    prior is null || prior.ConflictsWith(claim)) return null;
            }
            if (!IsLive || cancellationToken.IsCancellationRequested ||
                !RetainedStateRetention.TryCandidate(now, out _, out var platformExpiry) ||
                !SolusContextClaimRecordCodec.TryEncode(claim, out var payload)) return null;
            byte[] encrypted = [];
            try
            {
                var snapshot = new SelectedLineageSnapshot(head.BaseScopeDigest, head.Epoch,
                    head.SessionId, head.ObjectIdentity, selected.LineageHead.Head.Transition);
                if (!RetainedStatePersistence.TryPrepareEnvelope(Locator, lineageRequest.Access,
                        lineageRequest.BaseScope, snapshot, StateObjectClass.ContextClaim,
                        claim.AcceptanceIdentity, claim.LogicalGenerationIdentity,
                        lineageRequest.ProducingRunIdentity, lineageRequest.ProducingRunAttempt,
                        now, binding.ExpiresAtUnixSeconds, platformExpiry, payload,
                        out var name, out encrypted, out var header, out _) || name is null || header is null)
                    return null;
                var persisted = await new RetainedStatePersistence(issuer, store, clock, diagnostics)
                    .UploadAndReconcileAsync(Locator, lineageRequest.Access, lineageRequest.BaseScope,
                        head.BaseScopeDigest, name, encrypted, header, payload,
                        lineageRequest.ProducingRunIdentity, lineageRequest.ProducingRunAttempt,
                        platformExpiry, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!persisted.Succeeded || !IsLive || cancellationToken.IsCancellationRequested ||
                        clock.GetUtcNow().ToUnixTimeSeconds() >= binding.ExpiresAtUnixSeconds) return null;
                    return new SolusConsumedContext(issuer, Request.Targets, claim,
                        record.Compatibility, record.RestrictedPayload, clock);
                }
                finally
                {
                    if (persisted.Payload is not null) CryptographicOperations.ZeroMemory(persisted.Payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            OperationCanceledException or ArgumentException or InvalidOperationException or OverflowException)
        { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(envelope);
        Locator.Dispose();
        keys.Dispose();
        lineageRequest.Access.Dispose();
    }

    private sealed class LocatorKeys(AuthorizedStateAccess expected,
        AuthorizedLocatorAccess authority, LocatorContext locator) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? key)
        { key = null; return false; }

        public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string keyId,
            long expiresAtUnixSeconds, out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, expected) || !LineageValidation.IsSha256(keyId) ||
                !LineageValidation.IsTime(expiresAtUnixSeconds)) return false;
            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!locator.TryCopyApprovedReadKey(authority, keyId, material)) return false;
                key = new(keyId, material);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
    }
}

// A receipt-backed one-shot capability. Disposing cannot refund its durable claim.
internal sealed class SolusConsumedContext : IDisposable
{
    private readonly byte[] payload;
    private int used;
    internal bool WasClaimed => Volatile.Read(ref used) == 1;
    private int disposed;
    private readonly TimeProvider clock;

    internal SolusConsumedContext(object issuer, SolusFreshHostContext targets,
        SolusContextClaimRecord claim, SolusRestrictedStateCompatibility compatibility,
        ReadOnlySpan<byte> payload, TimeProvider clock)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        Targets = targets;
        Claim = claim;
        Compatibility = compatibility;
        this.payload = payload.ToArray();
        this.clock = clock;
    }

    internal SolusFreshHostContext Targets { get; }
    internal SolusContextClaimRecord Claim { get; }
    internal SolusRestrictedStateCompatibility Compatibility { get; }
    internal byte[] CopyPayload() => payload.ToArray();
    internal bool TryClaim(SolusFreshHostContext targets, SolusRestrictedStateCompatibility compatibility,
        SolusRestrictedStateCheckpoint source, Guid grantId, Guid executionId, Guid roundId,
        ReadOnlySpan<byte> exactPayload) => Volatile.Read(ref disposed) == 0 &&
        ReferenceEquals(targets, Targets) && targets.IsLive && compatibility == Compatibility &&
        clock.GetUtcNow().ToUnixTimeSeconds() < Claim.SourceExpiresAtUnixSeconds &&
        source == Claim.Source && grantId == Claim.GrantId && executionId == Claim.ExecutionId &&
        roundId == Claim.RoundId && exactPayload.SequenceEqual(payload) &&
        Interlocked.CompareExchange(ref used, 1, 0) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) CryptographicOperations.ZeroMemory(payload);
    }
}
