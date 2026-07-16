using System.Collections.Immutable;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerTransitionValidator
{
    public static TransitionOutcome ValidateBootstrap(BootstrapTransition expected, ValidatedLedger candidate)
    {
        var header = candidate.Model.Header;
        if (header.Kind != "bootstrap")
        {
            return new TransitionOutcome(ImmutableArray.Create(KindMismatch()));
        }

        var builder = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (!IdentitiesMatch(expected.Identities, header))
        {
            builder.Add(IdentityMismatch());
        }

        if (expected.SessionEpoch != header.SessionEpoch)
        {
            builder.Add(SessionEpochMismatch());
        }

        if (expected.LedgerEpoch != header.LedgerEpoch)
        {
            builder.Add(LedgerEpochMismatch());
        }

        if (expected.StateGeneration != header.StateGeneration)
        {
            builder.Add(StateGenerationMismatch());
        }

        if (!IsRootRecordsShape(candidate.Model))
        {
            builder.Add(RootRecordsShapeMismatch());
        }

        return new TransitionOutcome(builder.ToImmutable());
    }

    public static TransitionOutcome ValidateContinuation(
        ContinuationTransition expected,
        ValidatedLedger predecessor,
        ValidatedLedger candidate)
    {
        var header = candidate.Model.Header;
        if (header.Kind != "continuation")
        {
            return new TransitionOutcome(ImmutableArray.Create(KindMismatch()));
        }

        var builder = ImmutableArray.CreateBuilder<LedgerDiagnostic>();
        var predHeader = predecessor.Model.Header;

        if (!IdentitiesMatch(expected.Identities, header) || !IdentitiesMatch(expected.Identities, predHeader))
        {
            builder.Add(IdentityMismatch());
        }

        if (expected.SessionEpoch != header.SessionEpoch || expected.SessionEpoch != predHeader.SessionEpoch)
        {
            builder.Add(SessionEpochMismatch());
        }

        if (expected.LedgerEpoch != header.LedgerEpoch || expected.LedgerEpoch != predHeader.LedgerEpoch)
        {
            builder.Add(LedgerEpochMismatch());
        }

        if (expected.StateGeneration != header.StateGeneration ||
            predHeader.StateGeneration + 1 != header.StateGeneration)
        {
            builder.Add(StateGenerationMismatch());
        }

        if (expected.PredecessorLedgerSha256 != header.PredecessorLedgerSha256 ||
            predecessor.ContentSha256 != header.PredecessorLedgerSha256)
        {
            builder.Add(PredecessorHashMismatch());
        }

        if (expected.PredecessorLedgerEpoch != header.PredecessorLedgerEpoch ||
            predHeader.LedgerEpoch != header.PredecessorLedgerEpoch)
        {
            builder.Add(PredecessorLedgerEpochMismatch());
        }

        if (expected.PredecessorStateGeneration != header.PredecessorStateGeneration ||
            predHeader.StateGeneration != header.PredecessorStateGeneration)
        {
            builder.Add(PredecessorGenerationMismatch());
        }

        if (!IsContinuationPrefixValid(predecessor, candidate))
        {
            builder.Add(ContinuationPrefixMismatch());
        }

        return new TransitionOutcome(builder.ToImmutable());
    }

    public static TransitionOutcome ValidateReset(
        ResetTransition expected,
        ValidatedLedger predecessor,
        ValidatedLedger candidate)
    {
        var header = candidate.Model.Header;
        if (header.Kind != "reset")
        {
            return new TransitionOutcome(ImmutableArray.Create(KindMismatch()));
        }

        var builder = ImmutableArray.CreateBuilder<LedgerDiagnostic>();
        var predHeader = predecessor.Model.Header;

        if (!SessionScopeIdentitiesMatch(expected.Identities, header) ||
            !SessionScopeIdentitiesMatch(expected.Identities, predHeader))
        {
            builder.Add(IdentityMismatch());
        }
        else if (expected.ResetReason != "cache_contract_change" &&
                 !CacheContractIdentitiesMatch(header, predHeader))
        {
            builder.Add(IdentityMismatch());
        }
        else if (!CacheContractIdentitiesMatch(expected.Identities, header))
        {
            builder.Add(IdentityMismatch());
        }

        if (expected.SessionEpoch != header.SessionEpoch || expected.SessionEpoch != predHeader.SessionEpoch)
        {
            builder.Add(SessionEpochMismatch());
        }

        if (expected.LedgerEpoch != header.LedgerEpoch)
        {
            builder.Add(LedgerEpochMismatch());
        }
        else if (header.LedgerEpoch == predHeader.LedgerEpoch)
        {
            builder.Add(ResetEpochNotFresh());
        }

        if (expected.StateGeneration != header.StateGeneration ||
            predHeader.StateGeneration + 1 != header.StateGeneration)
        {
            builder.Add(StateGenerationMismatch());
        }

        if (expected.PredecessorLedgerSha256 != header.PredecessorLedgerSha256 ||
            predecessor.ContentSha256 != header.PredecessorLedgerSha256)
        {
            builder.Add(PredecessorHashMismatch());
        }

        if (expected.PredecessorManifestSha256 != header.PredecessorManifestSha256)
        {
            builder.Add(PredecessorManifestHashMismatch());
        }

        if (expected.PredecessorLedgerEpoch != header.PredecessorLedgerEpoch ||
            predHeader.LedgerEpoch != header.PredecessorLedgerEpoch)
        {
            builder.Add(PredecessorLedgerEpochMismatch());
        }

        if (expected.PredecessorStateGeneration != header.PredecessorStateGeneration ||
            predHeader.StateGeneration != header.PredecessorStateGeneration)
        {
            builder.Add(PredecessorGenerationMismatch());
        }

        if (expected.ResetReason != header.ResetReason)
        {
            builder.Add(ResetReasonMismatch());
        }

        if (!IsResetRecordsShape(candidate.Model))
        {
            builder.Add(ResetRecordsShapeMismatch());
        }

        return new TransitionOutcome(builder.ToImmutable());
    }

    public static TransitionOutcome ValidateRecoveryRoot(
        RecoveryRootTransition expected,
        ValidatedLedger candidate)
    {
        var header = candidate.Model.Header;
        if (header.Kind != "recovery_root")
        {
            return new TransitionOutcome(ImmutableArray.Create(KindMismatch()));
        }

        var builder = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (!IdentitiesMatch(expected.Identities, header))
        {
            builder.Add(IdentityMismatch());
        }

        if (expected.SessionEpoch != header.SessionEpoch)
        {
            builder.Add(SessionEpochMismatch());
        }

        if (expected.LedgerEpoch != header.LedgerEpoch)
        {
            builder.Add(LedgerEpochMismatch());
        }

        if (expected.StateGeneration != header.StateGeneration)
        {
            builder.Add(StateGenerationMismatch());
        }

        if (expected.RecoveryReason != header.RecoveryReason)
        {
            builder.Add(RecoveryRootReasonMismatch());
        }

        if (!IsRootRecordsShape(candidate.Model))
        {
            builder.Add(RootRecordsShapeMismatch());
        }

        return new TransitionOutcome(builder.ToImmutable());
    }

    private static bool IdentitiesMatch(ExpectedIdentities expected, LedgerHeader header)
    {
        return expected.Repository == header.Repository &&
               expected.HeadRepository == header.HeadRepository &&
               expected.PullRequest == header.PullRequest &&
               expected.WorkflowIdentity == header.WorkflowIdentity &&
               expected.TrustedExecutionDomain == header.TrustedExecutionDomain &&
               expected.ProviderId == header.ProviderId &&
               expected.ModelId == header.ModelId &&
               expected.AdapterId == header.AdapterId &&
               expected.TemplateId == header.TemplateId &&
               expected.PolicyId == header.PolicyId &&
               expected.ToolDefinitionId == header.ToolDefinitionId &&
               expected.CacheConfigId == header.CacheConfigId;
    }

    private static bool SessionScopeIdentitiesMatch(ExpectedIdentities expected, LedgerHeader header)
    {
        return expected.Repository == header.Repository &&
               expected.HeadRepository == header.HeadRepository &&
               expected.PullRequest == header.PullRequest &&
               expected.WorkflowIdentity == header.WorkflowIdentity &&
               expected.TrustedExecutionDomain == header.TrustedExecutionDomain;
    }

    private static bool CacheContractIdentitiesMatch(ExpectedIdentities expected, LedgerHeader header)
    {
        return expected.ProviderId == header.ProviderId &&
               expected.ModelId == header.ModelId &&
               expected.AdapterId == header.AdapterId &&
               expected.TemplateId == header.TemplateId &&
               expected.PolicyId == header.PolicyId &&
               expected.ToolDefinitionId == header.ToolDefinitionId &&
               expected.CacheConfigId == header.CacheConfigId;
    }

    private static bool CacheContractIdentitiesMatch(LedgerHeader a, LedgerHeader b)
    {
        return a.ProviderId == b.ProviderId &&
               a.ModelId == b.ModelId &&
               a.AdapterId == b.AdapterId &&
               a.TemplateId == b.TemplateId &&
               a.PolicyId == b.PolicyId &&
               a.ToolDefinitionId == b.ToolDefinitionId &&
               a.CacheConfigId == b.CacheConfigId;
    }

    private static bool IsRootRecordsShape(LedgerModel model)
    {
        return IsResetRecordsShape(model);
    }

    private static bool IsResetRecordsShape(LedgerModel model)
    {
        if (model.Records.Length != 2)
        {
            return false;
        }

        if (model.Records[0] is not ReviewContextRecord context ||
            model.Records[1] is not ReviewOutcomeRecord outcome)
        {
            return false;
        }

        return context.Role == "review_context" &&
               outcome.Role == "review_outcome" &&
               context.InteractionOrdinal == 0 &&
               outcome.InteractionOrdinal == 0;
    }

    private static bool IsContinuationPrefixValid(ValidatedLedger predecessor, ValidatedLedger candidate)
    {
        if (candidate.Model.Records.Length != predecessor.Model.Records.Length + 2)
        {
            return false;
        }

        for (var i = 0; i < predecessor.Model.Records.Length; i++)
        {
            var predBytes = LedgerCanonicalizer.SerializeRecord(predecessor.Model.Records[i]);
            var candBytes = LedgerCanonicalizer.SerializeRecord(candidate.Model.Records[i]);
            if (!predBytes.SequenceEqual(candBytes))
            {
                return false;
            }
        }

        return true;
    }

    private static LedgerDiagnostic KindMismatch() =>
        new() { Code = LedgerDiagnosticCodes.TransitionKindMismatch, Message = "Candidate header kind does not match expected transition kind." };

    private static LedgerDiagnostic IdentityMismatch() =>
        new() { Code = LedgerDiagnosticCodes.IdentityMismatch, Message = "Identity fields do not match expected values." };

    private static LedgerDiagnostic SessionEpochMismatch() =>
        new() { Code = LedgerDiagnosticCodes.SessionEpochMismatch, Message = "sessionEpoch does not match expected value." };

    private static LedgerDiagnostic LedgerEpochMismatch() =>
        new() { Code = LedgerDiagnosticCodes.LedgerEpochMismatch, Message = "ledgerEpoch does not match expected value." };

    private static LedgerDiagnostic ResetEpochNotFresh() =>
        new() { Code = LedgerDiagnosticCodes.ResetEpochNotFresh, Message = "Reset ledgerEpoch is not fresh; it matches predecessor ledgerEpoch." };

    private static LedgerDiagnostic StateGenerationMismatch() =>
        new() { Code = LedgerDiagnosticCodes.StateGenerationMismatch, Message = "stateGeneration does not match expected value." };

    private static LedgerDiagnostic PredecessorHashMismatch() =>
        new() { Code = LedgerDiagnosticCodes.PredecessorHashMismatch, Message = "predecessorLedgerSha256 does not match expected or predecessor content hash." };

    private static LedgerDiagnostic PredecessorManifestHashMismatch() =>
        new() { Code = LedgerDiagnosticCodes.PredecessorManifestHashMismatch, Message = "predecessorManifestSha256 does not match expected value." };

    private static LedgerDiagnostic PredecessorLedgerEpochMismatch() =>
        new() { Code = LedgerDiagnosticCodes.PredecessorLedgerEpochMismatch, Message = "predecessorLedgerEpoch does not match expected or predecessor ledgerEpoch." };

    private static LedgerDiagnostic PredecessorGenerationMismatch() =>
        new() { Code = LedgerDiagnosticCodes.PredecessorGenerationMismatch, Message = "predecessorStateGeneration does not match expected or predecessor stateGeneration." };

    private static LedgerDiagnostic ResetReasonMismatch() =>
        new() { Code = LedgerDiagnosticCodes.ResetReasonMismatch, Message = "resetReason does not match expected value." };

    private static LedgerDiagnostic RecoveryRootReasonMismatch() =>
        new() { Code = LedgerDiagnosticCodes.RecoveryRootReasonMismatch, Message = "recoveryReason does not match expected value." };

    private static LedgerDiagnostic ContinuationPrefixMismatch() =>
        new() { Code = LedgerDiagnosticCodes.ContinuationPrefixMismatch, Message = "Continuation candidate does not preserve predecessor records." };

    private static LedgerDiagnostic RootRecordsShapeMismatch() =>
        new() { Code = LedgerDiagnosticCodes.RootRecordsShapeMismatch, Message = "Root ledger does not contain exactly one context/outcome pair with ordinal 0." };

    private static LedgerDiagnostic ResetRecordsShapeMismatch() =>
        new() { Code = LedgerDiagnosticCodes.ResetRecordsShapeMismatch, Message = "Reset ledger does not contain exactly one context/outcome pair with ordinal 0." };
}
