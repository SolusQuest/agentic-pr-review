using System.Collections.Immutable;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

internal static partial class Program
{
    // Frozen 22-char base64url EpochId literals used by fixtures.
    private const string SessionEpochA = "AAAAAAAAAAAAAAAAAAAAAA";
    private const string LedgerEpoch1  = "BBBBBBBBBBBBBBBBBBBBBB";
    private const string LedgerEpoch2  = "CCCCCCCCCCCCCCCCCCCCCC";
    private const string LedgerEpoch3  = "DDDDDDDDDDDDDDDDDDDDDD";

    internal static readonly ExpectedIdentities Ident = new(
        Repository: "acme/example",
        HeadRepository: "acme/example",
        PullRequest: 123,
        WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
        TrustedExecutionDomain: "github-actions",
        ProviderId: "provider.reference",
        ModelId: "model-2026-01",
        AdapterId: new string('a', 64),
        TemplateId: new string('b', 64),
        PolicyId: new string('c', 64),
        ToolDefinitionId: new string('d', 64),
        CacheConfigId: new string('e', 64));

    internal static readonly ExpectedIdentities IdentAltCache = Ident with { AdapterId = new string('f', 64) };

    private const string HeadShaA = "1111111111111111111111111111111111111111";
    private const string HeadShaB = "3333333333333333333333333333333333333333";
    private const string HeadShaC = "4444444444444444444444444444444444444444";
    private const string HeadShaD = "5555555555555555555555555555555555555555";
    private const string HeadShaE = "7777777777777777777777777777777777777777";
    private const string BaseShaA = "2222222222222222222222222222222222222222";
    private const string BaseShaB = "6666666666666666666666666666666666666666";

    private static ReviewContextRecord Ctx(int ordinal, string headSha, string baseSha, ExpectedIdentities identities)
    {
        var interactionId = MakeInteractionId(ordinal);
        // Deterministic subjectDigest (host-supplied pass-through): SHA-256 of the ordinal-tagged label.
        var subjectDigest = LedgerCanonicalizer.ComputeSha256Hex(
            System.Text.Encoding.UTF8.GetBytes("fixture-subject/" + interactionId));
        var source = new ValidatedContextSource
        {
            SubjectDigest = subjectDigest,
            ReviewedHeadSha = headSha,
            ReviewedBaseSha = baseSha,
            ChangedFiles = ImmutableArray.Create(new LedgerChangedFile
            {
                Path = "src/main.cs",
                PreviousPath = null,
                Status = "modified",
                Additions = 1,
                Deletions = 0,
                Changes = 1,
                Patch = new LedgerBoundedPatch
                {
                    Sha256 = new string('9', 64),
                    Truncated = false,
                    MaxChars = 4000,
                },
            }),
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, identities, new InteractionIdentity(interactionId, ordinal));
        return outcome.Record ?? throw new InvalidOperationException("context build failed: " + FirstCode(outcome.Diagnostics));
    }

    private static ReviewOutcomeRecord Outcome(int ordinal, string summary)
    {
        var interactionId = MakeInteractionId(ordinal);
        var source = new ValidatedOutcomeSource
        {
            Summary = summary,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray.Create("No live provider was invoked."),
        };
        var built = LedgerBuilder.BuildReviewOutcome(source, new InteractionIdentity(interactionId, ordinal));
        return built.Record ?? throw new InvalidOperationException("outcome build failed: " + FirstCode(built.Diagnostics));
    }

    // Produce a deterministic 64-hex interaction id unique per ordinal (up to 4B).
    private static string MakeInteractionId(int ordinal)
    {
        var prefix = ordinal.ToString("x8");
        return prefix + new string('0', 64 - prefix.Length);
    }

    private static string FirstCode(ImmutableArray<LedgerDiagnostic> diags)
        => diags.IsDefaultOrEmpty ? "unknown" : diags[0].Code;

    private static ValidatedLedger BuildBootstrap()
    {
        var ctx = SourceContext(0, HeadShaA, BaseShaA, Ident);
        var oc = SourceOutcome(0, "Bootstrap review complete.");
        var transition = new BootstrapTransition(Ident, SessionEpochA, StateGeneration: 0, LedgerEpoch: LedgerEpoch1);
        var built = LedgerBuilder.CreateBootstrap(
            transition,
            ctx, new InteractionIdentity(MakeInteractionId(0), 0),
            oc, new InteractionIdentity(MakeInteractionId(0), 0));
        return built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
    }

    private static ValidatedLedger BuildContinuation(ValidatedLedger predecessor)
    {
        var ctx = SourceContext(1, HeadShaB, BaseShaA, Ident);
        var oc = SourceOutcome(1, "Continuation review complete.");
        var expected = new ContinuationTransition(
            Identities: Ident,
            SessionEpoch: SessionEpochA,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            PredecessorLedgerEpoch: predecessor.Model.Header.LedgerEpoch,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: predecessor.Model.Header.LedgerEpoch);
        var built = LedgerBuilder.AppendContinuation(
            expected, predecessor,
            ctx, new InteractionIdentity(MakeInteractionId(1), 1),
            oc, new InteractionIdentity(MakeInteractionId(1), 1));
        return built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
    }

    private static ValidatedLedger BuildResetCacheContract(ValidatedLedger predecessor)
    {
        var ctx = SourceContext(0, HeadShaC, BaseShaA, IdentAltCache);
        var oc = SourceOutcome(0, "Reset after cache-contract change.");
        var expected = new ResetTransition(
            Identities: IdentAltCache,
            SessionEpoch: SessionEpochA,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: new string('7', 64),
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            PredecessorLedgerEpoch: predecessor.Model.Header.LedgerEpoch,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: LedgerEpoch2,
            ResetReason: "cache_contract_change");
        var built = LedgerBuilder.CreateReset(
            expected, predecessor,
            ctx, new InteractionIdentity(MakeInteractionId(0), 0),
            oc, new InteractionIdentity(MakeInteractionId(0), 0));
        return built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
    }

    private static ValidatedLedger BuildResetBase(ValidatedLedger predecessor)
    {
        var ctx = SourceContext(0, HeadShaD, BaseShaB, Ident);
        var oc = SourceOutcome(0, "Reset after base change.");
        var expected = new ResetTransition(
            Identities: Ident,
            SessionEpoch: SessionEpochA,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: new string('8', 64),
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            PredecessorLedgerEpoch: predecessor.Model.Header.LedgerEpoch,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: LedgerEpoch3,
            ResetReason: "base_change");
        var built = LedgerBuilder.CreateReset(
            expected, predecessor,
            ctx, new InteractionIdentity(MakeInteractionId(0), 0),
            oc, new InteractionIdentity(MakeInteractionId(0), 0));
        return built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
    }

    private static ValidatedLedger BuildRecoveryRoot()
    {
        var ctx = SourceContext(0, HeadShaE, BaseShaA, Ident);
        var oc = SourceOutcome(0, "Recovery root complete.");
        var expected = new RecoveryRootTransition(
            Identities: Ident,
            SessionEpoch: SessionEpochA,
            LedgerEpoch: LedgerEpoch1,
            RecoveryReason: "unavailable_accepted_artifact");
        var built = LedgerBuilder.CreateRecoveryRoot(
            expected,
            ctx, new InteractionIdentity(MakeInteractionId(0), 0),
            oc, new InteractionIdentity(MakeInteractionId(0), 0));
        return built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
    }

    private static ValidatedLedger BuildMaxInteractions()
    {
        var ledger = BuildBootstrap();
        for (var i = 1; i < LedgerLimits.MaxInteractionPairs; i++)
        {
            var ctx = SourceContext(i, HeadShaB, BaseShaA, Ident);
            var oc = SourceOutcome(i, "Continuation " + i);
            var expected = new ContinuationTransition(
                Identities: Ident,
                SessionEpoch: SessionEpochA,
                PredecessorLedgerSha256: ledger.ContentSha256,
                PredecessorStateGeneration: ledger.Model.Header.StateGeneration,
                PredecessorLedgerEpoch: ledger.Model.Header.LedgerEpoch,
                StateGeneration: ledger.Model.Header.StateGeneration + 1,
                LedgerEpoch: ledger.Model.Header.LedgerEpoch);
            var built = LedgerBuilder.AppendContinuation(
                expected, ledger,
                ctx, new InteractionIdentity(MakeInteractionId(i), i),
                oc, new InteractionIdentity(MakeInteractionId(i), i));
            ledger = built.Candidate ?? throw new InvalidOperationException(FirstCode(built.Diagnostics));
        }
        return ledger;
    }

    // -----------------------------------------------------------------
    // Source-DTO constructors (used by tests/mutations that want raw sources)

    private static ValidatedContextSource SourceContext(int ordinal, string headSha, string baseSha, ExpectedIdentities identities)
    {
        var interactionId = MakeInteractionId(ordinal);
        var subjectDigest = LedgerCanonicalizer.ComputeSha256Hex(
            System.Text.Encoding.UTF8.GetBytes("fixture-subject/" + interactionId));
        return new ValidatedContextSource
        {
            SubjectDigest = subjectDigest,
            ReviewedHeadSha = headSha,
            ReviewedBaseSha = baseSha,
            ChangedFiles = ImmutableArray.Create(new LedgerChangedFile
            {
                Path = "src/main.cs",
                PreviousPath = null,
                Status = "modified",
                Additions = 1,
                Deletions = 0,
                Changes = 1,
                Patch = new LedgerBoundedPatch
                {
                    Sha256 = new string('9', 64),
                    Truncated = false,
                    MaxChars = 4000,
                },
            }),
        };
    }

    private static ValidatedOutcomeSource SourceOutcome(int ordinal, string summary)
        => new()
        {
            Summary = summary,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray.Create("No live provider was invoked."),
        };
}
