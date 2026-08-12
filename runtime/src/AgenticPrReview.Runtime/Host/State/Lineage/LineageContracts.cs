using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageFormat
{
    internal const string HeaderMagic = "APRSCH01";
    internal const string EnvelopeMagic = "APRSCE01";
    internal const string HeadMagic = "APRSLH01";
    internal const string IntentMagic = "APRSTI01";
    internal const string EnvelopeAadPrefix = "APR-SCOPED-AAD-1\0";
    internal const string BaseScopeDomain = "apr.base-scope.s4";
    internal const string InitialEpochDomain = "apr.epoch.initial.s4";
    internal const string ResetEpochDomain = "apr.epoch.reset.s4";
    internal const string ExpiryEpochDomain = "apr.epoch.expiry.s4";
    internal const string SessionDomain = "apr.session-id.s4";
    internal const string ObjectIdentityDomain = "apr.object-identity.s4";
    internal const string CorrelationDomain = "apr.state-correlation.s4";
    internal const ushort Version = 1;
    internal const ushort Aes256GcmAlgorithm = 1;
    internal const int DigestBytes = 32;
    internal const int KeyBytes = 32;
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int MaximumHeaderBytes = 16 * 1024;
    internal const int MaximumPayloadBytes = 1024 * 1024;
    internal const int MaximumReaderPayloadBytes = 1_500_000;
    internal const int MaximumEnvelopeBytes = MaximumHeaderBytes +
        MaximumReaderPayloadBytes + 1024;
    internal const int MaximumPhysicalPerClass = 8;
    internal const int MaximumScopedObjects = 9 * MaximumPhysicalPerClass;
    internal const int MaximumTextBytes = 512;
    internal const int MaximumRunIdentityBytes = 256;
    internal const int MaximumEvidenceObjects = MaximumScopedObjects;
    internal const long MaximumJavaScriptInteger = 9_007_199_254_740_991;

    internal static bool IsPayloadLengthAllowed(
        StateObjectClass objectClass,
        int payloadLength) =>
        payloadLength >= 0 &&
        payloadLength <= (objectClass is
            StateObjectClass.Candidate or StateObjectClass.Acceptance
                ? MaximumReaderPayloadBytes
                : MaximumPayloadBytes);
}

internal static class LineageCodes
{
    internal const string Ready = "state_lineage_ready";
    internal const string AccessDenied = "state_lineage_access_denied";
    internal const string Invalid = "state_lineage_invalid";
    internal const string Unavailable = "state_lineage_unavailable";
    internal const string Conflict = "state_lineage_conflict";
    internal const string KeyUnavailable = "state_lineage_key_unavailable";
    internal const string AuthenticationFailed =
        "state_lineage_authentication_failed";
    internal const string CleanupFailed = "state_lineage_cleanup_failed";
    internal const string RetentionFailed = "state_lineage_retention_failed";
}

internal enum StateObjectClass
{
    LocatorRoot,
    LineageHead,
    Candidate,
    PublicationIntent,
    Acceptance,
    PublicationFailure,
    Abandonment,
    Reset,
    ExpiryTransition,
    Cleanup,
}

internal static class StateObjectClasses
{
    internal static ImmutableArray<StateObjectClass> All { get; } =
    [
        StateObjectClass.LocatorRoot,
        StateObjectClass.LineageHead,
        StateObjectClass.Candidate,
        StateObjectClass.PublicationIntent,
        StateObjectClass.Acceptance,
        StateObjectClass.PublicationFailure,
        StateObjectClass.Abandonment,
        StateObjectClass.Reset,
        StateObjectClass.ExpiryTransition,
        StateObjectClass.Cleanup,
    ];

    internal static ImmutableArray<StateObjectClass> Scoped { get; } =
        All.Where(value => value != StateObjectClass.LocatorRoot)
            .ToImmutableArray();

    internal static string ToWireName(StateObjectClass value) =>
        value switch
        {
            StateObjectClass.LocatorRoot => "locator_root",
            StateObjectClass.LineageHead => "lineage_head",
            StateObjectClass.Candidate => "candidate",
            StateObjectClass.PublicationIntent => "publication_intent",
            StateObjectClass.Acceptance => "acceptance",
            StateObjectClass.PublicationFailure => "publication_failure",
            StateObjectClass.Abandonment => "abandonment",
            StateObjectClass.Reset => "reset",
            StateObjectClass.ExpiryTransition => "expiry_transition",
            StateObjectClass.Cleanup => "cleanup",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    internal static bool TryParse(string value, out StateObjectClass result)
    {
        foreach (var candidate in All.Where(candidate =>
            StringComparer.Ordinal.Equals(ToWireName(candidate), value)))
        {
            result = candidate;
            return true;
        }

        result = default;
        return false;
    }
}

internal sealed record LineageBaseScope(
    string RepositoryId,
    string TrustedWorkflowIdentity,
    string TrustedSourceIdentity,
    long PullRequestNumber,
    string Provider,
    string Model,
    string Adapter,
    string ConfigSha256,
    string InstructionSha256,
    string ToolsetSha256,
    string LimitsSha256,
    string PayloadBuildIdentity);

internal sealed record ReviewedTransitionFacts(
    string BaseSha,
    string HeadSha);

internal sealed record StateControlHeaderDraft(
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    StateObjectClass ObjectClass,
    string? PredecessorIdentity,
    string? SuccessorIdentity,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    long CreatedAtUnixSeconds,
    long LogicalExpiresAtUnixSeconds,
    long RequiredPlatformExpiresAtUnixSeconds);

internal sealed record StateControlHeaderV1(
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    StateObjectClass ObjectClass,
    string KeyId,
    string ObjectIdentity,
    string? PredecessorIdentity,
    string? SuccessorIdentity,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    long CreatedAtUnixSeconds,
    long LogicalExpiresAtUnixSeconds,
    long RequiredPlatformExpiresAtUnixSeconds);

internal sealed record AuthenticatedStateObject(
    OpaqueStoreObjectMetadata Metadata,
    StateControlHeaderV1 Header,
    byte[] Payload);

internal sealed record UnknownStateObject(
    OpaqueStoreObjectMetadata Metadata,
    string FailureCode);

internal enum LineageTransitionKind
{
    Initial,
    Reset,
    Expiry,
}

internal sealed record LineageArtifactEvidence(
    string Name,
    string ObjectId,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    string ArchiveSha256,
    string EncryptedObjectSha256,
    long ExpiresAtUnixSeconds,
    long Size);

internal sealed record LineageHeadV1(
    LineageTransitionKind Transition,
    ulong Ordinal,
    ReviewedTransitionFacts Reviewed,
    string? PreviousEpoch,
    string? PreviousHeadIdentity,
    string? TransitionEvidenceIdentity,
    long? ExpiryBoundaryUnixSeconds,
    ImmutableArray<LineageArtifactEvidence> PhysicalPredecessors,
    ImmutableArray<LineageArtifactEvidence> PhysicalSuperseded,
    ImmutableArray<LineageArtifactEvidence> Superseded,
    ImmutableArray<LineageArtifactEvidence> CompletedCleanup,
    string? ResetAuthorityRunIdentity = null,
    long? ResetAuthorityRunAttempt = null);

internal sealed record LineageHeadCandidate(
    OpaqueStoreObjectMetadata Metadata,
    StateControlHeaderV1 Header,
    LineageHeadV1 Head);

internal sealed record LineageHeadSelection(
    LineageHeadCandidate Head,
    LineageHeadCandidate? ImmediatePredecessor,
    ImmutableArray<OpaqueStoreObjectMetadata> EquivalentPhysical,
    ImmutableArray<OpaqueStoreObjectMetadata> SafeNonAnchors,
    ImmutableArray<OpaqueStoreObjectMetadata> SafeChainAnchors,
    int PhysicalCount)
{
    internal ImmutableArray<OpaqueStoreObjectMetadata> SafeToDelete =>
        SafeNonAnchors.AddRange(SafeChainAnchors);
}

internal sealed record LineageSelectionResult(
    string Code,
    bool IsAbsent,
    LineageHeadSelection? Selection)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready);

    internal static LineageSelectionResult Absent() =>
        new(LineageCodes.Ready, IsAbsent: true, null);

    internal static LineageSelectionResult Success(
        LineageHeadSelection selection) =>
        new(LineageCodes.Ready, IsAbsent: false, selection);

    internal static LineageSelectionResult Fail(string code) =>
        new(code, IsAbsent: false, null);
}

internal enum LineageTransitionIntentKind
{
    Reset,
    Expiry,
}

internal sealed record LineageTransitionIntentV1(
    LineageTransitionIntentKind Kind,
    string PriorHeadIdentity,
    string PriorEpoch,
    string TransitionEvidenceIdentity,
    long? ExpiryBoundaryUnixSeconds,
    ReviewedTransitionFacts Reviewed,
    string InventorySha256,
    ImmutableArray<LineageArtifactEvidence> Targets,
    string? ResetAuthorityRunIdentity = null,
    long? ResetAuthorityRunAttempt = null);

internal sealed record ScopedStateInventorySnapshot(
    ImmutableDictionary<StateObjectClass, OpaqueStoreName> Names,
    ImmutableArray<AuthenticatedStateObject> Authenticated,
    ImmutableArray<AuthenticatedStateObject> UnderRetained,
    ImmutableArray<UnknownStateObject> Unknown,
    int PhysicalCount);

internal sealed record LineageResolveRequest(
    AuthorizedLocatorAccess Access,
    LineageBaseScope BaseScope,
    ReviewedTransitionFacts Reviewed,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    long RequiredLogicalExpiresAtUnixSeconds,
    AuthorizedLineageReset? Reset);

internal sealed record SelectedLineageSnapshot(
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    string LineageHeadIdentity,
    LineageTransitionKind Transition);

internal sealed record LineageResolveResult(
    string Code,
    SelectedLineageContext? Context)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        Context is not null;

    internal static LineageResolveResult Success(
        SelectedLineageContext context) =>
        new(LineageCodes.Ready, context);

    internal static LineageResolveResult Fail(string code) =>
        new(code, null);
}

internal sealed record LineageReadOnlyObservationResult(
    string Code,
    LineageReadOnlyObservationContext? Context)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        Context is not null;

    internal static LineageReadOnlyObservationResult Success(
        LineageReadOnlyObservationContext context) =>
        new(LineageCodes.Ready, context);

    internal static LineageReadOnlyObservationResult Fail(string code) =>
        new(code, null);
}

internal enum LineageInterruptedTransitionRecoveryPhase
{
    None,
    PendingIntact,
    RecoveredSuccessor,
}

internal sealed record LineageInterruptedTransitionRecoveryResult(
    string Code,
    LineageInterruptedTransitionRecoveryPhase Phase,
    SelectedLineageContext? Context)
{
    internal bool Recovered =>
        Phase == LineageInterruptedTransitionRecoveryPhase.RecoveredSuccessor;
    internal bool RequiresTypedExpiry =>
        Phase == LineageInterruptedTransitionRecoveryPhase.PendingIntact;

    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        (!Recovered || Context is not null);

    internal static LineageInterruptedTransitionRecoveryResult None() =>
        new(
            LineageCodes.Ready,
            LineageInterruptedTransitionRecoveryPhase.None,
            null);

    internal static LineageInterruptedTransitionRecoveryResult
        AwaitingTypedExpiry() =>
        new(
            LineageCodes.Ready,
            LineageInterruptedTransitionRecoveryPhase.PendingIntact,
            null);

    internal static LineageInterruptedTransitionRecoveryResult Success(
        SelectedLineageContext context) =>
        new(
            LineageCodes.Ready,
            LineageInterruptedTransitionRecoveryPhase.RecoveredSuccessor,
            context);

    internal static LineageInterruptedTransitionRecoveryResult Fail(
        string code) =>
        new(
            code,
            LineageInterruptedTransitionRecoveryPhase.None,
            null);
}

internal sealed class LineageReadOnlyObservationContext : IDisposable
{
    private ScopedStateInventorySnapshot? snapshot;

    internal LineageReadOnlyObservationContext(
        ScopedStateInventorySnapshot snapshot,
        LineageSelectionResult selection,
        string baseScopeDigest,
        string currentKeyId,
        string inventoryDigest,
        long requiredPlatformExpiresAtUnixSeconds)
    {
        this.snapshot = snapshot;
        Selection = selection;
        BaseScopeDigest = baseScopeDigest;
        CurrentKeyId = currentKeyId;
        InventoryDigest = inventoryDigest;
        RequiredPlatformExpiresAtUnixSeconds =
            requiredPlatformExpiresAtUnixSeconds;
    }

    internal ScopedStateInventorySnapshot? Snapshot =>
        Volatile.Read(ref snapshot);
    internal LineageSelectionResult Selection { get; }
    internal string BaseScopeDigest { get; }
    internal string CurrentKeyId { get; }
    internal string InventoryDigest { get; }
    internal long RequiredPlatformExpiresAtUnixSeconds { get; }

    internal ScopedStateInventorySnapshot? DetachSnapshot() =>
        Interlocked.Exchange(ref snapshot, null);

    public void Dispose() =>
        ScopedStateInventory.Clear(
            Interlocked.Exchange(ref snapshot, null));

    public override string ToString() =>
        nameof(LineageReadOnlyObservationContext);
}

internal sealed class SelectedLineageContext : IDisposable
{
    private readonly AuthorizedLocatorAccess authority;
    private readonly string repositoryId;
    private SelectedLineageSnapshot? snapshot;

    internal SelectedLineageContext(
        AuthorizedLocatorAccess authority,
        string repositoryId,
        SelectedLineageSnapshot snapshot)
    {
        this.authority = authority;
        this.repositoryId = repositoryId;
        this.snapshot = snapshot;
    }

    internal bool TryGetSnapshot(
        AuthorizedLocatorAccess? access,
        out SelectedLineageSnapshot? value)
    {
        value = null;
        var current = Volatile.Read(ref snapshot);
        if (current is null || !authority.Allows(access, repositoryId))
        {
            return false;
        }

        value = current;
        return true;
    }

    public void Dispose() => Interlocked.Exchange(ref snapshot, null);

    public override string ToString() => nameof(SelectedLineageContext);
}

internal static class LineageValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsValid(LineageBaseScope? value) =>
        value is not null &&
        IsText(value.RepositoryId) &&
        IsText(value.TrustedWorkflowIdentity) &&
        IsText(value.TrustedSourceIdentity) &&
        value.PullRequestNumber is > 0 and <=
            LineageFormat.MaximumJavaScriptInteger &&
        IsText(value.Provider) &&
        IsText(value.Model) &&
        IsText(value.Adapter) &&
        IsSha256(value.ConfigSha256) &&
        IsSha256(value.InstructionSha256) &&
        IsSha256(value.ToolsetSha256) &&
        IsSha256(value.LimitsSha256) &&
        IsText(value.PayloadBuildIdentity);

    internal static bool IsValid(ReviewedTransitionFacts? value) =>
        value is not null && IsGitSha(value.BaseSha) && IsGitSha(value.HeadSha);

    internal static bool IsValid(StateControlHeaderDraft? value) =>
        value is not null &&
        IsSha256(value.BaseScopeDigest) &&
        IsSha256(value.Epoch) &&
        IsSha256(value.SessionId) &&
        value.ObjectClass != StateObjectClass.LocatorRoot &&
        IsOptionalSha256(value.PredecessorIdentity) &&
        IsOptionalSha256(value.SuccessorIdentity) &&
        IsText(value.ProducingRunIdentity, LineageFormat.MaximumRunIdentityBytes) &&
        value.ProducingRunAttempt >= 0 &&
        IsTime(value.CreatedAtUnixSeconds) &&
        IsTime(value.LogicalExpiresAtUnixSeconds) &&
        IsTime(value.RequiredPlatformExpiresAtUnixSeconds) &&
        value.CreatedAtUnixSeconds <= value.LogicalExpiresAtUnixSeconds &&
        value.LogicalExpiresAtUnixSeconds <=
            value.RequiredPlatformExpiresAtUnixSeconds;

    internal static bool IsValid(StateControlHeaderV1? value) =>
        value is not null &&
        IsValid(new StateControlHeaderDraft(
            value.BaseScopeDigest,
            value.Epoch,
            value.SessionId,
            value.ObjectClass,
            value.PredecessorIdentity,
            value.SuccessorIdentity,
            value.ProducingRunIdentity,
            value.ProducingRunAttempt,
            value.CreatedAtUnixSeconds,
            value.LogicalExpiresAtUnixSeconds,
            value.RequiredPlatformExpiresAtUnixSeconds)) &&
        IsSha256(value.KeyId) &&
        IsSha256(value.ObjectIdentity);

    internal static bool IsValid(LineageArtifactEvidence? value) =>
        value is not null &&
        IsText(value.Name, OpaqueStoreLimits.MaximumNameBytes) &&
        IsText(value.ObjectId, OpaqueStoreLimits.MaximumIdentityBytes) &&
        IsText(
            value.ProducingRunIdentity,
            OpaqueStoreLimits.MaximumIdentityBytes) &&
        value.ProducingRunAttempt >= 0 &&
        IsSha256(value.ArchiveSha256) &&
        IsSha256(value.EncryptedObjectSha256) &&
        IsTime(value.ExpiresAtUnixSeconds) &&
        value.Size is > 0 and <= OpaqueStoreLimits.MaximumObjectBytes;

    internal static bool IsSha256(string? value) =>
        value is { Length: LineageFormat.DigestBytes * 2 } &&
        value.All(character => character is >= '0' and <= '9' or
            >= 'a' and <= 'f');

    internal static bool IsOptionalSha256(string? value) =>
        value is null || IsSha256(value);

    internal static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(character => character is >= '0' and <= '9' or
            >= 'a' and <= 'f');

    internal static bool IsTime(long value) =>
        value is >= 0 and <= RestrictedStateFormat.MaximumUnixSeconds;

    internal static bool IsText(
        string? value,
        int maximumBytes = LineageFormat.MaximumTextBytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) is > 0 &&
                StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
