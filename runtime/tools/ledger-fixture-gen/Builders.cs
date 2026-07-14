using System.Collections.Immutable;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

internal static partial class Program
{
    private static readonly ExpectedIdentities Ident = new(
        Repository: "acme/example",
        HeadRepository: "acme/example",
        PullRequest: 123,
        WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
        TrustedExecutionDomain: "github-actions",
        SessionEpoch: "epoch-0",
        ProviderId: "provider.reference",
        ModelId: "model-2026-01",
        AdapterId: new string('a', 64),
        TemplateId: new string('b', 64),
        PolicyId: new string('c', 64),
        ToolDefinitionId: new string('d', 64),
        CacheConfigId: new string('e', 64));

    private static readonly ExpectedIdentities IdentAltCache = Ident with { AdapterId = new string('f', 64) };

    private static ReviewContextRecord Ctx(int ordinal, string headSha, string baseSha, ExpectedIdentities identities)
    {
        var interactionId = MakeInteractionId(ordinal);
        var source = new ValidatedContextSource(
            ReviewedHeadSha: headSha,
            ReviewedBaseSha: baseSha,
            ChangedFiles: ImmutableArray.Create(
                new ValidatedChangedFileSource(
                    Path: "src/main.cs",
                    PreviousPath: null,
                    Status: "modified",
                    Additions: 1,
                    Deletions: 0,
                    Changes: 1,
                    Patch: new ValidatedPatchSource(new string('9', 64), false, 4000))));
        var outcome = LedgerBuilder.BuildReviewContext(source, identities, new InteractionIdentity(interactionId, ordinal));
        return outcome.Record ?? throw new InvalidOperationException("context build failed: " + outcome.Failure?.Code);
    }

    private static ReviewOutcomeRecord Outcome(int ordinal, string summary)
    {
        var interactionId = MakeInteractionId(ordinal);
        var source = new ValidatedOutcomeSource(
            Summary: summary,
            Findings: ImmutableArray<ValidatedFindingSource>.Empty,
            Limitations: ImmutableArray.Create("No live provider was invoked."));
        var built = LedgerBuilder.BuildReviewOutcome(source, new InteractionIdentity(interactionId, ordinal));
        return built.Record ?? throw new InvalidOperationException("outcome build failed: " + built.Failure?.Code);
    }

    private static char IdChar(int ordinal)
    {
        var hexChars = new[] { '0','1','2','3','4','5','6','7','8','9','a','b','c','d','e','f' };
        return hexChars[ordinal % 16];
    }

    // Produce a deterministic 64-hex interaction id unique per ordinal (up to millions).
    private static string MakeInteractionId(int ordinal)
    {
        // Use the ordinal as an 8-hex-digit prefix; pad the rest with '0'.
        var prefix = ordinal.ToString("x8");
        return prefix + new string('0', 64 - prefix.Length);
    }

    private static ValidatedLedger BuildBootstrap()
    {
        var ctx = Ctx(0, "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", Ident);
        var oc = Outcome(0, "Bootstrap review complete.");
        var built = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Ident, 0, 1), ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildContinuation(ValidatedLedger predecessor)
    {
        var ctx = Ctx(1, "3333333333333333333333333333333333333333", "2222222222222222222222222222222222222222", Ident);
        var oc = Outcome(1, "Continuation review complete.");
        var expected = new ContinuationTransition(Ident, predecessor.ContentSha256, 0, 1, 1);
        var built = LedgerBuilder.AppendContinuation(predecessor, expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildResetCacheContract(ValidatedLedger predecessor)
    {
        var ctx = Ctx(0, "4444444444444444444444444444444444444444", "2222222222222222222222222222222222222222", IdentAltCache);
        var oc = Outcome(0, "Reset after cache-contract change.");
        var expected = new ResetTransition(
            Identities: IdentAltCache,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: new string('7', 64),
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            LedgerEpoch: 2,
            ResetReason: "cache_contract_changed");
        var built = LedgerBuilder.CreateReset(predecessor, expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildResetBase(ValidatedLedger predecessor)
    {
        var ctx = Ctx(0, "5555555555555555555555555555555555555555", "6666666666666666666666666666666666666666", Ident);
        var oc = Outcome(0, "Reset after base change.");
        var expected = new ResetTransition(
            Identities: Ident,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: new string('8', 64),
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            LedgerEpoch: 2,
            ResetReason: "base_changed");
        var built = LedgerBuilder.CreateReset(predecessor, expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildRecovery()
    {
        var ctx = Ctx(0, "7777777777777777777777777777777777777777", "2222222222222222222222222222222222222222", Ident);
        var oc = Outcome(0, "Recovery review complete.");
        var expected = new RecoveryTransition(Ident, 0, 1, "predecessor_unavailable");
        var built = LedgerBuilder.CreateRecovery(expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildMaxInteractions()
    {
        var ledger = BuildBootstrap();
        for (var i = 1; i < 32; i++)
        {
            var ctx = Ctx(i, "3333333333333333333333333333333333333333", "2222222222222222222222222222222222222222", Ident);
            var oc = Outcome(i, "Continuation " + i);
            var expected = new ContinuationTransition(Ident, ledger.ContentSha256, i - 1, i, 1);
            var built = LedgerBuilder.AppendContinuation(ledger, expected, ctx, oc);
            ledger = built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
        }
        return ledger;
    }

    private static ValidatedLedger BuildNearByteLimit()
    {
        var ledger = BuildLargePairsBootstrap();
        for (var i = 1; i < 31; i++)
        {
            var ctx = Ctx(i, "3333333333333333333333333333333333333333", "2222222222222222222222222222222222222222", Ident);
            var oc = LargeOutcome(i);
            var expected = new ContinuationTransition(Ident, ledger.ContentSha256, i - 1, i, 1);
            var built = LedgerBuilder.AppendContinuation(ledger, expected, ctx, oc);
            ledger = built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
        }
        return ledger;
    }

    private static ValidatedLedger BuildLargePairsBootstrap()
    {
        var ctx = Ctx(0, "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", Ident);
        var oc = LargeOutcome(0);
        var built = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Ident, 0, 1), ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ReviewOutcomeRecord LargeOutcome(int ordinal)
    {
        var summary = new string('x', LedgerLimits.MaxSummaryChars);
        var interactionId = MakeInteractionId(ordinal);
        var source = new ValidatedOutcomeSource(
            Summary: summary,
            Findings: ImmutableArray<ValidatedFindingSource>.Empty,
            Limitations: ImmutableArray<string>.Empty);
        var built = LedgerBuilder.BuildReviewOutcome(source, new InteractionIdentity(interactionId, ordinal));
        return built.Record!;
    }
}



