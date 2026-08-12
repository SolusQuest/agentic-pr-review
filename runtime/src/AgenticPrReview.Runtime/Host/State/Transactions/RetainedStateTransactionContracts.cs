using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

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
        Authority = authority;
        Run = run;
        Generation = generation;
        Publication = publication;
        this.canonicalGeneration = canonicalGeneration;
        Name = name;
        this.outerEnvelope = outerEnvelope;
        Header = header;
        LogicalGenerationIdentity = logicalGenerationIdentity;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal AgentRunRequest? Run { get; }
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
        if (!ReferenceEquals(Authority, authority) ||
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

    internal static RetainedStatePreparedCandidate Create(
        RetainedStateTransactionAuthority authority,
        AgentRunRequest run,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        OpaqueStoreName name,
        byte[] outerEnvelope,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity) =>
        new(
            authority,
            run,
            generation,
            publication,
            canonicalGeneration,
            name,
            outerEnvelope,
            header,
            logicalGenerationIdentity);

    internal static RetainedStatePreparedCandidate CreateRecovered(
        RetainedStateTransactionAuthority authority,
        StateGenerationRecordV1 generation,
        ValidatedPublicationPayloadV1 publication,
        byte[] canonicalGeneration,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        string logicalGenerationIdentity) =>
        new(
            authority,
            run: null,
            generation,
            publication,
            canonicalGeneration,
            name,
            outerEnvelope: [],
            header,
            logicalGenerationIdentity);

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
        Authority = authority;
        Prepared = prepared;
        Metadata = metadata;
        InventoryDigest = inventoryDigest;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal RetainedStatePreparedCandidate Prepared { get; }
    internal OpaqueStoreObjectMetadata Metadata { get; }
    internal string InventoryDigest { get; }

    internal static RetainedStatePersistedCandidate Create(
        RetainedStateTransactionAuthority authority,
        RetainedStatePreparedCandidate prepared,
        OpaqueStoreObjectMetadata metadata,
        string inventoryDigest) =>
        new(authority, prepared, metadata, inventoryDigest);

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
        long observedAtUnixSeconds)
    {
        Authority = authority;
        Candidate = candidate;
        SelectedLineage = selectedLineage;
        InventoryDigest = inventoryDigest;
        ObservedAtUnixSeconds = observedAtUnixSeconds;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal SelectedLineageSnapshot SelectedLineage { get; }
    internal string InventoryDigest { get; }
    internal long ObservedAtUnixSeconds { get; }
    internal bool IsUsable => Volatile.Read(ref usable) == 1;

    internal bool TryConsume(RetainedStateTransactionAuthority authority) =>
        ReferenceEquals(Authority, authority) &&
        authority.IsLive &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    internal static RetainedStateOwnership Create(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        SelectedLineageSnapshot selectedLineage,
        string inventoryDigest,
        long observedAtUnixSeconds) =>
        new(
            authority,
            candidate,
            selectedLineage,
            inventoryDigest,
            observedAtUnixSeconds);

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateOpaqueWriteRequest(
    StateObjectClass ObjectClass,
    ImmutableArray<byte> Payload,
    string? PredecessorIdentity,
    string? SuccessorIdentity,
    long SemanticRequiredExpiresAtUnixSeconds);

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
        Authority = authority;
        ObjectClass = objectClass;
        Metadata = metadata;
        Header = header;
        this.payload = payload;
        InventoryDigest = inventoryDigest;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
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
        if (!ReferenceEquals(Authority, authority) ||
            !authority.IsLive ||
            current is null)
        {
            return false;
        }

        value = ImmutableArray.CreateRange(current);
        return true;
    }

    internal bool MatchesAuthenticated(
        RetainedStateTransactionAuthority authority,
        AuthenticatedStateObject value)
    {
        var current = Volatile.Read(ref payload);
        return ReferenceEquals(Authority, authority) &&
            authority.IsLive &&
            current is not null &&
            value.Header.ObjectClass == ObjectClass &&
            value.Metadata == Metadata &&
            value.Header == Header &&
            value.Payload.AsSpan().SequenceEqual(current);
    }

    internal static RetainedStateOpaqueRecord Create(
        RetainedStateTransactionAuthority authority,
        StateObjectClass objectClass,
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        byte[] payload,
        string inventoryDigest) =>
        new(
            authority,
            objectClass,
            metadata,
            header,
            payload,
            inventoryDigest);

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

internal sealed class RetainedStateAcceptanceEvidence : IDisposable
{
    private readonly object gate = new();
    private RetainedStateAcceptanceAttempt? attempt;
    private RetainedStateAcceptanceEvidence(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        ExactHeadRevalidationResult exactHead,
        string inventoryDigest)
    {
        Authority = authority;
        Candidate = candidate;
        Receipt = receipt;
        ExactHead = exactHead;
        InventoryDigest = inventoryDigest;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal RetainedStatePersistedCandidate Candidate { get; }
    internal StickyCommentPublisher.StickyPublicationReceipt Receipt { get; }
    internal ExactHeadRevalidationResult ExactHead { get; }
    internal string InventoryDigest { get; }

    internal static RetainedStateAcceptanceEvidence Create(
        RetainedStateTransactionAuthority authority,
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        ExactHeadRevalidationResult exactHead,
        string inventoryDigest) =>
        new(authority, candidate, receipt, exactHead, inventoryDigest);

    internal RetainedStateAcceptanceAttempt? GetAttempt(
        RetainedStateTransactionAuthority authority)
    {
        lock (gate)
        {
            return ReferenceEquals(Authority, authority) ? attempt : null;
        }
    }

    internal bool TrySetAttempt(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptanceAttempt value)
    {
        lock (gate)
        {
            if (!ReferenceEquals(Authority, authority) || attempt is not null)
            {
                return false;
            }

            attempt = value;
            return true;
        }
    }

    internal bool TryClearAttempt(
        RetainedStateTransactionAuthority authority,
        RetainedStateAcceptanceAttempt value)
    {
        lock (gate)
        {
            if (!ReferenceEquals(Authority, authority) ||
                !ReferenceEquals(attempt, value))
            {
                return false;
            }

            attempt = null;
        }

        value.Dispose();
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            attempt?.Dispose();
            attempt = null;
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateAcceptanceAttempt : IDisposable
{
    private byte[]? receiptBytes;
    private byte[]? envelopeBytes;

    internal RetainedStateAcceptanceAttempt(
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        AcceptanceReceiptV1 receipt,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        byte[] receiptBytes,
        byte[] envelopeBytes)
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
    }

    internal long AcceptedAtUnixSeconds { get; }
    internal long LogicalExpiresAtUnixSeconds { get; }
    internal long RequiredPlatformExpiresAtUnixSeconds { get; }
    internal AcceptanceReceiptV1 Receipt { get; }
    internal OpaqueStoreName Name { get; }
    internal StateControlHeaderV1 Header { get; }

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
        Authority = authority;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        AcceptanceReceiptIdentity = acceptanceReceiptIdentity;
        ReceiptMetadata = receiptMetadata;
        AcceptedAtUnixSeconds = acceptedAtUnixSeconds;
        LogicalExpiresAtUnixSeconds = logicalExpiresAtUnixSeconds;
        InventoryDigest = inventoryDigest;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal string LogicalGenerationIdentity { get; }
    internal string AcceptanceReceiptIdentity { get; }
    internal OpaqueStoreObjectMetadata ReceiptMetadata { get; }
    internal long AcceptedAtUnixSeconds { get; }
    internal long LogicalExpiresAtUnixSeconds { get; }
    internal string InventoryDigest { get; }

    internal static VerifiedRetainedStateAcceptance Create(
        RetainedStateTransactionAuthority authority,
        string logicalGenerationIdentity,
        string acceptanceReceiptIdentity,
        OpaqueStoreObjectMetadata receiptMetadata,
        long acceptedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        string inventoryDigest) =>
        new(
            authority,
            logicalGenerationIdentity,
            acceptanceReceiptIdentity,
            receiptMetadata,
            acceptedAtUnixSeconds,
            logicalExpiresAtUnixSeconds,
            inventoryDigest);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record RetainedStateCleanupTarget(
    OpaqueStoreObjectMetadata Metadata);

internal sealed record RetainedStateCleanupRequest(
    VerifiedRetainedStateAcceptance Acceptance,
    ImmutableArray<RetainedStateCleanupTarget> Targets,
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
