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
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha);
        var activeV1 = ActionHostTrustedWorkflowContract.Render(
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha);

        Assert.NotEqual(activeV1, rendered);
        Assert.Contains("PAYLOAD_SOURCE_SHA: ${{ github.workflow_sha }}", rendered,
            StringComparison.Ordinal);
        Assert.Contains("pull.base?.ref !== 'main'", rendered,
            StringComparison.Ordinal);
        Assert.Contains("pull.base?.sha !== workflowSha", rendered,
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
            "apr-r4-e2p-trusted-proof-payload-v2",
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
    public async Task V2FinalAdmissionRejectsWrongBaseWhileDefaultV1RemainsStable()
    {
        var v1 = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var v1Result = await v1.CreateAuthorizer().AuthorizeAsync(
            v1.Launch,
            CancellationToken.None);
        Assert.NotNull(v1Result.Invocation);

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
        var rejectedSha = await CreateV2Authorizer(v2).AuthorizeAsync(
            CreateCurrentHeadLaunch(v2),
            CancellationToken.None);
        Assert.Null(rejectedSha.Invocation);
        Assert.Equal(
            ActionHostAuthorizationFailure.PullRequestInvalid,
            rejectedSha.Failure);

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
        scenario.Transport.PullRequest = scenario.Transport.PullRequest with
        {
            BaseSha = TrustedProofPayloadBuildIdentity.SourceCommit,
        };
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
                ActionHostAuthorizationScenario.ActionSha,
                ActionHostAuthorizationScenario.PayloadSha));
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        scenario.Transport.Source = scenario.Transport.Source with
        {
            BlobSha = Convert.ToHexString(SHA1.HashData(
                header.Concat(bytes).ToArray())).ToLowerInvariant(),
            Bytes = bytes,
        };
    }
}
