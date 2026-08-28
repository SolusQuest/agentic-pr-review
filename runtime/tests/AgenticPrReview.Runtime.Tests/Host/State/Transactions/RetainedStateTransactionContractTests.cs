using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class RetainedStateTransactionContractTests
{
    [Fact]
    public void CandidateRetentionUsesExactUnifiedBoundaries()
    {
        const long now = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryCandidate(
            now,
            out var logical,
            out var platform));

        Assert.Equal(
            now + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            now + StateRetentionRequirements.ScopedPlatformRequestSeconds,
            platform);
        Assert.True(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds,
            now));
        Assert.False(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds - 1,
            now));
    }

    [Fact]
    public void AcceptanceReceiptRequiresTwoLogicalWindows()
    {
        const long acceptedAt = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryAcceptance(
            acceptedAt,
            out var logical,
            out var platform));

        Assert.Equal(
            acceptedAt + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            acceptedAt +
                2 * StateRetentionRequirements.LogicalWindowSeconds,
            platform);
    }

    [Fact]
    public void RetentionOverflowFailsClosed()
    {
        Assert.False(RetainedStateRetention.TryCandidate(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryAcceptance(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryOpaque(
            RestrictedStateFormat.MaximumUnixSeconds,
            RestrictedStateFormat.MaximumUnixSeconds,
            out _));
    }

    [Fact]
    public void CleanupRecordIsCanonicalAndOperationBound()
    {
        var first = Metadata("candidate-name", "2");
        var second = Metadata("acceptance-name", "1");
        Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64),
            [first, second],
            out var value));
        Assert.NotNull(value);
        Assert.Equal(second, value!.Targets[0]);
        Assert.Equal(first, value.Targets[1]);
        Assert.True(RetainedStateCleanupRecordCodec.TryEncode(
            value,
            out var bytes));
        Assert.True(RetainedStateCleanupRecordCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(value.TerminalAcceptanceIdentity,
            decoded!.TerminalAcceptanceIdentity);
        Assert.Equal(value.BaseScopeDigest, decoded.BaseScopeDigest);
        Assert.Equal(value.Epoch, decoded.Epoch);
        Assert.Equal(value.SessionId, decoded.SessionId);
        Assert.Equal(value.PreCleanupInventoryDigest,
            decoded.PreCleanupInventoryDigest);
        Assert.Equal(value.OperationIdentity, decoded.OperationIdentity);
        Assert.True(value.Targets.SequenceEqual(decoded.Targets));

        var tampered = bytes.ToArray();
        tampered[^1] ^= 1;
        Assert.False(RetainedStateCleanupRecordCodec.TryDecode(
            tampered,
            out _));
    }

    [Fact]
    public void RetainedSyntheticCleanupVectorMatchesProductionCodec()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "codec-vectors",
            "retained-transaction-host-evidence.json")));
        var fixture = document.RootElement.GetProperty("fixture");
        var cleanup = document.RootElement
            .GetProperty("state")
            .GetProperty("created")
            .EnumerateArray()
            .Single(item => StringComparer.Ordinal.Equals(
                    item.GetProperty("object_class").GetString(),
                    "cleanup") &&
                StringComparer.Ordinal.Equals(
                    item.GetProperty("decoded_record")
                        .GetProperty("record_kind").GetString(),
                    "s6-final-cleanup"))
            .GetProperty("decoded_record");
        var targets = cleanup.GetProperty("targets")
            .EnumerateArray()
            .Select(TargetMetadata)
            .ToImmutableArray();
        var evidence = targets.Select(target =>
            new LineageArtifactEvidence(
                target.Reference.Name.Value,
                target.Reference.ObjectId.Value,
                target.ProducingRun.Identity,
                target.ProducingRun.Attempt,
                target.ArchiveDigest.Sha256,
                target.EncryptedObjectDigest.Sha256,
                target.ExpiresAtUnixSeconds,
                target.Size));

        Assert.Equal(
            cleanup.GetProperty("pre_cleanup_inventory_digest").GetString(),
            LineageCryptography.InventoryDigest(evidence));
        Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
            cleanup.GetProperty("terminal_acceptance_identity").GetString()!,
            cleanup.GetProperty("base_scope_digest").GetString()!,
            cleanup.GetProperty("epoch").GetString()!,
            cleanup.GetProperty("session_id").GetString()!,
            cleanup.GetProperty("pre_cleanup_inventory_digest").GetString()!,
            targets,
            out var value));
        Assert.NotNull(value);
        Assert.Equal(
            cleanup.GetProperty("operation_identity").GetString(),
            value!.OperationIdentity);
        Assert.NotEqual(
            fixture.GetProperty("normal_operation_id").GetString(),
            value.OperationIdentity);

        var internalCleanups = document.RootElement
            .GetProperty("state")
            .GetProperty("created")
            .EnumerateArray()
            .Where(item => StringComparer.Ordinal.Equals(
                    item.GetProperty("object_class").GetString(),
                    "cleanup") &&
                StringComparer.Ordinal.Equals(
                    item.GetProperty("decoded_record")
                        .GetProperty("record_kind").GetString(),
                    "s6-internal-cleanup"))
            .ToArray();
        Assert.Equal(2, internalCleanups.Length);
        Assert.Equal(
            new[] { "bootstrap", "continuation" },
            internalCleanups
                .Select(item => item.GetProperty("creation_phase").GetString())
                .Order(StringComparer.Ordinal));
        Assert.All(internalCleanups, item =>
        {
            var decoded = item.GetProperty("decoded_record");
            var phase = item.GetProperty("creation_phase").GetString();
            var internalTargets = decoded.GetProperty("targets")
                .EnumerateArray()
                .Select(TargetMetadata)
                .ToImmutableArray();
            if (StringComparer.Ordinal.Equals(phase, "bootstrap"))
            {
                Assert.Empty(internalTargets);
            }
            else
            {
                Assert.Equal("continuation", phase);
                Assert.Single(internalTargets);
                Assert.Equal(
                    "10028",
                    internalTargets[0].Reference.ObjectId.Value);
            }

            Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
                decoded.GetProperty("terminal_acceptance_identity")
                    .GetString()!,
                decoded.GetProperty("base_scope_digest").GetString()!,
                decoded.GetProperty("epoch").GetString()!,
                decoded.GetProperty("session_id").GetString()!,
                decoded.GetProperty("pre_cleanup_inventory_digest")
                    .GetString()!,
                internalTargets,
                out var internalValue));
            Assert.NotNull(internalValue);
            Assert.Equal(
                decoded.GetProperty("operation_identity").GetString(),
                internalValue!.OperationIdentity);
        });
    }

    [Fact]
    public void RetainedSyntheticTransactionsMatchProductionCodecs()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "codec-vectors",
            "retained-transaction-host-evidence.json")));
        var evidence = document.RootElement;
        var records = evidence
            .GetProperty("state")
            .GetProperty("created")
            .EnumerateArray()
            .ToArray();
        var canonicalPayloads = new Dictionary<string, byte[]>(
            StringComparer.Ordinal);

        foreach (var phase in new[] { "bootstrap", "continuation" })
        {
            var candidate = Record(records, phase, "candidate");
            var acceptance = Record(records, phase, "acceptance");
            var initialIntent = RecoveryRecord(
                records,
                phase,
                "initial_intent");
            var stickyReadback = RecoveryRecord(
                records,
                phase,
                "sticky_readback");
            var acceptanceRecovery = RecoveryRecord(
                records,
                phase,
                "acceptance_recovery");
            var candidateValue = candidate.GetProperty("decoded_record");
            var publicationValue = candidateValue
                .GetProperty("publication_payload");

            Assert.True(ValidatedPublicationPayloadV1.TryCreate(
                publicationValue.GetProperty("finalized_comment").GetString(),
                PositiveId(publicationValue, "repository_id"),
                publicationValue.GetProperty("repository_name").GetString(),
                PositiveId(publicationValue, "pull_request_number"),
                publicationValue
                    .GetProperty("policy_identity_sha256")
                    .GetString(),
                publicationValue.GetProperty("payload_sha256").GetString(),
                publicationValue
                    .GetProperty("build_discriminator")
                    .GetString(),
                publicationValue.GetProperty("rendering_version").GetString(),
                out var publication));
            Assert.NotNull(publication);
            Assert.Equal(
                publicationValue.GetProperty("scope_sha256").GetString(),
                publication!.ScopeSha256);
            Assert.Equal(
                publicationValue.GetProperty("body_sha256").GetString(),
                publication.BodySha256);
            Assert.Equal(
                publicationValue.GetProperty("reviewed_head_sha").GetString(),
                publication.ReviewedHeadSha);
            Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
                publication,
                out var publicationBytes));
            Assert.Equal(
                candidateValue
                    .GetProperty("publication_payload_sha256")
                    .GetString(),
                AcceptedStateRecordValidation.Sha256(publicationBytes));

            var generation = new StateGenerationRecordV1(
                ImmutableArray.CreateRange(Convert.FromBase64String(
                    candidateValue
                        .GetProperty("encrypted_state_envelope_base64")
                        .GetString()!)),
                candidateValue.GetProperty("state_envelope_sha256").GetString()!,
                candidateValue.GetProperty("session_sha256").GetString()!,
                candidateValue.GetProperty("producer_base_sha").GetString()!,
                candidateValue.GetProperty("producer_head_sha").GetString()!,
                candidateValue.GetProperty("session_generation").GetInt64(),
                OptionalString(candidateValue, "predecessor_envelope_sha256"),
                OptionalString(
                    candidateValue,
                    "previous_logical_generation_identity"),
                candidateValue
                    .GetProperty("prepared_at_unix_seconds")
                    .GetInt64(),
                candidateValue
                    .GetProperty("prepared_expires_at_unix_seconds")
                    .GetInt64(),
                ImmutableArray.CreateRange(publicationBytes),
                candidateValue
                    .GetProperty("publication_payload_sha256")
                    .GetString()!,
                candidateValue
                    .GetProperty("policy_identity_sha256")
                    .GetString()!,
                candidateValue.GetProperty("config_sha256").GetString()!,
                candidateValue
                    .GetProperty("instructions_sha256")
                    .GetString()!,
                candidateValue.GetProperty("payload_sha256").GetString()!,
                candidateValue
                    .GetProperty("build_discriminator")
                    .GetString()!);
            Assert.True(RestrictedStateEnvelope.TryParse(
                generation.EncryptedStateEnvelope.AsSpan(),
                out _), phase);
            Assert.Equal(
                generation.StateEnvelopeSha256,
                RestrictedStateEnvelope.EnvelopeSha256(
                    generation.EncryptedStateEnvelope.AsSpan()));
            Assert.True(AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out _), phase);
            Assert.True(AcceptedStateRecordValidation.IsValid(generation), phase);
            Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
                generation,
                out var generationBytes));
            canonicalPayloads.Add(
                candidate.GetProperty("physical_artifact_id").GetString()!,
                generationBytes);
            Assert.True(AcceptedStateGenerationRecordCodec.TryDecode(
                generationBytes,
                out var decodedGeneration));
            Assert.NotNull(decodedGeneration);
            Assert.True(generation.EncryptedStateEnvelope.AsSpan().SequenceEqual(
                decodedGeneration!.EncryptedStateEnvelope.AsSpan()));
            Assert.True(generation.PublicationPayloadBytes.AsSpan().SequenceEqual(
                decodedGeneration.PublicationPayloadBytes.AsSpan()));
            Assert.Equal(
                generation.StateEnvelopeSha256,
                decodedGeneration.StateEnvelopeSha256);
            Assert.Equal(
                generation.PublicationPayloadSha256,
                decodedGeneration.PublicationPayloadSha256);
            Assert.Equal(
                generation.BuildDiscriminator,
                decodedGeneration.BuildDiscriminator);

            var recoveryPublication = new PublicationRecoveryPublicationV1(
                publication.ReviewedHeadSha,
                publication.ScopeSha256,
                publication.BodySha256);
            var initialIntentValue = initialIntent
                .GetProperty("decoded_record");
            Assert.True(PublicationIntentV1Codec.TryCreate(
                recoveryPublication,
                initialIntentValue
                    .GetProperty("created_at_unix_seconds")
                    .GetInt64(),
                out var intent));
            Assert.NotNull(intent);
            Assert.Equal(
                initialIntentValue.GetProperty("record_identity").GetString(),
                intent!.RecordIdentity);
            Assert.NotEqual(
                initialIntent.GetProperty("object_identity").GetString(),
                intent.RecordIdentity);
            Assert.True(PublicationIntentV1Codec.TryEncode(
                intent,
                out var intentBytes));
            canonicalPayloads.Add(
                initialIntent.GetProperty("physical_artifact_id").GetString()!,
                intentBytes);

            var stickyReadbackValue = stickyReadback
                .GetProperty("decoded_record");
            Assert.True(StickyCommentPublisher.StickyPublicationReceipt
                .TryRehydrate(
                    (StickyPublicationOperation)stickyReadbackValue
                        .GetProperty("publication_operation")
                        .GetInt32(),
                    PositiveId(stickyReadbackValue, "repository_id"),
                    PositiveId(stickyReadbackValue, "pull_request_number"),
                    PositiveId(stickyReadbackValue, "comment_id"),
                    stickyReadbackValue.GetProperty("comment_url").GetString(),
                    recoveryPublication.ScopeSha256,
                    recoveryPublication.BodySha256,
                    recoveryPublication.ReviewedHeadSha,
                    out var stickyReceipt));
            Assert.NotNull(stickyReceipt);
            Assert.True(StickyReadbackRecordV1Codec.TryCreate(
                recoveryPublication,
                stickyReadbackValue
                    .GetProperty("attempt_intent_record_identity")
                    .GetString()!,
                stickyReceipt!,
                stickyReadbackValue
                    .GetProperty("observed_at_unix_seconds")
                    .GetInt64(),
                out var readback));
            Assert.NotNull(readback);
            Assert.Equal(
                stickyReadbackValue.GetProperty("record_identity").GetString(),
                readback!.RecordIdentity);
            Assert.NotEqual(
                stickyReadback.GetProperty("object_identity").GetString(),
                readback.RecordIdentity);
            Assert.True(StickyReadbackRecordV1Codec.TryEncode(
                readback,
                out var readbackBytes));
            canonicalPayloads.Add(
                stickyReadback.GetProperty("physical_artifact_id").GetString()!,
                readbackBytes);

            var acceptanceRecoveryValue = acceptanceRecovery
                .GetProperty("decoded_record");
            Assert.True(RecoveryRecordV1Codec.TryCreate(
                recoveryPublication,
                readback,
                ImmutableArray.CreateRange(Convert.FromBase64String(
                    acceptanceRecoveryValue
                        .GetProperty("acceptance_recovery_handoff_base64")
                        .GetString()!)),
                acceptanceRecoveryValue
                    .GetProperty("minimum_semantic_expires_at_unix_seconds")
                    .GetInt64(),
                out var recovery));
            Assert.NotNull(recovery);
            Assert.Equal(
                acceptanceRecoveryValue
                    .GetProperty("record_identity")
                    .GetString(),
                recovery!.RecordIdentity);
            Assert.NotEqual(
                acceptanceRecovery.GetProperty("object_identity").GetString(),
                recovery.RecordIdentity);
            Assert.True(RecoveryRecordV1Codec.TryEncode(
                recovery,
                out var recoveryBytes,
                out _,
                out _));
            canonicalPayloads.Add(
                acceptanceRecovery
                    .GetProperty("physical_artifact_id").GetString()!,
                recoveryBytes);

            var acceptanceValue = acceptance.GetProperty("decoded_record");
            var receipt = new AcceptanceReceiptV1(
                acceptanceValue
                    .GetProperty("logical_generation_identity")
                    .GetString()!,
                acceptanceValue
                    .GetProperty("original_candidate_object_identity")
                    .GetString()!,
                OptionalString(
                    acceptanceValue,
                    "previous_logical_generation_identity"),
                OptionalString(
                    acceptanceValue,
                    "previous_acceptance_receipt_identity"),
                acceptanceValue.GetProperty("reviewed_head_sha").GetString()!,
                (StickyPublicationOperation)acceptanceValue
                    .GetProperty("publication_operation")
                    .GetInt32(),
                PositiveId(acceptanceValue, "repository_id"),
                PositiveId(acceptanceValue, "pull_request_number"),
                PositiveId(acceptanceValue, "comment_id"),
                acceptanceValue.GetProperty("comment_url").GetString()!,
                acceptanceValue.GetProperty("scope_sha256").GetString()!,
                acceptanceValue.GetProperty("body_sha256").GetString()!,
                acceptanceValue
                    .GetProperty("publication_payload_sha256")
                    .GetString()!,
                acceptanceValue
                    .GetProperty("producing_run_identity")
                    .GetString()!,
                acceptanceValue
                    .GetProperty("producing_run_attempt")
                    .GetInt64(),
                acceptanceValue
                    .GetProperty("accepted_at_unix_seconds")
                    .GetInt64(),
                acceptanceValue
                    .GetProperty("logical_expires_at_unix_seconds")
                    .GetInt64());
            Assert.True(AcceptedStateAcceptanceReceiptCodec.TryEncode(
                receipt,
                out var acceptanceBytes));
            canonicalPayloads.Add(
                acceptance.GetProperty("physical_artifact_id").GetString()!,
                acceptanceBytes);
            Assert.True(AcceptedStateAcceptanceReceiptCodec.TryDecode(
                acceptanceBytes,
                out var decodedAcceptance));
            Assert.Equal(receipt, decodedAcceptance);
            Assert.Equal(
                candidate.GetProperty("object_identity").GetString(),
                receipt.OriginalCandidateObjectIdentity);
            Assert.Equal(
                candidateValue
                    .GetProperty("publication_payload_sha256")
                    .GetString(),
                receipt.PublicationPayloadSha256);

            Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                generationBytes,
                candidate.GetProperty("scope_digest").GetString(),
                candidate.GetProperty("epoch").GetString(),
                candidate.GetProperty("session_id").GetString(),
                OptionalString(
                    acceptanceValue,
                    "previous_acceptance_receipt_identity"),
                out var logicalGenerationIdentity));
            Assert.Equal(
                candidateValue.GetProperty("logical_generation_identity")
                    .GetString(),
                logicalGenerationIdentity);

            var handoff = Convert.FromBase64String(
                acceptanceRecoveryValue
                    .GetProperty("acceptance_recovery_handoff_base64")
                    .GetString()!);
            Assert.True(RetainedStateAcceptanceRecoveryCodec.TryDecode(
                handoff,
                out var acceptanceName,
                out var acceptanceEnvelope,
                out var predecessorCopy));
            using var predecessorCopyLifetime = predecessorCopy;
            Assert.NotNull(acceptanceName);
            Assert.Equal(
                acceptance.GetProperty("opaque_name").GetString(),
                acceptanceName!.Value);
            Assert.True(acceptanceEnvelope.AsSpan().SequenceEqual(
                Convert.FromBase64String(
                    acceptance.GetProperty("encrypted_envelope_base64")
                        .GetString()!)));
            if (StringComparer.Ordinal.Equals(phase, "bootstrap"))
            {
                Assert.Null(predecessorCopy);
            }
            else
            {
                Assert.NotNull(predecessorCopy);
                var copy = records.Single(item =>
                    StringComparer.Ordinal.Equals(
                        item.GetProperty("object_class").GetString(),
                        "candidate") &&
                    item.GetProperty("decoded_record")
                        .TryGetProperty("record_kind", out var recordKind) &&
                    StringComparer.Ordinal.Equals(
                        recordKind.GetString(),
                        "accepted_state_physical_copy"));
                var copyValue = copy.GetProperty("decoded_record");
                Assert.Equal(
                    copyValue.GetProperty("logical_generation_identity")
                        .GetString(),
                    predecessorCopy!.LogicalGenerationIdentity);
                Assert.Equal(
                    copy.GetProperty("logical_expires_at_unix_seconds")
                        .GetInt64(),
                    predecessorCopy.RequiredLogicalExpiresAtUnixSeconds);
                Assert.Equal(
                    copy.GetProperty(
                        "required_platform_expires_at_unix_seconds")
                        .GetInt64(),
                    predecessorCopy.RequiredPlatformExpiresAtUnixSeconds);
                Assert.Equal(
                    copy.GetProperty("opaque_name").GetString(),
                    predecessorCopy.Name.Value);
                Assert.True(predecessorCopy.Envelope.AsSpan().SequenceEqual(
                    Convert.FromBase64String(
                        copy.GetProperty("encrypted_envelope_base64")
                            .GetString()!)));
            }
        }

        AssertCanonicalScopedRecords(records, canonicalPayloads);
    }

    [Fact]
    public void OpaqueWriteAnchorIsCanonicalTamperBoundAndExactlyBounded()
    {
        var envelope = Enumerable.Range(0, 257)
            .Select(index => (byte)index)
            .ToArray();
        var value = Anchor(envelope);
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryEncode(
            value,
            out var bytes));
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(value.CandidateObjectIdentity,
            decoded!.CandidateObjectIdentity);
        Assert.Equal(value.OperationIdentity, decoded.OperationIdentity);
        Assert.Equal(value.ObjectClass, decoded.ObjectClass);
        Assert.Equal(value.PredecessorIdentity,
            decoded.PredecessorIdentity);
        Assert.Equal(value.SuccessorIdentity, decoded.SuccessorIdentity);
        Assert.Equal(value.SemanticRequiredExpiresAtUnixSeconds,
            decoded.SemanticRequiredExpiresAtUnixSeconds);
        Assert.Equal(value.RequiredPlatformExpiresAtUnixSeconds,
            decoded.RequiredPlatformExpiresAtUnixSeconds);
        Assert.Equal(value.ProducingRunIdentity,
            decoded.ProducingRunIdentity);
        Assert.Equal(value.ProducingRunAttempt,
            decoded.ProducingRunAttempt);
        Assert.Equal(value.TargetName, decoded.TargetName);
        Assert.Equal(value.TargetObjectIdentity,
            decoded.TargetObjectIdentity);
        Assert.True(value.TargetEnvelope.AsSpan().SequenceEqual(
            decoded.TargetEnvelope.AsSpan()));
        Assert.Equal(value.TargetEnvelopeSha256,
            decoded.TargetEnvelopeSha256);
        Assert.Equal(value.DispatchPhase, decoded.DispatchPhase);
        Assert.Equal(value.TargetPayloadSha256,
            decoded.TargetPayloadSha256);

        var tampered = bytes.ToArray();
        var envelopeOffset = tampered.AsSpan().IndexOf(envelope);
        Assert.True(envelopeOffset >= 0);
        tampered[envelopeOffset] ^= 1;
        Assert.False(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            tampered,
            out _));

        var low = 1;
        var high = LineageFormat.MaximumEnvelopeBytes;
        var maximumEnvelopeBytes = 0;
        byte[] maximumEncoding = [];
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (RetainedStateOpaqueWriteAnchorCodec.TryEncode(
                    Anchor(new byte[middle]),
                    out var encoded))
            {
                maximumEnvelopeBytes = middle;
                maximumEncoding = encoded;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        Assert.Equal(LineageFormat.MaximumPayloadBytes,
            maximumEncoding.Length);
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            maximumEncoding,
            out _));
        Assert.False(RetainedStateOpaqueWriteAnchorCodec.TryEncode(
            Anchor(new byte[maximumEnvelopeBytes + 1]),
            out _));
    }

    [Fact]
    public void PublicationCapabilityCarriesExactOutcomeAndRendering()
    {
        var outcome = R4PublicationTestData.Outcome(summary: "exact A");
        Assert.True(R4PreparedPublication.TryCreate(
            outcome,
            R4PublicationTestData.Scope,
            out var prepared));
        Assert.NotNull(prepared);
        Assert.True(prepared!.TryProject(
            out var projectedOutcome,
            out var rendered,
            out var scope));
        Assert.Same(outcome, projectedOutcome);
        Assert.Equal(R4PublicationTestData.Scope, scope);
        Assert.Contains("exact A", rendered!.Comment, StringComparison.Ordinal);
        Assert.Equal("[PRIVATE]", prepared.ToString());
    }

    [Fact]
    public void RetainedStateCapabilitiesDoNotExposeOrSelfIssueAuthority()
    {
        var capabilities = new[]
        {
            typeof(RetainedStatePreparedCandidate),
            typeof(RetainedStatePersistedCandidate),
            typeof(RetainedStateOwnership),
            typeof(RetainedStateOpaqueWriteAttempt),
            typeof(RetainedStateOpaqueRecord),
            typeof(RetainedStateOpaquePayloadExtraction),
            typeof(RetainedStateAcceptancePreparation),
            typeof(RetainedStateAcceptanceRecoveryDurability),
            typeof(RetainedStateAcceptanceEvidence),
            typeof(RetainedStateAcceptanceAttempt),
            typeof(RetainedStatePredecessorCopyAttempt),
            typeof(RetainedStatePendingCandidateEvidence),
            typeof(RetainedStateP5CleanupAuthorization),
            typeof(VerifiedRetainedStateAcceptance),
            typeof(RetainedStateCleanupAuthorization),
            typeof(RetainedStateAuthorityLease),
            typeof(RetainedStateObservation),
        };
        foreach (var capability in capabilities)
        {
            Assert.DoesNotContain(
                capability.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                property => property.PropertyType ==
                    typeof(RetainedStateTransactionAuthority));
        }

        var recoveryMethods = new[]
        {
            typeof(RestrictedStateService).GetMethod(
                "BindRetainedStateAcceptanceRecoveryAsync",
                BindingFlags.Static | BindingFlags.NonPublic),
            typeof(RestrictedStateService).GetMethod(
                "RecoverRetainedStateAcceptancePreparationAsync",
                BindingFlags.Static | BindingFlags.NonPublic),
        };
        Assert.All(recoveryMethods, method =>
        {
            Assert.NotNull(method);
            Assert.Contains(
                method!.GetParameters(),
                parameter => parameter.ParameterType ==
                    typeof(RetainedStateOpaquePayloadExtraction));
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType ==
                    typeof(ImmutableArray<byte>));
        });

        var issuerFactories = capabilities
            .SelectMany(type => type.GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic))
            .Where(method => method.Name == "Create")
            .ToArray();
        Assert.NotEmpty(issuerFactories);
        Assert.All(issuerFactories, method => Assert.Equal(
            typeof(object),
            method.GetParameters()[0].ParameterType));

        var serviceConstructor = Assert.Single(
            typeof(RetainedStateTransactionService).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        var exception = Assert.Throws<TargetInvocationException>(() =>
            serviceConstructor.Invoke([new object()]));
        Assert.IsType<ArgumentException>(exception.InnerException);

        var persistenceConstructor = Assert.Single(
            typeof(RetainedStatePersistence).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(
            typeof(object),
            persistenceConstructor.GetParameters()[0].ParameterType);
        var authorityFactory = Assert.Single(
            typeof(RetainedStateTransactionAuthority).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "TryCreate");
        Assert.Equal(
            typeof(object),
            authorityFactory.GetParameters()[0].ParameterType);
    }

    private static void AssertCanonicalScopedRecords(
        JsonElement[] records,
        IReadOnlyDictionary<string, byte[]> canonicalPayloads)
    {
        using var context = LineageTestData.Context();
        foreach (var record in records.Where(item =>
            !StringComparer.Ordinal.Equals(
                item.GetProperty("object_class").GetString(),
                "locator_root")))
        {
            var id = record.GetProperty("physical_artifact_id").GetString()!;
            var envelope = Convert.FromBase64String(
                record.GetProperty("encrypted_envelope_base64").GetString()!);
            Assert.Equal(
                record.GetProperty("encrypted_object_sha256").GetString(),
                OpaqueStoreHash.Sha256(envelope));
            Assert.Equal(record.GetProperty("size").GetInt64(), envelope.Length);
            Assert.True(StateControlEnvelopeV1Codec.TryDecrypt(
                context.Context,
                context.Access,
                new OpaqueStoreName(
                    record.GetProperty("opaque_name").GetString()!),
                envelope,
                out var header,
                out var payload,
                out var code),
                $"{id}:{code}");
            Assert.NotNull(header);
            Assert.Equal(LineageCodes.Ready, code);
            Assert.Equal(record.GetProperty("scope_digest").GetString(),
                header!.BaseScopeDigest);
            Assert.Equal(record.GetProperty("epoch").GetString(),
                header.Epoch);
            Assert.Equal(record.GetProperty("session_id").GetString(),
                header.SessionId);
            Assert.Equal(record.GetProperty("key_id").GetString(),
                header.KeyId);
            Assert.Equal(record.GetProperty("object_identity").GetString(),
                header.ObjectIdentity);
            Assert.Equal(OptionalString(record, "predecessor_identity"),
                header.PredecessorIdentity);
            Assert.Equal(OptionalString(record, "successor_identity"),
                header.SuccessorIdentity);
            Assert.Equal(record.GetProperty("producing_run_id").GetString(),
                header.ProducingRunIdentity);
            Assert.Equal(
                record.GetProperty("producing_run_attempt").GetInt64(),
                header.ProducingRunAttempt);
            Assert.Equal(
                record.GetProperty("created_at_unix_seconds").GetInt64(),
                header.CreatedAtUnixSeconds);
            Assert.Equal(
                record.GetProperty("logical_expires_at_unix_seconds")
                    .GetInt64(),
                header.LogicalExpiresAtUnixSeconds);
            Assert.Equal(
                record.GetProperty(
                    "required_platform_expires_at_unix_seconds").GetInt64(),
                header.RequiredPlatformExpiresAtUnixSeconds);
            Assert.Equal(
                record.GetProperty("object_class").GetString(),
                StateObjectClasses.ToWireName(header.ObjectClass));

            if (!canonicalPayloads.TryGetValue(id, out var canonical))
            {
                var decoded = record.GetProperty("decoded_record");
                var objectClass = record.GetProperty("object_class").GetString();
                if (StringComparer.Ordinal.Equals(objectClass, "lineage_head"))
                {
                    Assert.True(LineageHeadCodec.TryDecode(payload, out var head));
                    Assert.NotNull(head);
                    Assert.True(LineageHeadCodec.TryEncode(head, out canonical));
                }
                else if (StringComparer.Ordinal.Equals(
                    decoded.GetProperty("record_kind").GetString(),
                    "opaque_write_anchor"))
                {
                    var anchor = new RetainedStateOpaqueWriteAnchor(
                        decoded.GetProperty("candidate_object_identity")
                            .GetString()!,
                        decoded.GetProperty("operation_identity").GetString()!,
                        StateObjectClass.PublicationIntent,
                        OptionalString(decoded, "predecessor_identity"),
                        OptionalString(decoded, "successor_identity"),
                        decoded.GetProperty(
                            "semantic_required_expires_at_unix_seconds")
                            .GetInt64(),
                        decoded.GetProperty(
                            "required_platform_expires_at_unix_seconds")
                            .GetInt64(),
                        decoded.GetProperty("producing_run_identity")
                            .GetString()!,
                        decoded.GetProperty("producing_run_attempt").GetInt64(),
                        new OpaqueStoreName(
                            decoded.GetProperty("target_name").GetString()!),
                        decoded.GetProperty("target_object_identity")
                            .GetString()!,
                        ImmutableArray.CreateRange(Convert.FromBase64String(
                            decoded.GetProperty("target_envelope_base64")
                                .GetString()!)),
                        decoded.GetProperty("target_envelope_sha256")
                            .GetString()!,
                        (RetainedStateOpaqueWriteAnchorPhase)decoded
                            .GetProperty("dispatch_phase").GetInt32(),
                        decoded.GetProperty("target_payload_sha256")
                            .GetString()!);
                    Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryEncode(
                        anchor,
                        out canonical));
                }
                else if (StringComparer.Ordinal.Equals(
                    objectClass,
                    "candidate") &&
                    StringComparer.Ordinal.Equals(
                        decoded.GetProperty("record_kind").GetString(),
                        "accepted_state_physical_copy"))
                {
                    var copy = new AcceptedStatePhysicalCopyV1(
                        ImmutableArray.CreateRange(Convert.FromBase64String(
                            decoded.GetProperty("canonical_generation_base64")
                                .GetString()!)),
                        decoded.GetProperty("logical_generation_identity")
                            .GetString()!,
                        decoded.GetProperty(
                            "original_candidate_object_identity").GetString()!,
                        decoded.GetProperty("source_artifact_id").GetString()!,
                        decoded.GetProperty("source_archive_sha256")
                            .GetString()!,
                        decoded.GetProperty(
                            "source_encrypted_envelope_sha256").GetString()!);
                    Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
                        copy,
                        out canonical));
                }
                else
                {
                    var targets = decoded.GetProperty("targets")
                        .EnumerateArray()
                        .Select(TargetMetadata)
                        .ToImmutableArray();
                    Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
                        decoded.GetProperty("terminal_acceptance_identity")
                            .GetString()!,
                        decoded.GetProperty("base_scope_digest").GetString()!,
                        decoded.GetProperty("epoch").GetString()!,
                        decoded.GetProperty("session_id").GetString()!,
                        decoded.GetProperty("pre_cleanup_inventory_digest")
                            .GetString()!,
                        targets,
                        out var cleanup));
                    Assert.NotNull(cleanup);
                    Assert.Equal(
                        decoded.GetProperty("operation_identity").GetString(),
                        cleanup!.OperationIdentity);
                    Assert.True(RetainedStateCleanupRecordCodec.TryEncode(
                        cleanup,
                        out canonical));
                }
            }

            Assert.True(payload.AsSpan().SequenceEqual(canonical), id);
        }
    }

    private static OpaqueStoreObjectMetadata Metadata(
        string name,
        string id) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(name),
                new OpaqueStoreObjectId(id)),
            new OpaqueStoreProducingRun("900", 2),
            new OpaqueStoreArchiveDigest(new string('a', 64)),
            new OpaqueStoreEncryptedObjectDigest(new string('b', 64)),
            1_800_000_000,
            100);

    private static OpaqueStoreObjectMetadata TargetMetadata(
        JsonElement target) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(target.GetProperty("name").GetString()!),
                new OpaqueStoreObjectId(
                    target.GetProperty("object_id").GetString()!)),
            new OpaqueStoreProducingRun(
                target.GetProperty("producing_run_identity").GetString()!,
                target.GetProperty("producing_run_attempt").GetInt64()),
            new OpaqueStoreArchiveDigest(
                target.GetProperty("archive_sha256").GetString()!),
            new OpaqueStoreEncryptedObjectDigest(
                target.GetProperty("encrypted_object_sha256").GetString()!),
            target.GetProperty("expires_at_unix_seconds").GetInt64(),
            target.GetProperty("size").GetInt64());

    private static JsonElement Record(
        JsonElement[] records,
        string phase,
        string objectClass) => records.Single(record =>
        StringComparer.Ordinal.Equals(
            record.GetProperty("creation_phase").GetString(),
            phase) &&
        StringComparer.Ordinal.Equals(
            record.GetProperty("object_class").GetString(),
            objectClass) &&
        (!StringComparer.Ordinal.Equals(objectClass, "candidate") ||
            !record.GetProperty("decoded_record")
                .TryGetProperty("record_kind", out var recordKind) ||
            !StringComparer.Ordinal.Equals(
                recordKind.GetString(),
                "accepted_state_physical_copy")));

    private static JsonElement RecoveryRecord(
        JsonElement[] records,
        string phase,
        string recordKind) => records.Single(record =>
        StringComparer.Ordinal.Equals(
            record.GetProperty("creation_phase").GetString(),
            phase) &&
        StringComparer.Ordinal.Equals(
            record.GetProperty("object_class").GetString(),
            "publication_intent") &&
        StringComparer.Ordinal.Equals(
            record
                .GetProperty("decoded_record")
                .GetProperty("record_kind")
                .GetString(),
            recordKind));

    private static long PositiveId(JsonElement value, string propertyName) =>
        long.Parse(
            value.GetProperty(propertyName).GetString()!,
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static string? OptionalString(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null
            ? null
            : property.GetString();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Join(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException("Repository root not found.");
    }

    private static RetainedStateOpaqueWriteAnchor Anchor(byte[] envelope) =>
        new(
            new string('a', 64),
            new string('b', 64),
            StateObjectClass.PublicationIntent,
            new string('a', 64),
            new string('c', 64),
            1_700_000_000,
            1_800_000_000,
            "900",
            2,
            new OpaqueStoreName("target"),
            new string('d', 64),
            ImmutableArray.CreateRange(envelope),
            OpaqueStoreHash.Sha256(envelope),
            RetainedStateOpaqueWriteAnchorPhase.PreparedBeforeTargetDispatch,
            new string('e', 64));
}
