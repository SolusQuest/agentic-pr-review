using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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
        // Null input objects are caller bugs but still surface as a typed
        // failure — no exception crosses the public boundary.
        if (input is null
            || input.History is null
            || input.ExpectedIdentities is null
            || input.CurrentContext is null
            || input.Interaction is null
            || input.Envelopes is null
            || input.SessionEpoch is null
            || input.History is MaterializationHistory.ContinuationHistory { Prior: null }
            || input.History is MaterializationHistory.ResetHistory { AcceptedPredecessor: null })
        {
            return Fail(PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityInvalid));
        }

        // Stage: host-declared identities.
        var identityError = ValidateHostIdentities(input);
        if (identityError is not null)
        {
            return Fail(identityError);
        }

        // Stage: envelope validation in the frozen global order (structure for
        // all five → embedded identities for all five → canonical/digest for
        // all five, each in template → policy → tools → config → adapter order).
        ValidatedEnvelope? template = null;
        ValidatedEnvelope? policy = null;
        ValidatedEnvelope? tools = null;
        ValidatedEnvelope? cacheConfig = null;
        ValidatedEnvelope? adapter = null;
        try
        {
            var envelopeError = ValidateEnvelopesPhased(input, ref template, ref policy, ref tools, ref cacheConfig, ref adapter);
            if (envelopeError is not null)
            {
                return Fail(envelopeError);
            }
        }
        catch (ObjectDisposedException)
        {
            return Fail(PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid));
        }
        catch (InvalidOperationException)
        {
            return Fail(PrefixDiagnostic.Create(PrefixDiagnosticCodes.CanonicalInputRejected));
        }
        catch (System.Text.Json.JsonException)
        {
            return Fail(PrefixDiagnostic.Create(PrefixDiagnosticCodes.CanonicalInputRejected));
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

        var evidenceError = ValidateCurrentEvidence(input.CurrentContext);
        if (evidenceError is not null)
        {
            return Fail(evidenceError);
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
            (
                LogicalProjection.ReviewContextKind,
                input.CurrentContext.CurrentEvidence is null
                    ? LogicalProjection.ProjectReviewContextSegment(contextOutcome.Value)
                    : LogicalProjection.ProjectCurrentReviewSegment(
                        contextOutcome.Value, input.CurrentContext.CurrentEvidence)),
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

    private static PrefixDiagnostic? ValidateEnvelopesPhased(
        PrefixMaterializationInput input,
        ref ValidatedEnvelope? template,
        ref ValidatedEnvelope? policy,
        ref ValidatedEnvelope? tools,
        ref ValidatedEnvelope? cacheConfig,
        ref ValidatedEnvelope? adapter)
    {
        var structureError =
            PrefixEnvelopeValidator.ValidateStructure(PrefixEnvelopeValidator.EnvelopeKind.Template, input.Envelopes.Template)
            ?? PrefixEnvelopeValidator.ValidateStructure(PrefixEnvelopeValidator.EnvelopeKind.Policy, input.Envelopes.Policy)
            ?? PrefixEnvelopeValidator.ValidateStructure(PrefixEnvelopeValidator.EnvelopeKind.Tools, input.Envelopes.Tools)
            ?? PrefixEnvelopeValidator.ValidateStructure(PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, input.Envelopes.CacheConfig)
            ?? PrefixEnvelopeValidator.ValidateStructure(PrefixEnvelopeValidator.EnvelopeKind.Adapter, input.Envelopes.Adapter);
        if (structureError is not null)
        {
            return structureError;
        }

        var embeddedError =
            PrefixEnvelopeValidator.ValidateEmbeddedIdentity(PrefixEnvelopeValidator.EnvelopeKind.Template, input.Envelopes.Template)
            ?? PrefixEnvelopeValidator.ValidateEmbeddedIdentity(PrefixEnvelopeValidator.EnvelopeKind.Policy, input.Envelopes.Policy)
            ?? PrefixEnvelopeValidator.ValidateEmbeddedIdentity(PrefixEnvelopeValidator.EnvelopeKind.Tools, input.Envelopes.Tools)
            ?? PrefixEnvelopeValidator.ValidateEmbeddedIdentity(PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, input.Envelopes.CacheConfig)
            ?? PrefixEnvelopeValidator.ValidateEmbeddedIdentity(PrefixEnvelopeValidator.EnvelopeKind.Adapter, input.Envelopes.Adapter);
        if (embeddedError is not null)
        {
            return embeddedError;
        }

        return
            CanonicalizeAll(input, out var templateBytes, out var policyBytes, out var toolsBytes, out var configBytes, out var adapterBytes,
                out var templateCapped, out var policyCapped, out var toolsCapped, out var configCapped, out var adapterCapped)
            ?? PrefixEnvelopeValidator.CheckCanonicalCap(PrefixEnvelopeValidator.EnvelopeKind.Template, templateCapped)
            ?? PrefixEnvelopeValidator.CheckCanonicalCap(PrefixEnvelopeValidator.EnvelopeKind.Policy, policyCapped)
            ?? PrefixEnvelopeValidator.CheckCanonicalCap(PrefixEnvelopeValidator.EnvelopeKind.Tools, toolsCapped)
            ?? PrefixEnvelopeValidator.CheckCanonicalCap(PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, configCapped)
            ?? PrefixEnvelopeValidator.CheckCanonicalCap(PrefixEnvelopeValidator.EnvelopeKind.Adapter, adapterCapped)
            ?? SealAll(input, templateBytes, policyBytes, toolsBytes, configBytes, adapterBytes, ref template, ref policy, ref tools, ref cacheConfig, ref adapter);
    }

    private static PrefixDiagnostic? CanonicalizeAll(
        PrefixMaterializationInput input,
        out ImmutableArray<byte> templateBytes,
        out ImmutableArray<byte> policyBytes,
        out ImmutableArray<byte> toolsBytes,
        out ImmutableArray<byte> configBytes,
        out ImmutableArray<byte> adapterBytes,
        out bool templateCapped,
        out bool policyCapped,
        out bool toolsCapped,
        out bool configCapped,
        out bool adapterCapped)
    {
        templateBytes = ImmutableArray<byte>.Empty;
        policyBytes = ImmutableArray<byte>.Empty;
        toolsBytes = ImmutableArray<byte>.Empty;
        configBytes = ImmutableArray<byte>.Empty;
        adapterBytes = ImmutableArray<byte>.Empty;
        templateCapped = false;
        policyCapped = false;
        toolsCapped = false;
        configCapped = false;
        adapterCapped = false;

        var error = PrefixEnvelopeValidator.Canonicalize(
            PrefixEnvelopeValidator.EnvelopeKind.Template, input.Envelopes.Template, out templateBytes, out templateCapped)
            ?? PrefixEnvelopeValidator.Canonicalize(PrefixEnvelopeValidator.EnvelopeKind.Policy, input.Envelopes.Policy, out policyBytes, out policyCapped)
            ?? PrefixEnvelopeValidator.Canonicalize(PrefixEnvelopeValidator.EnvelopeKind.Tools, input.Envelopes.Tools, out toolsBytes, out toolsCapped)
            ?? PrefixEnvelopeValidator.Canonicalize(PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, input.Envelopes.CacheConfig, out configBytes, out configCapped)
            ?? PrefixEnvelopeValidator.Canonicalize(PrefixEnvelopeValidator.EnvelopeKind.Adapter, input.Envelopes.Adapter, out adapterBytes, out adapterCapped);
        return error;
    }

    private static PrefixDiagnostic? SealAll(
        PrefixMaterializationInput input,
        ImmutableArray<byte> templateBytes,
        ImmutableArray<byte> policyBytes,
        ImmutableArray<byte> toolsBytes,
        ImmutableArray<byte> configBytes,
        ImmutableArray<byte> adapterBytes,
        ref ValidatedEnvelope? template,
        ref ValidatedEnvelope? policy,
        ref ValidatedEnvelope? tools,
        ref ValidatedEnvelope? cacheConfig,
        ref ValidatedEnvelope? adapter)
    {
        template = PrefixEnvelopeValidator.SealValidatedEnvelope(
            PrefixEnvelopeValidator.EnvelopeKind.Template, input.Envelopes.Template, templateBytes);
        policy = PrefixEnvelopeValidator.SealValidatedEnvelope(
            PrefixEnvelopeValidator.EnvelopeKind.Policy, input.Envelopes.Policy, policyBytes);
        tools = PrefixEnvelopeValidator.SealValidatedEnvelope(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, input.Envelopes.Tools, toolsBytes);
        cacheConfig = PrefixEnvelopeValidator.SealValidatedEnvelope(
            PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, input.Envelopes.CacheConfig, configBytes);
        adapter = PrefixEnvelopeValidator.SealValidatedEnvelope(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, input.Envelopes.Adapter, adapterBytes);
        return null;
    }

    private static PrefixDiagnostic? ValidateHostIdentities(PrefixMaterializationInput input)
    {
        var identities = input.ExpectedIdentities;
        if (!PrefixIdentityValidation.IsValidIdentity(identities.ProviderId)
            || !PrefixIdentityValidation.IsValidIdentity(identities.ModelId)
            || !PrefixIdentityValidation.IsValidIdentity(identities.WorkflowIdentity)
            || !PrefixIdentityValidation.IsValidIdentity(identities.TrustedExecutionDomain))
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

    private static PrefixDiagnostic? ValidateCurrentEvidence(ValidatedContextSource source)
    {
        var evidence = source.CurrentEvidence;
        if (evidence is null)
        {
            return null;
        }

        if (evidence.Subject is null
            || evidence.Subject.Length > 4_000
            || ContainsInvalidUnicode(evidence.Subject)
            || evidence.Files.IsDefault
            || evidence.Files.Length > 256
            || source.ChangedFiles.IsDefault
            || evidence.Files.Length != source.ChangedFiles.Length)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CurrentContextInvalid);
        }

        var totalBytes = Encoding.UTF8.GetByteCount(evidence.Subject);
        for (var index = 0; index < evidence.Files.Length; index++)
        {
            var file = evidence.Files[index];
            var sourceFile = source.ChangedFiles[index];
            if (file is null
                || sourceFile is null
                || !StringComparer.Ordinal.Equals(file.Path, sourceFile.Path)
                || !IsSafeRelativePath(file.Path)
                || CountCodePoints(file.Path) > 500
                || ContainsInvalidUnicode(file.Path)
                || (file.Patch is null) != (sourceFile.Patch is null)
                || (file.Patch is not null && (file.Patch.Length > 20_000 || ContainsInvalidUnicode(file.Patch))))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CurrentContextInvalid);
            }

            totalBytes += Encoding.UTF8.GetByteCount(file.Path);
            if (file.Patch is not null)
            {
                if (sourceFile.Patch is null
                    || !StringComparer.Ordinal.Equals(
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Patch))).ToLowerInvariant(),
                        sourceFile.Patch.Sha256))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CurrentContextInvalid);
                }

                totalBytes += Encoding.UTF8.GetByteCount(file.Patch);
            }

            if (totalBytes > 200_000)
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CurrentContextInvalid);
            }
        }

        return null;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\')
            || System.Text.RegularExpressions.Regex.IsMatch(path, "^[A-Za-z][A-Za-z0-9+.-]*:"))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static int CountCodePoints(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++, count++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                index++;
            }
        }

        return count;
    }

    private static bool ContainsInvalidUnicode(string value)
    {
        if (value.Contains('\0'))
        {
            return true;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static PrefixMaterializationOutcome Fail(PrefixDiagnostic diagnostic) =>
        new(null, ImmutableArray.Create(diagnostic));
}
