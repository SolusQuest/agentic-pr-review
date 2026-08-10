using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class LocatorRootFormat
{
    internal const string StoreName = "agentic-pr-review-state-root-v1";
    internal const string PlaintextMagic = "APRLRV01";
    internal const string EnvelopeMagic = "APRLSN01";
    internal const string AadPrefix = "APR-LOCATOR-AAD-1\0";
    internal const ushort Version = 1;
    internal const ushort Aes256GcmAlgorithm = 1;
    internal const int KeyBytes = 32;
    internal const int RootBytes = 32;
    internal const int DigestBytes = 32;
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int MaximumEnvelopeBytes = 64 * 1024;
    internal const int MaximumPhysicalSentinels = 8;
    internal const int MaximumReferences = MaximumPhysicalSentinels;
    internal const int MaximumCanonicalNameInputBytes = 2_048;
}

internal static class LocatorCodes
{
    internal const string Ready = "state_locator_ready";
    internal const string AccessDenied = "state_locator_access_denied";
    internal const string Invalid = "state_locator_invalid";
    internal const string Unavailable = "state_locator_unavailable";
    internal const string Conflict = "state_locator_conflict";
    internal const string KeyUnavailable = "state_locator_key_unavailable";
    internal const string AuthenticationFailed =
        "state_locator_authentication_failed";
    internal const string CleanupFailed = "state_locator_cleanup_failed";
}

internal sealed record LocatorArtifactIdentity(
    string ObjectId,
    string ArchiveSha256,
    string EnvelopeSha256);

internal sealed record LocatorRootSentinel(
    byte[] Root,
    ulong Generation,
    string WriterKeyId,
    long CreatedAtUnixSeconds,
    long RequiredExpiresAtUnixSeconds,
    ImmutableArray<LocatorArtifactIdentity> Predecessors,
    ImmutableArray<LocatorArtifactIdentity> Superseded);

internal sealed record LocatorPhysicalCandidate(
    OpaqueStoreObjectMetadata Metadata,
    LocatorRootSentinel Sentinel);

internal sealed record LocatorUnknownArtifact(
    OpaqueStoreObjectMetadata Metadata,
    string FailureCode);

internal enum LocatorCleanupStageKind
{
    NonAnchor,
    ChainAnchor,
}

internal sealed record LocatorCleanupStage(
    OpaqueStoreObjectMetadata Target,
    LocatorCleanupStageKind Kind);

internal sealed record LocatorSelection(
    LocatorPhysicalCandidate Head,
    ImmutableArray<LocatorCleanupStage> CleanupStages,
    int PhysicalCount)
{
    internal ImmutableArray<OpaqueStoreObjectMetadata> SafeToDelete { get; } =
        CleanupStages.Select(stage => stage.Target).ToImmutableArray();
}

internal enum LocatorCleanupMode
{
    GenerationZeroAbsenceAllowed,
    SuccessorRequiresFallback,
}

internal sealed record LocatorCleanupDebt(
    ImmutableArray<OpaqueStoreObjectMetadata> Objects,
    LocatorCleanupMode Mode,
    byte[] ExpectedRoot,
    ulong MinimumGeneration);

internal sealed record LocatorSelectionResult(
    string Code,
    bool IsAbsent,
    LocatorSelection? Selection,
    LocatorCleanupDebt? CleanupDebt)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LocatorCodes.Ready);

    internal bool RequiresCleanup =>
        Succeeded && CleanupDebt is not null;

    internal static LocatorSelectionResult Absent() =>
        new(LocatorCodes.Ready, IsAbsent: true, null, null);

    internal static LocatorSelectionResult Success(
        LocatorSelection selection) =>
        new(LocatorCodes.Ready, IsAbsent: false, selection, null);

    internal static LocatorSelectionResult Cleanup(
        LocatorCleanupDebt cleanupDebt) =>
        new(LocatorCodes.Ready, IsAbsent: false, null, cleanupDebt);

    internal static LocatorSelectionResult Fail(string code) =>
        new(code, IsAbsent: false, null, null);
}

internal sealed record LocatorRootResult(
    string Code,
    LocatorContext? Context)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LocatorCodes.Ready) &&
        Context is not null;

    internal static LocatorRootResult Success(LocatorContext context) =>
        new(LocatorCodes.Ready, context);

    internal static LocatorRootResult Fail(string code) => new(code, null);
}

internal enum LocatorDependencyKind
{
    RestrictedState,
    Transaction,
}

internal sealed record LocatorRequiredDependency(
    LocatorDependencyKind Kind,
    string KeyId,
    long ExpiresAtUnixSeconds);
