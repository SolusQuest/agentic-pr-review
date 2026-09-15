using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;

namespace AgenticPrReview.Runtime.Tests.Host.State
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class R5ResetHandoffTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public Task OrdinaryAbsenceWithAcceptedPredecessor(bool appeared, bool retry) =>
            Action.ActionHostCompositionTests.VerifyOrdinaryAbsenceWithAcceptedPredecessorAsync(appeared, retry);

        [Theory]
        [InlineData("intent", false)]
        [InlineData("intent", true)]
        [InlineData("successor", true)]
        public Task ResetUploadCutsPreserveDurableTargetAndExactRetry(string boundary, bool committed) =>
            Action.ActionHostCompositionTests.VerifyResetUploadCutAsync(boundary, committed);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task UnknownDeletionIsReconciledWithoutRemintingReset(bool removed) =>
            Action.ActionHostCompositionTests.VerifyResetDeleteCutAsync(removed);

        [Fact]
        public Task ChangedSourceInventoryCannotUseAnEarlierCapturedTarget() =>
            Action.ActionHostCompositionTests.VerifyResetSourceRaceAsync();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ReplayedOldEpochOrTamperedHeadCannotRestoreOldAuthority(bool tamperedHead) =>
            Action.ActionHostCompositionTests.VerifyResetReplayAsync(tamperedHead);

        [Fact]
        public Task AbsenceGrantCannotAdoptANewlyAppearedComment() =>
            Action.ActionHostCompositionTests.VerifyAppearedResetTargetAsync();

        [Fact]
        public Task TargetExpiryIsCheckedAtTheFinalWriteBoundary() =>
            Action.ActionHostCompositionTests.VerifyResetTargetExpiresBeforeWriteAsync();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task NewResetDistinguishesCandidateOnlyFromUnresolvedPublication(bool uncertainWrite) =>
            Action.ActionHostCompositionTests.VerifyResetPendingPublicationAsync(uncertainWrite);

        [Fact]
        public Task SuccessiveExplicitResetsPreserveOneOriginalTargetAndExpiry() =>
            Action.ActionHostCompositionTests.VerifySuccessiveResetTargetAsync();

        [Theory]
        [InlineData("copy", false)]
        [InlineData("missing", false)]
        [InlineData("edited", false)]
        [InlineData("duplicate", false)]
        [InlineData("incomplete", false)]
        [InlineData("copy", true)]
        [InlineData("missing", true)]
        [InlineData("edited", true)]
        [InlineData("duplicate", true)]
        [InlineData("incomplete", true)]
        public Task OnlyTheExactTargetSurvivesBothPublicationChecks(string fault, bool finalCheck) =>
            Action.ActionHostCompositionTests.VerifyResetTargetConflictAsync(fault, finalCheck);

        [Fact]
        public Task ExpiredHandoffDoesNotTurnAnUnknownWriteIntoRetryPermission() =>
            Action.ActionHostCompositionTests.VerifyUnknownResetWriteAfterExpiryAsync();

        [Fact]
        public Task ExpiredOldTargetCannotStartAnotherProviderOrWrite() =>
            Action.ActionHostCompositionTests.VerifyExpiredResetTargetAsync();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task WrittenNewPublicationRecoversAfterOldTargetExpiry(bool acceptanceCommitted) =>
            Action.ActionHostCompositionTests.VerifyResetAcceptanceAfterTargetExpiryAsync(acceptanceCommitted);
    }
}

namespace AgenticPrReview.Runtime.Tests.Host.Action
{
    public sealed partial class ActionHostCompositionTests
    {
        internal static async Task VerifyOrdinaryAbsenceWithAcceptedPredecessorAsync(bool appeared, bool retry)
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(1);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            var before = ResetHead(world, initial.Launch);
            var oldReceipt = Assert.Single(ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time).Acceptances);
            world.Remote.RemoveTarget();
            world.Remote.AppearBeforeCreate = appeared && !retry;
            world.Remote.KnownNotSentOnce = retry;
            world.Remote.AppearOnRetry = appeared && retry;
            var next = await world.RunAsync(2);
            Assert.Equal(1, next.Provider.Creates);
            var after = ResetHead(world, next.Launch);
            Assert.Equal(before.Header.Epoch, after.Header.Epoch);
            Assert.Equal(before.Header.SessionId, after.Header.SessionId);
            Assert.Null(after.Head.ResetPublicationTarget);
            var records = ReadAcceptedStateRecords(world.Store, next.Launch, world.Time);
            if (appeared)
            {
                Assert.NotEqual(ActionHostStateDisposition.Accepted, next.Completion.Summary.StateDisposition);
                Assert.Equal(retry ? 2 : 1, world.Remote.MutationAttempts);
                Assert.Equal(oldReceipt.Header.ObjectIdentity, Assert.Single(records.Acceptances).Header.ObjectIdentity);
            }
            else
            {
                Assert.Equal(ActionHostStateDisposition.Accepted, next.Completion.Summary.StateDisposition);
                Assert.Equal(retry ? 3 : 2, world.Remote.MutationAttempts);
                var accepted = records.Acceptances.Single(value => value.Receipt.PreviousAcceptanceReceiptIdentity == oldReceipt.Header.ObjectIdentity);
                Assert.Equal(AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky.StickyPublicationOperation.Create, accepted.Receipt.PublicationOperation);
                Assert.NotEqual(oldReceipt.Receipt.CommentId, accepted.Receipt.CommentId);
                Assert.Equal(1, records.Generations.First(value => value.Header.ObjectIdentity == accepted.Receipt.OriginalCandidateObjectIdentity).Generation.Generation);
            }
        }

        internal static async Task VerifyResetUploadCutAsync(string boundary, bool committed)
        {
            var calibration = new ResetProbeWorld();
            var initialCalibration = await calibration.RunAsync(0);
            var beforeCalibration = calibration.Store.UploadCalls;
            var call = 0;
            calibration.Store.AfterUpload = (request, currentCall) =>
                ReadAcceptedStateRecords(calibration.Store, initialCalibration.Launch, calibration.Time, (header, payload, metadata) =>
                {
                    if (metadata.Reference.Name != request.Name) return;
                    if (boundary == "intent" && header.ObjectClass == StateObjectClass.Reset ||
                        boundary == "successor" && header.ObjectClass == StateObjectClass.LineageHead &&
                        LineageHeadCodec.TryDecode(payload, out var head) && head!.Transition == LineageTransitionKind.Reset)
                        call = currentCall;
                });
            await calibration.RunAsync(1, reset: true, failProvider: true);
            Assert.True(call > beforeCalibration);

            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            var prior = ResetHead(world, initial.Launch);
            var oldObjects = world.Store.Objects;
            var deletes = world.Store.DeleteCalls;
            world.Store.FailUploadOnUploadCall = world.Store.UploadCalls + call - beforeCalibration;
            world.Store.ScheduledUploadFailure = committed ? OpaqueStoreFailure.OutcomeUnknown : OpaqueStoreFailure.Io;
            world.Store.ScheduledUploadMutationState = committed ? OpaqueStoreMutationState.Committed : OpaqueStoreMutationState.NotCommitted;
            world.Store.PersistFailedUpload = committed;
            world.Store.HideFailedUploadForNextLists = committed ? 30 : 0;
            var cut = await world.RunAsync(1, reset: true);
            Assert.Equal(0, cut.Provider.Creates);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, cut.Completion.Summary.StateDisposition);
            if (boundary == "intent")
            {
                Assert.Equal(deletes, world.Store.DeleteCalls);
                Assert.All(oldObjects, item => Assert.Contains(item, world.Store.Objects));
                if (!committed) Assert.True(oldObjects.SequenceEqual(world.Store.Objects));
            }
            else
            {
                Assert.True(world.Store.DeleteCalls > deletes);
                Assert.NotEqual(prior.Header.Epoch, ResetHead(world, cut.Launch).Header.Epoch);
            }
            world.Store.HideNextUploadedObjectForNextLists = 0;
            if (boundary == "intent" && committed)
            {
                var beforeWrong = world.Store.Objects;
                var wrong = await world.RunAsync(2, reset: true);
                Assert.Equal(0, wrong.Provider.Creates);
                Assert.True(beforeWrong.SequenceEqual(world.Store.Objects));
            }
            var recovered = await world.RunAsync(1, reset: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, recovered.Completion.Summary.StateDisposition);
            Assert.Equal(prior.Head.Ordinal + 1, ResetHead(world, recovered.Launch).Head.Ordinal);
            Assert.Equal(2, world.Remote.Writes);
            var retry = await world.RunAsync(1, reset: true);
            Assert.Equal(0, retry.Provider.Creates);
            Assert.Equal(2, world.Remote.Writes);
        }

        internal static async Task VerifyResetDeleteCutAsync(bool removed)
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            var before = ResetHead(world, initial.Launch);
            world.Store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
            world.Store.NextDeleteMutationState = OpaqueStoreMutationState.OutcomeUnknown;
            world.Store.DeleteFailuresRemaining = 1;
            world.Store.RemoveOnDeleteFailure = removed;
            var cut = await world.RunAsync(1, reset: true, failProvider: true);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, cut.Completion.Summary.StateDisposition);
            Assert.Equal(1, world.Remote.Writes);
            Assert.True(world.Store.DeleteCalls > 0);
            var recovered = await world.RunAsync(1, reset: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, recovered.Completion.Summary.StateDisposition);
            Assert.Equal(before.Head.Ordinal + 1, ResetHead(world, recovered.Launch).Head.Ordinal);
            Assert.Equal(2, world.Remote.Writes);
        }

        internal static async Task VerifyResetSourceRaceAsync()
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            OpaqueStoreObjectMetadata? candidate = null;
            ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time, (header, _, metadata) =>
            {
                if (header.ObjectClass == StateObjectClass.Candidate) candidate = metadata;
            });
            Assert.NotNull(candidate);
            var uploads = world.Store.UploadCalls;
            var deletes = world.Store.DeleteCalls;
            var lists = 0;
            world.Store.BeforeList = (request, _) =>
            {
                if (request.Name == candidate.Reference.Name && ++lists == 2) world.Store.CopyPhysicalObject(candidate);
            };
            var reset = await world.RunAsync(1, reset: true);
            Assert.True(lists >= 2);
            Assert.Equal(0, reset.Provider.Creates);
            Assert.Equal(uploads, world.Store.UploadCalls);
            Assert.Equal(deletes, world.Store.DeleteCalls);
            Assert.Equal(1, world.Remote.Writes);
        }

        internal static async Task VerifyResetReplayAsync(bool tamperedHead)
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            OpaqueStoreObjectMetadata? oldCandidate = null;
            ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time, (header, _, metadata) =>
            {
                if (header.ObjectClass == StateObjectClass.Candidate) oldCandidate = metadata;
            });
            var oldBytes = world.Store.Bytes(oldCandidate!);
            var reset = await world.RunAsync(1, reset: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
            var metadata = oldCandidate!;
            var bytes = oldBytes;
            if (tamperedHead)
            {
                var current = ResetHead(world, reset.Launch);
                ReadAcceptedStateRecords(world.Store, reset.Launch, world.Time, (header, _, item) =>
                {
                    if (header.ObjectIdentity == current.Header.ObjectIdentity) metadata = item;
                });
                bytes = world.Store.Bytes(metadata);
                bytes[^1] ^= 1;
            }
            world.Store.ProducingRunIdentity = metadata.ProducingRun.Identity;
            world.Store.ProducingRunAttempt = metadata.ProducingRun.Attempt;
            world.Store.Add(bytes, metadata.ExpiresAtUnixSeconds, name: metadata.Reference.Name);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(oldBytes);
            var uploads = world.Store.UploadCalls;
            var deletes = world.Store.DeleteCalls;
            var rejected = await world.RunAsync(2);
            Assert.Equal(0, rejected.Provider.Creates);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, rejected.Completion.Summary.StateDisposition);
            Assert.Equal(uploads, world.Store.UploadCalls);
            Assert.Equal(deletes, world.Store.DeleteCalls);
            Assert.Equal(2, world.Remote.Writes);
        }

        internal static async Task VerifyAppearedResetTargetAsync()
        {
            var world = new ResetProbeWorld();
            world.Remote.AppearBeforeCreate = true;
            var result = await world.RunAsync(0);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
            Assert.Equal(0, world.Remote.MutationAttempts);
        }

        internal static async Task VerifyResetTargetExpiresBeforeWriteAsync()
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            var expiry = Assert.Single(ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time).Acceptances)
                .Receipt.LogicalExpiresAtUnixSeconds;
            world.Time.UnixSeconds = expiry - 60;
            world.Remote.BeforeCreate = () => world.Time.UnixSeconds = expiry + 1;
            var result = await world.RunAsync(1, reset: true);
            Assert.Equal(1, result.Provider.Creates);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
            Assert.Equal(1, world.Remote.MutationAttempts);
        }

        internal static async Task VerifyResetPendingPublicationAsync(bool uncertainWrite)
        {
            var world = new ResetProbeWorld();
            Assert.Equal(ActionHostStateDisposition.Accepted, (await world.RunAsync(0)).Completion.Summary.StateDisposition);
            world.Remote.UnknownWithoutWrite = uncertainWrite;
            world.Remote.IncompleteDiscovery = !uncertainWrite;
            var pending = await world.RunAsync(1, reset: true);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, pending.Completion.Summary.StateDisposition);
            Assert.Equal(1, pending.Provider.Creates);
            var before = ResetHead(world, pending.Launch);
            var physical = world.Store.Objects;
            var uploads = world.Store.UploadCalls;
            var deletes = world.Store.DeleteCalls;
            world.Remote.UnknownWithoutWrite = false;
            world.Remote.IncompleteDiscovery = false;
            var next = await world.RunAsync(2, reset: true);
            if (uncertainWrite)
            {
                Assert.Equal(0, next.Provider.Creates);
                Assert.NotEqual(ActionHostStateDisposition.Accepted, next.Completion.Summary.StateDisposition);
                Assert.Equal(uploads, world.Store.UploadCalls);
                Assert.Equal(deletes, world.Store.DeleteCalls);
                Assert.True(physical.SequenceEqual(world.Store.Objects));
                Assert.Equal(before.Header.Epoch, ResetHead(world, next.Launch).Header.Epoch);
            }
            else
            {
                Assert.Equal(ActionHostStateDisposition.Accepted, next.Completion.Summary.StateDisposition);
                Assert.NotEqual(before.Header.Epoch, ResetHead(world, next.Launch).Header.Epoch);
            }
        }

        internal static async Task VerifySuccessiveResetTargetAsync()
        {
            var world = new ResetProbeWorld();
            Assert.Equal(ActionHostStateDisposition.Accepted, (await world.RunAsync(0)).Completion.Summary.StateDisposition);
            var first = await world.RunAsync(1, reset: true, failProvider: true);
            var before = ResetHead(world, first.Launch);
            world.Time.UnixSeconds++;
            var second = await world.RunAsync(2, reset: true, failProvider: true);
            var after = ResetHead(world, second.Launch);
            Assert.NotEqual(before.Header.Epoch, after.Header.Epoch);
            Assert.NotEqual(before.Header.SessionId, after.Header.SessionId);
            Assert.Equal(before.Head.ResetPublicationTarget, after.Head.ResetPublicationTarget);
            var retry = await world.RunAsync(2, reset: true, failProvider: true);
            Assert.Equal(after.Header.Epoch, ResetHead(world, retry.Launch).Header.Epoch);
            Assert.Equal(1, world.Remote.Writes);
        }

        internal static async Task VerifyResetTargetConflictAsync(string fault, bool finalCheck)
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            System.Action mutate = fault switch
            {
                "copy" => world.Remote.CopyTarget,
                "missing" => world.Remote.RemoveTarget,
                "edited" => world.Remote.EditTarget,
                "duplicate" => world.Remote.DuplicateTarget,
                "incomplete" => () => world.Remote.IncompleteDiscovery = true,
                _ => throw new InvalidOperationException(),
            };
            if (finalCheck) world.Remote.BeforeCreate = mutate;
            else mutate();
            var reset = await world.RunAsync(1, reset: true);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
            Assert.Equal(1, world.Remote.Writes);
            Assert.Equal(1, world.Remote.MutationAttempts);
            Assert.NotEqual(ResetHead(world, initial.Launch).Header.SessionId, initial.Provider.Request!.SessionId);
        }

        internal static async Task VerifyUnknownResetWriteAfterExpiryAsync()
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            var expiry = Assert.Single(ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time).Acceptances)
                .Receipt.LogicalExpiresAtUnixSeconds;
            world.Time.UnixSeconds = expiry - 60;
            world.Remote.UnknownWithoutWrite = true;
            var interrupted = await world.RunAsync(1, reset: true);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, interrupted.Completion.Summary.StateDisposition);
            Assert.Equal(1, world.Remote.Writes);
            Assert.Equal(2, world.Remote.MutationAttempts);
            var before = ResetHead(world, interrupted.Launch);
            world.Time.UnixSeconds = expiry + 1;
            world.Remote.UnknownWithoutWrite = false;
            var recovered = await world.RunAsync(1, reset: true);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, recovered.Completion.Summary.StateDisposition);
            Assert.Equal(0, recovered.Provider.Creates);
            Assert.Equal(2, world.Remote.MutationAttempts);
            Assert.Equal(before.Header.Epoch, ResetHead(world, recovered.Launch).Header.Epoch);
            Assert.Empty(ReadAcceptedStateRecords(world.Store, recovered.Launch, world.Time).Acceptances);
        }

        internal static async Task VerifyExpiredResetTargetAsync()
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            var expiry = Assert.Single(ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time).Acceptances)
                .Receipt.LogicalExpiresAtUnixSeconds;
            world.Time.UnixSeconds = expiry - 60;
            var reset = await world.RunAsync(1, reset: true, failProvider: true);
            Assert.Equal(1, reset.Provider.Creates);
            var before = ResetHead(world, reset.Launch);
            Assert.Equal(expiry, before.Head.ResetPublicationTarget!.ExpiresAtUnixSeconds);
            world.Time.UnixSeconds = expiry + 1;
            var retry = await world.RunAsync(1, reset: true);
            Assert.Equal(0, retry.Provider.Creates);
            Assert.Equal(1, world.Remote.Writes);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, retry.Completion.Summary.StateDisposition);
            var after = ResetHead(world, retry.Launch);
            Assert.Equal(before.Header.Epoch, after.Header.Epoch);
            Assert.Equal(before.Header.SessionId, after.Header.SessionId);
            Assert.Equal(before.Head.ResetPublicationTarget, after.Head.ResetPublicationTarget);
        }

        internal static async Task VerifyResetAcceptanceAfterTargetExpiryAsync(bool committed)
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            OpaqueStoreName? acceptanceName = null;
            var records = ReadAcceptedStateRecords(world.Store, initial.Launch, world.Time,
                (header, _, metadata) =>
                {
                    if (header.ObjectClass == StateObjectClass.Acceptance) acceptanceName = metadata.Reference.Name;
                });
            var expiry = Assert.Single(records.Acceptances).Receipt.LogicalExpiresAtUnixSeconds;
            Assert.NotNull(acceptanceName);
            world.Time.UnixSeconds = expiry - 60;
            world.Remote.AfterWrite = () =>
            {
                world.Store.FailNextUploadForName = acceptanceName;
                world.Store.ScheduledUploadFailure = committed ? OpaqueStoreFailure.OutcomeUnknown : OpaqueStoreFailure.Io;
                world.Store.ScheduledUploadMutationState = committed ? OpaqueStoreMutationState.Committed : OpaqueStoreMutationState.NotCommitted;
                world.Store.PersistFailedUpload = committed;
                world.Store.HideFailedUploadForNextLists = committed ? 30 : 0;
            };
            var interrupted = await world.RunAsync(1, reset: true);
            Assert.Equal(2, world.Remote.Writes);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, interrupted.Completion.Summary.StateDisposition);
            var before = ResetHead(world, interrupted.Launch);
            var physicalBefore = ReadAcceptedStateRecords(world.Store, interrupted.Launch, world.Time).Acceptances;
            Assert.Equal(committed ? 1 : 0, physicalBefore.Length);
            world.Remote.AfterWrite = null;
            world.Store.HideNextUploadedObjectForNextLists = 0;
            world.Time.UnixSeconds = expiry + 1;
            var recovered = await world.RunAsync(1, reset: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, recovered.Completion.Summary.StateDisposition);
            Assert.Equal(0, recovered.Provider.Creates);
            Assert.Equal(2, world.Remote.Writes);
            var after = ResetHead(world, recovered.Launch);
            Assert.Equal(before.Header.Epoch, after.Header.Epoch);
            Assert.Equal(before.Header.SessionId, after.Header.SessionId);
            Assert.Equal(before.Head.ResetPublicationTarget, after.Head.ResetPublicationTarget);
            var accepted = Assert.Single(ReadAcceptedStateRecords(world.Store, recovered.Launch, world.Time).Acceptances);
            Assert.True(accepted.Receipt.LogicalExpiresAtUnixSeconds > world.Time.UnixSeconds);
            if (committed) Assert.Equal(physicalBefore[0].Header.ObjectIdentity, accepted.Header.ObjectIdentity);
        }

        private static (StateControlHeaderV1 Header, LineageHeadV1 Head) ResetHead(
            ResetProbeWorld world, ActionHostLaunchContract launch)
        {
            var heads = new List<(StateControlHeaderV1 Header, LineageHeadV1 Head)>();
            ReadAcceptedStateRecords(world.Store, launch, world.Time, (header, payload, _) =>
            {
                if (header.ObjectClass != StateObjectClass.LineageHead) return;
                Assert.True(LineageHeadCodec.TryDecode(payload, out var head));
                heads.Add((header, head!));
            });
            Assert.NotEmpty(heads);
            return heads.MaxBy(value => value.Head.Ordinal);
        }
    }
}
