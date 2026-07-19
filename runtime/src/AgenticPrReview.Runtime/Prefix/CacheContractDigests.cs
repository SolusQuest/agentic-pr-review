using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Host-authoritative cache-contract digest producers. Each helper validates
/// the raw envelope, canonicalizes it, and computes digestId per ## Prefix
/// Contract. No exception escapes; failures are typed outcomes.
/// </summary>
public static class CacheContractDigests
{
    public static DigestOutcome ComputeTemplateId(JsonElement envelope) =>
        Compute(PrefixEnvelopeValidator.EnvelopeKind.Template, envelope);

    public static DigestOutcome ComputePolicyId(JsonElement envelope) =>
        Compute(PrefixEnvelopeValidator.EnvelopeKind.Policy, envelope);

    public static DigestOutcome ComputeToolDefinitionId(JsonElement envelope) =>
        Compute(PrefixEnvelopeValidator.EnvelopeKind.Tools, envelope);

    public static DigestOutcome ComputeCacheConfigId(JsonElement envelope) =>
        Compute(PrefixEnvelopeValidator.EnvelopeKind.CacheConfig, envelope);

    public static DigestOutcome ComputeAdapterId(JsonElement envelope) =>
        Compute(PrefixEnvelopeValidator.EnvelopeKind.Adapter, envelope);

    private static DigestOutcome Compute(PrefixEnvelopeValidator.EnvelopeKind kind, JsonElement envelope)
    {
        var error = PrefixEnvelopeValidator.Validate(kind, envelope, out var validated);
        if (error is not null)
        {
            return new DigestOutcome(null, ImmutableArray.Create(error));
        }

        return new DigestOutcome(validated!.Digest, ImmutableArray<PrefixDiagnostic>.Empty);
    }
}
