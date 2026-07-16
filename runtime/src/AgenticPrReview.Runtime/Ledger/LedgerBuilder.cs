using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerBuilder
{
    public static BuildOutcome<ReviewContextRecord> BuildReviewContext(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        var unicodeDiagnostic = ScanContextSource(identities, source, interaction);
        if (unicodeDiagnostic is not null)
        {
            return new BuildOutcome<ReviewContextRecord>(null, ImmutableArray.Create(unicodeDiagnostic));
        }

        var record = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = interaction.InteractionId,
            InteractionOrdinal = interaction.InteractionOrdinal,
            SubjectDigest = source.SubjectDigest,
            CacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities),
            ReviewedHeadSha = source.ReviewedHeadSha,
            ReviewedBaseSha = source.ReviewedBaseSha,
            ChangedFiles = source.ChangedFiles
        };

        var schemaDiagnostics = ValidateRecordSchema(record, identities);
        if (!schemaDiagnostics.IsEmpty)
        {
            return new BuildOutcome<ReviewContextRecord>(null, schemaDiagnostics);
        }

        var identityDiagnostic = CheckIdentityStrings(identities);
        if (identityDiagnostic is not null)
        {
            return new BuildOutcome<ReviewContextRecord>(null, ImmutableArray.Create(identityDiagnostic));
        }

        return new BuildOutcome<ReviewContextRecord>(record, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    public static BuildOutcome<ReviewOutcomeRecord> BuildReviewOutcome(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        var unicodeDiagnostic = ScanOutcomeSource(source, interaction);
        if (unicodeDiagnostic is not null)
        {
            return new BuildOutcome<ReviewOutcomeRecord>(null, ImmutableArray.Create(unicodeDiagnostic));
        }

        var record = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = interaction.InteractionId,
            InteractionOrdinal = interaction.InteractionOrdinal,
            Summary = source.Summary,
            Findings = source.Findings,
            Limitations = source.Limitations
        };

        // Dummy identities are only used for the temporary ledger header; the outcome record does not depend on them.
        var dummyIdentities = new ExpectedIdentities(
            "owner/repo", "owner/repo", 1,
            "ci", "trusted",
            "provider", "model",
            "adapter", "template", "policy", "tools", "cacheconfig");

        var schemaDiagnostics = ValidateRecordSchema(record, dummyIdentities);
        if (!schemaDiagnostics.IsEmpty)
        {
            return new BuildOutcome<ReviewOutcomeRecord>(null, schemaDiagnostics);
        }

        var findingDiagnostic = CheckFindingLocations(record);
        if (findingDiagnostic is not null)
        {
            return new BuildOutcome<ReviewOutcomeRecord>(null, ImmutableArray.Create(findingDiagnostic));
        }

        return new BuildOutcome<ReviewOutcomeRecord>(record, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    public static CandidateOutcome CreateBootstrap(
        BootstrapTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        var header = new LedgerHeader
        {
            Kind = "bootstrap",
            SessionEpoch = expected.SessionEpoch,
            LedgerEpoch = expected.LedgerEpoch,
            StateGeneration = expected.StateGeneration,
            PredecessorLedgerSha256 = "bootstrap",
            Repository = expected.Identities.Repository,
            HeadRepository = expected.Identities.HeadRepository,
            PullRequest = expected.Identities.PullRequest,
            WorkflowIdentity = expected.Identities.WorkflowIdentity,
            TrustedExecutionDomain = expected.Identities.TrustedExecutionDomain,
            ProviderId = expected.Identities.ProviderId,
            ModelId = expected.Identities.ModelId,
            AdapterId = expected.Identities.AdapterId,
            TemplateId = expected.Identities.TemplateId,
            PolicyId = expected.Identities.PolicyId,
            ToolDefinitionId = expected.Identities.ToolDefinitionId,
            CacheConfigId = expected.Identities.CacheConfigId
        };

        return BuildCandidate(expected, header, ImmutableArray.Create<LedgerRecord>(context, outcome), predecessor: null);
    }

    public static CandidateOutcome AppendContinuation(
        ContinuationTransition expected, ValidatedLedger predecessor,
        ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        var header = new LedgerHeader
        {
            Kind = "continuation",
            SessionEpoch = expected.SessionEpoch,
            LedgerEpoch = expected.LedgerEpoch,
            StateGeneration = expected.StateGeneration,
            PredecessorLedgerSha256 = expected.PredecessorLedgerSha256,
            PredecessorLedgerEpoch = expected.PredecessorLedgerEpoch,
            PredecessorStateGeneration = expected.PredecessorStateGeneration,
            Repository = expected.Identities.Repository,
            HeadRepository = expected.Identities.HeadRepository,
            PullRequest = expected.Identities.PullRequest,
            WorkflowIdentity = expected.Identities.WorkflowIdentity,
            TrustedExecutionDomain = expected.Identities.TrustedExecutionDomain,
            ProviderId = expected.Identities.ProviderId,
            ModelId = expected.Identities.ModelId,
            AdapterId = expected.Identities.AdapterId,
            TemplateId = expected.Identities.TemplateId,
            PolicyId = expected.Identities.PolicyId,
            ToolDefinitionId = expected.Identities.ToolDefinitionId,
            CacheConfigId = expected.Identities.CacheConfigId
        };

        var records = predecessor.Model.Records.Add(context).Add(outcome);
        return BuildCandidate(expected, header, records, predecessor);
    }

    public static CandidateOutcome CreateReset(
        ResetTransition expected, ValidatedLedger predecessor,
        ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        var header = new LedgerHeader
        {
            Kind = "reset",
            SessionEpoch = expected.SessionEpoch,
            LedgerEpoch = expected.LedgerEpoch,
            StateGeneration = expected.StateGeneration,
            PredecessorLedgerSha256 = expected.PredecessorLedgerSha256,
            PredecessorManifestSha256 = expected.PredecessorManifestSha256,
            PredecessorLedgerEpoch = expected.PredecessorLedgerEpoch,
            PredecessorStateGeneration = expected.PredecessorStateGeneration,
            ResetReason = expected.ResetReason,
            Repository = expected.Identities.Repository,
            HeadRepository = expected.Identities.HeadRepository,
            PullRequest = expected.Identities.PullRequest,
            WorkflowIdentity = expected.Identities.WorkflowIdentity,
            TrustedExecutionDomain = expected.Identities.TrustedExecutionDomain,
            ProviderId = expected.Identities.ProviderId,
            ModelId = expected.Identities.ModelId,
            AdapterId = expected.Identities.AdapterId,
            TemplateId = expected.Identities.TemplateId,
            PolicyId = expected.Identities.PolicyId,
            ToolDefinitionId = expected.Identities.ToolDefinitionId,
            CacheConfigId = expected.Identities.CacheConfigId
        };

        return BuildCandidate(expected, header, ImmutableArray.Create<LedgerRecord>(context, outcome), predecessor);
    }

    public static CandidateOutcome CreateRecoveryRoot(
        RecoveryRootTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        var header = new LedgerHeader
        {
            Kind = "recovery_root",
            SessionEpoch = expected.SessionEpoch,
            LedgerEpoch = expected.LedgerEpoch,
            StateGeneration = expected.StateGeneration,
            PredecessorLedgerSha256 = "bootstrap",
            RecoveryReason = expected.RecoveryReason,
            Repository = expected.Identities.Repository,
            HeadRepository = expected.Identities.HeadRepository,
            PullRequest = expected.Identities.PullRequest,
            WorkflowIdentity = expected.Identities.WorkflowIdentity,
            TrustedExecutionDomain = expected.Identities.TrustedExecutionDomain,
            ProviderId = expected.Identities.ProviderId,
            ModelId = expected.Identities.ModelId,
            AdapterId = expected.Identities.AdapterId,
            TemplateId = expected.Identities.TemplateId,
            PolicyId = expected.Identities.PolicyId,
            ToolDefinitionId = expected.Identities.ToolDefinitionId,
            CacheConfigId = expected.Identities.CacheConfigId
        };

        return BuildCandidate(expected, header, ImmutableArray.Create<LedgerRecord>(context, outcome), predecessor: null);
    }

    private static CandidateOutcome BuildCandidate(
        ExpectedTransition expected,
        LedgerHeader header,
        ImmutableArray<LedgerRecord> records,
        ValidatedLedger? predecessor)
    {
        var model = new LedgerModel
        {
            SchemaVersion = 1,
            PrefixContractVersion = 1,
            Header = header,
            Records = records
        };

        var unicodeDiagnostic = ScanCandidateModel(model);
        if (unicodeDiagnostic is not null)
        {
            return new CandidateOutcome(null, ImmutableArray.Create(unicodeDiagnostic));
        }

        ImmutableArray<byte> canonicalBytes;
        try
        {
            canonicalBytes = LedgerCanonicalizer.SerializeCanonical(model);
        }
        catch (LedgerCanonicalizationException ex)
        {
            return new CandidateOutcome(null, ImmutableArray.Create(
                new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = ex.Message }));
        }

        if (records.Length > 64)
        {
            return OverBoundAppend(LedgerDiagnosticCodes.InteractionLimitExceeded);
        }

        if (canonicalBytes.Length > LedgerParser.LedgerCanonicalByteLimit)
        {
            return OverBoundAppend(LedgerDiagnosticCodes.CanonicalByteLimitExceeded);
        }

        var identityDiagnostic = CheckIdentityStrings(expected.Identities);
        if (identityDiagnostic is not null)
        {
            return new CandidateOutcome(null, ImmutableArray.Create(identityDiagnostic));
        }

        var semanticDiagnostics = LedgerParser.RunSemanticInvariants(model);
        if (!semanticDiagnostics.IsEmpty)
        {
            return new CandidateOutcome(null, semanticDiagnostics);
        }

        var parseOutcome = LedgerParser.ParseAndValidate(canonicalBytes.AsSpan());
        if (parseOutcome.Ledger is null)
        {
            return new CandidateOutcome(null, parseOutcome.Diagnostics);
        }

        var transitionOutcome = expected switch
        {
            BootstrapTransition bt => LedgerTransitionValidator.ValidateBootstrap(bt, parseOutcome.Ledger),
            ContinuationTransition ct when predecessor is not null =>
                LedgerTransitionValidator.ValidateContinuation(ct, predecessor, parseOutcome.Ledger),
            ResetTransition rt when predecessor is not null =>
                LedgerTransitionValidator.ValidateReset(rt, predecessor, parseOutcome.Ledger),
            RecoveryRootTransition rrt => LedgerTransitionValidator.ValidateRecoveryRoot(rrt, parseOutcome.Ledger),
            _ => new TransitionOutcome(ImmutableArray.Create(
                new LedgerDiagnostic { Code = LedgerDiagnosticCodes.TransitionKindMismatch, Message = "Unsupported transition type." }))
        };

        if (!transitionOutcome.Diagnostics.IsEmpty)
        {
            return new CandidateOutcome(null, transitionOutcome.Diagnostics);
        }

        return new CandidateOutcome(parseOutcome.Ledger, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    private static ImmutableArray<LedgerDiagnostic> ValidateRecordSchema(LedgerRecord record, ExpectedIdentities identities)
    {
        var dummyHeader = new LedgerHeader
        {
            Kind = "bootstrap",
            SessionEpoch = "aaaaaaaaaaaaaaaaaaaaaa",
            LedgerEpoch = "bbbbbbbbbbbbbbbbbbbbbb",
            StateGeneration = 0,
            PredecessorLedgerSha256 = "bootstrap",
            Repository = identities.Repository,
            HeadRepository = identities.HeadRepository,
            PullRequest = identities.PullRequest,
            WorkflowIdentity = identities.WorkflowIdentity,
            TrustedExecutionDomain = identities.TrustedExecutionDomain,
            ProviderId = identities.ProviderId,
            ModelId = identities.ModelId,
            AdapterId = identities.AdapterId,
            TemplateId = identities.TemplateId,
            PolicyId = identities.PolicyId,
            ToolDefinitionId = identities.ToolDefinitionId,
            CacheConfigId = identities.CacheConfigId
        };

        var cacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities);
        ImmutableArray<LedgerRecord> records;
        if (record is ReviewContextRecord context)
        {
            var dummyOutcome = new ReviewOutcomeRecord
            {
                Role = "review_outcome",
                InteractionId = context.InteractionId,
                InteractionOrdinal = context.InteractionOrdinal,
                Summary = "x",
                Findings = ImmutableArray<LedgerFinding>.Empty,
                Limitations = ImmutableArray<string>.Empty
            };
            records = ImmutableArray.Create<LedgerRecord>(context, dummyOutcome);
        }
        else if (record is ReviewOutcomeRecord outcome)
        {
            var dummyContext = new ReviewContextRecord
            {
                Role = "review_context",
                InteractionId = outcome.InteractionId,
                InteractionOrdinal = outcome.InteractionOrdinal,
                SubjectDigest = "0000000000000000000000000000000000000000000000000000000000000000",
                CacheContractDigest = cacheContractDigest,
                ReviewedHeadSha = "0000000000000000000000000000000000000000",
                ReviewedBaseSha = "1111111111111111111111111111111111111111",
                ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
            };
            records = ImmutableArray.Create<LedgerRecord>(dummyContext, outcome);
        }
        else
        {
            return ImmutableArray.Create(new LedgerDiagnostic
            {
                Code = LedgerDiagnosticCodes.SchemaViolation,
                Message = "Unsupported record type."
            });
        }

        var tempModel = new LedgerModel
        {
            SchemaVersion = 1,
            PrefixContractVersion = 1,
            Header = dummyHeader,
            Records = records
        };

        ImmutableArray<byte> canonicalBytes;
        try
        {
            canonicalBytes = LedgerCanonicalizer.SerializeCanonical(tempModel);
        }
        catch (LedgerCanonicalizationException ex)
        {
            return ImmutableArray.Create(new LedgerDiagnostic
            {
                Code = LedgerDiagnosticCodes.InvalidUnicode,
                Message = ex.Message
            });
        }

        using var document = JsonDocument.Parse(canonicalBytes.AsSpan().ToArray());
        var results = SchemaContracts.Load(typeof(LedgerBuilder).Assembly).GetSchema(SchemaKind.Ledger).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            return LedgerSchemaMapper.Map(document, results);
        }

        return ImmutableArray<LedgerDiagnostic>.Empty;
    }

    private static LedgerDiagnostic? CheckIdentityStrings(ExpectedIdentities identities)
    {
        var values = new[]
        {
            identities.Repository,
            identities.HeadRepository,
            identities.WorkflowIdentity,
            identities.TrustedExecutionDomain,
            identities.ProviderId,
            identities.ModelId,
            identities.AdapterId,
            identities.TemplateId,
            identities.PolicyId,
            identities.ToolDefinitionId,
            identities.CacheConfigId
        };

        foreach (var value in values)
        {
            if (Encoding.UTF8.GetByteCount(value) > 256)
            {
                return new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.IdentityByteLengthExceeded,
                    Message = "Identity string exceeds 256 UTF-8 bytes."
                };
            }
        }

        foreach (var value in values)
        {
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7f)
                {
                    return new LedgerDiagnostic
                    {
                        Code = LedgerDiagnosticCodes.ControlCharacterInIdentity,
                        Message = "Identity string contains a control character."
                    };
                }
            }
        }

        return null;
    }

    private static LedgerDiagnostic? CheckFindingLocations(ReviewOutcomeRecord outcome)
    {
        foreach (var finding in outcome.Findings)
        {
            var hasStart = finding.StartLine.HasValue;
            var hasEnd = finding.EndLine.HasValue;
            if (hasStart != hasEnd)
            {
                return new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.FindingLocationMismatch,
                    Message = "Finding has mismatched startLine/endLine presence."
                };
            }

            if (hasStart && string.IsNullOrEmpty(finding.Path))
            {
                return new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.FindingLocationMissingPath,
                    Message = "Finding with line range is missing path."
                };
            }

            if (finding.StartLine is { } start && finding.EndLine is { } end && start > end)
            {
                return new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.FindingLineRangeInvalid,
                    Message = "Finding startLine is greater than endLine."
                };
            }
        }

        return null;
    }

    private static LedgerDiagnostic? ScanContextSource(ExpectedIdentities identities, ValidatedContextSource source, InteractionIdentity interaction)
    {
        if (ContainsInvalidUnicode(interaction.InteractionId))
        {
            return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "interactionId contains invalid Unicode." };
        }

        if (ContainsInvalidUnicode(identities.Repository) ||
            ContainsInvalidUnicode(identities.HeadRepository) ||
            ContainsInvalidUnicode(identities.WorkflowIdentity) ||
            ContainsInvalidUnicode(identities.TrustedExecutionDomain) ||
            ContainsInvalidUnicode(identities.ProviderId) ||
            ContainsInvalidUnicode(identities.ModelId) ||
            ContainsInvalidUnicode(identities.AdapterId) ||
            ContainsInvalidUnicode(identities.TemplateId) ||
            ContainsInvalidUnicode(identities.PolicyId) ||
            ContainsInvalidUnicode(identities.ToolDefinitionId) ||
            ContainsInvalidUnicode(identities.CacheConfigId) ||
            ContainsInvalidUnicode(source.SubjectDigest) ||
            ContainsInvalidUnicode(source.ReviewedHeadSha) ||
            ContainsInvalidUnicode(source.ReviewedBaseSha))
        {
            return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Context source contains invalid Unicode." };
        }

        foreach (var file in source.ChangedFiles)
        {
            if (ContainsInvalidUnicode(file.Path) ||
                ContainsInvalidUnicode(file.Status) ||
                (file.PreviousPath is not null && ContainsInvalidUnicode(file.PreviousPath)) ||
                (file.Patch is not null && ContainsInvalidUnicode(file.Patch.Sha256)))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Changed file contains invalid Unicode." };
            }
        }

        return null;
    }

    private static LedgerDiagnostic? ScanOutcomeSource(ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        if (ContainsInvalidUnicode(interaction.InteractionId))
        {
            return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "interactionId contains invalid Unicode." };
        }

        if (ContainsInvalidUnicode(source.Summary))
        {
            return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "summary contains invalid Unicode." };
        }

        foreach (var limitation in source.Limitations)
        {
            if (ContainsInvalidUnicode(limitation))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "limitation contains invalid Unicode." };
            }
        }

        foreach (var finding in source.Findings)
        {
            if (ContainsInvalidUnicode(finding.Severity) ||
                ContainsInvalidUnicode(finding.Confidence) ||
                ContainsInvalidUnicode(finding.Category) ||
                ContainsInvalidUnicode(finding.Title) ||
                ContainsInvalidUnicode(finding.Body) ||
                (finding.Path is not null && ContainsInvalidUnicode(finding.Path)) ||
                (finding.Evidence is not null && ContainsInvalidUnicode(finding.Evidence)) ||
                (finding.SuggestedAction is not null && ContainsInvalidUnicode(finding.SuggestedAction)) ||
                (finding.InlinePreference is not null && ContainsInvalidUnicode(finding.InlinePreference)))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Finding contains invalid Unicode." };
            }
        }

        return null;
    }

    private static LedgerDiagnostic? ScanCandidateModel(LedgerModel model)
    {
        var header = model.Header;
        var headerStrings = new[]
        {
            header.Kind, header.SessionEpoch, header.LedgerEpoch, header.PredecessorLedgerSha256,
            header.Repository, header.HeadRepository, header.WorkflowIdentity, header.TrustedExecutionDomain,
            header.ProviderId, header.ModelId, header.AdapterId, header.TemplateId, header.PolicyId,
            header.ToolDefinitionId, header.CacheConfigId,
            header.PredecessorLedgerEpoch, header.PredecessorManifestSha256, header.ResetReason, header.RecoveryReason
        };

        foreach (var value in headerStrings)
        {
            if (value is not null && ContainsInvalidUnicode(value))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate header contains invalid Unicode." };
            }
        }

        foreach (var record in model.Records)
        {
            if (ContainsInvalidUnicode(record.Role) || ContainsInvalidUnicode(record.InteractionId))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate record contains invalid Unicode." };
            }

            if (record is ReviewContextRecord context)
            {
                if (ContainsInvalidUnicode(context.SubjectDigest) ||
                    ContainsInvalidUnicode(context.CacheContractDigest) ||
                    ContainsInvalidUnicode(context.ReviewedHeadSha) ||
                    ContainsInvalidUnicode(context.ReviewedBaseSha))
                {
                    return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate context record contains invalid Unicode." };
                }

                foreach (var file in context.ChangedFiles)
                {
                    if (ContainsInvalidUnicode(file.Path) ||
                        ContainsInvalidUnicode(file.Status) ||
                        (file.PreviousPath is not null && ContainsInvalidUnicode(file.PreviousPath)) ||
                        (file.Patch is not null && ContainsInvalidUnicode(file.Patch.Sha256)))
                    {
                        return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate changed file contains invalid Unicode." };
                    }
                }
            }
            else if (record is ReviewOutcomeRecord outcome)
            {
                if (ContainsInvalidUnicode(outcome.Summary))
                {
                    return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate outcome summary contains invalid Unicode." };
                }

                foreach (var limitation in outcome.Limitations)
                {
                    if (ContainsInvalidUnicode(limitation))
                    {
                        return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate limitation contains invalid Unicode." };
                    }
                }

                foreach (var finding in outcome.Findings)
                {
                    if (ContainsInvalidUnicode(finding.Severity) ||
                        ContainsInvalidUnicode(finding.Confidence) ||
                        ContainsInvalidUnicode(finding.Category) ||
                        ContainsInvalidUnicode(finding.Title) ||
                        ContainsInvalidUnicode(finding.Body) ||
                        (finding.Path is not null && ContainsInvalidUnicode(finding.Path)) ||
                        (finding.Evidence is not null && ContainsInvalidUnicode(finding.Evidence)) ||
                        (finding.SuggestedAction is not null && ContainsInvalidUnicode(finding.SuggestedAction)) ||
                        (finding.InlinePreference is not null && ContainsInvalidUnicode(finding.InlinePreference)))
                    {
                        return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate finding contains invalid Unicode." };
                    }
                }
            }
        }

        return null;
    }

    private static bool ContainsInvalidUnicode(string value)
    {
        if (value.Contains('\0'))
        {
            return true;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    private static CandidateOutcome OverBoundAppend(string causeCode)
    {
        return new CandidateOutcome(null, ImmutableArray.Create(new LedgerDiagnostic
        {
            Code = LedgerDiagnosticCodes.OverBoundAppend,
            Message = $"Candidate append exceeds bounds.",
            CauseCode = causeCode
        }));
    }
}
