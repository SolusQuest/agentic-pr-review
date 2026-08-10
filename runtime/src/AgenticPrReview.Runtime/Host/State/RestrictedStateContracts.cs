using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State;

internal static class RestrictedStateFormat
{
    internal const string StateClass = "workflow-restricted-completed-review";
    internal const string Magic = "APRAST01";
    internal const ushort Version = 1;
    internal const ushort Aes256GcmAlgorithm = 1;
    internal const string Namespace = "agentic-pr-review/agent-session";
    internal const string Discriminator = "r2-current-1";
    internal const string AadPrefix = "APR-STATE-AAD-1\0";
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int KeyBytes = 32;
    internal const int MaximumBindingBytes = 2_048;
    internal const long MaximumReviewTarget = long.MaxValue;
    internal const long MaximumGeneration = long.MaxValue;
    internal const long MaximumUnixSeconds = 253_402_300_799;
    internal const long MaximumRetentionSeconds =
        StateRetentionRequirements.LogicalWindowSeconds;
}

internal enum StateAction
{
    Authorized,
    Enumerated,
    Prepared,
    Restored,
    Bootstrap,
    Reset,
    Accepted,
    Idempotent,
    HandoffReady,
    Denied,
    Failed,
}

internal static class RestrictedStateCodes
{
    internal const string Authorized = "state_authorized";
    internal const string AccessDenied = "state_access_denied";
    internal const string Enumerated = "state_enumerated";
    internal const string EnumerationInvalid = "state_enumeration_invalid";
    internal const string Prepared = "state_prepared";
    internal const string Absent = "state_absent";
    internal const string ExplicitMissing = "state_explicit_missing";
    internal const string Restored = "state_restored";
    internal const string Accepted = "state_accepted";
    internal const string Idempotent = "state_idempotent";
    internal const string Reset = "state_reset";
    internal const string HandoffReady = "state_handoff_ready";
    internal const string Cancelled = "state_cancelled";
    internal const string CurrentMissing = "state_current_missing";
    internal const string Expired = "state_expired";
    internal const string EnvelopeInvalid = "state_envelope_invalid";
    internal const string KeyUnavailable = "state_key_unavailable";
    internal const string AuthenticationFailed = "state_authentication_failed";
    internal const string LineageMismatch = "state_lineage_mismatch";
    internal const string ReplayRejected = "state_replay_rejected";
    internal const string Conflict = "state_conflict";
    internal const string CleanupFailed = "state_cleanup_failed";
    internal const string IoFailed = "state_io_failed";

    internal static ImmutableArray<string> All { get; } =
    [
        Authorized,
        AccessDenied,
        Enumerated,
        EnumerationInvalid,
        Prepared,
        Absent,
        ExplicitMissing,
        Restored,
        Accepted,
        Idempotent,
        Reset,
        HandoffReady,
        Cancelled,
        CurrentMissing,
        Expired,
        EnvelopeInvalid,
        KeyUnavailable,
        AuthenticationFailed,
        LineageMismatch,
        ReplayRejected,
        Conflict,
        CleanupFailed,
        IoFailed,
    ];
}

internal sealed record RestrictedStateScope(
    string RepositoryId,
    string WorkflowIdentity,
    long ReviewTarget,
    string SessionId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string PolicySha256,
    string LimitsSha256,
    string ToolsetSha256,
    string BuildId);

internal sealed record RestrictedStateAccessRequest(
    RestrictedStateScope RequestedScope,
    RestrictedStateScope AuthorizedScope,
    bool IsTrustedWorkflow,
    bool IsSameRepository,
    bool IsForkOrigin);

internal sealed class AuthorizedStateAccess
{
    private AuthorizedStateAccess(RestrictedStateScope scope)
    {
        Scope = scope;
    }

    internal RestrictedStateScope Scope { get; }

    internal static StateResult Authorize(
        RestrictedStateAccessRequest request,
        out AuthorizedStateAccess? access)
    {
        access = null;
        if (request is null ||
            request.RequestedScope is null ||
            request.AuthorizedScope is null ||
            !request.IsTrustedWorkflow ||
            !request.IsSameRepository ||
            request.IsForkOrigin ||
            !RestrictedStateValidation.IsValidScope(request.RequestedScope) ||
            request.RequestedScope != request.AuthorizedScope)
        {
            return StateResult.Create(
                StateAction.Denied,
                RestrictedStateCodes.AccessDenied);
        }

        access = new AuthorizedStateAccess(request.RequestedScope);
        return StateResult.Create(
            StateAction.Authorized,
            RestrictedStateCodes.Authorized);
    }
}

internal sealed record RestrictedStateBinding(
    RestrictedStateScope Scope,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    long AcceptedAtUnixSeconds,
    long ExpiresAtUnixSeconds);

internal sealed record AcceptedLineage(
    RestrictedStateScope Scope,
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    string? ExpectedPredecessorEnvelopeSha256,
    long AcceptedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    bool TransitionAuthorized);

internal sealed record PreparedStateReceipt(
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    string ObjectIdentity);

internal sealed record RestrictedStateCandidate(
    RestrictedStateBinding Binding,
    string SessionSha256,
    string EnvelopeSha256,
    string ObjectIdentity,
    byte[] Envelope);

internal sealed record RestrictedStateSnapshot(
    ImmutableArray<RestrictedStateCandidate> Accepted,
    RestrictedStateCandidate? Staging)
{
    internal static RestrictedStateSnapshot Empty { get; } =
        new([], null);
}

internal sealed record RestrictedStateSnapshotVersion(
    string Sha256,
    bool Exists)
{
    internal static RestrictedStateSnapshotVersion Absent { get; } =
        new(string.Empty, false);
}

internal enum RestrictedStateStoreFailure
{
    None,
    Cancelled,
    Invalid,
    Conflict,
    Cleanup,
    KeyUnavailable,
    Authentication,
    Io,
}

internal sealed record RestrictedStateStoreRead(
    RestrictedStateStoreFailure Failure,
    RestrictedStateSnapshot? Snapshot,
    RestrictedStateSnapshotVersion? Version)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None &&
        Snapshot is not null &&
        Version is not null;
}

internal sealed record RestrictedStateStoreWrite(
    RestrictedStateStoreFailure Failure,
    RestrictedStateSnapshotVersion? Version,
    bool Committed)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None &&
        Version is not null &&
        Committed;
}

internal sealed record RestrictedStateRawVersion(
    string Identity,
    long Length,
    bool Exists)
{
    internal static RestrictedStateRawVersion Absent { get; } =
        new(string.Empty, 0, false);
}

internal sealed record RestrictedStateStoreRawRead(
    RestrictedStateStoreFailure Failure,
    RestrictedStateRawVersion? Version)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None &&
        Version is not null;
}

internal sealed class RestrictedStateKey : IDisposable
{
    private byte[]? material;

    internal RestrictedStateKey(string keyId, ReadOnlySpan<byte> keyMaterial)
    {
        KeyId = keyId;
        material = keyMaterial.ToArray();
    }

    internal string KeyId { get; }

    internal bool TryCopyMaterial(Span<byte> destination)
    {
        var current = material;
        if (current is null || current.Length != destination.Length)
        {
            return false;
        }

        current.CopyTo(destination);
        return true;
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref material, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }
}

internal interface IRestrictedStateKeyResolver
{
    bool TryGetCurrentWriteKey(
        AuthorizedStateAccess access,
        out RestrictedStateKey? key);

    bool TryGetApprovedReadKey(
        AuthorizedStateAccess access,
        string keyId,
        long expiresAtUnixSeconds,
        out RestrictedStateKey? key);
}

internal interface IRestrictedStateSessionAdmission
{
    RestrictedStateSessionAdmissionResult Admit(
        AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext,
        RestrictedStateSessionAdmissionContext context);
}

internal sealed record RestrictedStateSessionAdmissionContext(
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    AgentSessionStateAdmissionContext SessionContext);

internal sealed record RestrictedStateAdmittedSession(
    byte[] Plaintext,
    string SessionSha256,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    AgentSessionStateAdmittedValue Value);

internal sealed record RestrictedStateSessionAdmissionResult(
    RestrictedStateAdmittedSession? Session)
{
    internal bool Succeeded => Session is not null;

    internal static RestrictedStateSessionAdmissionResult Failure() =>
        new((RestrictedStateAdmittedSession?)null);

    internal static RestrictedStateSessionAdmissionResult Success(
        RestrictedStateAdmittedSession session) =>
        new(session);
}

internal enum RestrictedStateRestoreIntent
{
    Automatic,
    Explicit,
}

internal enum RestrictedStateLocatorFamily
{
    Absent,
    NonCurrent,
    Current,
}

internal sealed record RestrictedStateRestoreRequest(
    RestrictedStateLocatorFamily LocatorFamily,
    RestrictedStateRestoreIntent Intent,
    AcceptedLineage? Lineage,
    RestrictedStateSessionAdmissionContext SessionContext);

internal sealed record RestrictedStatePrepareRequest(
    AcceptedLineage? Lineage,
    ReadOnlyMemory<byte> Plaintext,
    RestrictedStateSessionAdmissionContext SessionContext);

internal sealed record StateResult(
    StateAction Action,
    string Code,
    long? Generation,
    string? SessionSha256,
    string? EnvelopeSha256)
{
    internal static StateResult Create(
        StateAction action,
        string code,
        long? generation = null,
        string? sessionSha256 = null,
        string? envelopeSha256 = null) =>
        new(action, code, generation, sessionSha256, envelopeSha256);
}

internal sealed record RestrictedStatePrepareResult(
    StateResult Result,
    PreparedStateReceipt? Receipt);

internal sealed record RestrictedStateRestoreResult(
    StateResult Result,
    RestrictedStateAdmittedSession? Session);

internal sealed record RestrictedStateEnumerationResult(
    StateResult Result,
    ImmutableArray<RestrictedStateCandidate> Candidates);

internal sealed record RestrictedStateHandoffReceipt(
    string ScopeIdentity,
    string SnapshotSha256,
    long Generation,
    string EnvelopeSha256);

internal sealed record RestrictedStateHandoffResult(
    StateResult Result,
    RestrictedStateHandoffReceipt? Receipt);
