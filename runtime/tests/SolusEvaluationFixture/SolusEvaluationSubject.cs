using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.SolusFreshHostProof;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal sealed partial class EvaluationAttempt
{
    internal static EvaluationAttempt? AdmitSolus(EvaluationRunInput run, SolusWitness witness, string policyFingerprint) =>
        run is { Valid: true, Mode: "deterministic" } && witness.ExecutionId != Guid.Empty &&
        witness.ConfigurationFingerprint == run.ProviderConfigurationSha256 && EvaluationLimits.Hash(policyFingerprint)
            ? new(run, Hash("configuration", "solus-managed", policyFingerprint, witness.ConfigurationFingerprint, run.Mode)) : null;
}

internal sealed partial class EvaluationSubject
{
    private EvaluationSubject(EvaluationAttempt attempt, ReviewedIdentity identity, AgentTerminalReview review,
        ImmutableArray<EvaluationObservation> observations, string execution)
    {
        Attempt = attempt;
        ReviewedIdentity = identity;
        Findings = review.Findings;
        Observations = observations.Select(o => new RequiredObservation(o.Tool, o.Observation.ObservationId)).ToImmutableArray();
        GroundedObservations = observations;
        ExecutionSha256 = execution;
    }

    // Compiled fixture-local live boundary, not a report/transcript importer.
    // Durable state acceptance and context transfer remain separate Host facts.
    internal static EvaluationSubject? AdmitSolus(SolusFreshReviewResult? result, ReviewedIdentity identity,
        EvaluationRunInput run, SolusWitness witness, string policyFingerprint, IReadOnlyList<string> wireDigests)
    {
        try
        {
            var attempt = EvaluationAttempt.AdmitSolus(run, witness, policyFingerprint);
            var context = result?.Context;
            var usage = result?.Work?.CurrentUsage;
            if (attempt is null || result is not { ProductReviewAccepted: true, TerminalAccepted: true } ||
                result.Review is null || identity != witness.Identity || result.ExecutionId != witness.ExecutionId || context?.ExecutionId != result.ExecutionId ||
                context.Outcome?.ExecutionId != result.ExecutionId || context.Outcome.Reason != AgentTerminationReason.Completed ||
                !((context.Intent == ContextExecutionIntent.Fresh && context.Admission == ContextAdmission.Fresh && context.History.Count == 0) ||
                  (context.Intent == ContextExecutionIntent.NewRunFromContext && context.Admission == ContextAdmission.Supplied && context.History.Count > 0)) ||
                usage is not { Coverage: UsageInventoryCoverage.Complete } || usage.ExecutionId != result.ExecutionId ||
                usage.Attempts.Count != witness.Exchanges || wireDigests.Count != witness.Exchanges ||
                wireDigests.Any(d => !EvaluationLimits.Hash(d)) || witness.Final is null ||
                !witness.TryProject(identity, result.Observations, out var observations) ||
                !AgentToolArguments.TryFinishReviewProvider(witness.Final, out var terminal) ||
                !TerminalReviewValidator.TryValidate(terminal!, identity, result.Observations, out var validated)) return null;
            var actual = validated!;
            var returned = AgentToolArguments.WriteFinishReview(result.Review.Summary, result.Review.Findings);
            if (actual.TerminalSha256 != result.Review.TerminalSha256 ||
                !actual.CanonicalBytes.AsSpan().SequenceEqual(result.Review.CanonicalBytes) ||
                !actual.CanonicalBytes.AsSpan().SequenceEqual(returned)) return null;
            var history = context.History.Select(r => HashRound(r.LogicalWorkId, r.RoundId, r.Usage.ExecutionId,
                r.Usage.Attempts.Count, r.Usage.ToolInvocations?.Invoked));
            var execution = EvaluationAttempt.Hash("execution", attempt.AttemptSha256, result.ExecutionId.ToString("N"),
                context.Intent.ToString(), context.Checkpoint?.CheckpointId.ToString("N") ?? "no-capture",
                context.Checkpoint?.RoundId.ToString("N") ?? "no-capture", string.Join(',', history),
                actual.TerminalSha256, string.Join(',', observations.Select(o => o.Tool + ":" + o.Observation.ObservationId)),
                string.Join(',', wireDigests));
            return new(attempt, identity, actual, observations, execution);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }

    private static string HashRound(Guid work, Guid round, Guid execution, int attempts, int? tools) =>
        EvaluationAttempt.Hash("solus-historical-round", work.ToString("N"), round.ToString("N"), execution.ToString("N"),
            attempts.ToString(System.Globalization.CultureInfo.InvariantCulture), tools?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
}
