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
            out var evidence,
            out var failure), failure.ToString());
        Assert.Equal(ActionHostAuthorizationPolicy.ConcurrencyGroup,
            evidence!.ConcurrencyGroup);
    }

    public static TheoryData<string, string> RejectedMutations => new()
    {
        {
            "permissions: {}\nconcurrency:",
            "permissions:\n  actions: write\nconcurrency:"
        },
        { "cancel-in-progress: false", "cancel-in-progress: true" },
        {
            "needs: authorization-preflight",
            "needs: unrelated-preflight"
        },
        {
            "github.event_name == 'workflow_run' && needs.authorization-preflight.outputs.authorized == 'true'",
            "github.event_name == 'workflow_run'"
        },
        {
            "github.event_name == 'workflow_dispatch' && needs.authorization-preflight.outputs.authorized == 'true'",
            "github.event_name == 'workflow_dispatch' || inputs.pr-number > 0"
        },
        {
            "permissions: {}\n    runs-on: ubuntu-latest\n    outputs:",
            "permissions:\n      actions: write\n    runs-on: ubuntu-latest\n    outputs:"
        },
        {
            "@" + ActionHostAuthorizationScenario.ActionSha,
            "@main"
        },
        {
            "required: true\n        type: number",
            "required: true\n        type: number\n        default: 147"
        },
        {
            "actions: write\n      contents: read",
            "actions: write\n      actions: write\n      contents: read"
        },
        {
            "permissions: {}\n    runs-on: ubuntu-latest",
            "permissions: &empty {}\n    runs-on: ubuntu-latest"
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
        var mutated = canonical.Replace(
            before,
            after,
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            out _,
            out _));
    }

    [Fact]
    public void ActionInvocationOutsidePrivilegedJobsIsRejected()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        var mutated = canonical.Replace(
            "outputs:\n      authorized:",
            "steps:\n      - uses: " +
                ActionHostAuthorizationPolicy.ActionPath +
                ActionHostAuthorizationScenario.ActionSha +
                "\n    outputs:\n      authorized:",
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            out _,
            out _));
    }

    [Fact]
    public void BlockScalarUsesCannotHideAnActionInvocation()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        var mutated = canonical.Replace(
            "outputs:\n      authorized:",
            "steps:\n      - uses: |\n          " +
                ActionHostAuthorizationPolicy.ActionPath +
                ActionHostAuthorizationScenario.ActionSha +
                "\n    outputs:\n      authorized:",
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            out _,
            out _));
    }

    [Fact]
    public void CaseAliasCannotHideAnActionInvocationOutsidePrivilegedJobs()
    {
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        var mutated = canonical.Replace(
            "outputs:\n      authorized:",
            "steps:\n      - uses: solusquest/agentic-pr-review/" +
                ".github/actions/agentic-pr-review@" +
                ActionHostAuthorizationScenario.ActionSha +
                "\n    outputs:\n      authorized:",
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            out _,
            out _));
    }

    public static TheoryData<string> NonStepActionReferenceShapes
    {
        get
        {
            var reference = ActionHostAuthorizationPolicy.ActionPath +
                ActionHostAuthorizationScenario.ActionSha;
            return new()
            {
                "env:\n      uses: " + reference +
                    "\n    steps:\n      - run: echo decoy",
                "steps:\n      - run: echo decoy\n        with:\n" +
                    "          uses: " + reference,
                "outputs:\n      uses: " + reference +
                    "\n    steps:\n      - run: echo decoy",
                "uses: " + reference +
                    "\n    steps:\n      - run: echo decoy",
                "steps:\n      - run: echo decoy",
                "steps:\n      - uses: " + reference +
                    "\n      - uses: " + reference,
            };
        }
    }

    [Theory]
    [MemberData(nameof(NonStepActionReferenceShapes))]
    public void OnlyAnImmediateStepUsesCanBindTheActionSource(string replacement)
    {
        var reference = ActionHostAuthorizationPolicy.ActionPath +
            ActionHostAuthorizationScenario.ActionSha;
        var canonical = ActionHostAuthorizationScenario.ValidWorkflow(
            ActionHostAuthorizationScenario.ActionSha);
        var actualStep = "steps:\n      - uses: " + reference;
        Assert.Contains(actualStep, canonical, StringComparison.Ordinal);
        var mutated = canonical.Replace(
            actualStep,
            replacement,
            StringComparison.Ordinal);

        Assert.False(ActionHostTrustedWorkflowPolicy.TryValidate(
            Encoding.UTF8.GetBytes(mutated),
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
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
            out _,
            out _));
    }
}
