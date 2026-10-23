using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Private supporting reader only. It never executes a review, restores state or
// substitutes an assembly in the original archive. No raw data goes to CI output.
internal static class TransactionInventoryProof
{
    internal sealed record Entry(string Id, string Name, string Archive, string Run, long Attempt);
    internal sealed record Input(string Mode, JsonElement Launch, Entry[] Artifacts,
        Dictionary<string, string>? Names, string? ScopeDigest);
    private sealed record ScopeResult(LineageBaseScope Scope, string PolicySha256);
    private sealed record Decoded(string Id, string Kind, string ObjectIdentity, string Run,
        string? Head = null, string? Candidate = null, string? Logical = null,
        string? StateEnvelope = null, string? Previous = null, string? Body = null,
        string? PublicationScope = null, string? Intent = null, string? Acceptance = null);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        Require(bytes.Length is > 0 and <= 96 * 1024 * 1024);
        var input = JsonSerializer.Deserialize<Input>(bytes, Json)!;
        CryptographicOperations.ZeroMemory(bytes);
        Require(input.Mode is "derive" or "decode" && input.Artifacts.Length is > 0 and <= 256 &&
            input.Artifacts.Select(item => item.Id).Distinct().Count() == input.Artifacts.Length);
        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle("42");
        Require(LocatorStateKeyRing.TryCreate(access, "42",
            Convert.ToBase64String(Enumerable.Repeat((byte)0x74, 32).ToArray()), null, out var keys, out _));
        using (keys)
        {
            var carriers = new Dictionary<string, ArtifactTransportEnvelope>();
            try
            {
                foreach (var entry in input.Artifacts)
                {
                    var zip = Convert.FromBase64String(entry.Archive);
                    try
                    {
                        var carrier = await ArtifactTransportFormat.DecodeAsync(zip,
                            Convert.ToHexStringLower(SHA256.HashData(zip)), OpaqueStoreLimits.MaximumObjectBytes, default);
                        Require(carrier.RunId == entry.Run && carrier.Attempt == entry.Attempt);
                        carriers.Add(entry.Id, carrier);
                    }
                    finally { CryptographicOperations.ZeroMemory(zip); }
                }
                var root = input.Artifacts.First(item => ArtifactTransportFormat.Member(LocatorRootFormat.StoreName, item.Name));
                Require(LocatorRootSentinelCodec.TryDecrypt(access, keys!, carriers[root.Id].Bytes, out var sentinel, out _) && sentinel is not null);
                try
                {
                    Require(LocatorContext.TryCreate(access, keys!, sentinel!.Root, true,
                        sentinel.RequiredExpiresAtUnixSeconds, TimeProvider.System, out var locator));
                    using (locator)
                    {
                        var names = input.Names;
                        var scopeDigest = input.ScopeDigest;
                        string? policy = null;
                        if (input.Mode == "derive")
                        {
                            var launchBytes = JsonSerializer.SerializeToUtf8Bytes(input.Launch);
                            Require(ActionHostJsonCodec.TryReadLaunch(launchBytes, out var launch, out _) && launch is not null);
                            CryptographicOperations.ZeroMemory(launchBytes);
                            using var inputs = launch!.Inputs;
                            // Called only after the archive Host closed. This callback
                            // reads trusted sources; it grants no state/provider effects.
                            var result = await new ActionHostComposition().RunPreparedFreshAsync(launch, prepared =>
                            {
                                Require(AcceptedStateProductionAuthorization.TryAuthorize(prepared.RestoreRequest, out var authority));
                                return Task.FromResult(new ScopeResult(AuthorizedAcceptedStateComposer.BaseScope(authority!), prepared.Policy.PolicySha256));
                            }, default);
                            Require(result.Value is not null && result.Failure is null);
                            Require(LineageBaseScopeCodec.TryEncode(result.Value!.Scope, out var canonical));
                            try
                            {
                                scopeDigest = Convert.ToHexStringLower(SHA256.HashData(canonical));
                                policy = result.Value.PolicySha256;
                                names = new() { ["locator_root"] = LocatorRootFormat.StoreName };
                                foreach (var kind in StateObjectClasses.Scoped)
                                {
                                    var wire = StateObjectClasses.ToWireName(kind);
                                    Require(locator!.TryDeriveOpaqueName(access, wire, canonical, out var name));
                                    names.Add(wire, name!.Value);
                                }
                            }
                            finally { CryptographicOperations.ZeroMemory(canonical); }
                        }
                        Require(names?.Count == StateObjectClasses.All.Length && LineageValidation.IsSha256(scopeDigest));
                        var records = new List<Decoded>();
                        foreach (var entry in input.Artifacts)
                        {
                            var matching = names!.Where(pair => ArtifactTransportFormat.Member(pair.Value, entry.Name)).ToArray();
                            Require(matching.Length == 1);
                            var kind = matching[0].Key;
                            if (kind == "locator_root") continue;
                            Require(StateControlEnvelopeV1Codec.TryDecrypt(locator!, access, new(matching[0].Value),
                                carriers[entry.Id].Bytes, out var header, out var payload, out _) && header is not null);
                            try
                            {
                                Require(StateObjectClasses.ToWireName(header!.ObjectClass) == kind && header.BaseScopeDigest == scopeDigest &&
                                    header.ProducingRunIdentity == entry.Run && header.ProducingRunAttempt == entry.Attempt);
                                var record = new Decoded(entry.Id, kind, header.ObjectIdentity, entry.Run);
                                if (header.ObjectClass == StateObjectClass.Candidate)
                                {
                                    var direct = AcceptedStateGenerationRecordCodec.TryDecode(payload, out var generation);
                                    string? original = null;
                                    if (!direct)
                                    {
                                        Require(AcceptedStatePhysicalCopyCodec.TryDecode(payload, out var copy) && copy is not null);
                                        Require(AcceptedStateGenerationRecordCodec.TryDecode(copy!.CanonicalGenerationBytes.AsSpan(), out generation));
                                        original = copy.OriginalCandidateObjectIdentity;
                                    }
                                    Require(generation is not null);
                                    Require(AcceptedStatePublicationPayloadCodec.TryDecode(generation!.PublicationPayloadBytes.AsSpan(), out var publication) && publication is not null);
                                    record = record with { Kind = direct ? "candidate" : "candidate_copy", Head = generation.ProducerHeadSha,
                                        Candidate = original ?? header.ObjectIdentity, StateEnvelope = generation.StateEnvelopeSha256,
                                        Previous = generation.PredecessorEnvelopeSha256, Body = publication!.BodySha256, PublicationScope = publication.ScopeSha256 };
                                }
                                else if (header.ObjectClass == StateObjectClass.Acceptance)
                                {
                                    Require(AcceptedStateAcceptanceReceiptCodec.TryDecode(payload, out var acceptance) && acceptance is not null);
                                    record = record with { Head = acceptance!.ReviewedHeadSha, Candidate = acceptance.OriginalCandidateObjectIdentity,
                                        Logical = acceptance.LogicalGenerationIdentity, Previous = acceptance.PreviousLogicalGenerationIdentity,
                                        Body = acceptance.BodySha256, PublicationScope = acceptance.ScopeSha256 };
                                }
                                else if (header.ObjectClass == StateObjectClass.ContextClaim)
                                {
                                    Require(SolusContextClaimRecordCodec.TryDecode(payload, out var claim) && claim is not null);
                                    record = record with { Acceptance = claim!.AcceptanceIdentity, Logical = claim.LogicalGenerationIdentity,
                                        StateEnvelope = claim.EnvelopeSha256 };
                                }
                                else if (header.ObjectClass == StateObjectClass.PublicationIntent)
                                {
                                    if (PublicationIntentV1Codec.TryDecode(payload, out var intent) && intent is not null)
                                        record = record with { Kind = "publication_intent", Head = intent.Publication.ReviewedHeadSha,
                                            Body = intent.Publication.BodySha256, PublicationScope = intent.Publication.ScopeSha256, Intent = intent.RecordIdentity };
                                    else if (StickyReadbackRecordV1Codec.TryDecode(payload, out var readback) && readback is not null)
                                        record = record with { Kind = "sticky_readback", Head = readback.Publication.ReviewedHeadSha,
                                            Body = readback.Publication.BodySha256, PublicationScope = readback.Publication.ScopeSha256, Intent = readback.AttemptIntentRecordIdentity };
                                    else record = record with { Kind = "publication_recovery" };
                                }
                                records.Add(record);
                            }
                            finally { CryptographicOperations.ZeroMemory(payload); }
                        }
                        Console.WriteLine(JsonSerializer.Serialize(new { names, scopeDigest, policy, records,
                            profile = SolusRestrictedStateFormat.Profile, package = SolusRestrictedStateFormat.PackageVersion,
                            contextFormat = SolusRestrictedStateFormat.ContextFormat, contextCompatibility = SolusRestrictedStateFormat.ContextCompatibility }, Json));
                    }
                }
                finally { if (sentinel is not null) CryptographicOperations.ZeroMemory(sentinel.Root); }
            }
            finally { foreach (var carrier in carriers.Values) CryptographicOperations.ZeroMemory(carrier.Bytes); }
        }
        return 0;
    }

    private static void Require(bool value)
    { if (!value) throw new InvalidOperationException("transaction_inventory_refused"); }
}
