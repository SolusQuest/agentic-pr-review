using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostDownstreamAuthorizationTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private static byte[] SerializedAutomaticEvent() => File.ReadAllBytes(Path.Join(RepositoryRoot(),
        "runtime/tests/fixtures/action-host/downstream/workflow-run-event.json"));

    [Fact]
    public async Task ProductionAuthorizesTheSerializedNodeAutomaticFixture()
    {
        var bytes = SerializedAutomaticEvent();
        Assert.True(ActionHostEventParser.TryParse(bytes, out var fact, out _), "Node workflow_run facts must satisfy the production document mapper.");
        var trigger = fact!.WorkflowRun!;
        var reference = Assert.Single(trigger.PullRequests);
        var test = DownstreamAuthorizationScenario.Create(workflowRun: true, serializedEvent: bytes);
        test.Scenario.Transport.TriggerRun = trigger;
        var pullRequest = test.Scenario.Transport.PullRequest with { BaseSha = reference.BaseSha, HeadSha = reference.HeadSha };
        test.Scenario.Transport.PullRequest = pullRequest;
        test.Scenario.Transport.AssociatedPages = [new([pullRequest], true)];
        var result = await test.Authorize();
        Assert.NotNull(result.Invocation);
        Assert.Equal(ActionHostAuthorizationFailure.None, result.Failure);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("head")]
    public void SerializedAutomaticFixtureRequiresEachRepositoryReferenceUrl(string side)
    {
        var document = JsonNode.Parse(SerializedAutomaticEvent())!;
        document["workflow_run"]!["pull_requests"]![0]![side]!["repo"]!.AsObject().Remove("url");
        Assert.False(ActionHostEventParser.TryParse(Encoding.UTF8.GetBytes(document.ToJsonString()), out _, out _));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ProductionDefaultsAdmitBothInstallationAlternatives(bool automatic, bool workflowRun)
    {
        var test = DownstreamAuthorizationScenario.Create(automatic, workflowRun);
        var result = await test.Authorize();
        var invocation = Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(result.Invocation);
        Assert.Same(ActionHostProductionWorkflowAdmission.Instance,
            ActionHostCompositionDependencies.Production().WorkflowAdmission);
        Assert.Equal(ActionHostDownstreamWorkflowContract.WorkflowPath, invocation.WorkflowPath);
        Assert.Equal(test.Scenario.Transport.Source.BlobSha, invocation.WorkflowBlobSha);
        Assert.Equal(test.Launch.ActionSourceSha, invocation.ActionSourceSha);
        Assert.Equal("agentic-pr-review-r4-42-pr-147", invocation.ConcurrencyIdentity);
        Assert.Equal("actions:write,contents:read,pull-requests:write", invocation.DeclaredPermissions);
        Assert.Equal(ActionHostPayloadContinuityMode.ExactExecutable, invocation.PayloadContinuityMode);
        Assert.Equal(ActionHostSameHeadContinuationPolicy.ReuseAcceptedState, invocation.SameHeadContinuationPolicy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShippedTemplateIsTheEmbeddedAuthorizationSource(bool automatic)
    {
        var file = File.ReadAllText(Path.Join(RepositoryRoot(), "examples/workflows",
            automatic ? "automatic.yml.template" : "manual.yml.template"));
        Assert.Equal(file.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("__ACTION_SOURCE_SHA__", ActionHostAuthorizationScenario.ActionSha, StringComparison.Ordinal),
            ActionHostDownstreamWorkflowContract.Render(ActionHostAuthorizationScenario.ActionSha, automatic));
        Assert.Throws<ArgumentException>(() => ActionHostDownstreamWorkflowContract.Render("main", automatic));
        Assert.Throws<ArgumentException>(() => ActionHostDownstreamWorkflowContract.Render(new string('A', 40), automatic));
    }

    public static TheoryData<string, string> Mutations => new()
    {
        { "@" + ActionHostAuthorizationScenario.ActionSha, "@v1" },
        { "/.github/actions/agentic-pr-review@", "@" },
        { "actions: write", "actions: read" },
        { "contents: read", "contents: write" },
        { "pull-requests: write", "pull-requests: read" },
        { "    permissions:\n", "    environment: production\n    permissions:\n" },
        { "    permissions:\n", "    permissions:\n      id-token: write\n" },
        { "agentic-pr-review-r4-", "different-" },
        { "cancel-in-progress: false", "cancel-in-progress: true" },
        { "config-path: .github/agentic-pr-review.json", "config-path: ${{ github.workspace }}/policy.json" },
        { "runs-on: ubuntu-24.04", "runs-on: ubuntu-latest" },
        { "    steps:\n", "    env:\n      APR_PREPARED_CANDIDATE: untrusted\n    steps:\n" },
        { "      - id: review\n", "      - run: echo injected\n      - id: review\n" },
        { "secrets.DEEPSEEK_API_KEY", "github.token" },
        { "timeout-minutes: 30", "timeout-minutes: 90" },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task TemplateSubstitutionsFailProductionAdmission(string before, string after)
    {
        foreach (var test in new[] { true, false }.Select(automatic => DownstreamAuthorizationScenario.Create(automatic)))
        {
            var source = Encoding.UTF8.GetString(test.Scenario.Transport.Source.Bytes);
            Assert.Contains(before, source, StringComparison.Ordinal);
            test.SetSource(source.Replace(before, after, StringComparison.Ordinal));
            Assert.Equal(ActionHostAuthorizationFailure.WorkflowSourceInvalid, (await test.Authorize()).Failure);
        }
    }

    [Fact]
    public async Task ManualOnlySourceCannotAuthorizeAnAutomaticRun()
    {
        var test = DownstreamAuthorizationScenario.Create(automatic: false, workflowRun: true);
        Assert.Equal(ActionHostAuthorizationFailure.WorkflowSourceInvalid, (await test.Authorize()).Failure);
    }

    [Theory]
    [InlineData("auto", true, false)]
    [InlineData("reset", false, false)]
    [InlineData("reset", true, true)]
    [InlineData("auto", false, true)]
    [InlineData("unknown", false, false)]
    public async Task DispatchResetMustMatchExplicitEventInput(string eventMode, bool reset, bool granted)
    {
        var test = DownstreamAuthorizationScenario.Create(reset: reset, eventStateMode: eventMode);
        Assert.Equal(granted, (await test.Authorize()).Invocation is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentEventModeOnlyAllowsTheAutoDefault(bool reset)
    {
        var test = DownstreamAuthorizationScenario.Create(reset: reset, omitEventStateMode: true);
        Assert.Equal(!reset, (await test.Authorize()).Invocation is not null);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("branch")]
    [InlineData("policy")]
    [InlineData("actor")]
    [InlineData("draft")]
    [InlineData("fork")]
    [InlineData("automatic-reset")]
    [InlineData("path")]
    public async Task InvalidRouteFactsNeverMintAuthority(string mutation)
    {
        var test = DownstreamAuthorizationScenario.Create(candidate: mutation == "candidate",
            branch: mutation == "branch" ? "feature" : null,
            configPath: mutation == "policy" ? ".github/other.json" : null,
            workflowRun: mutation == "automatic-reset", reset: mutation == "automatic-reset",
            workflowPath: mutation == "path" ? ".github/workflows/other.yml" : null);
        if (mutation == "actor") test.Scenario.Transport.Permission = test.Scenario.Transport.Permission with { Permission = "read" };
        if (mutation == "draft") test.Scenario.Transport.PullRequest = test.Scenario.Transport.PullRequest with { Draft = true };
        if (mutation == "fork") test.Scenario.Transport.PullRequest = test.Scenario.Transport.PullRequest with {
            HeadRepository = new(99, "attacker/repo") };
        Assert.Null((await test.Authorize()).Invocation);
    }

    [Fact]
    public async Task HistoricalAndDownstreamTemplateAuthoritiesCannotBeMixed()
    {
        var test = DownstreamAuthorizationScenario.Create();
        test.SetSource(ActionHostTrustedWorkflowContract.Render(test.Launch.ActionSourceSha, test.Launch.PayloadSha256));
        Assert.Equal(ActionHostAuthorizationFailure.WorkflowSourceInvalid, (await test.Authorize()).Failure);
        var historical = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var bytes = Encoding.UTF8.GetBytes(ActionHostDownstreamWorkflowContract.Render(historical.Launch.ActionSourceSha, true));
        historical.Transport.Source = historical.Transport.Source with { Bytes = bytes, BlobSha = DownstreamAuthorizationScenario.BlobSha(bytes) };
        Assert.Equal(ActionHostAuthorizationFailure.WorkflowSourceInvalid,
            (await historical.CreateAuthorizer().AuthorizeAsync(historical.Launch, default)).Failure);
        IActionHostTrustedWorkflowAdmission proof = TrustedProofV2WorkflowAdmission.Instance;
        Assert.False(proof.IsWorkflowPathAllowed(ActionHostDownstreamWorkflowContract.WorkflowPath));
    }
}
