using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTwoAcceptedSourcesAndFailedConsumptionResetWithoutDeletingFixtureClaims(bool advanceClock)
    {
        var (world, source) = await CurrentClaimedResetWorldAsync(advanceClock);
        var original = world.Store.Objects.ToArray();
        var sourceBytes = original.ToDictionary(item => item.Reference.ObjectId.Value,
            item => world.Store.Bytes(item), StringComparer.Ordinal);
        var clone = world.Store.CloneForProof();
        Assert.Equal(original, clone.Objects.ToArray());
        var resetWorld = world.WithProofStore(clone);
        var reset = await resetWorld.RunAsync(3, reset: true, fresh: true);
        Assert.Equal(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
        Assert.Equal(ActionHostStatus.Reviewed, reset.Completion.Status);
        Assert.NotEqual(source.Provider.Request!.SessionId, reset.Provider.Request!.SessionId);
        Assert.Null(reset.Provider.Request.Continuation);
        Assert.Null(reset.Provider.Request.StablePlan.PriorSessionSha256);
        // The untouched source world proves this is not fixture deletion used
        // to manufacture a fresh admission. Actual reset owns clone cleanup.
        Assert.Equal(original, world.Store.Objects.ToArray());
        Assert.All(original, item => Assert.Equal(sourceBytes[item.Reference.ObjectId.Value], world.Store.Bytes(item)));
        var retry = await resetWorld.RunAsync(3, reset: true, fresh: true);
        Assert.Equal(ActionHostStateDisposition.Accepted, retry.Completion.Summary.StateDisposition);
        Assert.Equal(0, retry.Provider.Creates);
        Assert.True(retry.Completion.Accounting.IsCompleteZero);
    }

    [Theory]
    [InlineData("base", (int)ActionHostStatus.OutcomeAmbiguous)]
    [InlineData("epoch", (int)ActionHostStatus.StateConflict)]
    [InlineData("session", (int)ActionHostStatus.StateConflict)]
    [InlineData("head", (int)ActionHostStatus.StateConflict)]
    [InlineData("acceptance", (int)ActionHostStatus.StateConflict)]
    [InlineData("generation", (int)ActionHostStatus.StateConflict)]
    [InlineData("envelope", (int)ActionHostStatus.StateConflict)]
    [InlineData("record", (int)ActionHostStatus.StateConflict)]
    [InlineData("checkpoint", (int)ActionHostStatus.StateConflict)]
    [InlineData("source-work", (int)ActionHostStatus.StateConflict)]
    [InlineData("source-round", (int)ActionHostStatus.StateConflict)]
    [InlineData("source-expiry", (int)ActionHostStatus.StateConflict)]
    [InlineData("expired", (int)ActionHostStatus.StateConflict)]
    [InlineData("future", (int)ActionHostStatus.StateConflict)]
    [InlineData("producer", (int)ActionHostStatus.StateConflict)]
    [InlineData("attempt", (int)ActionHostStatus.StateConflict)]
    [InlineData("successful-consumer", (int)ActionHostStatus.StateConflict)]
    [InlineData("grant", (int)ActionHostStatus.OutcomeAmbiguous)]
    [InlineData("duplicate", (int)ActionHostStatus.StateConflict)]
    [InlineData("under-retained", (int)ActionHostStatus.StateConflict)]
    [InlineData("unknown", (int)ActionHostStatus.OutcomeAmbiguous)]
    public async Task CurrentClaimedResetRejectsControlledAuthenticatedFaultWithoutEffects(
        string mutation, int expected)
    {
        var (world, source) = await CurrentClaimedResetWorldAsync();
        var original = world.Store.Objects.ToArray();
        var claims = new List<(StateControlHeaderV1 Header, SolusContextClaimRecord Claim, OpaqueStoreObjectMetadata Metadata)>();
        var heads = new List<StateControlHeaderV1>();
        ReadAcceptedStateRecords(world.Store, source.Launch, world.Time, (header, bytes, metadata) =>
        {
            if (header.ObjectClass == StateObjectClass.LineageHead) heads.Add(header);
            if (header.ObjectClass != StateObjectClass.ContextClaim) return;
            Assert.True(SolusContextClaimRecordCodec.TryDecode(bytes, out var claim));
            claims.Add((header, claim!, metadata));
        });
        Assert.Equal(2, claims.Count);
        // Actual same-epoch logical heads are preserved across these accepts;
        // a physical retention copy may differ while the logical ID does not.
        Assert.All(claims, item => Assert.Contains(heads, head => head.ObjectIdentity == item.Claim.SelectedHeadIdentity &&
            head.BaseScopeDigest == item.Claim.BaseScopeDigest && head.Epoch == item.Claim.Epoch && head.SessionId == item.Claim.SessionId));
        var failedRun = source.Launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = mutation == "successful-consumer"
            ? Assert.Single(claims, item => item.Header.ProducingRunIdentity != failedRun)
            : Assert.Single(claims, item => item.Header.ProducingRunIdentity == failedRun);
        var claim = target.Claim;
        var header = target.Header;
        var now = world.Time.GetUtcNow().ToUnixTimeSeconds();
        switch (mutation)
        {
            case "base": claim = claim with { BaseScopeDigest = new('b', 64) }; break;
            case "epoch": claim = claim with { Epoch = new('c', 64) }; break;
            case "session": claim = claim with { SessionId = new('d', 64) }; break;
            case "head": claim = claim with { SelectedHeadIdentity = new('e', 64) }; break;
            case "acceptance": claim = claim with { AcceptanceIdentity = new('f', 64) }; break;
            case "generation": claim = claim with { LogicalGenerationIdentity = new('a', 64) }; break;
            case "envelope": claim = claim with { EnvelopeSha256 = new('b', 64) }; break;
            case "record": claim = claim with { RecordSha256 = new('c', 64) }; break;
            case "checkpoint": claim = claim with { Source = claim.Source with { CheckpointId = Guid.NewGuid() } }; break;
            case "source-work": claim = claim with { Source = claim.Source with { LogicalWorkId = Guid.NewGuid() } }; break;
            case "source-round": claim = claim with { Source = claim.Source with { RoundId = Guid.NewGuid() } }; break;
            case "source-expiry": claim = claim with { SourceExpiresAtUnixSeconds = claim.SourceExpiresAtUnixSeconds - 1 }; break;
            case "expired": claim = claim with { SourceExpiresAtUnixSeconds = now - 1 }; break;
            case "successful-consumer": claim = claim with { ExecutionId = Guid.NewGuid(), RoundId = Guid.NewGuid() }; break;
        }
        // Keep the mutant's grant derivation valid so owner/checkpoint/hash
        // negatives cannot pass merely because the codec rejected stale data.
        claim = claim with { GrantId = SolusContextClaimRecordCodec.DeriveGrantId(claim.BaseScopeDigest,
            claim.Epoch, claim.SessionId, claim.LogicalGenerationIdentity, claim.Source.LogicalWorkId,
            claim.Source.RoundId, claim.ExecutionId, claim.RoundId) };
        Assert.True(SolusContextClaimRecordCodec.TryEncode(claim, out var payload));
        byte[] encrypted = [];
        try
        {
            if (mutation == "grant") payload[^56] ^= 1;
            ReadAcceptedStateRecords(world.Store, source.Launch, world.Time, inspectLocator: (locator, access) =>
            {
                var draft = new StateControlHeaderDraft(claim.BaseScopeDigest, claim.Epoch, claim.SessionId,
                    StateObjectClass.ContextClaim, claim.AcceptanceIdentity, claim.LogicalGenerationIdentity,
                    mutation == "producer" ? "777777" : header.ProducingRunIdentity,
                    mutation == "attempt" ? header.ProducingRunAttempt + 1 : header.ProducingRunAttempt,
                    mutation == "expired" ? now - 2 : mutation == "future" ? now + 1 : header.CreatedAtUnixSeconds,
                    header.LogicalExpiresAtUnixSeconds, header.RequiredPlatformExpiresAtUnixSeconds);
                Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(locator, access, target.Metadata.Reference.Name,
                    draft, payload, out encrypted, out _, out var code), code);
            });
            var store = mutation is "duplicate" or "unknown" ? world.Store.CloneForProof()
                : mutation == "under-retained" ? world.Store.CloneForProof(target.Metadata, changedExpiry: header.RequiredPlatformExpiresAtUnixSeconds - 1)
                : world.Store.CloneForProof(target.Metadata, encrypted);
            if (mutation == "duplicate") store.CopyPhysicalObject(target.Metadata);
            if (mutation == "unknown") store.Add([1, 2, 3], target.Metadata.ExpiresAtUnixSeconds, name: target.Metadata.Reference.Name);
            var negative = world.WithProofStore(store);
            var before = store.Objects.ToArray();
            var bytesBefore = before.ToDictionary(item => item.Reference.ObjectId.Value,
                item => store.Bytes(item), StringComparer.Ordinal);
            var writes = negative.Publications;
            var rejected = await negative.RunAsync(3, reset: true, fresh: true);
            Assert.Equal((ActionHostStatus)expected, rejected.Completion.Status);
            Assert.Null(rejected.Provider.Request);
            Assert.Equal(0, rejected.Provider.Creates);
            Assert.True(rejected.Completion.Accounting.IsCompleteZero);
            Assert.Equal(writes, negative.Publications);
            Assert.Equal(0, store.UploadCalls);
            Assert.Equal(0, store.DeleteCalls);
            Assert.Equal(before, store.Objects.ToArray());
            Assert.All(before, item => Assert.Equal(bytesBefore[item.Reference.ObjectId.Value], store.Bytes(item)));
            Assert.Equal(original, world.Store.Objects.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    [Fact]
    public async Task CurrentClaimBudgetAndEvictedResetSourcesRemainVisibleWithoutFreshFallback()
    {
        Assert.Equal(8, LineageFormat.MaximumPhysicalPerClass);
        var world = new CapacityResetWorld();
        CapacityResetInvocation? last = null;
        for (var phase = 0; phase <= LineageFormat.MaximumPhysicalPerClass; phase++)
        {
            last = await world.RunAsync(phase);
            Assert.Equal(ActionHostStateDisposition.Accepted, last.Completion.Summary.StateDisposition);
        }
        var claims = new List<SolusContextClaimRecord>();
        var claimIdentities = new List<string>();
        var selected = ReadAcceptedStateRecords(world.Store, last!.Launch, world.Time, (header, bytes, _) =>
        {
            if (header.ObjectClass != StateObjectClass.ContextClaim) return;
            Assert.True(SolusContextClaimRecordCodec.TryDecode(bytes, out var claim));
            claims.Add(claim!);
            claimIdentities.Add(header.ObjectIdentity);
        });
        Assert.Equal(8, claims.Count);
        var closedSources = selected.Generations.Select(value => value.Generation.SessionSha256).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, closedSources.Count);
        Assert.Equal(7, claims.Count(claim => !closedSources.Contains(claim.RecordSha256)));
        var writes = world.Publications;
        var uploads = world.Store.UploadCalls;
        var bookkeeping = new List<(OpaqueStoreName Name, byte[] Bytes)>();
        world.Store.AfterUpload = (request, _) => bookkeeping.Add((request.Name, request.EncryptedBytes.ToArray()));
        var denied = await world.RunAsync(LineageFormat.MaximumPhysicalPerClass + 1);
        world.Store.AfterUpload = null;
        Assert.Equal(ActionHostStatus.StateConflict, denied.Completion.Status);
        Assert.Null(denied.Provider.Request);
        Assert.Equal(0, denied.Provider.Creates);
        Assert.True(denied.Completion.Accounting.IsCompleteZero);
        Assert.Equal(writes, world.Publications);
        Assert.Equal(4, world.Store.UploadCalls - uploads);
        var afterDenial = ReadAcceptedStateRecords(world.Store, denied.Launch, world.Time,
            inspectLocator: (locator, access) =>
            {
                foreach (var write in bookkeeping)
                {
                    Assert.True(StateControlEnvelopeV1Codec.TryDecrypt(locator, access, write.Name, write.Bytes,
                        out var header, out var payload, out var code), code);
                    try
                    {
                        Assert.Equal(StateObjectClass.Cleanup, header!.ObjectClass);
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(write.Bytes); }
                }
            });
        Assert.Equal(selected.Acceptances.Select(value => value.Header.ObjectIdentity).Order(),
            afterDenial.Acceptances.Select(value => value.Header.ObjectIdentity).Order());
        var afterClaims = new List<string>();
        ReadAcceptedStateRecords(world.Store, denied.Launch, world.Time, (header, _, _) =>
        {
            if (header.ObjectClass == StateObjectClass.ContextClaim) afterClaims.Add(header.ObjectIdentity);
        });
        Assert.Equal(claimIdentities.Order(), afterClaims.Order());
        var beforeReset = world.Store.Objects.ToArray();
        var ciphertext = beforeReset.ToDictionary(item => item.Reference.ObjectId.Value,
            item => world.Store.Bytes(item), StringComparer.Ordinal);
        // Actual long-session limitation: seven original source records were
        // already evicted. Authentication of their claims alone is no source
        // authority, so this bounded repair must refuse instead of guessing.
        var reset = await world.RunAsync(LineageFormat.MaximumPhysicalPerClass + 2, reset: true, fresh: true);
        Assert.Equal(ActionHostStatus.StateConflict, reset.Completion.Status);
        Assert.Null(reset.Provider.Request);
        Assert.Equal(0, reset.Provider.Creates);
        Assert.Equal(writes, world.Publications);
        Assert.Equal(beforeReset, world.Store.Objects.ToArray());
        Assert.All(beforeReset, item => Assert.Equal(ciphertext[item.Reference.ObjectId.Value], world.Store.Bytes(item)));
    }

    [Theory]
    [InlineData("delete-before", (int)ActionHostStatus.OutcomeAmbiguous)]
    [InlineData("delete-after", (int)ActionHostStatus.Reviewed)]
    [InlineData("cancel-after-delete", (int)ActionHostStatus.OutcomeAmbiguous)]
    public async Task ActualClaimedResetReconcilesPartialCleanupAndRestart(string fault, int expected)
    {
        var (world, source) = await CurrentClaimedResetWorldAsync();
        var writes = world.Publications;
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancel-after-delete") world.Store.AfterDelete = cancellation.Cancel;
        else
        {
            world.Store.NextDeleteFailure = OpaqueStoreFailure.Io;
            world.Store.NextDeleteMutationState = fault == "delete-after"
                ? OpaqueStoreMutationState.OutcomeUnknown : OpaqueStoreMutationState.NotCommitted;
            world.Store.RemoveOnDeleteFailure = fault == "delete-after";
        }
        var interrupted = await world.RunAsync(3, reset: true, fresh: true, token: cancellation.Token);
        Assert.Equal((ActionHostStatus)expected, interrupted.Completion.Status);
        var reconciled = fault == "delete-after";
        Assert.Equal(reconciled ? ActionHostStateDisposition.Accepted : ActionHostStateDisposition.NotCommitted,
            interrupted.Completion.Summary.StateDisposition);
        Assert.Equal(reconciled ? 1 : 0, interrupted.Provider.Creates);
        Assert.Equal(writes + (reconciled ? 1 : 0), world.Publications);
        world.Store.AfterDelete = null;
        world.Store.RemoveOnDeleteFailure = false;
        var retry = await world.RunAsync(3, reset: true, fresh: true);
        Assert.Equal(ActionHostStateDisposition.Accepted, retry.Completion.Summary.StateDisposition);
        Assert.Equal(reconciled ? 0 : 1, retry.Provider.Creates);
        if (!reconciled)
        {
            Assert.NotEqual(source.Provider.Request!.SessionId, retry.Provider.Request!.SessionId);
            Assert.Null(retry.Provider.Request.Continuation);
        }
        else Assert.True(retry.Completion.Accounting.IsCompleteZero);
        Assert.Equal(writes + 1, world.Publications);
        var again = await world.RunAsync(3, reset: true, fresh: true);
        Assert.Equal(ActionHostStateDisposition.Accepted, again.Completion.Summary.StateDisposition);
        Assert.Equal(0, again.Provider.Creates);
        Assert.True(again.Completion.Accounting.IsCompleteZero);
        Assert.Equal(writes + 1, world.Publications);
    }

    [Fact]
    public async Task ActualUncertainContextClaimAnchorCannotAuthorizeExplicitReset()
    {
        var world = new CapacityResetWorld();
        Assert.Equal(ActionHostStateDisposition.Accepted, (await world.RunAsync(0)).Completion.Summary.StateDisposition);
        var accepted = await world.RunAsync(1);
        Assert.Equal(ActionHostStateDisposition.Accepted, accepted.Completion.Summary.StateDisposition);
        OpaqueStoreName? claimName = null;
        ReadAcceptedStateRecords(world.Store, accepted.Launch, world.Time, (header, _, metadata) =>
        {
            if (header.ObjectClass == StateObjectClass.ContextClaim) claimName = metadata.Reference.Name;
        });
        Assert.NotNull(claimName);
        var faultTriggered = false;
        world.Store.AfterUpload = (request, _) =>
        {
            if (request.Name != claimName) return;
            faultTriggered = true;
            world.Store.ReadBackFailure = OpaqueStoreFailure.Io;
            world.Store.MetadataFailure = OpaqueStoreFailure.Io;
            world.Store.DownloadFailure = OpaqueStoreFailure.Io;
        };
        var uncertain = await world.RunAsync(2);
        Assert.True(faultTriggered);
        Assert.Null(uncertain.Provider.Request);
        world.Store.AfterUpload = null;
        world.Store.ReadBackFailure = OpaqueStoreFailure.None;
        world.Store.MetadataFailure = OpaqueStoreFailure.None;
        world.Store.DownloadFailure = OpaqueStoreFailure.None;
        var anchors = new List<RetainedStateOpaqueWriteAnchor>();
        ReadAcceptedStateRecords(world.Store, uncertain.Launch, world.Time, (header, payload, _) =>
        {
            if (header.ObjectClass == StateObjectClass.Cleanup &&
                RetainedStateOpaqueWriteAnchorCodec.TryDecode(payload, out var anchor) && anchor is not null)
                anchors.Add(anchor);
        });
        Assert.Contains(anchors, anchor => anchor.ObjectClass == StateObjectClass.ContextClaim);
        var before = world.Store.Objects.ToArray();
        var encrypted = before.ToDictionary(item => item.Reference.ObjectId.Value,
            item => world.Store.Bytes(item), StringComparer.Ordinal);
        var uploads = world.Store.UploadCalls;
        var deletes = world.Store.DeleteCalls;
        var writes = world.Publications;
        var reset = await world.RunAsync(3, reset: true, fresh: true);
        Assert.Equal(ActionHostStatus.StateConflict, reset.Completion.Status);
        Assert.Null(reset.Provider.Request);
        Assert.True(reset.Completion.Accounting.IsCompleteZero);
        Assert.Equal(uploads, world.Store.UploadCalls);
        Assert.Equal(deletes, world.Store.DeleteCalls);
        Assert.Equal(writes, world.Publications);
        Assert.Equal(before, world.Store.Objects.ToArray());
        Assert.All(before, item => Assert.Equal(encrypted[item.Reference.ObjectId.Value], world.Store.Bytes(item)));
    }

    private static async Task<(CapacityResetWorld World, CapacityResetInvocation Source)> CurrentClaimedResetWorldAsync(bool advanceClock = false)
    {
        var world = new CapacityResetWorld();
        for (var phase = 0; phase < 2; phase++)
        {
            Assert.Equal(ActionHostStateDisposition.Accepted, (await world.RunAsync(phase)).Completion.Summary.StateDisposition);
            if (advanceClock) world.Time.UnixSeconds += 60;
        }
        var failed = await world.RunAsync(2, failProvider: true);
        Assert.Equal(ActionHostStateDisposition.NotCommitted, failed.Completion.Summary.StateDisposition);
        Assert.NotNull(failed.Provider.Request);
        return (world, failed);
    }
}
