using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProofScenario
{
    internal const string RepositoryId = "solusquest/agentic-pr-review";
    internal const long ReviewTarget = 88;
    internal const string WorkflowIdentity = "runtime-agent-proof";
    internal const string BuildId = "r2-agent-proof";
    internal const string ProviderId = "synthetic-loopback";
    internal const string ModelId = "synthetic-r2";
    internal const string AdapterId = "minimal-http-r2";
    internal const string SessionId = "issue88";
    internal const string ReviewedPath = "reviewed/fact.txt";
    internal const string PriorOnlyFact =
        "NEBULA-7F4C-91A2-R2-PRIOR-ONLY";
    internal const string ContinuationReadable =
        "Synthetic public reasoning marker.";
    internal const string ContinuationOpaque =
        "opaque-r2-7f4c91a2";
    internal const string ContinuationFraming =
        "{\"kind\":\"synthetic-r2\",\"ordinal\":1}";
    internal const long Now = 1_900_000_000;

    private static readonly byte[] Policy =
        "synthetic public R2 agent proof policy"u8.ToArray();

    internal static ReviewedIdentity BootstrapIdentity() => new(
        RepositoryId,
        ReviewTarget,
        new string('1', 40),
        new string('2', 40));

    internal static ReviewedIdentity ContinueIdentity() => new(
        RepositoryId,
        ReviewTarget,
        new string('2', 40),
        new string('3', 40));

    internal static ReviewedDiffSource BootstrapDiffSource(
        ReviewedIdentity identity) => new(
        identity,
        ReviewedPath,
        previousPath: null,
        "modified",
        sourceTruncated: false,
        [
            new ReviewedDiffHunk(
                oldStart: 0,
                oldCount: 0,
                newStart: 1,
                newCount: 1,
                [
                    new ReviewedDiffLine(
                        "addition",
                        OldLine: null,
                        NewLine: 1,
                        PriorOnlyFact),
                ]),
        ]);

    internal static ReviewedChangedFile BootstrapChangedFile(
        ReviewedDiffSource source) => new(
        ReviewedPath,
        PreviousPath: null,
        "modified",
        Additions: 1,
        Deletions: 0,
        Changes: 1,
        "available",
        source.PatchSha256,
        SourceTruncated: false);

    internal static AgentSessionTrustedRequest Trusted() => new(
        RepositoryId,
        ReviewTarget,
        WorkflowIdentity,
        Policy.ToArray(),
        BuildId,
        ProviderId,
        ModelId,
        AdapterId);

    internal static RestrictedStateScope Scope() => new(
        RepositoryId,
        WorkflowIdentity,
        ReviewTarget,
        SessionId,
        ProviderId,
        ModelId,
        AdapterId,
        AgentCanonical.HashRaw(Policy),
        AgentCanonical.LimitsSha256(),
        AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions),
        BuildId);

    internal static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    internal static string ScopeSha256(RestrictedStateScope scope) =>
        AgentCanonical.HashDomain(
            "apr.proof-scope.r2",
            RestrictedStateSnapshotCodec.WriteScopeIdentity(scope));
}

internal sealed class SyntheticContinuationCodec : IAgentContinuationCodec
{
    internal static SyntheticContinuationCodec Instance { get; } = new();

    public string CodecId => "synthetic-json";

    public string CodecDiscriminator => "current-1";

    public bool TryEncode(
        AgentContinuationCodecValue value,
        out AgentContinuationEncodedPayload? payload)
    {
        payload = null;
        if (value is null)
        {
            return false;
        }

        var bytes = ProofJson.Write(
            new ContinuationPayload(
                value.Readable,
                value.Opaque,
                value.Framing));
        payload = new AgentContinuationEncodedPayload("utf8", bytes);
        return true;
    }

    public bool TryDecode(
        string encoding,
        ReadOnlySpan<byte> payload,
        out AgentContinuationCodecValue? value)
    {
        value = null;
        if (!StringComparer.Ordinal.Equals(encoding, "utf8"))
        {
            return false;
        }

        var decoded = ProofJson.ReadContinuation(payload);
        if (decoded is null)
        {
            return false;
        }

        value = new AgentContinuationCodecValue(
            decoded.Readable,
            decoded.Opaque,
            decoded.Framing);
        return true;
    }
}

internal sealed class SyntheticStateKeyResolver(string scenario)
    : IRestrictedStateKeyResolver
{
    internal int Accesses { get; private set; }

    public bool TryGetCurrentWriteKey(
        AuthorizedStateAccess access,
        out RestrictedStateKey? key)
    {
        Accesses++;
        key = Create();
        return true;
    }

    public bool TryGetApprovedReadKey(
        AuthorizedStateAccess access,
        string keyId,
        long expiresAtUnixSeconds,
        out RestrictedStateKey? key)
    {
        Accesses++;
        if (!StringComparer.Ordinal.Equals(keyId, "synthetic-key"))
        {
            key = null;
            return false;
        }

        key = Create();
        return true;
    }

    private RestrictedStateKey Create()
    {
        var material = CanarySet.StateKey(scenario);
        try
        {
            return new RestrictedStateKey("synthetic-key", material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}

internal sealed class CountingStateStore(LocalRestrictedStateStore adapter)
    : RestrictedStateOpaqueSnapshotStore
{
    private readonly RestrictedStateOpaqueSnapshotStore inner =
        ProofOpaqueState.Coordinator(adapter);

    internal int Calls { get; private set; }

    internal override Task<RestrictedStateStoreRead> ReadAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.ReadAsync(access, cancellationToken);
    }

    internal override Task<RestrictedStateStoreRawRead> ReadRawVersionAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.ReadRawVersionAsync(access, cancellationToken);
    }

    internal override Task<RestrictedStateStoreWrite> CompareExchangeAsync(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.CompareExchangeAsync(
            access,
            expected,
            replacement,
            cancellationToken);
    }

    internal override Task<RestrictedStateStoreWrite> CompareDeleteAsync(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.CompareDeleteAsync(access, expected, cancellationToken);
    }

    internal override Task<RestrictedStateStoreWrite> CompareDeleteRawAsync(
        AuthorizedStateAccess access,
        RestrictedStateRawVersion expected,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.CompareDeleteRawAsync(
            access,
            expected,
            cancellationToken);
    }
}

internal static class ProofOpaqueState
{
    internal static RestrictedStateOpaqueSnapshotStore Coordinator(
        LocalRestrictedStateStore store) =>
        new(store, new SyntheticStateKeyResolver("issue88-proof"));

    internal static Task<RestrictedStateStoreRead> ReadAsync(
        LocalRestrictedStateStore store,
        AuthorizedStateAccess access,
        CancellationToken cancellationToken) =>
        Coordinator(store).ReadAsync(access, cancellationToken);

    internal static Task<RestrictedStateStoreWrite> CompareExchangeAsync(
        LocalRestrictedStateStore store,
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken) =>
        Coordinator(store).CompareExchangeAsync(
            access,
            expected,
            replacement,
            cancellationToken);
}

internal sealed class CountingSessionAdmission(
    IRestrictedStateSessionAdmission inner)
    : IRestrictedStateSessionAdmission
{
    internal int Calls { get; private set; }

    public RestrictedStateSessionAdmissionResult Admit(
        AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext,
        RestrictedStateSessionAdmissionContext context)
    {
        Calls++;
        return inner.Admit(access, plaintext, context);
    }
}

internal static class HostLineage
{
    internal static HostLineageFile Create(
        RestrictedStateScope scope,
        long generation,
        string sessionSha256,
        string envelopeSha256,
        string? predecessorEnvelopeSha256)
    {
        var scopeSha = ProofScenario.ScopeSha256(scope);
        var integrity = Integrity(
            generation,
            sessionSha256,
            envelopeSha256,
            predecessorEnvelopeSha256,
            ProofScenario.Now,
            ProofScenario.Now +
                RestrictedStateFormat.MaximumRetentionSeconds,
            scopeSha);
        return new HostLineageFile(
            "apr-agent-host-lineage",
            generation,
            sessionSha256,
            envelopeSha256,
            predecessorEnvelopeSha256,
            ProofScenario.Now,
            ProofScenario.Now +
                RestrictedStateFormat.MaximumRetentionSeconds,
            scopeSha,
            integrity);
    }

    internal static bool TryRead(
        ProofCommand command,
        RestrictedStateScope scope,
        out HostLineageFile? file,
        out AcceptedLineage? lineage)
    {
        file = null;
        lineage = null;
        try
        {
            var candidate = ProofJson.ReadLineage(
                File.ReadAllBytes(ProofPaths.Lineage(command)));
            if (candidate is null ||
                !StringComparer.Ordinal.Equals(
                    candidate.Kind,
                    "apr-agent-host-lineage") ||
                !StringComparer.Ordinal.Equals(
                    candidate.ScopeSha256,
                    ProofScenario.ScopeSha256(scope)) ||
                !StringComparer.Ordinal.Equals(
                    candidate.IntegritySha256,
                    Integrity(
                        candidate.Generation,
                        candidate.SessionSha256,
                        candidate.EnvelopeSha256,
                        candidate.PredecessorEnvelopeSha256,
                        candidate.AcceptedAtUnixSeconds,
                        candidate.ExpiresAtUnixSeconds,
                        candidate.ScopeSha256)))
            {
                return false;
            }

            file = candidate;
            lineage = new AcceptedLineage(
                scope,
                candidate.Generation,
                candidate.SessionSha256,
                candidate.EnvelopeSha256,
                candidate.PredecessorEnvelopeSha256,
                candidate.AcceptedAtUnixSeconds,
                candidate.ExpiresAtUnixSeconds,
                TransitionAuthorized: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Integrity(
        long generation,
        string sessionSha256,
        string envelopeSha256,
        string? predecessorEnvelopeSha256,
        long acceptedAt,
        long expiresAt,
        string scopeSha256)
    {
        var value = string.Join(
            "\n",
            generation.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            sessionSha256,
            envelopeSha256,
            predecessorEnvelopeSha256 ?? "none",
            acceptedAt.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            expiresAt.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            scopeSha256);
        return AgentCanonical.HashDomain(
            "apr.proof-lineage.r2",
            ProofFiles.StrictUtf8(value));
    }
}

internal static class ProofState
{
    internal static StateResult EnterAuthorizedComposition(
        bool trusted,
        bool sameRepository,
        bool fork,
        Func<AuthorizedStateAccess, bool> enter,
        out AuthorizedStateAccess? access,
        out bool entered)
    {
        entered = false;
        var result = Authorize(
            trusted,
            sameRepository,
            fork,
            out access);
        if (result.Action != StateAction.Authorized || access is null)
        {
            return result;
        }

        entered = enter(access);
        return result;
    }

    internal static StateResult Authorize(
        bool trusted,
        bool sameRepository,
        bool fork,
        out AuthorizedStateAccess? access)
    {
        var scope = ProofScenario.Scope();
        return AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                scope,
                scope,
                trusted,
                sameRepository,
                fork),
            out access);
    }

    internal static RestrictedStateSessionAdmissionContext SessionContext(
        AgentSessionArtifact artifact,
        ReviewedIdentity nextIdentity,
        AgentSessionHeadTransition transition,
        string? envelopeSha256 = null) => new(
        artifact.Document.ProducerBaseSha,
        artifact.Document.ProducerHeadSha,
        artifact.Document.Generation,
        artifact.Document.PredecessorStateSha256,
        new AgentSessionStateAdmissionContext(
            ProofScenario.Trusted(),
            ProofScenario.SessionId,
            nextIdentity,
            ProofScenario.User("Synthetic next review context."),
            transition,
            SyntheticContinuationCodec.Instance,
            envelopeSha256));
}
