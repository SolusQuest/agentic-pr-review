using System.Buffers;
using System.Collections.Immutable;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Deterministic canonical prefix materialization (issue #50). Fail-fast:
/// an outcome is either a value or exactly one diagnostic, in the frozen
/// stage order.
/// </summary>
public static class PrefixMaterializer
{
    public const long LedgerSchemaVersion = 1;
    public const long PrefixContractVersion = 1;

    public static PrefixMaterializationOutcome Materialize(PrefixMaterializationInput input)
    {
        // Stage: host-declared identities.
        var identityError = ValidateHostIdentities(input);
        if (identityError is not null)
        {
            return Fail(identityError);
        }

        // Stage: envelope validation (template → policy → tools → cache config → adapter).
        var envelopeError = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, input.Envelopes.Template, out var template);
        if (envelopeError is not null)
        {
            return Fail(envelopeError);
        }

        envelopeError = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Policy, input.Envelopes.Policy, out var policy);
        if (envelopeError is not null)
        {
            return Fail(envelopeError);
        }

        envelopeError = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, input.Envelopes.Tools, out var tools);
        if (envelopeError is not null)
        {
            return Fail(envelopeError);
        }

        envelopeError = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, input.Envelopes.CacheConfig, out var cacheConfig);
        if (envelopeError is not null)
        {
            return Fail(envelopeError);
        }

        envelopeError = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, input.Envelopes.Adapter, out var adapter);
        if (envelopeError is not null)
        {
            return Fail(envelopeError);
        }

        // Stage: digest equality against host-declared identities.
        var mismatch = CompareDigests(input.ExpectedIdentities, template!, policy!, tools!, cacheConfig!, adapter!);
        if (mismatch is not null)
        {
            return Fail(mismatch);
        }

        // Stage: history identity equality.
        var historyError = CheckHistoryIdentities(input);
        if (historyError is not null)
        {
            return Fail(historyError);
        }

        // Stage: dynamic context through the #49 builder.
        var contextOutcome = LedgerBuilder.BuildReviewContext(
            input.CurrentContext, input.ExpectedIdentities, input.Interaction);
        if (contextOutcome.Value is null)
        {
            var cause = contextOutcome.Diagnostics.Length > 0 ? contextOutcome.Diagnostics[0].Code : null;
            return Fail(PrefixDiagnostic.Create(PrefixDiagnosticCodes.CurrentContextInvalid, causeCode: cause));
        }

        // Segment assembly.
        var stableSegments = new List<(string Kind, ImmutableArray<byte> Bytes)>
        {
            (LogicalProjection.TemplateKind, LogicalProjection.ProjectTemplateSegment(template!)),
            (LogicalProjection.PolicyKind, LogicalProjection.ProjectPolicySegment(policy!)),
            (LogicalProjection.ToolsKind, LogicalProjection.ProjectToolsSegment(tools!)),
        };

        if (input.History is MaterializationHistory.ContinuationHistory continuation)
        {
            foreach (var record in continuation.Prior.Model.Records)
            {
                switch (record)
                {
                    case ReviewContextRecord context:
                        stableSegments.Add((LogicalProjection.ReviewContextKind, LogicalProjection.ProjectReviewContextSegment(context)));
                        break;
                    case ReviewOutcomeRecord outcome:
                        stableSegments.Add((LogicalProjection.ReviewOutcomeKind, LogicalProjection.ProjectReviewOutcomeSegment(outcome)));
                        break;
                }
            }
        }

        var dynamicSegments = new List<(string Kind, ImmutableArray<byte> Bytes)>
        {
            (LogicalProjection.ReviewContextKind, LogicalProjection.ProjectReviewContextSegment(contextOutcome.Value)),
        };

        // Stream framing and bounds.
        var streamError = FrameStreams(
            stableSegments,
            dynamicSegments,
            out var stableLogical,
            out var dynamicLogical,
            out var stableProvider,
            out var dynamicProvider);
        if (streamError is not null)
        {
            return Fail(streamError);
        }

        // Hash derivation.
        var logicalHash = ComputeLogicalPrefixSha256(stableLogical.AsSpan());
        var prefixHash = ComputePrefixSha256(
            input.ExpectedIdentities,
            template!.Digest,
            policy!.Digest,
            tools!.Digest,
            cacheConfig!.Digest,
            adapter!.Digest,
            stableProvider.AsSpan());

        return new PrefixMaterializationOutcome(
            new PrefixMaterialization(
                stableLogical,
                stableProvider,
                dynamicLogical,
                dynamicProvider,
                stableSegments.Count,
                logicalHash,
                prefixHash,
                template.Digest,
                policy.Digest,
                tools.Digest,
                cacheConfig.Digest,
                adapter.Digest),
            ImmutableArray<PrefixDiagnostic>.Empty);
    }

    // Test-only version seam for hash-framing invalidation vectors.
    internal static string ComputeLogicalPrefixSha256(
        ReadOnlySpan<byte> stableLogicalStream,
        long ledgerSchemaVersion = LedgerSchemaVersion,
        long prefixContractVersion = PrefixContractVersion)
    {
        var preimage = new ArrayBufferWriter<byte>(stableLogicalStream.Length + 128);
        preimage.Write(PrefixDomainTags.LogicalPrefix);
        PrefixHashPrimitives.WriteIdentity(preimage, ledgerSchemaVersion);
        PrefixHashPrimitives.WriteIdentity(preimage, prefixContractVersion);
        preimage.Write(stableLogicalStream);
        return PrefixHashPrimitives.Sha256Hex(preimage.WrittenSpan);
    }

    // Test-only version seam for hash-framing invalidation vectors.
    internal static string ComputePrefixSha256(
        ExpectedIdentities identities,
        string templateId,
        string policyId,
        string toolDefinitionId,
        string cacheConfigId,
        string adapterId,
        ReadOnlySpan<byte> stableProviderStream,
        long ledgerSchemaVersion = LedgerSchemaVersion,
        long prefixContractVersion = PrefixContractVersion)
    {
        var preimage = new ArrayBufferWriter<byte>(stableProviderStream.Length + 1024);
        preimage.Write(PrefixDomainTags.ProviderPrefix);
        PrefixHashPrimitives.WriteIdentity(preimage, ledgerSchemaVersion);
        PrefixHashPrimitives.WriteIdentity(preimage, prefixContractVersion);
        PrefixHashPrimitives.WriteIdentity(preimage, identities.ProviderId);
        PrefixHashPrimitives.WriteIdentity(preimage, identities.ModelId);
        PrefixHashPrimitives.WriteIdentity(preimage, adapterId);
        PrefixHashPrimitives.WriteIdentity(preimage, templateId);
        PrefixHashPrimitives.WriteIdentity(preimage, policyId);
        PrefixHashPrimitives.WriteIdentity(preimage, toolDefinitionId);
        PrefixHashPrimitives.WriteIdentity(preimage, cacheConfigId);
        preimage.Write(stableProviderStream);
        return PrefixHashPrimitives.Sha256Hex(preimage.WrittenSpan);
    }

    private static PrefixDiagnostic? ValidateHostIdentities(PrefixMaterializationInput input)
    {
        var identities = input.ExpectedIdentities;
        if (!PrefixIdentityValidation.IsValidIdentity(identities.ProviderId)
            || !PrefixIdentityValidation.IsValidIdentity(identities.ModelId))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityInvalid);
        }

        if (PrefixIdentityValidation.IsModelAliasLiteral(identities.ModelId))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.ModelAliasLiteral);
        }

        if (!PrefixIdentityValidation.IsValidDigest(identities.AdapterId)
            || !PrefixIdentityValidation.IsValidDigest(identities.TemplateId)
            || !PrefixIdentityValidation.IsValidDigest(identities.PolicyId)
            || !PrefixIdentityValidation.IsValidDigest(identities.ToolDefinitionId)
            || !PrefixIdentityValidation.IsValidDigest(identities.CacheConfigId))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.DigestInvalid);
        }

        if (!PrefixIdentityValidation.IsValidEpoch(input.SessionEpoch))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EpochInvalid);
        }

        if (!PrefixIdentityValidation.IsValidOrdinal(input.Interaction.InteractionOrdinal))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.OrdinalInvalid);
        }

        return null;
    }

    private static PrefixDiagnostic? CompareDigests(
        ExpectedIdentities identities,
        ValidatedEnvelope template,
        ValidatedEnvelope policy,
        ValidatedEnvelope tools,
        ValidatedEnvelope cacheConfig,
        ValidatedEnvelope adapter)
    {
        if (!StringComparer.Ordinal.Equals(template.Digest, identities.TemplateId)
            || !StringComparer.Ordinal.Equals(policy.Digest, identities.PolicyId)
            || !StringComparer.Ordinal.Equals(tools.Digest, identities.ToolDefinitionId)
            || !StringComparer.Ordinal.Equals(cacheConfig.Digest, identities.CacheConfigId)
            || !StringComparer.Ordinal.Equals(adapter.Digest, identities.AdapterId))
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CacheContractIdMismatch);
        }

        return null;
    }

    private static PrefixDiagnostic? CheckHistoryIdentities(PrefixMaterializationInput input)
    {
        switch (input.History)
        {
            case MaterializationHistory.BootstrapHistory:
                return null;

            case MaterializationHistory.ContinuationHistory continuation:
            {
                var header = continuation.Prior.Model.Header;
                var identities = input.ExpectedIdentities;
                if (!StringComparer.Ordinal.Equals(header.ProviderId, identities.ProviderId)
                    || !StringComparer.Ordinal.Equals(header.ModelId, identities.ModelId)
                    || !StringComparer.Ordinal.Equals(header.AdapterId, identities.AdapterId)
                    || !StringComparer.Ordinal.Equals(header.TemplateId, identities.TemplateId)
                    || !StringComparer.Ordinal.Equals(header.PolicyId, identities.PolicyId)
                    || !StringComparer.Ordinal.Equals(header.ToolDefinitionId, identities.ToolDefinitionId)
                    || !StringComparer.Ordinal.Equals(header.CacheConfigId, identities.CacheConfigId)
                    || !StringComparer.Ordinal.Equals(header.Repository, identities.Repository)
                    || !StringComparer.Ordinal.Equals(header.HeadRepository, identities.HeadRepository)
                    || header.PullRequest != identities.PullRequest
                    || !StringComparer.Ordinal.Equals(header.WorkflowIdentity, identities.WorkflowIdentity)
                    || !StringComparer.Ordinal.Equals(header.TrustedExecutionDomain, identities.TrustedExecutionDomain)
                    || !StringComparer.Ordinal.Equals(header.SessionEpoch, input.SessionEpoch))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityMismatch);
                }

                return null;
            }

            case MaterializationHistory.ResetHistory reset:
            {
                var header = reset.AcceptedPredecessor.Model.Header;
                var identities = input.ExpectedIdentities;
                if (!StringComparer.Ordinal.Equals(header.Repository, identities.Repository)
                    || !StringComparer.Ordinal.Equals(header.HeadRepository, identities.HeadRepository)
                    || header.PullRequest != identities.PullRequest
                    || !StringComparer.Ordinal.Equals(header.WorkflowIdentity, identities.WorkflowIdentity)
                    || !StringComparer.Ordinal.Equals(header.TrustedExecutionDomain, identities.TrustedExecutionDomain)
                    || !StringComparer.Ordinal.Equals(header.SessionEpoch, input.SessionEpoch))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityMismatch);
                }

                return null;
            }

            default:
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityInvalid);
        }
    }

    private static PrefixDiagnostic? FrameStreams(
        List<(string Kind, ImmutableArray<byte> Bytes)> stableSegments,
        List<(string Kind, ImmutableArray<byte> Bytes)> dynamicSegments,
        out ImmutableArray<byte> stableLogical,
        out ImmutableArray<byte> dynamicLogical,
        out ImmutableArray<byte> stableProvider,
        out ImmutableArray<byte> dynamicProvider)
    {
        stableLogical = ImmutableArray<byte>.Empty;
        dynamicLogical = ImmutableArray<byte>.Empty;
        stableProvider = ImmutableArray<byte>.Empty;
        dynamicProvider = ImmutableArray<byte>.Empty;

        foreach (var (_, bytes) in stableSegments.Concat(dynamicSegments))
        {
            var segmentError = PrefixGuards.CheckSegmentPayload(bytes.Length);
            if (segmentError is not null)
            {
                return segmentError;
            }
        }

        var stableLogicalWriter = new ArrayBufferWriter<byte>();
        var stableProviderWriter = new ArrayBufferWriter<byte>();
        foreach (var (kind, bytes) in stableSegments)
        {
            AppendFramed(stableLogicalWriter, bytes.AsSpan());
            var block = ProviderBlockMapper.MapBlock(kind, bytes.AsSpan());
            var blockError = PrefixGuards.CheckProviderBlockPayload(block.Length);
            if (blockError is not null)
            {
                return blockError;
            }

            AppendFramed(stableProviderWriter, block.AsSpan());
        }

        var stableLogicalError = PrefixGuards.CheckLogicalStableTotal(stableLogicalWriter.WrittenCount);
        if (stableLogicalError is not null)
        {
            return stableLogicalError;
        }

        var stableProviderError = PrefixGuards.CheckProviderStableTotal(stableProviderWriter.WrittenCount);
        if (stableProviderError is not null)
        {
            return stableProviderError;
        }

        var dynamicLogicalWriter = new ArrayBufferWriter<byte>();
        var dynamicProviderWriter = new ArrayBufferWriter<byte>();
        foreach (var (kind, bytes) in dynamicSegments)
        {
            AppendFramed(dynamicLogicalWriter, bytes.AsSpan());
            var block = ProviderBlockMapper.MapBlock(kind, bytes.AsSpan());
            var blockError = PrefixGuards.CheckProviderBlockPayload(block.Length);
            if (blockError is not null)
            {
                return blockError;
            }

            AppendFramed(dynamicProviderWriter, block.AsSpan());
        }

        var dynamicLogicalError = PrefixGuards.CheckLogicalDynamicTotal(dynamicLogicalWriter.WrittenCount);
        if (dynamicLogicalError is not null)
        {
            return dynamicLogicalError;
        }

        var dynamicProviderError = PrefixGuards.CheckProviderDynamicTotal(dynamicProviderWriter.WrittenCount);
        if (dynamicProviderError is not null)
        {
            return dynamicProviderError;
        }

        stableLogical = stableLogicalWriter.WrittenSpan.ToArray().ToImmutableArray();
        dynamicLogical = dynamicLogicalWriter.WrittenSpan.ToArray().ToImmutableArray();
        stableProvider = stableProviderWriter.WrittenSpan.ToArray().ToImmutableArray();
        dynamicProvider = dynamicProviderWriter.WrittenSpan.ToArray().ToImmutableArray();
        return null;
    }

    private static void AppendFramed(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> payload)
    {
        PrefixHashPrimitives.WriteUInt32BigEndian(writer, checked((uint)payload.Length));
        writer.Write(payload);
    }

    private static PrefixMaterializationOutcome Fail(PrefixDiagnostic diagnostic) =>
        new(null, ImmutableArray.Create(diagnostic));
}
