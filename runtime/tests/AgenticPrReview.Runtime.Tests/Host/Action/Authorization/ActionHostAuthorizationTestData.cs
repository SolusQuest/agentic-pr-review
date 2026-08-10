using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

internal sealed class ActionHostAuthorizationScenario
{
    internal const long RepositoryId = 42;
    internal const string RepositoryName = "SolusQuest/agentic-pr-review";
    internal const string DefaultBranch = "main";
    internal const long CurrentRunId = 900;
    internal const int CurrentAttempt = 2;
    internal const long TriggerRunId = 800;
    internal const int TriggerAttempt = 1;
    internal const long PullRequestId = 1000;
    internal const long PullRequestNumber = 147;
    internal static readonly string WorkflowSha = new('a', 40);
    internal static readonly string ActionSha = new('b', 40);
    internal static readonly string TriggerSha = new('c', 40);
    internal static readonly string BaseSha = new('d', 40);
    internal static readonly string HeadSha = new('e', 40);

    private ActionHostAuthorizationScenario(
        ActionHostAuthorizationRoute route,
        byte[] eventBytes,
        ActionHostLaunchContract launch,
        FakeEventReader eventReader,
        FakeGitHubTransport transport,
        FakeGitHubTransportFactory factory)
    {
        Route = route;
        EventBytes = eventBytes;
        Launch = launch;
        EventReader = eventReader;
        Transport = transport;
        Factory = factory;
    }

    internal ActionHostAuthorizationRoute Route { get; }
    internal byte[] EventBytes { get; }
    internal ActionHostLaunchContract Launch { get; }
    internal FakeEventReader EventReader { get; }
    internal FakeGitHubTransport Transport { get; }
    internal FakeGitHubTransportFactory Factory { get; }

    internal ActionHostAuthorizer CreateAuthorizer() => new(
        EventReader,
        Factory,
        ActionHostAuthorizationPolicy.TrustedProof);

    internal static ActionHostAuthorizationScenario Valid(
        ActionHostAuthorizationRoute route,
        bool includeToken = true,
        ActionHostCancellationState cancellation =
            ActionHostCancellationState.Active,
        string tokenValue = "github-token-canary-value")
    {
        var repository = new ActionHostGitHubRepositoryFact(
            RepositoryId,
            RepositoryName,
            DefaultBranch);
        var identity = new ActionHostGitHubRepositoryIdentity(
            RepositoryId,
            RepositoryName);
        var actor = new ActionHostGitHubActorFact(7, "maintainer");
        var reference = new ActionHostGitHubPullRequestReferenceFact(
            PullRequestId,
            PullRequestNumber,
            BaseSha,
            identity,
            HeadSha,
            identity);
        var trigger = new ActionHostGitHubWorkflowRunFact(
            TriggerRunId,
            TriggerAttempt,
            71,
            ActionHostAuthorizationPolicy.TriggerWorkflowName,
            ActionHostAuthorizationPolicy.TriggerWorkflowPath,
            "feature",
            TriggerSha,
            ActionHostAuthorizationPolicy.TriggerEvent,
            "success",
            identity,
            identity,
            actor,
            actor,
            [reference]);
        var current = new ActionHostGitHubWorkflowRunFact(
            CurrentRunId,
            CurrentAttempt,
            72,
            "R4 trusted proof",
            ActionHostAuthorizationPolicy.PrivilegedWorkflowPath,
            DefaultBranch,
            WorkflowSha,
            route == ActionHostAuthorizationRoute.WorkflowRun
                ? "workflow_run"
                : "workflow_dispatch",
            null,
            identity,
            identity,
            actor,
            actor,
            []);
        var pullRequest = new ActionHostGitHubPullRequestFact(
            PullRequestId,
            PullRequestNumber,
            "open",
            false,
            null,
            BaseSha,
            identity,
            HeadSha,
            identity);
        var workflowBytes = Encoding.UTF8.GetBytes(
            ValidWorkflow(ActionSha));
        var source = new ActionHostGitHubWorkflowSourceFact(
            ActionHostAuthorizationPolicy.PrivilegedWorkflowPath,
            "r4-trusted-proof.yml",
            GitBlobSha(workflowBytes),
            workflowBytes);
        var eventBytes = route == ActionHostAuthorizationRoute.WorkflowRun
            ? WorkflowRunEventJson(trigger)
            : WorkflowDispatchEventJson(actor);
        var launch = CreateLaunch(
            eventBytes,
            route,
            includeToken,
            cancellation,
            tokenValue: tokenValue);
        var transport = new FakeGitHubTransport
        {
            Repository = repository,
            CurrentRun = current,
            TriggerRun = trigger,
            Source = source,
            PullRequest = pullRequest,
            AssociatedPages =
            [
                new ActionHostGitHubPullRequestPageFact(
                    [pullRequest],
                    true),
            ],
            Permission = new("write"),
        };
        var eventReader = new FakeEventReader(eventBytes);
        var factory = new FakeGitHubTransportFactory(transport);
        return new(route, eventBytes, launch, eventReader, transport, factory);
    }

    internal static ActionHostLaunchContract CreateLaunch(
        byte[] eventBytes,
        ActionHostAuthorizationRoute route,
        bool includeToken = true,
        ActionHostCancellationState cancellation =
            ActionHostCancellationState.Active,
        long? pullRequestNumber = null,
        string tokenValue = "github-token-canary-value")
    {
        ActionHostGitHubToken? token = null;
        if (includeToken)
        {
            Assert.True(ActionHostGitHubToken.TryCreate(
                tokenValue,
                out token));
        }

        Assert.True(ActionHostInputs.TryCreate(
            token,
            null,
            null,
            null,
            null,
            pullRequestNumber ??
                (route == ActionHostAuthorizationRoute.WorkflowDispatch
                    ? PullRequestNumber
                    : null),
            ActionHostStateMode.Auto,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            "C:/runner/event.json",
            Convert.ToHexString(SHA256.HashData(eventBytes)).ToLowerInvariant(),
            RepositoryName,
            RepositoryId,
            CurrentRunId,
            CurrentAttempt,
            ActionHostAuthorizationPolicy.PrivilegedWorkflowPath,
            RepositoryName + "/" +
                ActionHostAuthorizationPolicy.PrivilegedWorkflowPath +
                "@refs/heads/main",
            WorkflowSha,
            ActionSha,
            new string('f', 64),
            "r4-h2-tests",
            cancellation,
            "C:/runner/bridge.sock",
            out var launch));
        return launch!;
    }

    internal static string ValidWorkflow(string actionSha) => $$$"""
        name: R4 trusted proof
        on:
          workflow_run:
            workflows:
              - CI
            types:
              - completed
          workflow_dispatch:
            inputs:
              pr-number:
                description: Pull request number
                required: true
                type: number
        permissions: {}
        concurrency:
          group: agentic-pr-review-r4-${{ github.repository_id }}-pr-${{ github.event.workflow_run.pull_requests[0].number || inputs.pr-number }}
          cancel-in-progress: false
        jobs:
          authorization-preflight:
            permissions: {}
            runs-on: ubuntu-latest
            outputs:
              authorized: ${{ steps.authorization.outputs.authorized }}
            steps:
              - id: authorization
                run: |
                  echo "authorized=false" >> "$GITHUB_OUTPUT"
          workflow-run-review:
            needs: authorization-preflight
            if: ${{ github.event_name == 'workflow_run' && needs.authorization-preflight.outputs.authorized == 'true' }}
            permissions:
              actions: write
              contents: read
              pull-requests: write
            runs-on: ubuntu-latest
            steps:
              - uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@{{{actionSha}}}
          workflow-dispatch-review:
            needs: authorization-preflight
            if: ${{ github.event_name == 'workflow_dispatch' && needs.authorization-preflight.outputs.authorized == 'true' }}
            permissions:
              actions: write
              contents: read
              pull-requests: write
            runs-on: ubuntu-latest
            steps:
              - uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@{{{actionSha}}}
        """;

    private static byte[] WorkflowDispatchEventJson(
        ActionHostGitHubActorFact actor) => Encoding.UTF8.GetBytes($$"""
        {
          "inputs": { "pr-number": {{PullRequestNumber}} },
          "repository": {
            "id": {{RepositoryId}},
            "full_name": "{{RepositoryName}}"
          },
          "sender": { "id": {{actor.Id}}, "login": "{{actor.Login}}" }
        }
        """);

    private static byte[] WorkflowRunEventJson(
        ActionHostGitHubWorkflowRunFact run)
    {
        var pullRequest = run.PullRequests[0];
        return Encoding.UTF8.GetBytes($$"""
        {
          "action": "completed",
          "workflow_run": {
            "id": {{run.Id}},
            "run_attempt": {{run.Attempt}},
            "workflow_id": {{run.WorkflowId}},
            "name": "{{run.Name}}",
            "path": "{{run.Path}}",
            "head_branch": "{{run.HeadBranch}}",
            "head_sha": "{{run.HeadSha}}",
            "event": "{{run.Event}}",
            "conclusion": "{{run.Conclusion}}",
            "repository": {
              "id": {{run.Repository.Id}},
              "full_name": "{{run.Repository.FullName}}"
            },
            "head_repository": {
              "id": {{run.HeadRepository.Id}},
              "full_name": "{{run.HeadRepository.FullName}}"
            },
            "actor": {
              "id": {{run.Actor.Id}},
              "login": "{{run.Actor.Login}}"
            },
            "triggering_actor": {
              "id": {{run.TriggeringActor.Id}},
              "login": "{{run.TriggeringActor.Login}}"
            },
            "pull_requests": [
              {
                "id": {{pullRequest.Id}},
                "number": {{pullRequest.Number}},
                "base": {
                  "sha": "{{pullRequest.BaseSha}}",
                  "repo": {
                    "id": {{pullRequest.BaseRepository.Id}},
                    "full_name": "{{pullRequest.BaseRepository.FullName}}"
                  }
                },
                "head": {
                  "sha": "{{pullRequest.HeadSha}}",
                  "repo": {
                    "id": {{pullRequest.HeadRepository.Id}},
                    "full_name": "{{pullRequest.HeadRepository.FullName}}"
                  }
                }
              }
            ]
          },
          "repository": {
            "id": {{RepositoryId}},
            "full_name": "{{RepositoryName}}"
          },
          "sender": { "id": 7, "login": "maintainer" }
        }
        """);
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }
}

internal sealed class FakeEventReader : IActionHostEventReader
{
    internal FakeEventReader(byte[] bytes)
    {
        Bytes = bytes;
    }

    internal byte[]? Bytes { get; set; }
    internal int Calls { get; private set; }

    public Task<ActionHostEventReadResult> ReadAsync(
        string exactPath,
        CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Bytes is null
            ? ActionHostEventReadResult.Failed()
            : ActionHostEventReadResult.Captured(Bytes));
    }
}

internal sealed class FakeGitHubTransportFactory :
    IActionHostGitHubAuthorizationTransportFactory
{
    private readonly FakeGitHubTransport _transport;

    internal FakeGitHubTransportFactory(FakeGitHubTransport transport)
    {
        _transport = transport;
    }

    internal int Calls { get; private set; }

    public IActionHostGitHubAuthorizationTransport Create(
        ActionHostGitHubToken token)
    {
        Calls++;
        return _transport;
    }
}

internal sealed class FakeGitHubTransport :
    IActionHostGitHubAuthorizationTransport
{
    internal required ActionHostGitHubRepositoryFact Repository { get; set; }
    internal required ActionHostGitHubWorkflowRunFact CurrentRun { get; set; }
    internal required ActionHostGitHubWorkflowRunFact TriggerRun { get; set; }
    internal required ActionHostGitHubWorkflowSourceFact Source { get; set; }
    internal required ActionHostGitHubPullRequestFact PullRequest { get; set; }
    internal required IReadOnlyList<ActionHostGitHubPullRequestPageFact>
        AssociatedPages { get; set; }
    internal required ActionHostGitHubPermissionFact Permission { get; set; }
    internal Dictionary<string, ActionHostGitHubPermissionFact>
        PermissionsByLogin { get; } = new(StringComparer.Ordinal);
    internal ActionHostGitHubFailure Failure { get; set; }
    internal TimeSpan Delay { get; set; }
    internal List<string> Calls { get; } = [];

    public Task<ActionHostGitHubResult<ActionHostGitHubRepositoryFact>>
        GetRepositoryAsync(
            string repositoryName,
            CancellationToken cancellationToken)
    {
        Calls.Add("repository");
        return Result(Repository, cancellationToken);
    }

    public Task<ActionHostGitHubResult<ActionHostGitHubWorkflowRunFact>>
        GetWorkflowRunAttemptAsync(
            string repositoryName,
            long runId,
            int attempt,
            CancellationToken cancellationToken)
    {
        Calls.Add($"run:{runId}:{attempt}");
        return Result(runId == ActionHostAuthorizationScenario.CurrentRunId
            ? CurrentRun
            : TriggerRun, cancellationToken);
    }

    public Task<ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>>
        GetWorkflowSourceAsync(
            string repositoryName,
            string workflowPath,
            string workflowCommitSha,
            CancellationToken cancellationToken)
    {
        Calls.Add("source");
        return Result(Source, cancellationToken);
    }

    public Task<ActionHostGitHubResult<ActionHostGitHubPullRequestPageFact>>
        GetCommitPullRequestsAsync(
            string repositoryName,
            string commitSha,
            int page,
            int perPage,
            CancellationToken cancellationToken)
    {
        Calls.Add($"associated:{page}");
        return Result(
            AssociatedPages[Math.Min(page - 1, AssociatedPages.Count - 1)],
            cancellationToken);
    }

    public Task<ActionHostGitHubResult<ActionHostGitHubPermissionFact>>
        GetCollaboratorPermissionAsync(
            string repositoryName,
            string login,
            CancellationToken cancellationToken)
    {
        Calls.Add($"permission:{login}");
        return Result(
            PermissionsByLogin.TryGetValue(login, out var permission)
                ? permission
                : Permission,
            cancellationToken);
    }

    public Task<ActionHostGitHubResult<ActionHostGitHubPullRequestFact>>
        GetPullRequestAsync(
            string repositoryName,
            long pullRequestNumber,
            CancellationToken cancellationToken)
    {
        Calls.Add("pull-request");
        return Result(PullRequest, cancellationToken);
    }

    public void Dispose() { }

    private async Task<ActionHostGitHubResult<T>> Result<T>(
        T value,
        CancellationToken cancellationToken)
        where T : class
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Failure == ActionHostGitHubFailure.None
            ? ActionHostGitHubResult<T>.Success(value)
            : ActionHostGitHubResult<T>.Failed(Failure);
    }
}
