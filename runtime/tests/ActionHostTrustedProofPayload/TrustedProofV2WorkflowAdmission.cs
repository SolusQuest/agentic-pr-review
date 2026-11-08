using System.Reflection;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofV2WorkflowAdmission :
    IActionHostTrustedWorkflowAdmission
{
    internal static TrustedProofV2WorkflowAdmission Instance { get; } = new();

    internal const string ProofKind =
        "apr-r4-e2p-trusted-proof-payload-v2";
    private const string ResourceName =
        "AgenticPrReview.Runtime.ActionHostTrustedProofPayload.TrustedProofWorkflowTemplateV2";
    private static readonly Lazy<string> Template = new(LoadTemplate);

    public bool TryValidateWorkflow(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch,
        out ActionHostTrustedWorkflowEvidence? evidence)
    {
        evidence = null;
        if (!StringComparer.Ordinal.Equals(launch.WorkflowSha, launch.ActionSourceSha) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowSha,
                TrustedProofPayloadBuildIdentity.SourceCommit))
        {
            return false;
        }

        if (!ActionHostTrustedWorkflowPolicy.TryValidateExact(
            source,
            policy,
            launch.ActionSourceSha,
            Render(launch.ActionSourceSha),
            out evidence,
            out _) ||
            evidence is null)
        {
            return false;
        }

        evidence = evidence with
        {
            SameHeadContinuationPolicy = ActionHostSameHeadContinuationPolicy
                .ContinueAcrossWorkflowRuns,
            PayloadContinuityMode = ActionHostPayloadContinuityMode.ExactSource,
            PayloadSourceCommit = TrustedProofPayloadBuildIdentity.SourceCommit,
            PayloadSourceTree = TrustedProofPayloadBuildIdentity.SourceTree,
        };
        return true;
    }

    public bool TryAdmitCurrentRun(
        ActionHostGitHubRepositoryFact repository,
        ActionHostEventFact eventFact,
        ActionHostGitHubWorkflowRunFact run,
        ActionHostLaunchContract launch)
    {
        var candidate = eventFact.CandidateExecutionPhase is not null;
        var expectedBranch = candidate
            ? run.HeadBranch
            : repository.DefaultBranch;
        var expectedWorkflowRef =
            $"{repository.FullName}/{launch.WorkflowPath}@" +
            $"refs/heads/{expectedBranch}";
        return candidate
            ? eventFact.Route == ActionHostAuthorizationRoute.WorkflowDispatch &&
                eventFact.CandidateSourcePullRequestNumber is > 0 &&
                eventFact.CandidateSourcePullRequestNumber !=
                    eventFact.DispatchPullRequestNumber &&
                !StringComparer.Ordinal.Equals(
                    run.HeadBranch,
                    repository.DefaultBranch) &&
                StringComparer.Ordinal.Equals(
                    launch.WorkflowRef,
                    expectedWorkflowRef)
            : eventFact.CandidateSourcePullRequestNumber is null &&
                StringComparer.Ordinal.Equals(
                    run.HeadBranch,
                    repository.DefaultBranch) &&
                StringComparer.Ordinal.Equals(
                    launch.WorkflowRef,
                    expectedWorkflowRef);
    }

    public bool TryAdmitPullRequest(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostLaunchContract launch,
        out string? effectiveReviewBaseSha)
    {
        effectiveReviewBaseSha = null;
        if (!StringComparer.Ordinal.Equals(repository.DefaultBranch, "main") ||
            !StringComparer.Ordinal.Equals(pullRequest.BaseRef, "main"))
        {
            return false;
        }

        effectiveReviewBaseSha = launch.WorkflowSha;
        return true;
    }

    internal static string Render(string actionSourceSha)
    {
        if (!IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceCommit, 40) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceTree, 40))
        {
            throw new ArgumentException("Workflow identities are invalid.");
        }

        return Template.Value;
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(TrustedProofV2WorkflowAdmission).Assembly
            .GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The v2 workflow template is missing.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        var v2 = reader.ReadToEnd();
        if (!v2.EndsWith('\n') || v2.Contains('\r'))
        {
            throw new InvalidOperationException("The v2 workflow template is invalid.");
        }

        if (v2.Contains("__PAYLOAD_SOURCE_SHA__", StringComparison.Ordinal) ||
            v2.Contains(ActionHostTrustedWorkflowContract.ActionSourcePlaceholder,
                StringComparison.Ordinal) ||
            v2.Contains(ActionHostTrustedWorkflowContract.PayloadShaPlaceholder,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The v2 workflow topology is invalid.");
        }

        return v2;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// Separate compiled repository-test authority. Expected identities are not
// authenticated observations: the ordinary Host still fetches the actual run,
// workflow and PR through its normal GitHub transport before invoking these checks.
internal enum OriginalRuntimePreviewSourceRole { PullRequest, ActualMain }
internal enum OriginalRuntimePreviewPhase { Bootstrap, Continuation }

internal sealed record OriginalRuntimePreviewExpected(
    string SourceCommit, string SourceTree, string WorkflowCommit, string WorkflowTemplateSha256,
    long RunId, int Attempt, string ArchiveSha256, string ReceiptSha256, string AssemblySha256,
    string ReviewBaseSha, string ReviewHead, OriginalRuntimePreviewSourceRole SourceRole,
    OriginalRuntimePreviewPhase Phase)
{
    public override string ToString() => "test_only_original_runtime_expected";
}

internal sealed class OriginalRuntimePreviewWorkflowAdmission : IActionHostTrustedWorkflowAdmission
{
    internal const long RepositoryId = 1383960589;
    internal const long WorkflowId = 410349377;
    internal const long ActorId = 16307884;
    internal const string Repository = "SolusQuest/agentic-pr-review-sandbox";
    internal const string WorkflowPath = ".github/workflows/agentic-pr-review.yml";
    internal const string Head0 = "dc4faa89877dbc2c9f31071ffe56d0867f854feb";
    internal const string Head1 = "29fbcd463304677ab3cacf7de6ec957895ba3c4b";
    internal const string Head2 = "f4794917c71513c5ab0fa3f7eb820a87f5d07bea";
    private readonly OriginalRuntimePreviewExpected expected;

    internal OriginalRuntimePreviewWorkflowAdmission(OriginalRuntimePreviewExpected expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Hex(expected.SourceCommit, 40) || !Hex(expected.SourceTree, 40) ||
            !Hex(expected.WorkflowCommit, 40) || !Hex(expected.WorkflowTemplateSha256, 64) ||
            !Hex(expected.ArchiveSha256, 64) || !Hex(expected.ReceiptSha256, 64) ||
            !Hex(expected.AssemblySha256, 64) || expected.RunId <= 0 || expected.Attempt != 1 ||
            expected.SourceCommit != TrustedProofPayloadBuildIdentity.SourceCommit ||
            expected.SourceTree != TrustedProofPayloadBuildIdentity.SourceTree ||
            expected.SourceCommit == expected.WorkflowCommit || !Hex(expected.ReviewBaseSha, 40) ||
            expected.SourceCommit is Head0 or Head1 or Head2 ||
            expected.WorkflowCommit is Head0 or Head1 or Head2 ||
            expected.ReviewHead != TargetHead(expected.SourceRole, expected.Phase) ||
            expected.WorkflowTemplateSha256 != Sha(System.Text.Encoding.UTF8.GetBytes(Render())))
            throw new ArgumentException("APR_TEST_ORIGINAL_PREVIEW_IDENTITY");
        this.expected = expected;
    }

    internal static string TargetHead(OriginalRuntimePreviewSourceRole role, OriginalRuntimePreviewPhase phase) =>
        (role, phase) switch {
            (OriginalRuntimePreviewSourceRole.PullRequest, OriginalRuntimePreviewPhase.Bootstrap) => Head0,
            (OriginalRuntimePreviewSourceRole.PullRequest, OriginalRuntimePreviewPhase.Continuation) => Head1,
            (OriginalRuntimePreviewSourceRole.ActualMain, OriginalRuntimePreviewPhase.Bootstrap) => Head1,
            (OriginalRuntimePreviewSourceRole.ActualMain, OriginalRuntimePreviewPhase.Continuation) => Head2,
            _ => throw new ArgumentException("APR_TEST_ORIGINAL_PREVIEW_ROLE_PHASE"),
        };

    // A pure predicate only. The fixed Host calls it only with the response bytes
    // of the normal authenticated fixed GitHub source-ref endpoint, never an env label.
    internal static bool MatchesActualSourceFact(OriginalRuntimePreviewSourceRole role, string source, byte[] bytes)
    {
        if (!Hex(source, 40) || bytes.Length is < 1 or > 262144) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            static void Unique(System.Text.Json.JsonElement node)
            {
                if (node.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject()) {
                        if (!names.Add(property.Name)) throw new FormatException();
                        Unique(property.Value);
                    }
                }
                else if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in node.EnumerateArray()) Unique(item);
            }
            Unique(document.RootElement);
            var root = document.RootElement;
            return role switch {
                OriginalRuntimePreviewSourceRole.ActualMain =>
                    root.GetProperty("ref").GetString() == "refs/heads/main" &&
                    root.GetProperty("object").GetProperty("type").GetString() == "commit" &&
                    root.GetProperty("object").GetProperty("sha").GetString() == source,
                OriginalRuntimePreviewSourceRole.PullRequest =>
                    root.GetProperty("number").GetInt32() == 494 &&
                    root.GetProperty("state").GetString() == "open" &&
                    root.GetProperty("merged").ValueKind == System.Text.Json.JsonValueKind.False &&
                    root.GetProperty("head").GetProperty("sha").GetString() == source &&
                    root.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString() == "SolusQuest/agentic-pr-review" &&
                    root.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString() == "SolusQuest/agentic-pr-review",
                _ => false,
            };
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException) { return false; }
    }

    public bool IsWorkflowPathAllowed(string path) => path == WorkflowPath;

    public bool TryValidateWorkflow(byte[]? source, ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch, out ActionHostTrustedWorkflowEvidence? evidence)
    {
        evidence = null;
        if (source is null || !MatchesLaunch(launch) || Sha(source) != expected.WorkflowTemplateSha256 ||
            !ActionHostTrustedWorkflowPolicy.TryValidateExact(source, policy, expected.SourceCommit,
                Render(), out evidence, out _) || evidence is null) return false;
        evidence = evidence with {
            ConcurrencyGroup = "apr-test-only-original-runtime-${{ github.repository_id }}-pr-3",
            ActionReference = "TEST_ONLY/original-runtime-preview@" + expected.SourceCommit,
            PreflightJobId = "review",
            // APR source and sandbox workflow are distinct. Do not use ExactSource,
            // whose production contract requires payload source == workflow source.
            PayloadContinuityMode = ActionHostPayloadContinuityMode.ExactExecutable,
            PayloadSourceCommit = null, PayloadSourceTree = null,
        };
        return true;
    }

    public bool TryAdmitCurrentRun(ActionHostGitHubRepositoryFact repository,
        ActionHostEventFact eventFact, ActionHostGitHubWorkflowRunFact run, ActionHostLaunchContract launch) =>
        MatchesLaunch(launch) && repository.Id == RepositoryId && repository.FullName == Repository &&
        repository.DefaultBranch == "main" && eventFact.Repository.Id == RepositoryId &&
        eventFact.Repository.FullName == Repository && eventFact.Route == ActionHostAuthorizationRoute.WorkflowDispatch &&
        eventFact.CandidateExecutionPhase is null && eventFact.CandidateSourcePullRequestNumber is null &&
        eventFact.DispatchPullRequestNumber == 3 && eventFact.Sender.Id == ActorId &&
        (eventFact.DispatchStateMode ?? "auto") == "auto" &&
        run.Id == expected.RunId && run.Attempt == expected.Attempt && run.WorkflowId == WorkflowId &&
        run.Name == "APR original runtime TEST_ONLY preview" &&
        run.Path == WorkflowPath && run.Event == "workflow_dispatch" && run.HeadBranch == "main" &&
        run.HeadSha == expected.WorkflowCommit && run.Repository.Id == RepositoryId &&
        run.Repository.FullName == Repository && run.HeadRepository.Id == RepositoryId &&
        run.HeadRepository.FullName == Repository && run.Actor.Id == ActorId && run.TriggeringActor.Id == ActorId;

    public bool TryAdmitPullRequest(ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest, ActionHostLaunchContract launch, out string? effectiveReviewBaseSha)
    {
        effectiveReviewBaseSha = null;
        if (!MatchesLaunch(launch) || repository.Id != RepositoryId || repository.FullName != Repository ||
            repository.DefaultBranch != "main" || pullRequest.Number != 3 || pullRequest.State != "open" ||
            pullRequest.Draft || pullRequest.MergedAt is not null || pullRequest.BaseRef != "main" ||
            pullRequest.BaseRepository.Id != RepositoryId || pullRequest.BaseRepository.FullName != Repository ||
            pullRequest.HeadRepository.Id != RepositoryId || pullRequest.HeadRepository.FullName != Repository ||
            pullRequest.BaseSha != expected.ReviewBaseSha || pullRequest.HeadSha != expected.ReviewHead) return false;
        effectiveReviewBaseSha = pullRequest.BaseSha;
        return true;
    }

    private bool MatchesLaunch(ActionHostLaunchContract launch) =>
        launch.RepositoryId == RepositoryId && launch.RepositoryName == Repository &&
        launch.RunId == expected.RunId && launch.RunAttempt == expected.Attempt &&
        launch.WorkflowPath == WorkflowPath && launch.WorkflowSha == expected.WorkflowCommit &&
        launch.WorkflowRef == Repository + "/" + WorkflowPath + "@refs/heads/main" &&
        launch.ActionSourceSha == expected.SourceCommit && launch.PayloadSha256 == expected.ArchiveSha256 &&
        launch.BuildDiscriminator == "r7-d0" &&
        launch.Inputs.PullRequestNumber == 3 && launch.Inputs.StateMode == ActionHostStateMode.Auto &&
        launch.Inputs.ConfigPath == ActionHostInputs.DefaultConfigPath;

    private static bool Hex(string value, int size) => value is not null && value.Length == size &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    // Prospective exact sandbox template, never an installed workflow. No SHA
    // points at itself: APR source is embedded; harness SHA is authenticated later.
    internal static string Render() => Template.Replace("__APR_SOURCE__",
        TrustedProofPayloadBuildIdentity.SourceCommit, StringComparison.Ordinal);

    private const string Template = """
name: APR original runtime TEST_ONLY preview
on:
  workflow_dispatch:
    inputs:
      pr-number:
        type: choice
        required: true
        options:
          - '3'
      request:
        type: string
        required: true
permissions:
  contents: read
  actions: read
  packages: read
  issues: write
  pull-requests: write
concurrency:
  group: apr-test-only-original-runtime-${{ github.repository_id }}-pr-3
  cancel-in-progress: false
jobs:
  review:
    runs-on: ubuntu-24.04
    timeout-minutes: 360
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0
        with:
          repository: SolusQuest/agentic-pr-review
          ref: __APR_SOURCE__
          path: control
          fetch-depth: 0
          persist-credentials: false
      - uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1
        with:
          dotnet-version: '10.0.109'
      - uses: actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38
        with:
          node-version: '24'
      - name: Install public locked tooling
        working-directory: control
        run: npm ci
      - name: Install fixed isolation prerequisites
        run: |
          sudo apt-get update
          sudo apt-get install -y iproute2 openssl strace
      - name: TEST_ONLY original runtime bounded preview
        working-directory: control
        env:
          GITHUB_TOKEN: ${{ github.token }}
          DEEPSEEK_API_KEY: ${{ secrets.DEEPSEEK_API_KEY }}
          AGENTIC_PR_REVIEW_STATE_KEY: ${{ secrets.AGENTIC_PR_REVIEW_STATE_KEY }}
        run: node tests/distribution/verify-managed-action.mjs --test-only-original-runtime-preview
""" + "\n";
}
