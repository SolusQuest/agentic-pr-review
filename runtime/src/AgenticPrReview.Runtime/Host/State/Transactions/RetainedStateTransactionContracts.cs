using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal static class RetainedStateCapabilityIssuer
{
    internal static void Require(object issuer)
    {
        if (!RestrictedStateService.IsRetainedStateIssuer(issuer))
        {
            throw new ArgumentException(
                "The retained-state issuer is not authorized.",
                nameof(issuer));
        }
    }
}

internal static class RetainedStateTransactionCodes
{
    internal const string Ready = "retained_state_ready";
    internal const string Prepared = "retained_state_prepared";
    internal const string Persisted = "retained_state_persisted";
    internal const string Owned = "retained_state_owned";
    internal const string Accepted = "retained_state_accepted";
    internal const string AccessDenied = "retained_state_access_denied";
    internal const string Invalid = "retained_state_invalid";
    internal const string Conflict = "retained_state_conflict";
    internal const string Stale = "retained_state_stale";
    internal const string Cancelled = "retained_state_cancelled";
    internal const string OutcomeUnknown = "retained_state_outcome_unknown";
    internal const string RetentionFailed = "retained_state_retention_failed";
    internal const string KeyUnavailable = "retained_state_key_unavailable";
    internal const string CleanupDebt = "retained_state_cleanup_debt";
}

internal static class RetainedStateRetention
{
    internal static bool TryCandidate(
        long preparedAtUnixSeconds,
        out long logicalExpiresAtUnixSeconds,
        out long requiredPlatformExpiresAtUnixSeconds)
    {
        logicalExpiresAtUnixSeconds = 0;
        requiredPlatformExpiresAtUnixSeconds = 0;
        if (!LineageValidation.IsTime(preparedAtUnixSeconds))
        {
            return false;
        }

        try
        {
            logicalExpiresAtUnixSeconds = checked(
                preparedAtUnixSeconds +
                    StateRetentionRequirements.LogicalWindowSeconds);
            requiredPlatformExpiresAtUnixSeconds = Math.Max(
                checked(preparedAtUnixSeconds +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                checked(logicalExpiresAtUnixSeconds +
                    StateRetentionRequirements.PreStickyBudgetSeconds));
            return LineageValidation.IsTime(logicalExpiresAtUnixSeconds) &&
                LineageValidation.IsTime(
                    requiredPlatformExpiresAtUnixSeconds);
        }
        catch (OverflowException)
        {
            logicalExpiresAtUnixSeconds = 0;
            requiredPlatformExpiresAtUnixSeconds = 0;
            return false;
        }
    }

    internal static bool TryAcceptance(
        long acceptedAtUnixSeconds,
        out long logicalExpiresAtUnixSeconds,
        out long receiptRequiredPlatformExpiresAtUnixSeconds)
    {
        logicalExpiresAtUnixSeconds = 0;
        receiptRequiredPlatformExpiresAtUnixSeconds = 0;
        if (!LineageValidation.IsTime(acceptedAtUnixSeconds))
        {
            return false;
        }

        try
        {
            logicalExpiresAtUnixSeconds = checked(
                acceptedAtUnixSeconds +
                    StateRetentionRequirements.LogicalWindowSeconds);
            receiptRequiredPlatformExpiresAtUnixSeconds = checked(
                acceptedAtUnixSeconds +
                    2 * StateRetentionRequirements.LogicalWindowSeconds);
            return LineageValidation.IsTime(logicalExpiresAtUnixSeconds) &&
                LineageValidation.IsTime(
                    receiptRequiredPlatformExpiresAtUnixSeconds);
        }
        catch (OverflowException)
        {
            logicalExpiresAtUnixSeconds = 0;
            receiptRequiredPlatformExpiresAtUnixSeconds = 0;
            return false;
        }
    }

    internal static bool TryOpaque(
        long nowUnixSeconds,
        long semanticRequiredExpiresAtUnixSeconds,
        out long requiredPlatformExpiresAtUnixSeconds)
    {
        requiredPlatformExpiresAtUnixSeconds = 0;
        if (!LineageValidation.IsTime(nowUnixSeconds) ||
            !LineageValidation.IsTime(semanticRequiredExpiresAtUnixSeconds))
        {
            return false;
        }

        try
        {
            requiredPlatformExpiresAtUnixSeconds = Math.Max(
                checked(nowUnixSeconds +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                semanticRequiredExpiresAtUnixSeconds);
            return LineageValidation.IsTime(
                requiredPlatformExpiresAtUnixSeconds);
        }
        catch (OverflowException)
        {
            requiredPlatformExpiresAtUnixSeconds = 0;
            return false;
        }
    }

    internal static bool CoversPreSticky(
        long returnedExpiresAtUnixSeconds,
        long currentTrustedTimeUnixSeconds)
    {
        try
        {
            return LineageValidation.IsTime(returnedExpiresAtUnixSeconds) &&
                LineageValidation.IsTime(currentTrustedTimeUnixSeconds) &&
                returnedExpiresAtUnixSeconds >= checked(
                    currentTrustedTimeUnixSeconds +
                        StateRetentionRequirements.LogicalWindowSeconds +
                        StateRetentionRequirements.PreStickyBudgetSeconds);
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

internal sealed record RetainedStateTransactionResult<T>(
    string Code,
    T? Value)
    where T : class
{
    internal bool Succeeded =>
        Value is not null &&
        (StringComparer.Ordinal.Equals(
                Code,
                RetainedStateTransactionCodes.Ready) ||
            StringComparer.Ordinal.Equals(
                Code,
                RetainedStateTransactionCodes.Prepared) ||
            StringComparer.Ordinal.Equals(
                Code,
                RetainedStateTransactionCodes.Persisted) ||
            StringComparer.Ordinal.Equals(
                Code,
                RetainedStateTransactionCodes.Owned) ||
            StringComparer.Ordinal.Equals(
                Code,
                RetainedStateTransactionCodes.Accepted));

    internal static RetainedStateTransactionResult<T> Success(
        string code,
        T value) => new(code, value);

    internal static RetainedStateTransactionResult<T> Fail(string code) =>
        new(code, null);
}

internal sealed record RetainedStateTransactionBinding(
    RestrictedStateScope StateScope,
    LineageBaseScope BaseScope,
    SelectedLineageSnapshot SelectedLineage,
    ReviewedTransitionFacts Reviewed,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    AcceptedStatePolicyBinding Policy,
    AcceptedStatePublicationBinding Publication,
    long? CurrentGeneration,
    string? CurrentLogicalGenerationIdentity,
    string? CurrentAcceptanceReceiptIdentity,
    string InitialInventoryDigest);

internal sealed record RetainedStateCurrentReviewProjection(
    ReviewedIdentity ReviewedIdentity,
    ImmutableArray<R4FindingIdentityV1> OrderedFindings);

internal sealed class RetainedStatePreparedCandidate : IDisposable
{
    private byte[]? canonicalGeneration;
    private byte[]? outerEnvelope;

    private RetainedStatePreparedCandidate(
        RetainedStateTransactionAuthority authority,
        AgentRunRequest? run,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        OpaqueStoreName name,
        byte[] outerEnvelope,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity)
    {
        this.authority = authority;
        Run = run;
        Generation = generation;
        Publication = publication;
        this.canonicalGeneration = canonicalGeneration;
        Name = name;
        this.outerEnvelope = outerEnvelope;
        Header = header;
        LogicalGenerationIdentity = logicalGenerationIdentity;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal AgentRunRequest? Run { get; }
    internal bool IsRecovered => Run is null;
    internal StateGenerationRecordV1 Generation { get; }
    internal ValidatedPublicationPayloadV1 Publication { get; }
    internal OpaqueStoreName Name { get; }
    internal StateControlHeaderV1 Header { get; }
    internal string LogicalGenerationIdentity { get; }

    internal bool TryGetBytes(
        RetainedStateTransactionAuthority authority,
        out ReadOnlyMemory<byte> generationBytes,
        out ReadOnlyMemory<byte> envelopeBytes)
    {
        generationBytes = default;
        envelopeBytes = default;
        var generation = Volatile.Read(ref canonicalGeneration);
        var envelope = Volatile.Read(ref outerEnvelope);
        if (!ReferenceEquals(this.authority, authority) ||
            !authority.IsLive ||
            generation is null ||
            envelope is null)
        {
            return false;
        }

        generationBytes = generation;
        envelopeBytes = envelope;
        return true;
    }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static RetainedStatePreparedCandidate Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        AgentRunRequest run,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        OpaqueStoreName name,
        byte[] outerEnvelope,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            run,
            generation,
            publication,
            canonicalGeneration,
            name,
            outerEnvelope,
            header,
            logicalGenerationIdentity);
    }

    internal static RetainedStatePreparedCandidate CreateRecovered(
        object issuer,
        RetainedStateTransactionAuthority authority,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            run: null,
            generation,
            publication,
            canonicalGeneration,
            name,
            outerEnvelope: [],
            header,
            logicalGenerationIdentity);
    }

    public void Dispose()
    {
        Zero(Generation.EncryptedStateEnvelope);
        Zero(Generation.PublicationPayloadBytes);
        Zero(Publication.FinalizedCommentUtf8);
        var generation = Interlocked.Exchange(
            ref canonicalGeneration,
            null);
        if (generation is not null)
        {
            CryptographicOperations.ZeroMemory(generation);
        }

        var envelope = Interlocked.Exchange(ref outerEnvelope, null);
        if (envelope is not null)
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static void Zero(ImmutableArray<byte> bytes)
    {
        var array = ImmutableCollectionsMarshal.AsArray(bytes);
        if (array is not null)
        {
            CryptographicOperations.ZeroMemory(array);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStatePersistedCandidate
{
    private RetainedStatePersistedCandidate(
        RetainedStateTransactionAuthority authority,
        RetainedStatePreparedCandidate prepared,
        OpaqueStoreObjectMetadata metadata,
        string inventoryDigest)
    {
        this.authority = authority;
        Prepared = prepared;
        Metadata = metadata;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStatePreparedCandidate Prepared { get; }
    internal OpaqueStoreObjectMetadata Metadata { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static RetainedStatePersistedCandidate Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePreparedCandidate prepared,
        OpaqueStoreObjectMetadata metadata,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(authority, prepared, metadata, inventoryDigest);
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateOwnership : IDisposable
{
    private int usable = 1;

    private RetainedStateOwnership(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        SelectedLineageSnapshot selectedLineage,
        string inventoryDigest,
        long observedAtUnixSeconds,
        long? acceptanceNotAfterUnixSeconds,
        StateObjectClass? restrictedObjectClass)
    {
        this.authority = authority;
        Candidate = candidate;
        SelectedLineage = selectedLineage;
        InventoryDigest = inventoryDigest;
        ObservedAtUnixSeconds = observedAtUnixSeconds;
        AcceptanceNotAfterUnixSeconds = acceptanceNotAfterUnixSeconds;
        RestrictedObjectClass = restrictedObjectClass;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal SelectedLineageSnapshot SelectedLineage { get; }
    internal string InventoryDigest { get; }
    internal long ObservedAtUnixSeconds { get; }
    internal long? AcceptanceNotAfterUnixSeconds { get; }
    internal StateObjectClass? RestrictedObjectClass { get; }
    internal bool IsUsable => Volatile.Read(ref usable) == 1;

    internal bool TryConsume(RetainedStateTransactionAuthority authority) =>
        ReferenceEquals(this.authority, authority) &&
        authority.IsLive &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateOwnership Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        SelectedLineageSnapshot selectedLineage,
        string inventoryDigest,
        long observedAtUnixSeconds,
        long? acceptanceNotAfterUnixSeconds = null)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            selectedLineage,
            inventoryDigest,
            observedAtUnixSeconds,
            acceptanceNotAfterUnixSeconds,
            restrictedObjectClass: null);
    }

    internal static RetainedStateOwnership CreateStaleAbandonment(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        SelectedLineageSnapshot selectedLineage,
        string inventoryDigest,
        long observedAtUnixSeconds)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            selectedLineage,
            inventoryDigest,
            observedAtUnixSeconds,
            acceptanceNotAfterUnixSeconds: null,
            StateObjectClass.Abandonment);
    }

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateOpaqueWriteRequest(
    StateObjectClass ObjectClass,
    ImmutableArray<byte> Payload,
    string? PredecessorIdentity,
    string? SuccessorIdentity,
    long SemanticRequiredExpiresAtUnixSeconds);

internal sealed class RetainedStateOpaqueWriteAttempt : IDisposable
{
    private byte[]? payload;
    private byte[]? envelope;
    private byte[]? recoveryPayload;
    private int dispatchState;

    private RetainedStateOpaqueWriteAttempt(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StateObjectClass objectClass,
        string operationIdentity,
        long semanticRequiredExpiresAtUnixSeconds,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] payload,
        byte[] envelope,
        byte[] recoveryPayload,
        OpaqueStoreObjectMetadata anchorMetadata,
        string inventoryDigest,
        bool reconcileOnly)
    {
        this.authority = authority;
        Candidate = candidate;
        ObjectClass = objectClass;
        OperationIdentity = operationIdentity;
        SemanticRequiredExpiresAtUnixSeconds =
            semanticRequiredExpiresAtUnixSeconds;
        Name = name;
        Header = header;
        this.payload = payload;
        this.envelope = envelope;
        this.recoveryPayload = recoveryPayload;
        AnchorMetadata = anchorMetadata;
        InventoryDigest = inventoryDigest;
        ReconcileOnly = reconcileOnly;
        dispatchState = reconcileOnly ? 1 : 0;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal StateObjectClass ObjectClass { get; }
    internal string OperationIdentity { get; }
    internal long SemanticRequiredExpiresAtUnixSeconds { get; }
    internal OpaqueStoreName Name { get; }
    internal StateControlHeaderV1 Header { get; }
    internal OpaqueStoreObjectMetadata AnchorMetadata { get; }
    internal string InventoryDigest { get; }
    internal bool ReconcileOnly { get; }
    internal bool HasEnteredDispatch => Volatile.Read(ref dispatchState) != 0;

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal bool TryGetBytes(
        RetainedStateTransactionAuthority value,
        out ReadOnlyMemory<byte> payloadBytes,
        out ReadOnlyMemory<byte> envelopeBytes)
    {
        payloadBytes = default;
        envelopeBytes = default;
        var currentPayload = Volatile.Read(ref payload);
        var currentEnvelope = Volatile.Read(ref envelope);
        if (!ReferenceEquals(authority, value) ||
            !value.IsLive ||
            currentPayload is null ||
            currentEnvelope is null)
        {
            return false;
        }

        payloadBytes = currentPayload;
        envelopeBytes = currentEnvelope;
        return true;
    }

    internal bool TryCreateRecoveryHandoff(
        out RetainedStateOpaqueWriteRecoveryHandoff? handoff)
    {
        var current = Volatile.Read(ref recoveryPayload);
        if (current is null || !authority.IsLive)
        {
            handoff = null;
            return false;
        }

        handoff = new RetainedStateOpaqueWriteRecoveryHandoff(
            ImmutableArray.CreateRange(current),
            SemanticRequiredExpiresAtUnixSeconds,
            Candidate.Prepared.Header.ObjectIdentity,
            ObjectClass,
            OperationIdentity);
        return true;
    }

    internal bool TryBeginDispatch() =>
        !ReconcileOnly &&
        Interlocked.CompareExchange(ref dispatchState, 1, 0) == 0;

    internal void ResetDispatchIfDefinitelyNotCommitted()
    {
        if (!ReconcileOnly)
        {
            Interlocked.CompareExchange(ref dispatchState, 0, 1);
        }
    }

    internal static RetainedStateOpaqueWriteAttempt Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StateObjectClass objectClass,
        string operationIdentity,
        long semanticRequiredExpiresAtUnixSeconds,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] payload,
        byte[] envelope,
        byte[] recoveryPayload,
        OpaqueStoreObjectMetadata anchorMetadata,
        string inventoryDigest,
        bool reconcileOnly = false)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            objectClass,
            operationIdentity,
            semanticRequiredExpiresAtUnixSeconds,
            name,
            header,
            payload,
            envelope,
            recoveryPayload,
            anchorMetadata,
            inventoryDigest,
            reconcileOnly);
    }

    public void Dispose()
    {
        Zero(ref payload);
        Zero(ref envelope);
        Zero(ref recoveryPayload);
    }

    private static void Zero(ref byte[]? value)
    {
        var current = Interlocked.Exchange(ref value, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateOpaqueWriteRecoveryHandoff(
    ImmutableArray<byte> OpaqueInnerPayload,
    long MinimumSemanticExpiresAtUnixSeconds,
    string CandidateObjectIdentity,
    StateObjectClass ObjectClass,
    string OperationIdentity);

internal sealed class RetainedStateOpaqueWriteAttemptSet : IDisposable
{
    private ImmutableArray<RetainedStateOpaqueWriteAttempt> attempts;

    private RetainedStateOpaqueWriteAttemptSet(
        ImmutableArray<RetainedStateOpaqueWriteAttempt> attempts) =>
        this.attempts = attempts;

    internal ImmutableArray<RetainedStateOpaqueWriteAttempt> Attempts =>
        attempts;

    internal static RetainedStateOpaqueWriteAttemptSet Create(
        ImmutableArray<RetainedStateOpaqueWriteAttempt> attempts) =>
        new(attempts);

    public void Dispose()
    {
        var current = attempts;
        attempts = [];
        foreach (var attempt in current)
        {
            attempt.Dispose();
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateOpaqueRecord : IDisposable
{
    private byte[]? payload;

    private RetainedStateOpaqueRecord(
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        byte[] payload,
        string inventoryDigest)
    {
        this.authority = authority;
        ObjectClass = objectClass;
        Metadata = metadata;
        Header = header;
        this.payload = payload;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal StateObjectClass ObjectClass { get; }
    internal OpaqueStoreObjectMetadata Metadata { get; }
    internal StateControlHeaderV1 Header { get; }
    internal string InventoryDigest { get; }

    internal bool TryCopyPayload(
        RetainedStateTransactionAuthority authority,
        out ImmutableArray<byte> value)
    {
        value = [];
        var current = Volatile.Read(ref payload);
        if (!ReferenceEquals(this.authority, authority) ||
            !authority.IsLive ||
            current is null)
        {
            return false;
        }

        value = ImmutableArray.CreateRange(current);
        return true;
    }

    internal bool TryCopyPayloadRange(
        RetainedStateTransactionAuthority authority,
        int offset,
        int length,
        out byte[] value,
        out string payloadSha256)
    {
        value = [];
        payloadSha256 = string.Empty;
        var current = Volatile.Read(ref payload);
        if (!ReferenceEquals(this.authority, authority) ||
            !authority.IsLive ||
            current is null ||
            offset < 0 ||
            length <= 0 ||
            offset > current.Length - length)
        {
            return false;
        }

        value = current.AsSpan(offset, length).ToArray();
        payloadSha256 = OpaqueStoreHash.Sha256(current);
        return true;
    }

    internal bool MatchesAuthenticated(
        RetainedStateTransactionAuthority authority,
        AuthenticatedStateObject value)
    {
        var current = Volatile.Read(ref payload);
        return ReferenceEquals(this.authority, authority) &&
            authority.IsLive &&
            current is not null &&
            value.Header.ObjectClass == ObjectClass &&
            value.Metadata == Metadata &&
            value.Header == Header &&
            value.Payload.AsSpan().SequenceEqual(current);
    }

    internal static RetainedStateOpaqueRecord Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        byte[] payload,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            objectClass,
            metadata,
            header,
            payload,
            inventoryDigest);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref payload, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateOpaquePayloadExtraction : IDisposable
{
    private byte[]? extractedPayload;
    private int usable = 1;

    private RetainedStateOpaquePayloadExtraction(
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        OpaqueStoreObjectMetadata sourceMetadata,
        StateControlHeaderV1 sourceHeader,
        string sourcePayloadSha256,
        int payloadOffset,
        byte[] extractedPayload,
        string sourceInventoryDigest)
    {
        this.authority = authority;
        ObjectClass = objectClass;
        SourceMetadata = sourceMetadata;
        SourceHeader = sourceHeader;
        SourcePayloadSha256 = sourcePayloadSha256;
        PayloadOffset = payloadOffset;
        this.extractedPayload = extractedPayload;
        ExtractedPayloadSha256 = OpaqueStoreHash.Sha256(extractedPayload);
        SourceInventoryDigest = sourceInventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal StateObjectClass ObjectClass { get; }
    internal OpaqueStoreObjectMetadata SourceMetadata { get; }
    internal StateControlHeaderV1 SourceHeader { get; }
    internal string SourcePayloadSha256 { get; }
    internal int PayloadOffset { get; }
    internal int PayloadLength =>
        Volatile.Read(ref extractedPayload)?.Length ?? 0;
    internal string ExtractedPayloadSha256 { get; }
    internal string SourceInventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Volatile.Read(ref usable) == 1 &&
        Volatile.Read(ref extractedPayload) is not null;

    internal bool MatchesExpected(
        RetainedStateTransactionAuthority value,
        ReadOnlySpan<byte> expected)
    {
        var current = Volatile.Read(ref extractedPayload);
        return IsIssuedBy(value) &&
            current is not null &&
            current.AsSpan().SequenceEqual(expected);
    }

    internal bool TryCopyExtractedPayload(
        RetainedStateTransactionAuthority value,
        out byte[] payload)
    {
        payload = [];
        var current = Volatile.Read(ref extractedPayload);
        if (!IsIssuedBy(value) || current is null)
        {
            return false;
        }

        payload = current.ToArray();
        return true;
    }

    internal bool MatchesAuthenticated(AuthenticatedStateObject value)
    {
        var current = Volatile.Read(ref extractedPayload);
        return current is not null &&
            value.Header.ObjectClass == ObjectClass &&
            value.Metadata == SourceMetadata &&
            value.Header == SourceHeader &&
            StringComparer.Ordinal.Equals(
                OpaqueStoreHash.Sha256(value.Payload),
                SourcePayloadSha256) &&
            PayloadOffset <= value.Payload.Length - current.Length &&
            value.Payload.AsSpan(PayloadOffset, current.Length)
                .SequenceEqual(current);
    }

    internal bool TryConsume(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Volatile.Read(ref extractedPayload) is not null &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateOpaquePayloadExtraction Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        OpaqueStoreObjectMetadata sourceMetadata,
        StateControlHeaderV1 sourceHeader,
        string sourcePayloadSha256,
        int payloadOffset,
        byte[] extractedPayload,
        string sourceInventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            objectClass,
            sourceMetadata,
            sourceHeader,
            sourcePayloadSha256,
            payloadOffset,
            extractedPayload,
            sourceInventoryDigest);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref usable, 0);
        var current = Interlocked.Exchange(ref extractedPayload, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateAcceptancePreparation : IDisposable
{
    private RetainedStateAcceptanceAttempt? attempt;
    private RetainedStatePredecessorCopyAttempt? predecessorCopyAttempt;
    private byte[]? recoveryPayload;

    private RetainedStateAcceptancePreparation(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        RetainedStateOwnership ownership,
        RetainedStateAcceptanceAttempt attempt,
        RetainedStatePredecessorCopyAttempt? predecessorCopyAttempt,
        byte[] recoveryPayload)
    {
        this.authority = authority;
        Candidate = candidate;
        Receipt = receipt;
        Ownership = ownership;
        this.attempt = attempt;
        this.predecessorCopyAttempt = predecessorCopyAttempt;
        this.recoveryPayload = recoveryPayload;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal StickyCommentPublisher.StickyPublicationReceipt Receipt { get; }
    internal RetainedStateOwnership Ownership { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal RetainedStateAcceptanceAttempt? GetAttempt(
        RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value)
            ? Volatile.Read(ref attempt)
            : null;

    internal RetainedStateAcceptanceAttempt? TakeAttempt(
        RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value)
            ? Interlocked.Exchange(ref attempt, null)
            : null;

    internal RetainedStatePredecessorCopyAttempt? GetPredecessorCopyAttempt(
        RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value)
            ? Volatile.Read(ref predecessorCopyAttempt)
            : null;

    internal bool TryCreateRecoveryHandoff(
        out RetainedStateAcceptanceRecoveryHandoff? handoff)
    {
        var current = Volatile.Read(ref recoveryPayload);
        var currentAttempt = Volatile.Read(ref attempt);
        if (current is null || currentAttempt is null || !authority.IsLive)
        {
            handoff = null;
            return false;
        }

        handoff = new RetainedStateAcceptanceRecoveryHandoff(
            ImmutableArray.CreateRange(current),
            currentAttempt.LogicalExpiresAtUnixSeconds,
            Candidate.Prepared.Header.ObjectIdentity);
        return true;
    }

    internal static RetainedStateAcceptancePreparation Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        RetainedStateOwnership ownership,
        RetainedStateAcceptanceAttempt attempt,
        RetainedStatePredecessorCopyAttempt? predecessorCopyAttempt,
        byte[] recoveryPayload)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            receipt,
            ownership,
            attempt,
            predecessorCopyAttempt,
            recoveryPayload);
    }

    public void Dispose()
    {
        Ownership.Dispose();
        Interlocked.Exchange(ref attempt, null)?.Dispose();
        Interlocked.Exchange(ref predecessorCopyAttempt, null)?.Dispose();
        var current = Interlocked.Exchange(ref recoveryPayload, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateAcceptanceRecoveryHandoff(
    ImmutableArray<byte> OpaqueInnerPayload,
    long MinimumSemanticExpiresAtUnixSeconds,
    string CandidateObjectIdentity);

internal sealed class RetainedStateAcceptanceRecoveryDurability : IDisposable
{
    private int predecessorAuthorized;
    private int usable = 1;

    private RetainedStateAcceptanceRecoveryDurability(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        OpaqueStoreObjectMetadata recoveryRecordMetadata,
        StateControlHeaderV1 recoveryRecordHeader,
        string recoveryRecordPayloadSha256,
        int extractionOffset,
        int extractionLength,
        string extractedPayloadSha256,
        string inventoryDigest)
    {
        this.authority = authority;
        Preparation = preparation;
        RecoveryRecordMetadata = recoveryRecordMetadata;
        RecoveryRecordHeader = recoveryRecordHeader;
        RecoveryRecordPayloadSha256 = recoveryRecordPayloadSha256;
        ExtractionOffset = extractionOffset;
        ExtractionLength = extractionLength;
        ExtractedPayloadSha256 = extractedPayloadSha256;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStateAcceptancePreparation Preparation { get; }
    internal OpaqueStoreObjectMetadata RecoveryRecordMetadata { get; }
    internal StateControlHeaderV1 RecoveryRecordHeader { get; }
    internal string RecoveryRecordPayloadSha256 { get; }
    internal int ExtractionOffset { get; }
    internal int ExtractionLength { get; }
    internal string ExtractedPayloadSha256 { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedFor(
        RetainedStateTransactionAuthority value,
        RetainedStateAcceptancePreparation preparation) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Volatile.Read(ref usable) == 1 &&
        ReferenceEquals(Preparation, preparation);

    internal bool MatchesRecoveryRecord(AuthenticatedStateObject value) =>
        value.Metadata == RecoveryRecordMetadata &&
        value.Header == RecoveryRecordHeader &&
        StringComparer.Ordinal.Equals(
            OpaqueStoreHash.Sha256(value.Payload),
            RecoveryRecordPayloadSha256) &&
        ExtractionOffset >= 0 &&
        ExtractionLength > 0 &&
        ExtractionOffset <= value.Payload.Length - ExtractionLength &&
        StringComparer.Ordinal.Equals(
            OpaqueStoreHash.Sha256(value.Payload.AsSpan(
                ExtractionOffset,
                ExtractionLength)),
            ExtractedPayloadSha256);

    internal bool TryAuthorizePredecessor(
        RetainedStateTransactionAuthority value,
        RetainedStateAcceptancePreparation preparation) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Volatile.Read(ref usable) == 1 &&
        ReferenceEquals(Preparation, preparation) &&
        Interlocked.CompareExchange(
            ref predecessorAuthorized,
            1,
            0) is 0 or 1;

    internal bool TryConsumeForEvidence(
        RetainedStateTransactionAuthority value,
        RetainedStateAcceptancePreparation preparation) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        ReferenceEquals(Preparation, preparation) &&
        Volatile.Read(ref predecessorAuthorized) == 1 &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateAcceptanceRecoveryDurability Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptancePreparation preparation,
        OpaqueStoreObjectMetadata recoveryRecordMetadata,
        StateControlHeaderV1 recoveryRecordHeader,
        string recoveryRecordPayloadSha256,
        int extractionOffset,
        int extractionLength,
        string extractedPayloadSha256,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            preparation,
            recoveryRecordMetadata,
            recoveryRecordHeader,
            recoveryRecordPayloadSha256,
            extractionOffset,
            extractionLength,
            extractedPayloadSha256,
            inventoryDigest);
    }

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateAcceptanceEvidence : IDisposable
{
    private RetainedStateAcceptanceAttempt? attempt;
    private RetainedStateAcceptanceEvidence(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        ExactHeadRevalidationResult exactHead,
        string inventoryDigest,
        RetainedStateAcceptanceAttempt attempt)
    {
        this.authority = authority;
        Candidate = candidate;
        Receipt = receipt;
        ExactHead = exactHead;
        InventoryDigest = inventoryDigest;
        this.attempt = attempt;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal StickyCommentPublisher.StickyPublicationReceipt Receipt { get; }
    internal ExactHeadRevalidationResult ExactHead { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static RetainedStateAcceptanceEvidence Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        ExactHeadRevalidationResult exactHead,
        string inventoryDigest,
        RetainedStateAcceptanceAttempt attempt)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            receipt,
            exactHead,
            inventoryDigest,
            attempt);
    }

    internal RetainedStateAcceptanceAttempt? GetAttempt(
        RetainedStateTransactionAuthority authority)
    {
        return ReferenceEquals(this.authority, authority)
            ? Volatile.Read(ref attempt)
            : null;
    }

    public void Dispose() =>
        Interlocked.Exchange(ref attempt, null)?.Dispose();

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateAcceptanceAttempt : IDisposable
{
    private byte[]? receiptBytes;
    private byte[]? envelopeBytes;
    private int dispatchState;

    private RetainedStateAcceptanceAttempt(
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        AcceptanceReceiptV1 receipt,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] receiptBytes,
        byte[] envelopeBytes,
        bool reconcileOnly)
    {
        AcceptedAtUnixSeconds = acceptedAtUnixSeconds;
        LogicalExpiresAtUnixSeconds = logicalExpiresAtUnixSeconds;
        RequiredPlatformExpiresAtUnixSeconds =
            requiredPlatformExpiresAtUnixSeconds;
        Receipt = receipt;
        Name = name;
        Header = header;
        this.receiptBytes = receiptBytes;
        this.envelopeBytes = envelopeBytes;
        ReconcileOnly = reconcileOnly;
        dispatchState = reconcileOnly ? 1 : 0;
    }

    internal static RetainedStateAcceptanceAttempt Create(
        object issuer,
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        AcceptanceReceiptV1 receipt,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] receiptBytes,
        byte[] envelopeBytes,
        bool reconcileOnly = false)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new RetainedStateAcceptanceAttempt(
            acceptedAtUnixSeconds,
            logicalExpiresAtUnixSeconds,
            requiredPlatformExpiresAtUnixSeconds,
            receipt,
            name,
            header,
            receiptBytes,
            envelopeBytes,
            reconcileOnly);
    }

    internal long AcceptedAtUnixSeconds { get; }
    internal long LogicalExpiresAtUnixSeconds { get; }
    internal long RequiredPlatformExpiresAtUnixSeconds { get; }
    internal AcceptanceReceiptV1 Receipt { get; }
    internal OpaqueStoreName Name { get; }
    internal StateControlHeaderV1 Header { get; }
    internal bool ReconcileOnly { get; }
    internal bool HasEnteredDispatch => Volatile.Read(ref dispatchState) != 0;

    internal bool TryBeginDispatch() =>
        !ReconcileOnly &&
        Interlocked.CompareExchange(ref dispatchState, 1, 0) == 0;

    internal void ResetDispatchIfDefinitelyNotCommitted()
    {
        if (!ReconcileOnly)
        {
            Interlocked.CompareExchange(ref dispatchState, 0, 1);
        }
    }

    internal bool TryGetBytes(
        out ReadOnlyMemory<byte> receipt,
        out ReadOnlyMemory<byte> envelope)
    {
        receipt = default;
        envelope = default;
        var currentReceipt = Volatile.Read(ref receiptBytes);
        var currentEnvelope = Volatile.Read(ref envelopeBytes);
        if (currentReceipt is null || currentEnvelope is null)
        {
            return false;
        }

        receipt = currentReceipt;
        envelope = currentEnvelope;
        return true;
    }

    public void Dispose()
    {
        var receipt = Interlocked.Exchange(ref receiptBytes, null);
        if (receipt is not null)
        {
            CryptographicOperations.ZeroMemory(receipt);
        }

        var envelope = Interlocked.Exchange(ref envelopeBytes, null);
        if (envelope is not null)
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStatePredecessorCopyAttempt : IDisposable
{
    private byte[]? payload;
    private byte[]? envelope;
    private int dispatchState;

    private RetainedStatePredecessorCopyAttempt(
        string logicalGenerationIdentity,
        long requiredLogicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] payload,
        byte[] envelope,
        bool reconcileOnly)
    {
        LogicalGenerationIdentity = logicalGenerationIdentity;
        RequiredLogicalExpiresAtUnixSeconds =
            requiredLogicalExpiresAtUnixSeconds;
        RequiredPlatformExpiresAtUnixSeconds =
            requiredPlatformExpiresAtUnixSeconds;
        Name = name;
        Header = header;
        this.payload = payload;
        this.envelope = envelope;
        ReconcileOnly = reconcileOnly;
        dispatchState = reconcileOnly ? 1 : 0;
    }

    internal string LogicalGenerationIdentity { get; }
    internal long RequiredLogicalExpiresAtUnixSeconds { get; }
    internal long RequiredPlatformExpiresAtUnixSeconds { get; }
    internal OpaqueStoreName Name { get; }
    internal StateControlHeaderV1 Header { get; }
    internal bool ReconcileOnly { get; }
    internal bool HasEnteredDispatch => Volatile.Read(ref dispatchState) != 0;

    internal bool TryBeginDispatch() =>
        !ReconcileOnly &&
        Interlocked.CompareExchange(ref dispatchState, 1, 0) == 0;

    internal void ResetDispatchIfDefinitelyNotCommitted()
    {
        if (!ReconcileOnly)
        {
            Interlocked.CompareExchange(ref dispatchState, 0, 1);
        }
    }

    internal bool TryGetBytes(
        out ReadOnlyMemory<byte> payloadBytes,
        out ReadOnlyMemory<byte> envelopeBytes)
    {
        payloadBytes = Volatile.Read(ref payload) ?? [];
        envelopeBytes = Volatile.Read(ref envelope) ?? [];
        return payloadBytes.Length > 0 && envelopeBytes.Length > 0;
    }

    internal static RetainedStatePredecessorCopyAttempt Create(
        object issuer,
        string logicalGenerationIdentity,
        long requiredLogicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] payload,
        byte[] envelope,
        bool reconcileOnly = false)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            logicalGenerationIdentity,
            requiredLogicalExpiresAtUnixSeconds,
            requiredPlatformExpiresAtUnixSeconds,
            name,
            header,
            payload,
            envelope,
            reconcileOnly);
    }

    public void Dispose()
    {
        var currentPayload = Interlocked.Exchange(ref payload, null);
        if (currentPayload is not null)
        {
            CryptographicOperations.ZeroMemory(currentPayload);
        }

        var currentEnvelope = Interlocked.Exchange(ref envelope, null);
        if (currentEnvelope is not null)
        {
            CryptographicOperations.ZeroMemory(currentEnvelope);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class VerifiedRetainedStateAcceptance
{
    private VerifiedRetainedStateAcceptance(
        RetainedStateTransactionAuthority authority,
        string logicalGenerationIdentity,
        string acceptanceReceiptIdentity,
        OpaqueStoreObjectMetadata receiptMetadata,
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        string inventoryDigest)
    {
        this.authority = authority;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        AcceptanceReceiptIdentity = acceptanceReceiptIdentity;
        ReceiptMetadata = receiptMetadata;
        AcceptedAtUnixSeconds = acceptedAtUnixSeconds;
        LogicalExpiresAtUnixSeconds = logicalExpiresAtUnixSeconds;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal string LogicalGenerationIdentity { get; }
    internal string AcceptanceReceiptIdentity { get; }
    internal OpaqueStoreObjectMetadata ReceiptMetadata { get; }
    internal long AcceptedAtUnixSeconds { get; }
    internal long LogicalExpiresAtUnixSeconds { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static VerifiedRetainedStateAcceptance Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        string logicalGenerationIdentity,
        string acceptanceReceiptIdentity,
        OpaqueStoreObjectMetadata receiptMetadata,
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            logicalGenerationIdentity,
            acceptanceReceiptIdentity,
            receiptMetadata,
            acceptedAtUnixSeconds,
            logicalExpiresAtUnixSeconds,
            inventoryDigest);
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class MatchedRetainedStateRecoveryAcceptance
{
    private MatchedRetainedStateRecoveryAcceptance(
        RetainedStateTransactionAuthority authority,
        string candidateObjectIdentity,
        string logicalGenerationIdentity,
        string acceptanceReceiptIdentity,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long logicalExpiresAtUnixSeconds,
        OpaqueStoreObjectMetadata recoveryRecordMetadata,
        StateControlHeaderV1 recoveryRecordHeader,
        string inventoryDigest)
    {
        this.authority = authority;
        CandidateObjectIdentity = candidateObjectIdentity;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        AcceptanceReceiptIdentity = acceptanceReceiptIdentity;
        Receipt = receipt;
        LogicalExpiresAtUnixSeconds = logicalExpiresAtUnixSeconds;
        RecoveryRecordMetadata = recoveryRecordMetadata;
        RecoveryRecordHeader = recoveryRecordHeader;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal string CandidateObjectIdentity { get; }
    internal string LogicalGenerationIdentity { get; }
    internal string AcceptanceReceiptIdentity { get; }
    internal StickyCommentPublisher.StickyPublicationReceipt Receipt { get; }
    internal long LogicalExpiresAtUnixSeconds { get; }
    internal OpaqueStoreObjectMetadata RecoveryRecordMetadata { get; }
    internal StateControlHeaderV1 RecoveryRecordHeader { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static MatchedRetainedStateRecoveryAcceptance Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        string candidateObjectIdentity,
        string logicalGenerationIdentity,
        string acceptanceReceiptIdentity,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long logicalExpiresAtUnixSeconds,
        OpaqueStoreObjectMetadata recoveryRecordMetadata,
        StateControlHeaderV1 recoveryRecordHeader,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidateObjectIdentity,
            logicalGenerationIdentity,
            acceptanceReceiptIdentity,
            receipt,
            logicalExpiresAtUnixSeconds,
            recoveryRecordMetadata,
            recoveryRecordHeader,
            inventoryDigest);
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateCleanupTarget(
    OpaqueStoreObjectMetadata Metadata);

internal sealed class RetainedStatePendingCandidateEvidence
{
    private RetainedStatePendingCandidateEvidence(
        RetainedStateTransactionAuthority authority,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity,
        long generation,
        string producerHeadSha,
        bool matchesCurrentReviewedHead,
        string inventoryDigest)
    {
        this.authority = authority;
        Metadata = metadata;
        Header = header;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        Generation = generation;
        ProducerHeadSha = producerHeadSha;
        MatchesCurrentReviewedHead = matchesCurrentReviewedHead;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal OpaqueStoreObjectMetadata Metadata { get; }
    internal StateControlHeaderV1 Header { get; }
    internal string LogicalGenerationIdentity { get; }
    internal long Generation { get; }
    internal string ProducerHeadSha { get; }
    internal bool MatchesCurrentReviewedHead { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) && value.IsLive;

    internal static RetainedStatePendingCandidateEvidence Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity,
        long generation,
        string producerHeadSha,
        bool matchesCurrentReviewedHead,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            metadata,
            header,
            logicalGenerationIdentity,
            generation,
            producerHeadSha,
            matchesCurrentReviewedHead,
            inventoryDigest);
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateObservedCandidate : IDisposable
{
    private byte[]? canonicalGeneration;

    private RetainedStateObservedCandidate(
        RetainedStateTransactionAuthority authority,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        bool matchesCurrentReviewedHead,
        string inventoryDigest)
    {
        this.authority = authority;
        Metadata = metadata;
        Header = header;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        Generation = generation;
        Publication = publication;
        this.canonicalGeneration = canonicalGeneration;
        MatchesCurrentReviewedHead = matchesCurrentReviewedHead;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal OpaqueStoreObjectMetadata Metadata { get; }
    internal StateControlHeaderV1 Header { get; }
    internal string LogicalGenerationIdentity { get; }
    internal StateGenerationRecordV1 Generation { get; }
    internal ValidatedPublicationPayloadV1 Publication { get; }
    internal bool MatchesCurrentReviewedHead { get; }
    internal string InventoryDigest { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Volatile.Read(ref canonicalGeneration) is not null;

    internal bool TryCopyCanonicalGeneration(
        RetainedStateTransactionAuthority value,
        out ImmutableArray<byte> bytes)
    {
        bytes = [];
        var current = Volatile.Read(ref canonicalGeneration);
        if (!IsIssuedBy(value) || current is null)
        {
            return false;
        }

        bytes = ImmutableArray.CreateRange(current);
        return true;
    }

    internal static RetainedStateObservedCandidate Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        bool matchesCurrentReviewedHead,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            metadata,
            header,
            logicalGenerationIdentity,
            generation,
            publication,
            canonicalGeneration,
            matchesCurrentReviewedHead,
            inventoryDigest);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref canonicalGeneration, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }

        Zero(Generation.EncryptedStateEnvelope);
        Zero(Generation.PublicationPayloadBytes);
        Zero(Publication.FinalizedCommentUtf8);
    }

    private static void Zero(ImmutableArray<byte> bytes)
    {
        var array = ImmutableCollectionsMarshal.AsArray(bytes);
        if (array is not null)
        {
            CryptographicOperations.ZeroMemory(array);
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStatePublicationRecoveryAnchorEvidence(
    OpaqueStoreObjectMetadata AnchorMetadata,
    StateControlHeaderV1 AnchorHeader,
    string CandidateObjectIdentity,
    string OperationIdentity,
    StateObjectClass ObjectClass,
    OpaqueStoreName TargetName,
    string TargetObjectIdentity,
    string TargetPayloadSha256,
    bool TargetIsPresent);

internal sealed record RetainedStatePublicationRecoveryCleanupEvidence(
    OpaqueStoreObjectMetadata CleanupMetadata,
    StateControlHeaderV1 CleanupHeader,
    RetainedStateCleanupRecord Cleanup,
    ImmutableArray<OpaqueStoreObjectMetadata> PresentTargets);

internal sealed class RetainedStatePublicationRecoveryInventory : IDisposable
{
    private ImmutableArray<RetainedStateOpaqueRecord> records;
    private RetainedStateObservedCandidate? candidate;

    private RetainedStatePublicationRecoveryInventory(
        RetainedStateTransactionAuthority authority,
        RetainedStateObservedCandidate? candidate,
        ImmutableArray<RetainedStateOpaqueRecord> records,
        VerifiedRetainedStateAcceptance? currentAcceptance,
        StickyCommentPublisher.StickyPublicationReceipt?
            currentAcceptancePublicationReceipt,
        string? currentAcceptanceCandidateObjectIdentity,
        ValidatedPublicationPayloadV1? currentAcceptedPublication,
        ImmutableArray<RetainedStatePublicationRecoveryAnchorEvidence>
            anchors,
        ImmutableArray<RetainedStatePublicationRecoveryCleanupEvidence>
            cleanupRecords,
        string inventoryDigest,
        long observedAtUnixSeconds)
    {
        this.authority = authority;
        this.candidate = candidate;
        this.records = records;
        CurrentAcceptance = currentAcceptance;
        CurrentAcceptancePublicationReceipt =
            currentAcceptancePublicationReceipt;
        CurrentAcceptanceCandidateObjectIdentity =
            currentAcceptanceCandidateObjectIdentity;
        CurrentAcceptedPublication = currentAcceptedPublication;
        Anchors = anchors;
        CleanupRecords = cleanupRecords;
        InventoryDigest = inventoryDigest;
        ObservedAtUnixSeconds = observedAtUnixSeconds;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStateObservedCandidate? Candidate =>
        Volatile.Read(ref candidate);
    internal ImmutableArray<RetainedStateOpaqueRecord> Records => records;
    internal VerifiedRetainedStateAcceptance? CurrentAcceptance { get; }
    internal StickyCommentPublisher.StickyPublicationReceipt?
        CurrentAcceptancePublicationReceipt { get; }
    internal string? CurrentAcceptanceCandidateObjectIdentity { get; }
    internal ValidatedPublicationPayloadV1? CurrentAcceptedPublication
    {
        get;
    }
    internal ImmutableArray<RetainedStatePublicationRecoveryAnchorEvidence>
        Anchors { get; }
    internal ImmutableArray<RetainedStatePublicationRecoveryCleanupEvidence>
        CleanupRecords { get; }
    internal string InventoryDigest { get; }
    internal long ObservedAtUnixSeconds { get; }

    internal bool IsIssuedBy(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        !records.IsDefault;

    internal static RetainedStatePublicationRecoveryInventory Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStateObservedCandidate? candidate,
        ImmutableArray<RetainedStateOpaqueRecord> records,
        VerifiedRetainedStateAcceptance? currentAcceptance,
        StickyCommentPublisher.StickyPublicationReceipt?
            currentAcceptancePublicationReceipt,
        string? currentAcceptanceCandidateObjectIdentity,
        ValidatedPublicationPayloadV1? currentAcceptedPublication,
        ImmutableArray<RetainedStatePublicationRecoveryAnchorEvidence>
            anchors,
        ImmutableArray<RetainedStatePublicationRecoveryCleanupEvidence>
            cleanupRecords,
        string inventoryDigest,
        long observedAtUnixSeconds)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            candidate,
            records,
            currentAcceptance,
            currentAcceptancePublicationReceipt,
            currentAcceptanceCandidateObjectIdentity,
            currentAcceptedPublication,
            anchors,
            cleanupRecords,
            inventoryDigest,
            observedAtUnixSeconds);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref candidate, null)?.Dispose();
        var current = records;
        records = default;
        if (!current.IsDefault)
        {
            foreach (var record in current)
            {
                record.Dispose();
            }
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal enum RetainedStateP5CleanupClassification
{
    StaleCandidateAbandonment,
    CompletedOpaqueRecord,
    CompletedOpaqueWriteAnchor,
}

internal sealed record RetainedStateP5CleanupDecision(
    RetainedStateP5CleanupClassification Classification,
    string ClassificationIdentity,
    string? MarkerEvidenceIdentity);

internal sealed class RetainedStateP5CleanupAuthorization : IDisposable
{
    private int usable = 1;

    private RetainedStateP5CleanupAuthorization(
        RetainedStateTransactionAuthority authority,
        RetainedStateP5CleanupDecision decision,
        ImmutableArray<OpaqueStoreObjectMetadata> targets,
        string inventoryDigest)
    {
        this.authority = authority;
        Decision = decision;
        Targets = targets;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal RetainedStateP5CleanupDecision Decision { get; }
    internal ImmutableArray<OpaqueStoreObjectMetadata> Targets { get; }
    internal string InventoryDigest { get; }

    internal bool TryConsume(RetainedStateTransactionAuthority value) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateP5CleanupAuthorization Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        RetainedStateP5CleanupDecision decision,
        ImmutableArray<OpaqueStoreObjectMetadata> targets,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(authority, decision, targets, inventoryDigest);
    }

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateP5CleanupRequest(
    RetainedStateP5CleanupAuthorization Authorization,
    long SemanticRequiredExpiresAtUnixSeconds);

internal sealed class RetainedStateCleanupAuthorization : IDisposable
{
    private int usable = 1;

    private RetainedStateCleanupAuthorization(
        RetainedStateTransactionAuthority authority,
        string terminalAcceptanceIdentity,
        ImmutableArray<RetainedStateCleanupTarget> targets,
        string inventoryDigest)
    {
        this.authority = authority;
        TerminalAcceptanceIdentity = terminalAcceptanceIdentity;
        Targets = targets;
        InventoryDigest = inventoryDigest;
    }

    private readonly RetainedStateTransactionAuthority authority;
    internal string TerminalAcceptanceIdentity { get; }
    internal ImmutableArray<RetainedStateCleanupTarget> Targets { get; }
    internal string InventoryDigest { get; }

    internal bool TryConsume(
        RetainedStateTransactionAuthority value,
        VerifiedRetainedStateAcceptance acceptance) =>
        ReferenceEquals(authority, value) &&
        value.IsLive &&
        acceptance.IsIssuedBy(value) &&
        StringComparer.Ordinal.Equals(
            TerminalAcceptanceIdentity,
            acceptance.AcceptanceReceiptIdentity) &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateCleanupAuthorization Create(
        object issuer,
        RetainedStateTransactionAuthority authority,
        string terminalAcceptanceIdentity,
        ImmutableArray<RetainedStateCleanupTarget> targets,
        string inventoryDigest)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        return new(
            authority,
            terminalAcceptanceIdentity,
            targets,
            inventoryDigest);
    }

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateCleanupRequest(
    VerifiedRetainedStateAcceptance Acceptance,
    RetainedStateCleanupAuthorization Authorization,
    long SemanticRequiredExpiresAtUnixSeconds);

internal sealed record RetainedStateCleanupResult(
    VerifiedRetainedStateAcceptance? Acceptance,
    bool Completed,
    string Code)
{
    internal bool AcceptanceRemainsVerified => Acceptance is not null;
}

internal sealed class RetainedStateOpaqueRecordSet : IDisposable
{
    private ImmutableArray<RetainedStateOpaqueRecord> records;

    private RetainedStateOpaqueRecordSet(
        ImmutableArray<RetainedStateOpaqueRecord> records) =>
        this.records = records;

    internal ImmutableArray<RetainedStateOpaqueRecord> Records => records;

    internal static RetainedStateOpaqueRecordSet Create(
        ImmutableArray<RetainedStateOpaqueRecord> records) => new(records);

    public void Dispose()
    {
        var current = records;
        records = [];
        foreach (var record in current)
        {
            record.Dispose();
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateKeyDependencyReport(
    ImmutableArray<LocatorRequiredDependency> RequiredDependencies,
    bool PreviousKeyMayRetire,
    string InventoryDigest);
