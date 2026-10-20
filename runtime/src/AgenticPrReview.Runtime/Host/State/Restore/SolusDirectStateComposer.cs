using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record SolusDirectStateSelectionResult(string Code, SolusDirectSelectedState? Selection = null);

// Direct authorization remains local. The shared snapshot reader and existing
// prepare/reconcile/accept engine own persistence; Action publication authority
// and its locator/epoch transaction are never fabricated here.
internal sealed class SolusDirectStateComposer
{
    private static readonly object Issuer = new();
    private readonly LiveAgentFreshProcessFileSystem files;
    private readonly SolusDirectReviewAuthorization authorization;
    private readonly SolusFreshHostContext targets;
    private readonly SolusRestrictedStateCompatibility compatibility;
    private readonly IRestrictedStateKeyResolver keys;
    private readonly TimeProvider clock;
    private readonly LocalRestrictedStateStore store;
    private readonly SolusDirectStateAdmission admission;

    internal SolusDirectStateComposer(LiveAgentFreshProcessFileSystem files,
        SolusDirectReviewAuthorization authorization, SolusFreshHostContext targets,
        SolusRestrictedStateCompatibility compatibility, IRestrictedStateKeyResolver keys, TimeProvider clock)
    {
        if (!authorization.IsLive || !targets.MatchesDirect(authorization) ||
            !SolusRestrictedStateAdmission.IsSupported(compatibility))
            throw new ArgumentException("APR_SOLUS_DIRECT_AUTHORITY");
        this.files = files;
        this.authorization = authorization;
        this.targets = targets;
        this.compatibility = compatibility;
        this.keys = keys;
        this.clock = clock;
        store = files.CreateStateStore(authorization.Root, authorization.Root.StateRootPath);
        admission = new(authorization, targets, compatibility);
    }

    internal static bool IsIssuer(object candidate) => ReferenceEquals(candidate, Issuer);

    internal LiveAgentStateCommitCoordinator CreateCommitCoordinator(ILiveAgentAcceptedLineageSink sink) =>
        new(new LiveAgentStateTransactionFactory(clock,
                path => files.CreateStateStore(authorization.Root, path), admission),
            sink);

    internal async Task<SolusDirectStateSelectionResult> SelectAsync(
        LiveAgentFreshProcessAdmittedLineage? prior, RestrictedStateSessionAdmissionContext current,
        CancellationToken cancellationToken)
    {
        if (!authorization.IsLive || !targets.MatchesDirect(authorization))
            return new(RestrictedStateCodes.AccessDenied);
        var input = authorization.Input;
        var access = authorization.Root.Access;
        if (input.LocatorFamily != RestrictedStateLocatorFamily.Current)
        {
            if (prior is not null || current.Generation != 0 || current.PredecessorEnvelopeSha256 is not null ||
                input.RestoreIntent != RestrictedStateRestoreIntent.Automatic)
                return new(RestrictedStateCodes.LineageMismatch);
            // A locator hint alone is not proof of true absence. Reuse the
            // actual bounded store reader before any provider work.
            var observed = await new RestrictedStateOpaqueSnapshotStore(store, keys)
                .ReadAsync(access, cancellationToken).ConfigureAwait(false);
            if (!observed.Succeeded || observed.Snapshot is not { } snapshot ||
                !snapshot.Accepted.IsEmpty || snapshot.Staging is not null)
                return new(RestrictedStateCodes.Conflict);
            return new(RestrictedStateCodes.Absent, new(Issuer, this, null, null, current));
        }
        if (prior is null || !PriorIsCurrent(prior) || current.Generation != prior.Lineage.Generation + 1 ||
            current.PredecessorEnvelopeSha256 != prior.Lineage.EnvelopeSha256)
            return new(RestrictedStateCodes.LineageMismatch);
        var restored = await Service().RestoreAsync(access,
            new(input.LocatorFamily, input.RestoreIntent, prior.Lineage, current), cancellationToken)
            .ConfigureAwait(false);
        if (restored.Result.Action != StateAction.Restored || restored.Result.Code != RestrictedStateCodes.Restored ||
            restored.Session?.SolusRecord is null)
        {
            if (restored.Session is { } rejected) CryptographicOperations.ZeroMemory(rejected.Plaintext);
            return new(restored.Result.Action == StateAction.Restored && restored.Result.Code == RestrictedStateCodes.Restored
                ? RestrictedStateCodes.EnvelopeInvalid : restored.Result.Code);
        }
        return new(RestrictedStateCodes.Restored, new(Issuer, this, prior, restored.Session, current));
    }

    private RestrictedStateService Service() => new(store, keys, admission,
        () => clock.GetUtcNow().ToUnixTimeSeconds());

    private bool PriorIsCurrent(LiveAgentFreshProcessAdmittedLineage prior)
    {
        var read = files.ReadLineage(authorization.Root);
        return read is not null && read.Version == prior.FileVersion &&
            LiveAgentFreshProcessLineageAdmission.TryAdmit(read, prior.RawSha256,
                authorization.Root.Access.Scope, out var current) && current is not null &&
            current.Lineage == prior.Lineage && current.ProducerIdentity == prior.ProducerIdentity;
    }

    internal bool IsLive => authorization.IsLive && targets.MatchesDirect(authorization);
    internal SolusFreshHostContext Targets => targets;
    internal SolusRestrictedStateCompatibility Compatibility => compatibility;
    internal TimeProvider Clock => clock;

    internal async Task<bool> RevalidateAsync(SolusDirectSelectedState selected, CancellationToken token)
    {
        if (!IsLive || selected.Prior is not { } prior || selected.Session is not { } original ||
            !PriorIsCurrent(prior) || clock.GetUtcNow().ToUnixTimeSeconds() >= prior.Lineage.ExpiresAtUnixSeconds)
            return false;
        var restored = await Service().RestoreAsync(authorization.Root.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit,
                prior.Lineage, selected.Current), token).ConfigureAwait(false);
        try
        {
            return restored.Result.Action == StateAction.Restored &&
                restored.Result.Code == RestrictedStateCodes.Restored && restored.Session is { } current &&
                current.SolusRecord is { } record && original.SolusRecord is { } expected &&
                current.SessionSha256 == original.SessionSha256 &&
                record.Checkpoint == expected.Checkpoint && current.Generation == original.Generation &&
                current.ProducerBaseSha == original.ProducerBaseSha &&
                current.ProducerHeadSha == original.ProducerHeadSha;
        }
        finally
        {
            if (restored.Session is { } current) CryptographicOperations.ZeroMemory(current.Plaintext);
        }
    }

    internal async Task<SolusDirectPreparedContext?> PrepareAsync(SolusDirectSelectedState selected,
        Guid executionId, Guid roundId, CancellationToken token)
    {
        if (!selected.TryBeginPreparation() || !IsLive || selected.Session?.SolusRecord is not { } record ||
            selected.Prior is not { } prior || executionId == Guid.Empty || roundId == Guid.Empty ||
            executionId == record.Checkpoint.ExecutionId || roundId == record.Checkpoint.RoundId)
            return null;
        using var preparation = await targets.Exclusion.EnterPreparationAsync(token).ConfigureAwait(false);
        if (preparation is null || !await RevalidateAsync(selected, token).ConfigureAwait(false)) return null;
        var access = authorization.Root.Access;
        var scopeBytes = RestrictedStateSnapshotCodec.WriteScopeIdentity(access.Scope);
        var scope = AgentCanonical.HashDomain("apr.solus-direct-scope.v1", scopeBytes);
        var epoch = AgentCanonical.HashDomain("apr.solus-direct-epoch.v1", scopeBytes);
        var session = AgentCanonical.HashDomain("apr.solus-direct-session.v1", scopeBytes);
        var generation = Hash("apr.solus-direct-generation.v1", scope,
            prior.Lineage.Generation.ToString(CultureInfo.InvariantCulture),
            prior.Lineage.EnvelopeSha256, prior.Lineage.SessionSha256);
        var grant = SolusContextClaimRecordCodec.DeriveGrantId(scope, epoch, session,
            generation, record.Checkpoint.LogicalWorkId, record.Checkpoint.RoundId, executionId, roundId);
        var claim = new SolusContextClaimRecord(scope, epoch, session, prior.RawSha256,
            prior.Lineage.EnvelopeSha256, generation, prior.Lineage.EnvelopeSha256,
            prior.Lineage.SessionSha256, record.Checkpoint, grant, executionId, roundId,
            prior.Lineage.ExpiresAtUnixSeconds);
        var name = new OpaqueStoreName(Hash("apr.solus-direct-claim-name.v1", scope,
            record.Checkpoint.LogicalWorkId.ToString("N"), record.Checkpoint.RoundId.ToString("N")));
        var binding = new RestrictedStateBinding(access.Scope, selected.Session.ProducerBaseSha,
            selected.Session.ProducerHeadSha, prior.Lineage.Generation, prior.Lineage.ExpectedPredecessorEnvelopeSha256,
            prior.Lineage.AcceptedAtUnixSeconds, prior.Lineage.ExpiresAtUnixSeconds);
        var listed = await store.ListExactAsync(new(name, 2, 8192), token).ConfigureAwait(false);
        if (!listed.Succeeded) return null;
        if (!listed.Objects.IsEmpty)
        {
            // Whether spent or invalid, an occupied source name cannot issue a
            // second grant. Authenticate the bounded record; neither outcome is
            // treated as a capability or as permission to reset.
            foreach (var reference in listed.Objects)
            {
                var metadata = await store.ReadMetadataAsync(new(reference, 8192), token).ConfigureAwait(false);
                if (!metadata.Succeeded || metadata.Metadata is null) return null;
                var priorClaim = await store.DownloadAsync(new(metadata.Metadata, 8192), token).ConfigureAwait(false);
                if (!priorClaim.Succeeded || !RestrictedStateEnvelope.TryDecrypt(access, binding,
                        priorClaim.EncryptedBytes.Span, keys, out var plaintext, out _)) return null;
                try
                {
                    if (!SolusContextClaimRecordCodec.TryDecode(plaintext!, out var decoded) || decoded is null ||
                        decoded.BaseScopeDigest != scope || decoded.Source.LogicalWorkId != record.Checkpoint.LogicalWorkId ||
                        decoded.Source.RoundId != record.Checkpoint.RoundId) return null;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext!); }
            }
            return null;
        }
        if (!IsLive || token.IsCancellationRequested ||
            !SolusContextClaimRecordCodec.TryEncode(claim, out var encoded)) return null;
        byte[]? envelope = null;
        try
        {
            if (!RestrictedStateEnvelope.TryEncrypt(access, binding, encoded, keys, out envelope, out _) ||
                envelope is null) return null;
            var written = await store.UploadImmutableAsync(new(name, new(grant.ToString("N")), envelope,
                new(OpaqueStoreHash.Sha256(envelope)), prior.Lineage.ExpiresAtUnixSeconds), token).ConfigureAwait(false);
            // Unknown mutation is never retried, refunded or supplied to import.
            if (!written.Succeeded || written.Metadata is not { } metadata || metadata.Reference.Name != name ||
                metadata.ExpiresAtUnixSeconds < prior.Lineage.ExpiresAtUnixSeconds) return null;
            var readBack = await store.ReadBackExactAsync(new(metadata), token).ConfigureAwait(false);
            if (!readBack.Succeeded || readBack.Metadata != metadata || !IsLive) return null;
            var exact = await store.DownloadAsync(new(metadata, 8192), token).ConfigureAwait(false);
            if (!exact.Succeeded || !exact.EncryptedBytes.Span.SequenceEqual(envelope)) return null;
            return SolusDirectPreparedContext.Create(Issuer, selected, claim, record.CopyRestrictedPayload());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static string Hash(string domain, params string[] values)
    {
        var writer = new LineageBinaryWriter(4096);
        writer.WriteString(domain);
        foreach (var value in values) writer.WriteString(value);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }
}

internal sealed class SolusDirectSelectedState : IDisposable
{
    private int preparing;
    private int disposed;
    internal SolusDirectSelectedState(object issuer, SolusDirectStateComposer owner,
        LiveAgentFreshProcessAdmittedLineage? prior, RestrictedStateAdmittedSession? session,
        RestrictedStateSessionAdmissionContext current)
    {
        if (!SolusDirectStateComposer.IsIssuer(issuer)) throw new ArgumentException("APR_SOLUS_DIRECT_ISSUER");
        Owner = owner; Prior = prior; Session = session; Current = current;
    }
    internal SolusDirectStateComposer Owner { get; }
    internal LiveAgentFreshProcessAdmittedLineage? Prior { get; }
    internal RestrictedStateAdmittedSession? Session { get; }
    internal RestrictedStateSessionAdmissionContext Current { get; }
    internal bool IsFresh => Session is null && Prior is null;
    internal bool IsLive => Volatile.Read(ref disposed) == 0 && Owner.IsLive;
    internal bool TryBeginPreparation() => IsLive && Interlocked.CompareExchange(ref preparing, 1, 0) == 0;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && Session is not null)
            CryptographicOperations.ZeroMemory(Session.Plaintext);
    }
    public override string ToString() => nameof(SolusDirectSelectedState);
}

// Nonserializable receipt of actual authenticated immutable write/readback.
// Only the direct composer issuer can create it, and the shared state service
// consumes it once using its original retained-state issuer.
internal sealed class SolusDirectPreparedContext : IDisposable
{
    private readonly SolusDirectSelectedState source;
    private readonly SolusContextClaimRecord claim;
    private byte[]? payload;
    private int transferred;
    private SolusDirectPreparedContext(SolusDirectSelectedState source, SolusContextClaimRecord claim, byte[] payload)
    { this.source = source; this.claim = claim; this.payload = payload; }
    internal static SolusDirectPreparedContext Create(object issuer, SolusDirectSelectedState source,
        SolusContextClaimRecord claim, byte[] payload)
    {
        if (!SolusDirectStateComposer.IsIssuer(issuer) || !source.IsLive ||
            !SolusContextClaimRecordCodec.IsValid(claim)) throw new ArgumentException("APR_SOLUS_DIRECT_ISSUER");
        return new(source, claim, payload);
    }
    internal bool TryTransfer(out SolusFreshHostContext? targets, out SolusContextClaimRecord? result,
        out SolusRestrictedStateCompatibility? compatibility, out byte[]? bytes, out TimeProvider? clock)
    {
        targets = null; result = null; compatibility = null; bytes = null; clock = null;
        var current = Volatile.Read(ref payload);
        if (current is null || !source.IsLive || source.Owner.Clock.GetUtcNow().ToUnixTimeSeconds() >=
                claim.SourceExpiresAtUnixSeconds || Interlocked.CompareExchange(ref transferred, 1, 0) != 0)
            return false;
        targets = source.Owner.Targets; result = claim; compatibility = source.Owner.Compatibility;
        bytes = current.ToArray(); clock = source.Owner.Clock;
        return true;
    }
    public void Dispose()
    {
        var current = Interlocked.Exchange(ref payload, null);
        if (current is not null) CryptographicOperations.ZeroMemory(current);
    }
    public override string ToString() => nameof(SolusDirectPreparedContext);
}
