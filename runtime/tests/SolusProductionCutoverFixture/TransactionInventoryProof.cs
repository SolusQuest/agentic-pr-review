using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
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
        var stage = InventoryStage.InputRead;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            Input input;
            try
            {
                stage = InventoryStage.InputShape;
                Require(bytes.Length is > 0 and <= 96 * 1024 * 1024);
                stage = InventoryStage.InputJson;
                input = JsonSerializer.Deserialize<Input>(bytes, Json)!;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            stage = InventoryStage.InputShape;
            Require(input.Mode is "derive" or "decode" && input.Artifacts.Length is > 0 and <= 256 &&
                input.Artifacts.Select(item => item.Id).Distinct().Count() == input.Artifacts.Length);
            stage = InventoryStage.LocatorRoot;
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
                        stage = InventoryStage.Transport;
                        var zip = Convert.FromBase64String(entry.Archive);
                        try
                        {
                            var carrier = await ArtifactTransportFormat.DecodeAsync(zip,
                                Convert.ToHexStringLower(SHA256.HashData(zip)), OpaqueStoreLimits.MaximumObjectBytes, default);
                            try
                            {
                                stage = InventoryStage.TransportIdentity;
                                Require(carrier.RunId == entry.Run && carrier.Attempt == entry.Attempt);
                                carriers.Add(entry.Id, carrier);
                            }
                            catch
                            {
                                CryptographicOperations.ZeroMemory(carrier.Bytes);
                                throw;
                            }
                        }
                        finally { CryptographicOperations.ZeroMemory(zip); }
                    }
                    stage = InventoryStage.LocatorRoot;
                    var root = input.Artifacts.First(item => ArtifactTransportFormat.Member(LocatorRootFormat.StoreName, item.Name));
                    LocatorRootSentinel? sentinel = null;
                    try
                    {
                        Require(LocatorRootSentinelCodec.TryDecrypt(access, keys!, carriers[root.Id].Bytes, out sentinel, out _) && sentinel is not null);
                        stage = InventoryStage.LocatorContext;
                        Require(LocatorContext.TryCreate(access, keys!, sentinel!.Root, true,
                            sentinel.RequiredExpiresAtUnixSeconds, TimeProvider.System, out var locator));
                        using (locator)
                        {
                            var names = input.Names;
                            var scopeDigest = input.ScopeDigest;
                            string? policy = null;
                            if (input.Mode == "derive")
                            {
                                stage = InventoryStage.ScopeDerive;
                                var launchBytes = JsonSerializer.SerializeToUtf8Bytes(input.Launch);
                                ActionHostLaunchContract? launch;
                                try
                                {
                                    Require(ActionHostJsonCodec.TryReadLaunch(launchBytes, out launch, out _) && launch is not null);
                                }
                                finally { CryptographicOperations.ZeroMemory(launchBytes); }
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
                            stage = InventoryStage.ScopeIdentity;
                            Require(names?.Count == StateObjectClasses.All.Length && LineageValidation.IsSha256(scopeDigest));
                            var records = new List<Decoded>();
                            foreach (var entry in input.Artifacts)
                            {
                                stage = InventoryStage.ObjectName;
                                var matching = names!.Where(pair => ArtifactTransportFormat.Member(pair.Value, entry.Name)).ToArray();
                                Require(matching.Length == 1);
                                var kind = matching[0].Key;
                                if (kind == "locator_root") continue;
                                byte[] payload = [];
                                try
                                {
                                    stage = InventoryStage.ControlEnvelope;
                                    Require(StateControlEnvelopeV1Codec.TryDecrypt(locator!, access, new(matching[0].Value),
                                        carriers[entry.Id].Bytes, out var header, out payload, out _) && header is not null);
                                    stage = InventoryStage.ControlIdentity;
                                    Require(StateObjectClasses.ToWireName(header!.ObjectClass) == kind && header.BaseScopeDigest == scopeDigest &&
                                        header.ProducingRunIdentity == entry.Run && header.ProducingRunAttempt == entry.Attempt);
                                    var record = new Decoded(entry.Id, kind, header.ObjectIdentity, entry.Run);
                                    if (header.ObjectClass == StateObjectClass.Candidate)
                                    {
                                        stage = InventoryStage.CandidateRecord;
                                        var direct = AcceptedStateGenerationRecordCodec.TryDecode(payload, out var generation);
                                        string? original = null;
                                        if (!direct)
                                        {
                                            stage = InventoryStage.CandidateCopy;
                                            Require(AcceptedStatePhysicalCopyCodec.TryDecode(payload, out var copy) && copy is not null);
                                            stage = InventoryStage.CandidateGeneration;
                                            Require(AcceptedStateGenerationRecordCodec.TryDecode(copy!.CanonicalGenerationBytes.AsSpan(), out generation));
                                            original = copy.OriginalCandidateObjectIdentity;
                                        }
                                        stage = InventoryStage.CandidateGeneration;
                                        Require(generation is not null);
                                        stage = InventoryStage.PublicationPayload;
                                        Require(AcceptedStatePublicationPayloadCodec.TryDecode(generation!.PublicationPayloadBytes.AsSpan(), out var publication) && publication is not null);
                                        record = record with { Kind = direct ? "candidate" : "candidate_copy", Head = generation.ProducerHeadSha,
                                            Candidate = original ?? header.ObjectIdentity, StateEnvelope = generation.StateEnvelopeSha256,
                                            Previous = generation.PredecessorEnvelopeSha256, Body = publication!.BodySha256, PublicationScope = publication.ScopeSha256 };
                                    }
                                    else if (header.ObjectClass == StateObjectClass.Acceptance)
                                    {
                                        stage = InventoryStage.AcceptanceRecord;
                                        Require(AcceptedStateAcceptanceReceiptCodec.TryDecode(payload, out var acceptance) && acceptance is not null);
                                        record = record with { Head = acceptance!.ReviewedHeadSha, Candidate = acceptance.OriginalCandidateObjectIdentity,
                                            Logical = acceptance.LogicalGenerationIdentity, Previous = acceptance.PreviousLogicalGenerationIdentity,
                                            Body = acceptance.BodySha256, PublicationScope = acceptance.ScopeSha256 };
                                    }
                                    else if (header.ObjectClass == StateObjectClass.ContextClaim)
                                    {
                                        stage = InventoryStage.ContextClaim;
                                        Require(SolusContextClaimRecordCodec.TryDecode(payload, out var claim) && claim is not null);
                                        record = record with { Acceptance = claim!.AcceptanceIdentity, Logical = claim.LogicalGenerationIdentity,
                                            StateEnvelope = claim.EnvelopeSha256 };
                                    }
                                    else if (header.ObjectClass == StateObjectClass.PublicationIntent)
                                    {
                                        stage = InventoryStage.PublicationRecord;
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
                            stage = InventoryStage.ResultWrite;
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
        catch (Exception exception)
        {
            // Only authored stage and exception TYPE leave this supporting reader.
            Console.Error.WriteLine(DescribeFailure(stage, exception));
            return 1;
        }
    }

    private enum InventoryStage
    {
        InputRead,
        InputJson,
        InputShape,
        Transport,
        TransportIdentity,
        LocatorRoot,
        LocatorContext,
        ScopeDerive,
        ScopeIdentity,
        ObjectName,
        ControlEnvelope,
        ControlIdentity,
        CandidateRecord,
        CandidateCopy,
        CandidateGeneration,
        PublicationPayload,
        AcceptanceRecord,
        ContextClaim,
        PublicationRecord,
        ResultWrite,
    }

    private static string DescribeFailure(InventoryStage stage, Exception exception)
    {
        var where = stage switch
        {
            InventoryStage.InputRead => "input_read",
            InventoryStage.InputJson => "input_json",
            InventoryStage.InputShape => "input_shape",
            InventoryStage.Transport => "transport",
            InventoryStage.TransportIdentity => "transport_identity",
            InventoryStage.LocatorRoot => "locator_root",
            InventoryStage.LocatorContext => "locator_context",
            InventoryStage.ScopeDerive => "scope_derive",
            InventoryStage.ScopeIdentity => "scope_identity",
            InventoryStage.ObjectName => "object_name",
            InventoryStage.ControlEnvelope => "control_envelope",
            InventoryStage.ControlIdentity => "control_identity",
            InventoryStage.CandidateRecord => "candidate_record",
            InventoryStage.CandidateCopy => "candidate_copy",
            InventoryStage.CandidateGeneration => "candidate_generation",
            InventoryStage.PublicationPayload => "publication_payload",
            InventoryStage.AcceptanceRecord => "acceptance_record",
            InventoryStage.ContextClaim => "context_claim",
            InventoryStage.PublicationRecord => "publication_record",
            InventoryStage.ResultWrite => "result_write",
            _ => "unknown",
        };
        // Never inspect Message, StackTrace, InnerException or data-bearing codes.
        var reason = exception switch
        {
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "access_denied",
            JsonException => "json",
            CryptographicException => "crypto",
            FormatException => "encoding",
            IOException => "io",
            ArgumentException => "argument",
            InvalidOperationException => "invalid_operation",
            _ => "unexpected",
        };
        return "APR_TRANSACTION_INVENTORY_FAILURE stage=" + where + " error=" + reason;
    }

    private static void Require(bool value)
    { if (!value) throw new InvalidOperationException("transaction_inventory_refused"); }
}
