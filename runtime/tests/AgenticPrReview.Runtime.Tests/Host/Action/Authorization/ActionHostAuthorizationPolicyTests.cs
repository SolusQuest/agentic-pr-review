using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostAuthorizationPolicyTests
{
    [Fact]
    public void CanonicalProofWorkflowIsAccepted()
    {
        var source = Encoding.UTF8.GetBytes(
            ActionHostAuthorizationScenario.ValidWorkflow(
                ActionHostAuthorizationScenario.ActionSha));

        Assert.True(ActionHostTrustedWorkflowPolicy.TryValidate(
            source,
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha,
            out var evidence,
            out var failure), failure.ToString());
        Assert.Equal(
            ActionHostAuthorizationPolicy.ConcurrencyGroup,
            evidence!.ConcurrencyGroup);
    }

    public static TheoryData<string, string> RejectedMutations => new()
    {
        { "ubuntu-24.04", "ubuntu-latest" },
        { "cancel-in-progress: false", "cancel-in-progress: true" },
        { "environment: r4-trusted-proof", "environment: production" },
        { "persist-credentials: false", "persist-credentials: true" },
        { "barrier hold", "barrier verify-completed" },
        { "barrier verify-completed", "barrier hold" },
        { "state-mode: auto", "state-mode: read-only" },
        {
            "config-path: .github/agentic-pr-review/trusted-proof.json",
            "config-path: .github/agentic-pr-review.json"
        },
        {
            "AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256: ${{ steps.prepare.outputs.prepared_payload_sha256 }}",
            "AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256: ${{ secrets.PAYLOAD_SHA256 }}"
        },
        {
            "provider-api-key: ${{ secrets.DEEPSEEK_API_KEY }}",
            "provider-api-key: ${{ github.token }}"
        },
        {
            "uses: actions/checkout@" +
                "d23441a48e516b6c34aea4fa41551a30e30af803",
            "uses: actions/checkout@v6"
        },
        {
            "run: |\n          sudo apt-get update",
            "run: |\n          echo changed\n          sudo apt-get update"
        },
        {
            "steps:\n      - id: checkout-control-root",
            "strategy:\n      matrix: {}\n    steps:\n" +
                "      - id: checkout-control-root"
        },
    };

    [Theory]
    [MemberData(nameof(RejectedMutations))]
    public void SecurityRelevantWorkflowMutationsAreRejected(
        string before,
        string after)
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        Assert.Contains(before, canonical, StringComparison.Ordinal);
        var mutated = canonical.Replace(before, after, StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha,
            out _,
            out _));
    }

    [Fact]
    public void ManifestPayloadMustEqualTheLaunchedPayload()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(canonical),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            new string('1', 64),
            out _,
            out var failure));
        Assert.Equal(ActionHostTrustedWorkflowFailure.JobInvalid, failure);
    }

    [Fact]
    public void ActionSourceMustBindCheckoutReferenceActionAndEnvironment()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(canonical),
            ActionHostAuthorizationPolicy.TrustedProof,
            new string('1', 40),
            ActionHostAuthorizationScenario.PayloadSha,
            out _,
            out _));
    }

    [Fact]
    public void ObsoleteOneStepWorkflowIsRejected()
    {
        var source = Encoding.UTF8.GetBytes("""
            name: R4 trusted proof
            on: {}
            permissions: {}
            jobs: {}
            """);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            source,
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha,
            out _,
            out _));
    }

    [Fact]
    public void CrLfCanonicalWorkflowIsAccepted()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
                ActionHostAuthorizationScenario.ActionSha)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.True(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(canonical),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha,
            out _,
            out var failure), failure.ToString());
    }

    [Fact]
    public void BareCarriageReturnIsRejected()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        var mutated = canonical.Replace(
            "name: R4 trusted proof\n",
            "name: R4 trusted proof\r",
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            ActionHostAuthorizationScenario.PayloadSha,
            out _,
            out _));
    }
}
