using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerBuilder
{
    // Schema-shaped stand-in (lowercase SHA-256 hex) for the cache-contract digest in
    // provisional records; the real digest is computed only after earlier stages pass.
    private const string PlaceholderDigest = "0000000000000000000000000000000000000000000000000000000000000000";

    public static BuildOutcome<ReviewContextRecord> BuildReviewContext(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        // Null argument objects are caller bugs; null *fields* inside fabricated DTOs are
        // tolerated and surface as schema diagnostics instead.
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(interaction);

        var unicodeDiagnostic = ScanContextSource(identities, source, interaction);
        if (unicodeDiagnostic is not null)
        {
            return new BuildOutcome<ReviewContextRecord>(null, ImmutableArray.Create(unicodeDiagnostic));
        }

        // Provisional record: the cache-contract digest is only computed after schema
        // validation and structural identity checks pass, so the hash producer never runs
        // on inputs that fail an earlier stage.
        var provisionalRecord = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = interaction.InteractionId,
            InteractionOrdinal = interaction.InteractionOrdinal,
            SubjectDigest = source.SubjectDigest,
            CacheContractDigest = PlaceholderDigest,
            ReviewedHeadSha = source.ReviewedHeadSha,
            ReviewedBaseSha = source.ReviewedBaseSha,
            ChangedFiles = source.ChangedFiles
        };

        var schemaDiagnostics = ValidateRecordSchema(provisionalRecord, identities);
        if (!schemaDiagnostics.IsEmpty)
        {
            return new BuildOutcome<ReviewContextRecord>(null, schemaDiagnostics);
        }

        var identityDiagnostic = CheckIdentityStrings(identities);
        if (identityDiagnostic is not null)
        {
            return new BuildOutcome<ReviewContextRecord>(null, ImmutableArray.Create(identityDiagnostic));
        }

        var record = new ReviewContextRecord
        {
            Role = provisionalRecord.Role,
            InteractionId = provisionalRecord.InteractionId,
            InteractionOrdinal = provisionalRecord.InteractionOrdinal,
            SubjectDigest = provisionalRecord.SubjectDigest,
            CacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities),
            ReviewedHeadSha = provisionalRecord.ReviewedHeadSha,
            ReviewedBaseSha = provisionalRecord.ReviewedBaseSha,
            ChangedFiles = provisionalRecord.ChangedFiles
        };

        return new BuildOutcome<ReviewContextRecord>(record, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    public static BuildOutcome<ReviewOutcomeRecord> BuildReviewOutcome(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(interaction);

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
        // The cache-contract IDs must satisfy the shared Sha256Hex schema domain.
        var dummyIdentities = new ExpectedIdentities(
            "owner/repo", "owner/repo", 1,
            "ci", "trusted",
            "provider", "model",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

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
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

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
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

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
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

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
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

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

        // Component schema replay (header variant + the fabricated record pair) so that
        // schema-class diagnostics precede aggregate bounds, structural, and semantic
        // stages. Predecessor records are already ledger-valid and are not replayed;
        // aggregate array bounds stay with the candidate-level gate below.
        var replayRecords = records.Length > 2
            ? ImmutableArray.Create(records[records.Length - 2], records[records.Length - 1])
            : records;
        var replayModel = new LedgerModel
        {
            SchemaVersion = model.SchemaVersion,
            PrefixContractVersion = model.PrefixContractVersion,
            Header = header,
            Records = replayRecords
        };
        var replayDiagnostics = EvaluateModelSchema(replayModel);
        if (!replayDiagnostics.IsEmpty)
        {
            return new CandidateOutcome(null, replayDiagnostics);
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
                CacheContractDigest = PlaceholderDigest,
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

        return EvaluateModelSchema(tempModel);
    }

    private static ImmutableArray<LedgerDiagnostic> EvaluateModelSchema(LedgerModel model)
    {
        // The schema replay is driven by the tolerant projection, not by the canonical
        // writer: the projection mirrors the wire names and null/absence semantics of
        // "Record shape and normalization" but never throws on malformed caller-fabricated
        // DTO content (runtime-null values surface as JSON null, so the schema's type
        // checks own the diagnostic). Canonical serialization stays downstream of the
        // replay and still only runs once every earlier stage has passed.
        var projectedBytes = ProjectModelForSchema(model);

        using var document = JsonDocument.Parse(projectedBytes);
        var results = SchemaContracts.Load(typeof(LedgerBuilder).Assembly).GetSchema(SchemaKind.Ledger).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            return LedgerSchemaMapper.Map(document, results);
        }

        return ImmutableArray<LedgerDiagnostic>.Empty;
    }

    // Tolerant schema projection: writes the schema-evaluable JSON shape of a model
    // without requiring canonical properties (no key ordering, no escape rules beyond
    // well-formed JSON) and without throwing on malformed DTO content.
    private static byte[] ProjectModelForSchema(LedgerModel model)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", model.SchemaVersion);
            writer.WriteNumber("prefixContractVersion", model.PrefixContractVersion);
            writer.WritePropertyName("header");
            WriteHeaderProjection(writer, model.Header);
            writer.WritePropertyName("records");
            WriteRecordsProjection(writer, model.Records);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteHeaderProjection(Utf8JsonWriter writer, LedgerHeader? header)
    {
        if (header is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", header.Kind);
        writer.WriteString("sessionEpoch", header.SessionEpoch);
        writer.WriteString("ledgerEpoch", header.LedgerEpoch);
        writer.WriteNumber("stateGeneration", header.StateGeneration);
        writer.WriteString("predecessorLedgerSha256", header.PredecessorLedgerSha256);
        if (header.PredecessorLedgerEpoch is not null)
        {
            writer.WriteString("predecessorLedgerEpoch", header.PredecessorLedgerEpoch);
        }

        if (header.PredecessorStateGeneration.HasValue)
        {
            writer.WriteNumber("predecessorStateGeneration", header.PredecessorStateGeneration.Value);
        }

        if (header.PredecessorManifestSha256 is not null)
        {
            writer.WriteString("predecessorManifestSha256", header.PredecessorManifestSha256);
        }

        if (header.ResetReason is not null)
        {
            writer.WriteString("resetReason", header.ResetReason);
        }

        if (header.RecoveryReason is not null)
        {
            writer.WriteString("recoveryReason", header.RecoveryReason);
        }

        writer.WriteString("repository", header.Repository);
        writer.WriteString("headRepository", header.HeadRepository);
        writer.WriteNumber("pullRequest", header.PullRequest);
        writer.WriteString("workflowIdentity", header.WorkflowIdentity);
        writer.WriteString("trustedExecutionDomain", header.TrustedExecutionDomain);
        writer.WriteString("providerId", header.ProviderId);
        writer.WriteString("modelId", header.ModelId);
        writer.WriteString("adapterId", header.AdapterId);
        writer.WriteString("templateId", header.TemplateId);
        writer.WriteString("policyId", header.PolicyId);
        writer.WriteString("toolDefinitionId", header.ToolDefinitionId);
        writer.WriteString("cacheConfigId", header.CacheConfigId);
        writer.WriteEndObject();
    }

    private static void WriteRecordsProjection(Utf8JsonWriter writer, ImmutableArray<LedgerRecord> records)
    {
        if (records.IsDefault)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var record in records)
        {
            WriteRecordProjection(writer, record);
        }

        writer.WriteEndArray();
    }

    private static void WriteRecordProjection(Utf8JsonWriter writer, LedgerRecord? record)
    {
        if (record is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", record.Role);
        writer.WriteString("interactionId", record.InteractionId);
        writer.WriteNumber("interactionOrdinal", record.InteractionOrdinal);

        if (record is ReviewContextRecord context)
        {
            writer.WriteString("subjectDigest", context.SubjectDigest);
            writer.WriteString("cacheContractDigest", context.CacheContractDigest);
            writer.WriteString("reviewedHeadSha", context.ReviewedHeadSha);
            writer.WriteString("reviewedBaseSha", context.ReviewedBaseSha);
            writer.WritePropertyName("changedFiles");
            WriteChangedFilesProjection(writer, context.ChangedFiles);
        }
        else if (record is ReviewOutcomeRecord outcome)
        {
            writer.WriteString("summary", outcome.Summary);
            writer.WritePropertyName("findings");
            WriteFindingsProjection(writer, outcome.Findings);
            writer.WritePropertyName("limitations");
            WriteLimitationsProjection(writer, outcome.Limitations);
        }

        writer.WriteEndObject();
    }

    private static void WriteChangedFilesProjection(Utf8JsonWriter writer, ImmutableArray<LedgerChangedFile> files)
    {
        if (files.IsDefault)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var file in files)
        {
            if (file is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            if (file.PreviousPath is not null)
            {
                writer.WriteString("previousPath", file.PreviousPath);
            }

            writer.WriteString("status", file.Status);
            writer.WriteNumber("additions", file.Additions);
            writer.WriteNumber("deletions", file.Deletions);
            writer.WriteNumber("changes", file.Changes);
            if (file.Patch is not null)
            {
                writer.WritePropertyName("patch");
                writer.WriteStartObject();
                writer.WriteString("sha256", file.Patch.Sha256);
                writer.WriteBoolean("truncated", file.Patch.Truncated);
                writer.WriteNumber("maxChars", file.Patch.MaxChars);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteFindingsProjection(Utf8JsonWriter writer, ImmutableArray<LedgerFinding> findings)
    {
        if (findings.IsDefault)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var finding in findings)
        {
            if (finding is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("severity", finding.Severity);
            writer.WriteString("confidence", finding.Confidence);
            writer.WriteString("category", finding.Category);
            writer.WriteString("title", finding.Title);
            writer.WriteString("body", finding.Body);

            // Path/StartLine/EndLine are always serialized; explicit null is preserved.
            if (finding.Path is not null)
            {
                writer.WriteString("path", finding.Path);
            }
            else
            {
                writer.WriteNull("path");
            }

            if (finding.StartLine.HasValue)
            {
                writer.WriteNumber("startLine", finding.StartLine.Value);
            }
            else
            {
                writer.WriteNull("startLine");
            }

            if (finding.EndLine.HasValue)
            {
                writer.WriteNumber("endLine", finding.EndLine.Value);
            }
            else
            {
                writer.WriteNull("endLine");
            }

            if (finding.Evidence is not null)
            {
                writer.WriteString("evidence", finding.Evidence);
            }

            if (finding.SuggestedAction is not null)
            {
                writer.WriteString("suggestedAction", finding.SuggestedAction);
            }

            if (finding.InlinePreference is not null)
            {
                writer.WriteString("inlinePreference", finding.InlinePreference);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteLimitationsProjection(Utf8JsonWriter writer, ImmutableArray<string> limitations)
    {
        if (limitations.IsDefault)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var limitation in limitations)
        {
            writer.WriteStringValue(limitation);
        }

        writer.WriteEndArray();
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

        foreach (var file in source.ChangedFiles.IsDefault ? ImmutableArray<LedgerChangedFile>.Empty : source.ChangedFiles)
        {
            if (file is null)
            {
                continue;
            }

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

        foreach (var limitation in source.Limitations.IsDefault ? ImmutableArray<string>.Empty : source.Limitations)
        {
            if (ContainsInvalidUnicode(limitation))
            {
                return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "limitation contains invalid Unicode." };
            }
        }

        foreach (var finding in source.Findings.IsDefault ? ImmutableArray<LedgerFinding>.Empty : source.Findings)
        {
            if (finding is null)
            {
                continue;
            }

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

        foreach (var record in model.Records.IsDefault ? ImmutableArray<LedgerRecord>.Empty : model.Records)
        {
            if (record is null)
            {
                continue;
            }

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

                foreach (var file in context.ChangedFiles.IsDefault ? ImmutableArray<LedgerChangedFile>.Empty : context.ChangedFiles)
                {
                    if (file is null)
                    {
                        continue;
                    }

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

                foreach (var limitation in outcome.Limitations.IsDefault ? ImmutableArray<string>.Empty : outcome.Limitations)
                {
                    if (ContainsInvalidUnicode(limitation))
                    {
                        return new LedgerDiagnostic { Code = LedgerDiagnosticCodes.InvalidUnicode, Message = "Candidate limitation contains invalid Unicode." };
                    }
                }

                foreach (var finding in outcome.Findings.IsDefault ? ImmutableArray<LedgerFinding>.Empty : outcome.Findings)
                {
                    if (finding is null)
                    {
                        continue;
                    }

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

    // Runtime-null strings are tolerated (treated as Unicode-safe) so that malformed
    // caller-fabricated DTOs flow into the schema stage, where the tolerant projection
    // writes them as JSON null and the schema's type checks own the diagnostic.
    private static bool ContainsInvalidUnicode(string? value)
    {
        if (value is null)
        {
            return false;
        }

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
