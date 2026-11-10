using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofV2AdmissionTests
{
    [Fact]
    public void StagedBuildIdentityOwnsOneDistinctExactRender()
    {
        Assert.Equal(new string('1', 40),
            TrustedProofPayloadBuildIdentity.SourceCommit);
        Assert.Equal(new string('2', 40),
            TrustedProofPayloadBuildIdentity.SourceTree);

        var rendered = TrustedProofV2WorkflowAdmission.Render(
            ActionHostAuthorizationScenario.ActionSha);
        var activeV1 = ActionHostTrustedWorkflowContract.Render(
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha);

        Assert.NotEqual(activeV1, rendered);
        Assert.Contains("PAYLOAD_SOURCE_SHA: ${{ github.workflow_sha }}", rendered,
            StringComparison.Ordinal);
        Assert.Contains("pull.base?.ref !== 'main'", rendered,
            StringComparison.Ordinal);
        Assert.Contains("!lowerHex40.test(pull.base?.sha ?? '')", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pull.base?.sha !== workflowSha", rendered,
            StringComparison.Ordinal);
        Assert.Contains("id: acquire-exact-sources", rendered,
            StringComparison.Ordinal);
        Assert.Contains("GIT_TERMINAL_PROMPT: '0'", rendered,
            StringComparison.Ordinal);
        Assert.Contains("CONTROL_ROOT_SHA: ${{ github.workflow_sha }}", rendered,
            StringComparison.Ordinal);
        Assert.Contains("fetch --depth=1 --no-tags origin \"$expected_sha\"", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", rendered,
            StringComparison.Ordinal);
        Assert.Contains(
            "uses: ./control-root/.github/actions/agentic-pr-review",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains(
            "acquire_exact_public_commit \"$GITHUB_WORKSPACE/payload-source\" " +
            "\"$PAYLOAD_SOURCE_SHA\"", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("__PAYLOAD_SOURCE_SHA__", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("__ACTION_SOURCE_SHA__", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("__PAYLOAD_SHA256__", rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PAYLOAD_SOURCE_SHA", activeV1,
            StringComparison.Ordinal);
        Assert.Equal(
            "apr-cr1-s5-current-host-payload-v1",
            TrustedProofPayloadHost.ProofKind);
    }

    [Fact]
    public async Task V2AdmissionRejectsWhenWorkflowActionAndCompiledPayloadDoNotShareOneCommit()
    {
        var scenario = CreateV2Scenario();

        var rejected = await CreateV2Authorizer(scenario).AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Null(rejected.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.WorkflowSourceInvalid,
            rejected.Failure);
    }

    [Fact]
    public async Task V2UsesWorkflowAsEffectiveBaseWhileDefaultV1UsesReportedBase()
    {
        var v1 = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var v1Result = await v1.CreateAuthorizer().AuthorizeAsync(
            v1.Launch,
            CancellationToken.None);
        var v1Invocation = Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            v1Result.Invocation);
        Assert.Equal(
            ActionHostSameHeadContinuationPolicy.ReuseAcceptedState,
            v1Invocation.SameHeadContinuationPolicy);
        Assert.Equal(
            ActionHostAuthorizationScenario.BaseSha,
            v1Invocation.PullRequest.BaseSha);
        Assert.Equal(
            ActionHostAuthorizationScenario.BaseSha,
            v1Invocation.PullRequest.ReportedBaseSha);

        var v2 = CreateV2Scenario();
        v2.Transport.PullRequest = v2.Transport.PullRequest with
        {
            BaseSha = ActionHostAuthorizationScenario.WorkflowSha,
            BaseRef = "release",
        };
        var rejectedRef = await CreateV2Authorizer(v2).AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        Assert.Null(rejectedRef.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.PullRequestInvalid,
            rejectedRef.Failure);

        v2.Transport.PullRequest = v2.Transport.PullRequest with
        {
            BaseRef = "main",
            BaseSha = ActionHostAuthorizationScenario.BaseSha,
        };
        var historicalBase = await CreateV2Authorizer(v2).AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        var v2Invocation = Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            historicalBase.Invocation);
        Assert.Equal(
            ActionHostSameHeadContinuationPolicy.ContinueAcrossWorkflowRuns,
            v2Invocation.SameHeadContinuationPolicy);
        Assert.Equal(
            TrustedProofPayloadBuildIdentity.SourceCommit,
            v2Invocation.PullRequest.BaseSha);
        Assert.Equal(
            ActionHostAuthorizationScenario.BaseSha,
            v2Invocation.PullRequest.ReportedBaseSha);

        var workflowRun = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        SetV2Workflow(workflowRun);
        var workflowRunResult = await CreateV2Authorizer(workflowRun).AuthorizeAsync(
            CreateCurrentHeadLaunch(workflowRun),
            CancellationToken.None);
        var workflowRunInvocation =
            Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
                workflowRunResult.Invocation);
        Assert.Equal(
            TrustedProofPayloadBuildIdentity.SourceCommit,
            workflowRunInvocation.PullRequest.BaseSha);
        Assert.Equal(
            ActionHostAuthorizationScenario.BaseSha,
            workflowRunInvocation.PullRequest.ReportedBaseSha);

        v2 = CreateV2Scenario();
        v2.Transport.Repository = v2.Transport.Repository with
        {
            DefaultBranch = "release",
        };
        v2.Transport.PullRequest = v2.Transport.PullRequest with
        {
            BaseSha = ActionHostAuthorizationScenario.WorkflowSha,
        };
        var rejectedDefault = await CreateV2Authorizer(v2).AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        Assert.Null(rejectedDefault.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.CurrentRunMismatch,
            rejectedDefault.Failure);

        v2 = CreateV2Scenario();
        var accepted = await CreateV2Authorizer(v2).AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        Assert.NotNull(accepted.Invocation);

        var rejectedByV1 = await v2.CreateAuthorizer().AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        Assert.Null(rejectedByV1.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.WorkflowSourceInvalid,
            rejectedByV1.Failure);
    }

    [Fact]
    public async Task V2AdmitsOnlyTheExplicitCandidateDispatchRun()
    {
        const string candidateBranch = "codex/issue-181-metadata-recovery";
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            candidateSourcePullRequestNumber: 234,
            candidateExecutionPhase: ActionHostCandidateExecutionPhase.Bootstrap,
            workflowBranch: candidateBranch);
        SetV2Workflow(scenario);

        var accepted = await CreateV2Authorizer(scenario).AuthorizeAsync(
            CreateCurrentHeadLaunch(scenario),
            CancellationToken.None);

        Assert.NotNull(accepted.Invocation);
        Assert.Equal(ActionHostAuthorizationFailure.None, accepted.Failure);

        var rejectedByDefault = await scenario.CreateAuthorizer().AuthorizeAsync(
            CreateCurrentHeadLaunch(scenario),
            CancellationToken.None);
        Assert.Null(rejectedByDefault.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.CurrentRunMismatch,
            rejectedByDefault.Failure);

        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            HeadBranch = "other-candidate",
        };
        var wrongBranch = await CreateV2Authorizer(scenario).AuthorizeAsync(
            CreateCurrentHeadLaunch(scenario),
            CancellationToken.None);
        Assert.Null(wrongBranch.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.CurrentRunMismatch,
            wrongBranch.Failure);
    }

    [Theory]
    [InlineData(null, "bootstrap")]
    [InlineData(147L, "continuation")]
    public async Task V2RejectsIncompleteCandidateDispatchAuthority(
        long? candidateSourcePullRequestNumber,
        string candidatePhase)
    {
        var candidateExecutionPhase = candidatePhase switch
        {
            "bootstrap" => ActionHostCandidateExecutionPhase.Bootstrap,
            "continuation" => ActionHostCandidateExecutionPhase.Continuation,
            _ => throw new ArgumentOutOfRangeException(nameof(candidatePhase)),
        };
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            candidateSourcePullRequestNumber: candidateSourcePullRequestNumber,
            candidateExecutionPhase: candidateExecutionPhase,
            workflowBranch: "codex/issue-181-metadata-recovery");
        SetV2Workflow(scenario);

        var rejected = await CreateV2Authorizer(scenario).AuthorizeAsync(
            CreateCurrentHeadLaunch(scenario),
            CancellationToken.None);

        Assert.Null(rejected.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.RouteInputInvalid,
            rejected.Failure);
    }

    [Fact]
    public void OriginalRuntimePreviewUsesDistinctSourceAndOnlyExactTestTemplate()
    {
        var expected = PreviewExpected();
        var admission = new OriginalRuntimePreviewWorkflowAdmission(expected);
        var launch = PreviewLaunch(expected);
        var bytes = Encoding.UTF8.GetBytes(OriginalRuntimePreviewWorkflowAdmission.Render());
        Assert.True(ActionHostTrustedWorkflowPolicy.TryValidateExact(bytes, ActionHostAuthorizationPolicy.TrustedProof,
            expected.SourceCommit, OriginalRuntimePreviewWorkflowAdmission.Render(), out _, out var parseFailure), parseFailure.ToString());
        Assert.True(admission.TryValidateWorkflow(bytes, ActionHostAuthorizationPolicy.TrustedProof, launch, out var evidence));
        Assert.Equal(ActionHostPayloadContinuityMode.ExactExecutable, evidence!.PayloadContinuityMode);
        Assert.Null(evidence.PayloadSourceCommit);
        Assert.Null(evidence.PayloadSourceTree);
        Assert.NotEqual(launch.WorkflowSha, launch.ActionSourceSha);
        Assert.StartsWith("TEST_ONLY/", evidence.ActionReference, StringComparison.Ordinal);
        Assert.False(admission.TryValidateWorkflow([.. bytes, (byte)'#'], ActionHostAuthorizationPolicy.TrustedProof, launch, out _));
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(expected with {
            WorkflowTemplateSha256 = new string('f', 64) }));
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(expected with {
            WorkflowCommit = expected.SourceCommit }));
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(expected with { Attempt = 2 }));
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(expected with {
            ReviewHead = new string('9', 40) }));
    }

    [Fact]
    public void OriginalRuntimePreviewRequiresExactAuthenticatedRunActorRepositoryAndManualContext()
    {
        var expected = PreviewExpected();
        var admission = new OriginalRuntimePreviewWorkflowAdmission(expected);
        var launch = PreviewLaunch(expected);
        var repository = new ActionHostGitHubRepositoryFact(OriginalRuntimePreviewWorkflowAdmission.RepositoryId,
            OriginalRuntimePreviewWorkflowAdmission.Repository, "main");
        var identity = new ActionHostGitHubRepositoryIdentity(repository.Id, repository.FullName);
        var actor = new ActionHostGitHubActorFact(OriginalRuntimePreviewWorkflowAdmission.ActorId, "Yuee98");
        var run = new ActionHostGitHubWorkflowRunFact(expected.RunId, 1, OriginalRuntimePreviewWorkflowAdmission.WorkflowId,
            "APR original runtime TEST_ONLY preview", OriginalRuntimePreviewWorkflowAdmission.WorkflowPath,
            "main", expected.WorkflowCommit, "workflow_dispatch", null, identity, identity, actor, actor, []);
        var eventFact = new ActionHostEventFact(ActionHostAuthorizationRoute.WorkflowDispatch, identity, actor,
            null, null, 3, null, null);
        Assert.True(admission.TryAdmitCurrentRun(repository, eventFact, run, launch));
        foreach (var wrong in new[] {
            run with { Id = run.Id + 1 }, run with { Attempt = 2 }, run with { WorkflowId = run.WorkflowId + 1 },
            run with { HeadSha = expected.SourceCommit }, run with { Event = "pull_request" },
            run with { HeadBranch = "other" }, run with { Repository = identity with { Id = 1 } },
            run with { HeadRepository = identity with { FullName = "other/repository" } },
            run with { Actor = actor with { Id = 1 } }, run with { TriggeringActor = actor with { Id = 1 } },
        }) Assert.False(admission.TryAdmitCurrentRun(repository, eventFact, wrong, launch));
        Assert.False(admission.TryAdmitCurrentRun(repository, eventFact with { DispatchPullRequestNumber = 4 }, run, launch));
        Assert.False(admission.TryAdmitCurrentRun(repository, eventFact with { DispatchStateMode = "reset" }, run, launch));
        Assert.False(admission.TryAdmitCurrentRun(repository with { Id = 1 }, eventFact, run, launch));
    }

    [Fact]
    public void OriginalRuntimePreviewNeverConflatesOriginalRuntimeWithSampleHeadOrChangesReviewBase()
    {
        var expected = PreviewExpected();
        var admission = new OriginalRuntimePreviewWorkflowAdmission(expected);
        var launch = PreviewLaunch(expected);
        var repository = new ActionHostGitHubRepositoryFact(OriginalRuntimePreviewWorkflowAdmission.RepositoryId,
            OriginalRuntimePreviewWorkflowAdmission.Repository, "main");
        var identity = new ActionHostGitHubRepositoryIdentity(repository.Id, repository.FullName);
        var pull = new ActionHostGitHubPullRequestFact(123, 3, "open", false, null,
            new string('4', 40), identity, OriginalRuntimePreviewWorkflowAdmission.Head0, identity, "main");
        Assert.True(admission.TryAdmitPullRequest(repository, pull, launch, out var actualBase));
        Assert.Equal(pull.BaseSha, actualBase);
        Assert.NotEqual(expected.SourceCommit, actualBase);
        foreach (var wrong in new[] {
            pull with { Number = 4 }, pull with { Draft = true }, pull with { State = "closed" },
            pull with { HeadSha = OriginalRuntimePreviewWorkflowAdmission.Head1 },
            pull with { BaseSha = new string('5', 40) },
            pull with { HeadRepository = identity with { Id = 1 } }, pull with { BaseRef = "other" },
        }) Assert.False(admission.TryAdmitPullRequest(repository, wrong, launch, out _));
    }

    [Fact]
    public void OriginalRuntimePreviewAdmitsOnlyClosedRolePhaseHeadPairs()
    {
        foreach (var (role, phase, head) in new[] {
            (OriginalRuntimePreviewSourceRole.PullRequest, OriginalRuntimePreviewPhase.Bootstrap, OriginalRuntimePreviewWorkflowAdmission.Head0),
            (OriginalRuntimePreviewSourceRole.PullRequest, OriginalRuntimePreviewPhase.Continuation, OriginalRuntimePreviewWorkflowAdmission.Head1),
            (OriginalRuntimePreviewSourceRole.ActualMain, OriginalRuntimePreviewPhase.Bootstrap, OriginalRuntimePreviewWorkflowAdmission.Head1),
            (OriginalRuntimePreviewSourceRole.ActualMain, OriginalRuntimePreviewPhase.Continuation, OriginalRuntimePreviewWorkflowAdmission.Head2),
        })
        {
            var expected = PreviewExpected() with { SourceRole = role, Phase = phase, ReviewHead = head };
            _ = new OriginalRuntimePreviewWorkflowAdmission(expected);
            Assert.Equal(head, OriginalRuntimePreviewWorkflowAdmission.TargetHead(role, phase));
            foreach (var wrong in new[] { OriginalRuntimePreviewWorkflowAdmission.Head0,
                OriginalRuntimePreviewWorkflowAdmission.Head1, OriginalRuntimePreviewWorkflowAdmission.Head2 }.Where(value => value != head))
                Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(expected with { ReviewHead = wrong }));
        }
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(PreviewExpected() with {
            SourceRole = (OriginalRuntimePreviewSourceRole)99 }));
        Assert.Throws<ArgumentException>(() => new OriginalRuntimePreviewWorkflowAdmission(PreviewExpected() with {
            Phase = (OriginalRuntimePreviewPhase)99 }));
    }

    [Fact]
    public void OriginalRuntimePreviewSourceRoleRequiresActualMatchingSourceFacts()
    {
        var source = PreviewExpected().SourceCommit;
        var main = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\",\"object\":{\"type\":\"commit\",\"sha\":\"" + source + "\"}}");
        var pr = Encoding.UTF8.GetBytes("{\"number\":494,\"state\":\"open\",\"merged\":false,\"head\":{\"sha\":\"" + source +
            "\",\"repo\":{\"full_name\":\"SolusQuest/agentic-pr-review\"}},\"base\":{\"repo\":{\"full_name\":\"SolusQuest/agentic-pr-review\"}}}");
        Assert.True(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.ActualMain, source, main));
        Assert.True(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.PullRequest, source, pr));
        Assert.False(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.ActualMain, source, pr));
        Assert.False(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.PullRequest, source, main));
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{\"sourceRole\":\"main\"}"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(main).Replace(source, new string('9', 40), StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(main).Replace("refs/heads/main", "refs/heads/other", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(main).Replace("\"ref\":", "\"ref\":\"refs/heads/main\",\"ref\":", StringComparison.Ordinal)),
        }) Assert.False(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.ActualMain, source, bytes));
        Assert.False(OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(OriginalRuntimePreviewSourceRole.PullRequest, source,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(pr).Replace("\"merged\":false", "\"merged\":true", StringComparison.Ordinal))));
    }

    private static OriginalRuntimePreviewExpected PreviewExpected() => new(
        TrustedProofPayloadBuildIdentity.SourceCommit, TrustedProofPayloadBuildIdentity.SourceTree,
        new string('3', 40), Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(OriginalRuntimePreviewWorkflowAdmission.Render()))),
        900, 1, new string('a', 64), new string('b', 64), new string('c', 64),
        new string('4', 40), OriginalRuntimePreviewWorkflowAdmission.Head0,
        OriginalRuntimePreviewSourceRole.PullRequest, OriginalRuntimePreviewPhase.Bootstrap);

    private static ActionHostLaunchContract PreviewLaunch(OriginalRuntimePreviewExpected expected)
    {
        // Synthetic predicate inputs only; no archive is loaded and no network/provider runs.
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        Assert.True(ActionHostInputs.TryCreate(null, null, null, null, ActionHostInputs.DefaultConfigPath,
            3, ActionHostStateMode.Auto, out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(inputs, scenario.Launch.EventJsonPath,
            scenario.Launch.EventJsonSha256, OriginalRuntimePreviewWorkflowAdmission.Repository,
            OriginalRuntimePreviewWorkflowAdmission.RepositoryId, expected.RunId, 1,
            OriginalRuntimePreviewWorkflowAdmission.WorkflowPath,
            OriginalRuntimePreviewWorkflowAdmission.Repository + "/" + OriginalRuntimePreviewWorkflowAdmission.WorkflowPath + "@refs/heads/main",
            expected.WorkflowCommit, expected.SourceCommit, expected.ArchiveSha256, "r7-d0",
            ActionHostCancellationState.Active, scenario.Launch.ArtifactBridgeEndpoint, out var launch));
        return launch!;
    }

    private static ActionHostAuthorizer CreateV2Authorizer(
        ActionHostAuthorizationScenario scenario) => new(
        scenario.EventReader,
        scenario.Factory,
        ActionHostAuthorizationPolicy.TrustedProof,
        workflowAdmission: TrustedProofV2WorkflowAdmission.Instance);

    private static ActionHostAuthorizationScenario CreateV2Scenario()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        SetV2Workflow(scenario);
        return scenario;
    }

    private static ActionHostLaunchContract CreateCurrentHeadLaunch(
        ActionHostAuthorizationScenario scenario)
    {
        var source = TrustedProofPayloadBuildIdentity.SourceCommit;
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            HeadSha = source,
        };
        Assert.True(ActionHostLaunchContract.TryCreate(
            scenario.Launch.Inputs,
            scenario.Launch.EventJsonPath,
            scenario.Launch.EventJsonSha256,
            scenario.Launch.RepositoryName,
            scenario.Launch.RepositoryId,
            scenario.Launch.RunId,
            scenario.Launch.RunAttempt,
            scenario.Launch.WorkflowPath,
            scenario.Launch.WorkflowRef,
            source,
            source,
            scenario.Launch.PayloadSha256,
            scenario.Launch.BuildDiscriminator,
            scenario.Launch.Cancellation,
            scenario.Launch.ArtifactBridgeEndpoint,
            out var current));
        return current!;
    }

    private static void SetV2Workflow(ActionHostAuthorizationScenario scenario)
    {
        var bytes = Encoding.UTF8.GetBytes(
            TrustedProofV2WorkflowAdmission.Render(
                ActionHostAuthorizationScenario.ActionSha));
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        scenario.Transport.Source = scenario.Transport.Source with
        {
            BlobSha = Convert.ToHexString(SHA1.HashData(
                header.Concat(bytes).ToArray())).ToLowerInvariant(),
            Bytes = bytes,
        };
    }
}
