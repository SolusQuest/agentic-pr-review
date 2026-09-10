using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// Test-only authority. No supplied SESSION is imported, no production credential is read.
internal sealed class ReplayState : IDisposable
{
    internal const long Now = 1_800_000_000;
    internal const string Build = "r5-completed-replay";
    private readonly Keys keys;
    internal ReplayState(AdmittedReplayRun run, string session, string root, byte[] key)
    {
        Trusted = run.CreateTrustedRequest(Build);
        if (!AgentStableRequestMaterializer.TryMaterialize(Trusted, null, out var stable)) throw new InvalidOperationException();
        var plan = stable!.StablePlan;
        var scope = new RestrictedStateScope(plan.RepositoryId, plan.WorkflowIdentity, plan.ReviewTarget, session,
            plan.ProviderId, plan.ModelId, plan.AdapterId, plan.PolicySha256, plan.LimitsSha256, plan.ToolsetSha256, plan.BuildId);
        var authorized = AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var access);
        if (authorized.Action != StateAction.Authorized || access is null) throw new InvalidOperationException();
        Access = access;
        keys = new(key);
        Directory.CreateDirectory(Path.Combine(root, "state"));
        Service = new(new LocalRestrictedStateStore(Path.Combine(root, "state")), keys,
            new AgentSessionRestrictedStateAdmission(), () => Now);
        Session = session;
    }

    internal AgentSessionTrustedRequest Trusted { get; }
    internal AuthorizedStateAccess Access { get; }
    internal RestrictedStateService Service { get; }
    internal string Session { get; }

    internal RestrictedStateSessionAdmissionContext Context(ReviewedIdentity producer, ReviewedIdentity current,
        long generation, string? predecessor, AgentSessionHeadTransition transition, string context) =>
        new(producer.BaseSha, producer.HeadSha, generation, predecessor,
            new AgentSessionStateAdmissionContext(Trusted, Session, current,
                new ProjectChatMessage("user", [new ProjectTextContent(context)]), transition,
                DeepSeekReasoningContinuationCodec.Instance, null));

    internal static AgentSessionHeadTransition Transition(AdmittedReplayRun current, AdmittedReplayRun? previous)
    {
        if (previous is null && current.Input.Transition == "initial") return AgentSessionHeadTransition.SameHead;
        if (previous is null || current.Input.PreviousRunId != previous.Input.Id) return AgentSessionHeadTransition.Unknown;
        if (current.Input.Transition == "same_head" && current.Input.ReviewedIdentity == previous.Input.ReviewedIdentity)
            return AgentSessionHeadTransition.SameHead;
        // Authored synthetic sequence authority, not GitHub ancestry or production restore authority.
        // R1 binds the immediate predecessor and scope; the R2 coverage oracle independently pins its authored head pair.
        return current.Input.Transition == "verified_ahead" &&
            current.Input.ReviewedIdentity.RepositoryId == previous.Input.ReviewedIdentity.RepositoryId &&
            current.Input.ReviewedIdentity.ReviewTarget == previous.Input.ReviewedIdentity.ReviewTarget &&
            current.Input.ReviewedIdentity.HeadSha != previous.Input.ReviewedIdentity.HeadSha
            ? AgentSessionHeadTransition.VerifiedAhead : AgentSessionHeadTransition.Unknown;
    }

    public void Dispose() => keys.Dispose();
    private sealed class Keys : IRestrictedStateKeyResolver, IDisposable
    {
        private readonly byte[] bytes;
        internal Keys(byte[] key) { if (key.Length != 32) throw new ArgumentException(); bytes = key.ToArray(); }
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? key)
        { key = new("r5-synthetic", bytes); return true; }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string keyId, long expiresAtUnixSeconds, out RestrictedStateKey? key)
        { key = keyId == "r5-synthetic" ? new("r5-synthetic", bytes) : null; return key is not null; }
        public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
    }
}
