using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed record MaterializedVerifierPhase(
    R3QualityCase TestCase,
    ReviewedIdentity ReviewedIdentity,
    string Phase,
    string Transition,
    string Invocation,
    string? SeedIdentitySha256);

internal static class FreshProcessMaterializer
{
    private const string WorkflowIdentity = "trusted-r3-live-verifier";
    private const string EvidenceTrustedPolicy =
        "Use only the immutable reviewed snapshot and grounded tool evidence. " +
        "Interact only through the supplied tools, never a standalone answer. " +
        "For this bounded proof, first call list_changed_files with an empty " +
        "object, then call read_diff with only each returned changed path. " +
        "The synthetic diff is complete; do not call list_files, read_file, " +
        "or search_text, and omit unused optional properties instead of " +
        "sending null. End every run with exactly one finish_review call.";
    private const string ContinuationTrustedPolicy =
        "Continuation phases prove restored-fact continuity, not defect " +
        "detection. Interact only through the supplied tools, never a " +
        "standalone answer. Do not call list_files, list_changed_files, " +
        "read_diff, read_file, or search_text. End every run with exactly one " +
        "finish_review call, an empty findings array, and the required prior " +
        "fact verbatim in the summary.";
    private const string BuildId = "build-111";
    private const string SeedBaseSha =
        "4444444444444444444444444444444444444444";

    internal static bool TryMaterialize(
        VerifierScenario scenario,
        string root,
        ReadOnlySpan<byte> corpusBytes,
        out MaterializedVerifierPhase? materialized,
        string? expectedLineageSha256 = null)
    {
        materialized = null;
        if (!R3QualityCorpusParser.TryParse(
                corpusBytes,
                out var corpus,
                out _) ||
            corpus is null ||
            !TrySelectCase(corpus, scenario, out var testCase))
        {
            return false;
        }

        var identity = scenario == VerifierScenario.ContinuationSeed
            ? new ReviewedIdentity(
                testCase!.ReviewedIdentity.RepositoryId,
                testCase.ReviewedIdentity.ReviewTarget,
                SeedBaseSha,
                testCase.ReviewedIdentity.BaseSha)
            : testCase!.ReviewedIdentity with { };
        var reviewContext = scenario == VerifierScenario.ContinuationSeed
            ? testCase.ProcessOneContext
            : scenario == VerifierScenario.CanaryRouting
                ? string.Concat(
                    "Review untrusted repository content containing ",
                    VerifierCanaries.Prompt,
                    ".")
            : testCase.InitialContext;
        if (reviewContext is null)
        {
            return false;
        }
        var trustedPolicy = TrustedPolicyFor(testCase.Kind);

        Directory.CreateDirectory(Path.Join(root, "input"));
        Directory.CreateDirectory(Path.Join(root, "host"));
        Directory.CreateDirectory(Path.Join(root, "state"));
        Directory.CreateDirectory(Path.Join(root, "private"));
        Directory.CreateDirectory(Path.Join(root, "output"));

        var trusted = new AgentSessionTrustedRequest(
            identity.RepositoryId,
            identity.ReviewTarget,
            WorkflowIdentity,
            Encoding.UTF8.GetBytes(trustedPolicy),
            BuildId,
            DeepSeekAdapterContext.Provider,
            DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter);
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var stable))
        {
            return false;
        }

        var sessionId = testCase.Kind == R3QualityCaseKind.Continuation
            ? "session-111-continuation"
            : string.Concat("session-111-", testCase.Id);
        var scope = new RestrictedStateScope(
            trusted.RepositoryId,
            trusted.WorkflowIdentity,
            trusted.ReviewTarget,
            sessionId,
            trusted.ProviderId,
            trusted.ModelId,
            trusted.AdapterId,
            stable!.StablePlan.PolicySha256,
            stable.StablePlan.LimitsSha256,
            stable.StablePlan.ToolsetSha256,
            trusted.BuildId);

        var continuing = VerifierScenarioDomain.IsContinuing(scenario);
        if (continuing != (expectedLineageSha256 is not null) ||
            expectedLineageSha256 is not null &&
                !LiveAgentFreshProcessDomain.IsSha256(
                    expectedLineageSha256))
        {
            return false;
        }

        var invocation = scenario switch
        {
            VerifierScenario.MustFind => "issue111_must_find",
            VerifierScenario.MustNotFind => "issue111_must_not_find",
            VerifierScenario.ContinuationSeed => "issue111_seed_process",
            VerifierScenario.ContinuationRestore => "issue111_restore_process",
            VerifierScenario.CanaryRouting => "issue111_canary_routing",
            VerifierScenario.OuterAuthorizationDenied =>
                "issue111_negative_outer_authorization",
            VerifierScenario.InnerAuthorizationDenied =>
                "issue111_negative_inner_authorization",
            VerifierScenario.ProviderHttpFailure =>
                "issue111_negative_provider_http",
            VerifierScenario.ProviderMalformedResponse =>
                "issue111_negative_provider_malformed",
            VerifierScenario.ToolArgumentsInvalid =>
                "issue111_negative_tool_arguments",
            VerifierScenario.TerminalUngrounded =>
                "issue111_negative_terminal_ungrounded",
            VerifierScenario.TransitionFromHeadInvalid =>
                "issue111_negative_transition",
            VerifierScenario.LineageTampered =>
                "issue111_negative_lineage_tampered",
            VerifierScenario.QualityFailedAfterCommit =>
                "issue111_negative_quality_after_commit",
            VerifierScenario.PublicResultCanary =>
                "issue111_negative_public_result_canary",
            _ => throw new InvalidOperationException(),
        };
        var transition = scenario == VerifierScenario.TransitionFromHeadInvalid
            ? "diverged"
            : continuing
                ? "verified_ahead"
                : "same_head";
        var fromHead = continuing
            ? testCase.ReviewedIdentity.BaseSha
            : identity.HeadSha;
        var receipt = LiveAgentFreshProcessDomain.TransitionReceiptSha256(
            expectedLineageSha256,
            transition,
            fromHead,
            identity.BaseSha,
            identity.HeadSha,
            invocation);
        var authorizedScope = LiveAgentFreshProcessDomain.ScopeDocument(scope);
        if (scenario == VerifierScenario.OuterAuthorizationDenied)
        {
            authorizedScope = authorizedScope with { BuildId = "other-build" };
        }

        var authorization = new LiveAgentFreshProcessAuthorizationDocument(
            LiveAgentFreshProcessDomain.AuthorizationKind,
            new LiveAgentFreshProcessStableAuthority(
                trusted.RepositoryId,
                trusted.ReviewTarget,
                trusted.WorkflowIdentity,
                trustedPolicy,
                trusted.BuildId,
                trusted.ProviderId,
                trusted.ModelId,
                trusted.AdapterId,
                sessionId),
            authorizedScope,
            IsTrustedWorkflow: true,
            IsSameRepository: true,
            IsForkOrigin: false,
            LiveAgentFreshProcessDomain.DeterministicProfile,
            continuing ? "current" : "absent",
            continuing ? "explicit" : "automatic",
            new LiveAgentFreshProcessTransitionDocument(
                transition,
                fromHead,
                identity.BaseSha,
                identity.HeadSha,
                receipt),
            invocation,
            expectedLineageSha256);
        var reviewed = new LiveAgentFreshProcessReviewedInputDocument(
            LiveAgentFreshProcessDomain.ReviewedInputKind,
            LiveAgentFreshProcessDomain.IdentityDocument(identity),
            reviewContext);
        var manifest = scenario == VerifierScenario.CanaryRouting
            ? CanaryManifest(identity, testCase)
            : Manifest(identity, testCase);

        Write(
            Path.Join(root, "host", "authorization.json"),
            LiveAgentFreshProcessCodec.Write(authorization));
        Write(
            Path.Join(root, "input", "reviewed-input.json"),
            LiveAgentFreshProcessCodec.Write(reviewed));
        Write(
            Path.Join(root, "input", "snapshot-manifest.json"),
            LiveAgentFreshProcessCodec.Write(manifest));
        var resultPath = Path.Join(root, "output", "result.json");
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        materialized = new MaterializedVerifierPhase(
            testCase,
            identity,
            continuing ? "continue" : "bootstrap",
            transition,
            invocation,
            scenario == VerifierScenario.ContinuationSeed
                ? LiveAgentFreshProcessDomain.RawSha256(
                    Encoding.UTF8.GetBytes(
                        string.Concat(
                            identity.RepositoryId,
                            "\n",
                            identity.ReviewTarget,
                            "\n",
                            identity.BaseSha,
                            "\n",
                            identity.HeadSha)))
                : null);
        return true;
    }

    internal static string TrustedPolicyFor(R3QualityCaseKind kind) =>
        kind switch
        {
            R3QualityCaseKind.MustFind or R3QualityCaseKind.MustNotFind =>
                EvidenceTrustedPolicy,
            R3QualityCaseKind.Continuation => ContinuationTrustedPolicy,
            _ => throw new InvalidOperationException(
                $"Unsupported R3 quality case kind: {kind}."),
        };

    private static bool TrySelectCase(
        R3QualityCorpus corpus,
        VerifierScenario scenario,
        out R3QualityCase? testCase)
    {
        var kind = scenario switch
        {
            VerifierScenario.MustFind => R3QualityCaseKind.MustFind,
            VerifierScenario.MustNotFind => R3QualityCaseKind.MustNotFind,
            VerifierScenario.ContinuationSeed or
                VerifierScenario.ContinuationRestore or
                VerifierScenario.TransitionFromHeadInvalid or
                VerifierScenario.LineageTampered =>
                    R3QualityCaseKind.Continuation,
            VerifierScenario.CanaryRouting or
                VerifierScenario.PublicResultCanary =>
                    R3QualityCaseKind.MustNotFind,
            VerifierScenario.OuterAuthorizationDenied or
                VerifierScenario.InnerAuthorizationDenied or
                VerifierScenario.ProviderHttpFailure or
                VerifierScenario.ProviderMalformedResponse or
                VerifierScenario.ToolArgumentsInvalid or
                VerifierScenario.TerminalUngrounded or
                VerifierScenario.QualityFailedAfterCommit =>
                    R3QualityCaseKind.MustFind,
            _ => throw new InvalidOperationException(),
        };
        testCase = corpus.Cases.SingleOrDefault(item => item.Kind == kind);
        return testCase is not null;
    }

    private static LiveAgentFreshProcessSnapshotManifestDocument Manifest(
        ReviewedIdentity identity,
        R3QualityCase testCase)
    {
        var files = testCase.Files.Select(file =>
        {
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            return new LiveAgentFreshProcessFileDocument(
                file.Path,
                bytes.Length,
                LiveAgentFreshProcessDomain.RawSha256(bytes),
                Convert.ToBase64String(bytes));
        }).ToArray();
        var changed = testCase.ChangedFile;
        var source = testCase.DiffSource;
        var reboundSource = new ReviewedDiffSource(
            identity,
            source.Path,
            source.PreviousPath,
            source.Status,
            source.SourceTruncated,
            source.Hunks.Select(hunk => new ReviewedDiffHunk(
                hunk.OldStart,
                hunk.OldCount,
                hunk.NewStart,
                hunk.NewCount,
                hunk.Lines)));
        var changedDocument = new LiveAgentFreshProcessChangedFileDocument(
            changed.Path,
            changed.PreviousPath,
            changed.Status,
            changed.Additions,
            changed.Deletions,
            changed.Changes,
            changed.PatchStatus,
            reboundSource.PatchSha256,
            changed.SourceTruncated);
        var sourceDocument = new LiveAgentFreshProcessDiffSourceDocument(
            source.Path,
            source.PreviousPath,
            source.Status,
            source.SourceTruncated,
            source.Hunks.Select(hunk =>
                new LiveAgentFreshProcessDiffHunkDocument(
                    hunk.OldStart,
                    hunk.OldCount,
                    hunk.NewStart,
                    hunk.NewCount,
                    hunk.Lines.Select(line =>
                        new LiveAgentFreshProcessDiffLineDocument(
                            line.Kind,
                            line.OldLine,
                            line.NewLine,
                            line.Text)).ToArray())).ToArray());
        return new LiveAgentFreshProcessSnapshotManifestDocument(
            LiveAgentFreshProcessDomain.SnapshotManifestKind,
            LiveAgentFreshProcessDomain.IdentityDocument(identity),
            files.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray(),
            files,
            [changedDocument],
            [sourceDocument]);
    }

    private static LiveAgentFreshProcessSnapshotManifestDocument CanaryManifest(
        ReviewedIdentity identity,
        R3QualityCase testCase)
    {
        var path = string.Concat("src/", VerifierCanaries.Path, ".cs");
        var content = string.Concat(
            "// ",
            VerifierCanaries.Repository,
            "\n// ",
            VerifierCanaries.Prompt,
            "\ninternal static class CanaryRoute {}\n");
        var bytes = Encoding.UTF8.GetBytes(content);
        var source = new ReviewedDiffSource(
            identity,
            path,
            null,
            "modified",
            false,
            [
                new ReviewedDiffHunk(
                    0,
                    0,
                    1,
                    3,
                    [
                        new ReviewedDiffLine(
                            "addition",
                            null,
                            1,
                            "// " + VerifierCanaries.Repository),
                        new ReviewedDiffLine(
                            "addition",
                            null,
                            2,
                            "// " + VerifierCanaries.Prompt),
                        new ReviewedDiffLine(
                            "addition",
                            null,
                            3,
                            "internal static class CanaryRoute {}"),
                    ]),
            ]);
        var file = new LiveAgentFreshProcessFileDocument(
            path,
            bytes.Length,
            LiveAgentFreshProcessDomain.RawSha256(bytes),
            Convert.ToBase64String(bytes));
        var changed = new LiveAgentFreshProcessChangedFileDocument(
            path,
            null,
            "modified",
            Additions: 3,
            Deletions: 0,
            Changes: 3,
            "available",
            source.PatchSha256,
            SourceTruncated: false);
        var sourceDocument = new LiveAgentFreshProcessDiffSourceDocument(
            path,
            null,
            "modified",
            SourceTruncated: false,
            source.Hunks.Select(hunk =>
                new LiveAgentFreshProcessDiffHunkDocument(
                    hunk.OldStart,
                    hunk.OldCount,
                    hunk.NewStart,
                    hunk.NewCount,
                    hunk.Lines.Select(line =>
                        new LiveAgentFreshProcessDiffLineDocument(
                            line.Kind,
                            line.OldLine,
                            line.NewLine,
                            line.Text)).ToArray())).ToArray());
        return new LiveAgentFreshProcessSnapshotManifestDocument(
            LiveAgentFreshProcessDomain.SnapshotManifestKind,
            LiveAgentFreshProcessDomain.IdentityDocument(identity),
            [path],
            [file],
            [changed],
            [sourceDocument]);
    }

    private static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
