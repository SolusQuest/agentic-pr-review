using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageRecoveryAndFailureTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("digest")]
    [InlineData("size")]
    public async Task UploadResponseMustBindBeforeReadBackOrDelete(
        string mutation)
    {
        await WithRootAsync(async root =>
        {
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: new MutableLineageTimeProvider(
                    LineageTestData.Now));
            var probe = new ProbedLineageStore(inner)
            {
                UploadMetadataTransform = metadata => mutation switch
                {
                    "name" => metadata with
                    {
                        Reference = metadata.Reference with
                        {
                            Name = new OpaqueStoreName("other-name"),
                        },
                    },
                    "digest" => metadata with
                    {
                        EncryptedObjectDigest =
                            new OpaqueStoreEncryptedObjectDigest(
                                new string('f', 64)),
                    },
                    "size" => metadata with { Size = metadata.Size + 1 },
                    _ => metadata,
                },
            };
            var result = await new ScopedStateUploadProtocol(probe)
                .UploadAndReadBackAsync(
                    new OpaqueStoreName("bound-upload"),
                    new byte[] { 1, 2, 3 },
                    LineageTestData.Now + 100,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Unavailable, result.Code);
            Assert.Equal(0, probe.ReadBackCalls);
            Assert.Equal(0, probe.DeleteCalls);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task IncompleteEnumerationCannotUploadInitialClaim(
        int incompleteAtListCall)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var probe = new ProbedLineageStore(
                new LocalRestrictedStateStore(
                    root,
                    timeProvider: lease.Time))
            {
                IncompleteAtListCall = incompleteAtListCall,
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Equal(incompleteAtListCall, probe.ListCalls);
            Assert.Equal(0, probe.UploadCalls);
            Assert.Empty(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    [Fact]
    public async Task InitialConvergenceRejectsInjectedUnderRetainedState()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var coordinates = DeriveInitialCoordinates(lease);
            OpaqueStoreObjectMetadata? injected = null;
            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    var uploaded = await UploadStateObjectAsync(
                        inner,
                        lease,
                        coordinates.BaseScopeDigest,
                        coordinates.Epoch,
                        coordinates.SessionId,
                        StateObjectClass.Candidate,
                        predecessorIdentity: null,
                        LineageTestData.LogicalExpiry,
                        payloadMarker: 1,
                        underRetained: true);
                    injected = uploaded.Metadata;
                },
            };

            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.RetentionFailed, result.Code);
            Assert.NotNull(injected);
            var candidateName = await ResolveNameAsync(
                lease,
                StateObjectClass.Candidate);
            var candidates = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    candidateName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(candidates.Succeeded);
            Assert.Contains(injected!.Reference, candidates.Objects);
        });
    }

    [Fact]
    public async Task FreshProcessResumesBoundIntentWithoutDuplicateUpload()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(inner, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadActiveOpaqueStateAsync(inner, lease, selected, 1);

            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var interruptedStore = new ProbedLineageStore(inner)
            {
                ThrowOnFirstDelete = true,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(
                        lease.Access,
                        reset,
                        LineageTestData.Reviewed('5', '6')),
                    CancellationToken.None);
            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.Unavailable, interrupted.Code);
            Assert.Equal(1, interruptedStore.UploadCalls);

            var resetName = await ResolveNameAsync(
                lease,
                StateObjectClass.Reset);
            var afterCrash = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(afterCrash.Succeeded);
            Assert.Single(afterCrash.Objects);

            var recoveredStore = new ProbedLineageStore(inner);
            var recovered = await new LineageService(
                    recoveredStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(
                        lease.Access,
                        reset,
                        LineageTestData.Reviewed('3', '4')),
                    CancellationToken.None);
            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Reset, snapshot!.Transition);
            }

            // Recovery writes only the pending successor head. It does not
            // upload a duplicate transition intent.
            Assert.Equal(1, recoveredStore.UploadCalls);
            var afterRecovery = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(afterRecovery.Succeeded);
            Assert.Empty(afterRecovery.Objects);
        });
    }

    [Theory]
    [InlineData("different-workflow-run", 1)]
    [InlineData("workflow-run-42", 2)]
    public async Task PendingResetRejectsDifferentRunAuthority(
        string retryRunIdentity,
        long retryRunAttempt)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadActiveOpaqueStateAsync(inner, lease, selected, 1);
            var requestIdentity = new string('e', 64);
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                requestIdentity);
            var interrupted = await new LineageService(
                    new ProbedLineageStore(inner)
                    {
                        ThrowOnFirstDelete = true,
                    },
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(
                        lease.Access,
                        reset,
                        LineageTestData.Reviewed('3', '4')),
                    CancellationToken.None);
            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.Unavailable, interrupted.Code);

            var retryReset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                requestIdentity,
                retryRunIdentity,
                retryRunAttempt);
            var retryRequest = LineageTestData.Request(
                lease.Access,
                retryReset,
                LineageTestData.Reviewed('5', '6')) with
            {
                ProducingRunIdentity = retryRunIdentity,
                ProducingRunAttempt = retryRunAttempt,
            };
            var retryStore = new ProbedLineageStore(inner);
            var retried = await new LineageService(retryStore, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    retryRequest,
                    CancellationToken.None);

            Assert.False(retried.Succeeded);
            Assert.Equal(LineageCodes.AccessDenied, retried.Code);
            Assert.Equal(0, retryStore.UploadCalls);
            Assert.Equal(0, retryStore.DeleteCalls);
        });
    }

    [Fact]
    public async Task StaleActiveEpochIntentIsRejectedBeforeMutation()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadResetIntentAsync(
                inner,
                lease,
                selected,
                new string('f', 64),
                LineageTestData.LogicalExpiry,
                LineageTestData.Now + 8 * 24 * 60 * 60);
            var probe = new ProbedLineageStore(inner);
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, reset),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Equal(0, probe.UploadCalls);
            Assert.Equal(0, probe.DeleteCalls);
        });
    }

    [Fact]
    public async Task SameIdentityIntentRefreshesBeforeTransitionCleanup()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var priorRequiredExpiry = LineageTestData.Now +
                StateRetentionRequirements.ScopedPlatformRequestSeconds;
            await UploadResetIntentAsync(
                inner,
                lease,
                selected,
                selected.LineageHeadIdentity,
                LineageTestData.LogicalExpiry,
                priorRequiredExpiry);
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var request = LineageTestData.Request(lease.Access, reset) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };
            var probe = new ProbedLineageStore(inner);
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            using (result.Context)
            {
                Assert.True(result.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Reset,
                    snapshot!.Transition);
            }

            Assert.Equal(2, probe.UploadCalls);
        });
    }

    [Fact]
    public async Task EightEquivalentStaleIntentsPruneBeforeRefresh()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var priorRequiredExpiry = LineageTestData.Now +
                StateRetentionRequirements.ScopedPlatformRequestSeconds;
            for (var copy = 0;
                copy < LineageFormat.MaximumPhysicalPerClass;
                copy++)
            {
                await UploadResetIntentAsync(
                    inner,
                    lease,
                    selected,
                    selected.LineageHeadIdentity,
                    LineageTestData.LogicalExpiry,
                    priorRequiredExpiry);
            }

            var resetName = await ResolveNameAsync(
                lease,
                StateObjectClass.Reset);
            var atCap = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(atCap.Succeeded);
            Assert.Equal(LineageFormat.MaximumPhysicalPerClass,
                atCap.Objects.Length);

            var maximumPhysicalCount = Directory.GetFiles(
                root,
                "*.aprobject").Length;
            var deletesBeforeFirstUpload = -1;
            var resetFamilyCompleteAfterRefresh = false;
            var resetFamilyCountAfterRefresh = -1;
            ProbedLineageStore? probe = null;
            probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall == 1)
                    {
                        deletesBeforeFirstUpload = probe!.DeleteCalls;
                        var family = await inner.ListExactAsync(
                            new OpaqueStoreListRequest(
                                resetName,
                                LineageFormat.MaximumPhysicalPerClass),
                            CancellationToken.None);
                        resetFamilyCompleteAfterRefresh = family.Succeeded;
                        resetFamilyCountAfterRefresh = family.Objects.Length;
                    }

                    maximumPhysicalCount = Math.Max(
                        maximumPhysicalCount,
                        Directory.GetFiles(root, "*.aprobject").Length);
                },
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var request = LineageTestData.Request(lease.Access, reset) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            result.Context!.Dispose();
            Assert.Equal(1, deletesBeforeFirstUpload);
            Assert.True(resetFamilyCompleteAfterRefresh);
            Assert.Equal(LineageFormat.MaximumPhysicalPerClass,
                resetFamilyCountAfterRefresh);
            Assert.True(maximumPhysicalCount <= 9);
            Assert.Equal(2, probe.UploadCalls);
            Assert.Single(probe.UploadedNames, name => name == resetName);

            var afterRecovery = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(afterRecovery.Succeeded);
            Assert.Empty(afterRecovery.Objects);
        });
    }

    [Fact]
    public async Task FreshProcessRetriesAmbiguousStaleIntentPrePrune()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var seedStore = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(seedStore, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var priorRequiredExpiry = LineageTestData.Now +
                StateRetentionRequirements.ScopedPlatformRequestSeconds;
            for (var copy = 0;
                copy < LineageFormat.MaximumPhysicalPerClass;
                copy++)
            {
                await UploadResetIntentAsync(
                    seedStore,
                    lease,
                    selected,
                    selected.LineageHeadIdentity,
                    LineageTestData.LogicalExpiry,
                    priorRequiredExpiry);
            }

            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var request = LineageTestData.Request(lease.Access, reset) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };
            var interruptedStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time))
            {
                ReturnDeleteOutcomeUnknownAlways = true,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.CleanupFailed, interrupted.Code);
            Assert.Null(interrupted.Context);
            Assert.Equal(1, interruptedStore.DeleteCalls);
            Assert.Equal(0, interruptedStore.UploadCalls);

            var resetName = await ResolveNameAsync(
                lease,
                StateObjectClass.Reset);
            var stillAtCap = await seedStore.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(stillAtCap.Succeeded);
            Assert.Equal(LineageFormat.MaximumPhysicalPerClass,
                stillAtCap.Objects.Length);

            var recoveredStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time));
            var recovered = await new LineageService(
                    recoveredStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            recovered.Context!.Dispose();
            Assert.Equal(2, recoveredStore.UploadCalls);
            Assert.Single(recoveredStore.UploadedNames,
                name => name == resetName);
        });
    }

    [Fact]
    public async Task EightUnknownTransitionRecordsBlockIntentAppend()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var foreignKey = Convert.ToBase64String(
                Enumerable.Repeat((byte)0xd4, 32).ToArray());
            using (var foreignLease = LineageTestData.Context(
                currentBase64: foreignKey))
            {
                for (byte marker = 1; marker <= 8; marker++)
                {
                    await UploadForeignTransitionObjectAsync(
                        inner,
                        foreignLease,
                        selected,
                        marker);
                }
            }

            var probe = new ProbedLineageStore(inner);
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, reset),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Null(result.Context);
            Assert.Equal(0, probe.UploadCalls);
            Assert.Equal(9, Directory.GetFiles(root, "*.aprobject").Length);
        });
    }

    [Fact]
    public async Task AmbiguousDeleteCannotPublishSuccessorOrSession()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initial = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initial.Succeeded, initial.Code);
            SelectedLineageSnapshot selected;
            using (initial.Context)
            {
                Assert.True(initial.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadActiveOpaqueStateAsync(
                inner,
                lease,
                selected,
                payloadMarker: 1);
            var probe = new ProbedLineageStore(inner)
            {
                ReturnDeleteOutcomeUnknownOnce = true,
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(
                        lease.Access,
                        reset,
                        LineageTestData.Reviewed('3', '4')),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.CleanupFailed, result.Code);
            Assert.Equal(1, probe.UploadCalls);
            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var heads = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(heads.Succeeded);
            Assert.Single(heads.Objects);
        });
    }

    [Fact]
    public async Task ObjectInjectedAfterTargetDeleteBlocksSuccessorPublication()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadActiveOpaqueStateAsync(inner, lease, selected, 1);
            var probe = new ProbedLineageStore(inner)
            {
                AfterFirstSuccessfulDelete = () =>
                    UploadForeignTransitionObjectAsync(
                        inner,
                        lease,
                        selected,
                        payloadMarker: 9),
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, reset),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Equal(1, probe.UploadCalls);
            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var heads = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(heads.Succeeded);
            Assert.Single(heads.Objects);
        });
    }

    [Fact]
    public async Task OlderEpochObjectInjectedAfterSuccessorBlocksSessionReturn()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = call => call == 2
                    ? UploadActiveOpaqueStateAsync(
                        inner,
                        lease,
                        selected,
                        payloadMarker: 7)
                    : Task.CompletedTask,
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, reset),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Equal(2, probe.UploadCalls);
        });
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("expiry")]
    public async Task TransitionSuccessorDoesNotReturnBeforeResidualCleanup(
        string transitionName)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            LineageResolveRequest request;
            StateObjectClass intentClass;
            LineageTransitionKind expectedTransition;
            if (StringComparer.Ordinal.Equals(transitionName, "reset"))
            {
                await UploadActiveOpaqueStateAsync(
                    inner,
                    lease,
                    selected,
                    payloadMarker: 1);
                var reset = LineageTestData.Reset(
                    lease.Access,
                    selected.LineageHeadIdentity,
                    new string('e', 64));
                request = LineageTestData.Request(lease.Access, reset);
                intentClass = StateObjectClass.Reset;
                expectedTransition = LineageTransitionKind.Reset;
            }
            else
            {
                Assert.Equal("expiry", transitionName);
                _ = await UploadStateObjectAsync(
                    inner,
                    lease,
                    selected,
                    StateObjectClass.Acceptance,
                    predecessorIdentity: null,
                    logicalExpiry: LineageTestData.Now,
                    payloadMarker: 1,
                    underRetained: false);
                request = LineageTestData.Request(lease.Access);
                intentClass = StateObjectClass.ExpiryTransition;
                expectedTransition = LineageTransitionKind.Expiry;
            }

            var interruptedStore = new ProbedLineageStore(inner)
            {
                // Target cleanup happens first. The next delete is the
                // transition-intent residual owned by CompleteTransitionAsync.
                ThrowOnDeleteCall = 2,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(interrupted.Succeeded);
            Assert.Null(interrupted.Context);
            Assert.Equal(LineageCodes.Unavailable, interrupted.Code);
            Assert.Equal(2, interruptedStore.UploadCalls);
            Assert.Equal(2, interruptedStore.DeleteCalls);
            var intentName = await ResolveNameAsync(
                lease,
                intentClass);
            var interruptedIntents = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    intentName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(interruptedIntents.Succeeded);
            Assert.Single(interruptedIntents.Objects);

            var recovered = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(expectedTransition, snapshot!.Transition);
            }

            var completedIntents = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    intentName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(completedIntents.Succeeded);
            Assert.Empty(completedIntents.Objects);
        });
    }

    [Fact]
    public async Task SuccessorConvergenceRetriesAStalePredecessorOnlyList()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var initialHeads = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(initialHeads.Succeeded);
            var predecessor = Assert.Single(initialHeads.Objects);
            var probe = new ProbedLineageStore(inner)
            {
                StaleLineageReference = predecessor,
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(
                        lease.Access,
                        reset,
                        LineageTestData.Reviewed('3', '4')),
                    CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            using (result.Context)
            {
                Assert.True(result.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Reset, snapshot!.Transition);
            }

            Assert.True(probe.StaleLineageListInjected);
        });
    }

    [Fact]
    public async Task RefreshConvergenceRetriesAStaleSameIdentitySource()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var initialHeads = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(initialHeads.Succeeded);
            var staleSource = Assert.Single(initialHeads.Objects);
            var probe = new ProbedLineageStore(inner)
            {
                StaleLineageReference = staleSource,
                StaleLineageAfterUploadCall = 1,
            };
            var request = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };

            var refreshed = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(refreshed.Succeeded, refreshed.Code);
            using (refreshed.Context)
            {
                Assert.True(refreshed.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(
                    selected.LineageHeadIdentity,
                    snapshot!.LineageHeadIdentity);
            }

            Assert.True(probe.StaleLineageListInjected);
            Assert.True(probe.ListCalls > 1);
        });
    }

    [Fact]
    public async Task RefreshConvergenceRejectsInjectedUnderRetainedState()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            OpaqueStoreObjectMetadata? injected = null;
            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    var uploaded = await UploadStateObjectAsync(
                        inner,
                        lease,
                        selected,
                        StateObjectClass.PublicationIntent,
                        predecessorIdentity: null,
                        LineageTestData.LogicalExpiry,
                        payloadMarker: 1,
                        underRetained: true);
                    injected = uploaded.Metadata;
                },
            };
            var request = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.RetentionFailed, result.Code);
            Assert.NotNull(injected);
            var publicationName = await ResolveNameAsync(
                lease,
                StateObjectClass.PublicationIntent);
            var publications = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    publicationName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(publications.Succeeded);
            Assert.Contains(injected!.Reference, publications.Objects);
        });
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("expiry")]
    public async Task PendingTransitionInjectedAfterRefreshDoesNotReturnContext(
        string transitionName)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var requiredLogicalExpiry = LineageTestData.LogicalExpiry + 60;
            var requiredPlatformExpiry = LineageTestData.Now +
                8 * 24 * 60 * 60;
            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    if (StringComparer.Ordinal.Equals(
                            transitionName,
                            "reset"))
                    {
                        await UploadTransitionIntentAsync(
                            inner,
                            lease,
                            selected,
                            LineageTransitionIntentKind.Reset,
                            selected.LineageHeadIdentity,
                            new string('e', 64),
                            expiryBoundaryUnixSeconds: null,
                            [],
                            requiredLogicalExpiry,
                            requiredPlatformExpiry);
                        return;
                    }

                    Assert.Equal("expiry", transitionName);
                    var acceptance = await UploadStateObjectAsync(
                        inner,
                        lease,
                        selected,
                        StateObjectClass.Acceptance,
                        predecessorIdentity: null,
                        logicalExpiry: LineageTestData.Now,
                        payloadMarker: 1,
                        underRetained: false);
                    await UploadTransitionIntentAsync(
                        inner,
                        lease,
                        selected,
                        LineageTransitionIntentKind.Expiry,
                        selected.LineageHeadIdentity,
                        acceptance.Header.ObjectIdentity,
                        LineageTestData.Now,
                        [LineageHeadCodec.Evidence(acceptance.Metadata)],
                        requiredLogicalExpiry,
                        requiredPlatformExpiry);
                },
            };
            var request = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = requiredLogicalExpiry,
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(LineageCodes.Unavailable, result.Code);

            var recoveryRequest = request;
            var expectedTransition = LineageTransitionKind.Expiry;
            if (StringComparer.Ordinal.Equals(transitionName, "reset"))
            {
                recoveryRequest = request with
                {
                    Reset = LineageTestData.Reset(
                        lease.Access,
                        selected.LineageHeadIdentity,
                        new string('e', 64)),
                };
                expectedTransition = LineageTransitionKind.Reset;
            }

            var recovered = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    recoveryRequest,
                    CancellationToken.None);
            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(expectedTransition, snapshot!.Transition);
            }
        });
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("siblings")]
    public async Task AcceptanceInjectedAfterRefreshDoesNotReturnContext(
        string acceptanceState)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    _ = await UploadStateObjectAsync(
                        inner,
                        lease,
                        selected,
                        StateObjectClass.Acceptance,
                        predecessorIdentity: null,
                        logicalExpiry: LineageTestData.Now,
                        payloadMarker: 1,
                        underRetained: false);
                    if (StringComparer.Ordinal.Equals(
                            acceptanceState,
                            "siblings"))
                    {
                        _ = await UploadStateObjectAsync(
                            inner,
                            lease,
                            selected,
                            StateObjectClass.Acceptance,
                            predecessorIdentity: null,
                            logicalExpiry: LineageTestData.Now,
                            payloadMarker: 2,
                            underRetained: false);
                    }
                    else
                    {
                        Assert.Equal("expired", acceptanceState);
                    }
                },
            };
            var request = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 60,
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Context);
            Assert.Equal(
                StringComparer.Ordinal.Equals(acceptanceState, "siblings")
                    ? LineageCodes.Conflict
                    : LineageCodes.Unavailable,
                result.Code);

            var recovered = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);
            if (StringComparer.Ordinal.Equals(acceptanceState, "siblings"))
            {
                Assert.False(recovered.Succeeded);
                Assert.Null(recovered.Context);
                Assert.Equal(LineageCodes.Conflict, recovered.Code);
                return;
            }

            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Expiry,
                    snapshot!.Transition);
            }
        });
    }

    [Fact]
    public async Task FreshProcessKeepsAdequateHeadWhileCleaningSevenUnderRetainedCopies()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            for (var attempt = 2; attempt <= 8; attempt++)
            {
                await UploadEquivalentUnderRetainedInitialHeadAsync(
                    inner,
                    lease,
                    selected,
                    attempt);
            }

            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var atCap = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(atCap.Succeeded);
            Assert.Equal(LineageFormat.MaximumPhysicalPerClass,
                atCap.Objects.Length);

            var recovered = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(
                    selected.LineageHeadIdentity,
                    snapshot!.LineageHeadIdentity);
            }

            var afterRecovery = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    lineageName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(afterRecovery.Succeeded);
            Assert.Single(afterRecovery.Objects);
        });
    }

    [Fact]
    public async Task AuthenticatedRetentionFloorCannotBeDowngradedByRetry()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();

            var probe = new ProbedLineageStore(inner)
            {
                ReadMetadataTransform = metadata => metadata with
                {
                    ExpiresAtUnixSeconds =
                        metadata.ExpiresAtUnixSeconds - 3_601,
                },
            };
            var service = new LineageService(probe, lease.Time);
            var rejected = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.False(rejected.Succeeded);
            Assert.Equal(LineageCodes.CleanupFailed, rejected.Code);

            var lowerRequest = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = LineageTestData.Now + 1,
            };
            var retried = await service.ResolveAsync(
                lease.Context,
                lowerRequest,
                CancellationToken.None);
            Assert.False(retried.Succeeded);
            Assert.Equal(LineageCodes.CleanupFailed, retried.Code);
            Assert.Equal(0, probe.UploadCalls);
        });
    }

    [Fact]
    public async Task UnderRetainedUploadWithUnknownCleanupCannotSucceed()
    {
        await WithRootAsync(async root =>
        {
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: new MutableLineageTimeProvider(
                    LineageTestData.Now));
            var probe = new ProbedLineageStore(inner)
            {
                UploadRequiredExpiryTransform = required => required - 3_601,
                ReturnDeleteOutcomeUnknownOnce = true,
            };
            var result = await new ScopedStateUploadProtocol(probe)
                .UploadAndReadBackAsync(
                    new OpaqueStoreName("under-retained-upload"),
                    new byte[] { 1, 2, 3 },
                    LineageTestData.Now + 100,
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.RetentionFailed, result.Code);
            Assert.Equal(2, probe.DeleteCalls);
            Assert.Empty(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    [Fact]
    public async Task FreshProcessReinitializesAfterUnderRetainedInitialClaim()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var interruptedStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time))
            {
                UploadRequiredExpiryTransform = required => required - 3_601,
                ReturnDeleteOutcomeUnknownAlways = true,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.CleanupFailed, interrupted.Code);
            Assert.Null(interrupted.Context);
            Assert.Equal(1, interruptedStore.UploadCalls);
            Assert.True(interruptedStore.DeleteCalls >=
                LineageFormat.MaximumScopedObjects);

            var lineageName = await ResolveNameAsync(
                lease,
                StateObjectClass.LineageHead);
            var persisted = await new LocalRestrictedStateStore(
                    root,
                    timeProvider: lease.Time)
                .ListExactAsync(
                    new OpaqueStoreListRequest(
                        lineageName,
                        LineageFormat.MaximumPhysicalPerClass),
                    CancellationToken.None);
            Assert.True(persisted.Succeeded);
            Assert.Single(persisted.Objects);

            var absenceObserved = false;
            var recoveredStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time))
            {
                AfterFirstSuccessfulDelete = () =>
                {
                    absenceObserved = Directory.GetFiles(
                        root,
                        "*.aprobject").Length == 0;
                    return Task.CompletedTask;
                },
            };
            var recovered = await new LineageService(
                    recoveredStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Initial,
                    snapshot!.Transition);
            }

            Assert.True(absenceObserved);
            Assert.Equal(1, recoveredStore.DeleteCalls);
            Assert.Equal(1, recoveredStore.UploadCalls);
            var reinitialized = await new LocalRestrictedStateStore(
                    root,
                    timeProvider: lease.Time)
                .ListExactAsync(
                    new OpaqueStoreListRequest(
                        lineageName,
                        LineageFormat.MaximumPhysicalPerClass),
                    CancellationToken.None);
            Assert.True(reinitialized.Succeeded);
            Assert.Single(reinitialized.Objects);
        });
    }

    [Fact]
    public async Task AuthorizedResetIncludesUnderRetainedLaterOwnedState()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            _ = await UploadStateObjectAsync(
                inner,
                lease,
                selected,
                StateObjectClass.Candidate,
                selected.LineageHeadIdentity,
                LineageTestData.LogicalExpiry,
                payloadMarker: 1,
                underRetained: true);
            var candidateName = await ResolveNameAsync(
                lease,
                StateObjectClass.Candidate);
            var resetName = await ResolveNameAsync(
                lease,
                StateObjectClass.Reset);
            var intentObservedBeforeTargetDelete = false;
            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    var candidates = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            candidateName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    var intents = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            resetName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    intentObservedBeforeTargetDelete =
                        candidates.Succeeded &&
                        candidates.Objects.Length == 1 &&
                        intents.Succeeded &&
                        intents.Objects.Length == 1;
                },
            };
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, reset),
                    CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            using (result.Context)
            {
                Assert.True(result.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Reset,
                    snapshot!.Transition);
            }

            Assert.True(intentObservedBeforeTargetDelete);
            var remainingCandidates = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    candidateName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(remainingCandidates.Succeeded);
            Assert.Empty(remainingCandidates.Objects);
        });
    }

    [Fact]
    public async Task ExpiryIncludesUnderRetainedCurrentAcceptance()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            _ = await UploadStateObjectAsync(
                inner,
                lease,
                selected,
                StateObjectClass.Acceptance,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.Now,
                payloadMarker: 1,
                underRetained: true);
            var acceptanceName = await ResolveNameAsync(
                lease,
                StateObjectClass.Acceptance);
            var intentName = await ResolveNameAsync(
                lease,
                StateObjectClass.ExpiryTransition);
            var intentObservedBeforeTargetDelete = false;
            var probe = new ProbedLineageStore(inner)
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    var acceptances = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            acceptanceName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    var intents = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            intentName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    intentObservedBeforeTargetDelete =
                        acceptances.Succeeded &&
                        acceptances.Objects.Length == 1 &&
                        intents.Succeeded &&
                        intents.Objects.Length == 1;
                },
            };
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            using (result.Context)
            {
                Assert.True(result.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Expiry,
                    snapshot!.Transition);
            }

            Assert.True(intentObservedBeforeTargetDelete);
            var remainingAcceptances = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    acceptanceName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(remainingAcceptances.Succeeded);
            Assert.Empty(remainingAcceptances.Objects);
        });
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("expiry")]
    public async Task FreshProcessRecoversUnderRetainedTransitionIntent(
        string transitionName)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            LineageResolveRequest request;
            StateObjectClass targetClass;
            StateObjectClass intentClass;
            LineageTransitionKind expectedTransition;
            if (StringComparer.Ordinal.Equals(transitionName, "reset"))
            {
                await UploadActiveOpaqueStateAsync(
                    inner,
                    lease,
                    selected,
                    payloadMarker: 1);
                var reset = LineageTestData.Reset(
                    lease.Access,
                    selected.LineageHeadIdentity,
                    new string('e', 64));
                request = LineageTestData.Request(lease.Access, reset);
                targetClass = StateObjectClass.Candidate;
                intentClass = StateObjectClass.Reset;
                expectedTransition = LineageTransitionKind.Reset;
            }
            else
            {
                Assert.Equal("expiry", transitionName);
                _ = await UploadStateObjectAsync(
                    inner,
                    lease,
                    selected,
                    StateObjectClass.Acceptance,
                    predecessorIdentity: null,
                    logicalExpiry: LineageTestData.Now,
                    payloadMarker: 1,
                    underRetained: false);
                request = LineageTestData.Request(lease.Access);
                targetClass = StateObjectClass.Acceptance;
                intentClass = StateObjectClass.ExpiryTransition;
                expectedTransition = LineageTransitionKind.Expiry;
            }

            var interruptedStore = new ProbedLineageStore(inner)
            {
                UploadRequiredExpiryTransform = required =>
                    required - 3_601,
                ReturnDeleteOutcomeUnknownAlways = true,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.CleanupFailed, interrupted.Code);
            Assert.Null(interrupted.Context);
            Assert.Equal(1, interruptedStore.UploadCalls);
            var intentName = await ResolveNameAsync(lease, intentClass);
            var targetName = await ResolveNameAsync(lease, targetClass);
            var persistedIntent = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    intentName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            var persistedTarget = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    targetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(persistedIntent.Succeeded);
            Assert.Single(persistedIntent.Objects);
            Assert.True(persistedTarget.Succeeded);
            Assert.Single(persistedTarget.Objects);

            var refreshObservedBeforeTargetDelete = false;
            var recoveredStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time))
            {
                AfterUpload = async uploadCall =>
                {
                    if (uploadCall != 1)
                    {
                        return;
                    }

                    var intents = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            intentName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    var targets = await inner.ListExactAsync(
                        new OpaqueStoreListRequest(
                            targetName,
                            LineageFormat.MaximumPhysicalPerClass),
                        CancellationToken.None);
                    refreshObservedBeforeTargetDelete =
                        intents.Succeeded &&
                        intents.Objects.Length == 2 &&
                        targets.Succeeded &&
                        targets.Objects.Length == 1;
                },
            };
            var recovered = await new LineageService(
                    recoveredStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(expectedTransition, snapshot!.Transition);
            }

            Assert.True(refreshObservedBeforeTargetDelete);
            Assert.Equal(2, recoveredStore.UploadCalls);
            var remainingIntents = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    intentName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            var remainingTargets = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    targetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(remainingIntents.Succeeded);
            Assert.Empty(remainingIntents.Objects);
            Assert.True(remainingTargets.Succeeded);
            Assert.Empty(remainingTargets.Objects);
        });
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("expiry")]
    public async Task FreshProcessRecoversExpiredPendingTransitionTarget(
        string transitionName)
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var requiredLogicalExpiry = LineageTestData.Now +
                15 * 24 * 60 * 60;
            var initialRequest = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = requiredLogicalExpiry,
            };
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    initialRequest,
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            LineageResolveRequest request;
            StateObjectClass targetClass;
            LineageTransitionKind expectedTransition;
            (StateControlHeaderV1 Header,
                OpaqueStoreObjectMetadata Metadata) target;
            if (StringComparer.Ordinal.Equals(transitionName, "reset"))
            {
                target = await UploadStateObjectAsync(
                    inner,
                    lease,
                    selected,
                    StateObjectClass.Candidate,
                    predecessorIdentity: null,
                    LineageTestData.LogicalExpiry,
                    payloadMarker: 1,
                    underRetained: true);
                var reset = LineageTestData.Reset(
                    lease.Access,
                    selected.LineageHeadIdentity,
                    new string('e', 64));
                request = LineageTestData.Request(lease.Access, reset) with
                {
                    RequiredLogicalExpiresAtUnixSeconds =
                        requiredLogicalExpiry,
                };
                targetClass = StateObjectClass.Candidate;
                expectedTransition = LineageTransitionKind.Reset;
            }
            else
            {
                Assert.Equal("expiry", transitionName);
                target = await UploadStateObjectAsync(
                    inner,
                    lease,
                    selected,
                    StateObjectClass.Acceptance,
                    predecessorIdentity: null,
                    logicalExpiry: LineageTestData.Now,
                    payloadMarker: 1,
                    underRetained: true);
                request = initialRequest;
                targetClass = StateObjectClass.Acceptance;
                expectedTransition = LineageTransitionKind.Expiry;
            }

            var interruptedStore = new ProbedLineageStore(inner)
            {
                ThrowOnFirstDelete = true,
            };
            var interrupted = await new LineageService(
                    interruptedStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.Unavailable, interrupted.Code);
            Assert.Null(interrupted.Context);
            Assert.Equal(1, interruptedStore.UploadCalls);
            Assert.Equal(1, interruptedStore.DeleteCalls);

            lease.Time.UnixSeconds = target.Metadata.ExpiresAtUnixSeconds;
            var expired = await inner.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    target.Metadata,
                    LineageFormat.MaximumEnvelopeBytes),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Expired, expired.Failure);
            var targetName = await ResolveNameAsync(lease, targetClass);
            var listed = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    targetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(listed.Succeeded);
            Assert.Single(listed.Objects);

            var recoveredStore = new ProbedLineageStore(
                new LocalRestrictedStateStore(root, timeProvider: lease.Time));
            var recovered = await new LineageService(
                    recoveredStore,
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(expectedTransition, snapshot!.Transition);
            }

            Assert.Equal(1, recoveredStore.UploadCalls);
            var remaining = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    targetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(remaining.Succeeded);
            Assert.Empty(remaining.Objects);
        });
    }

    [Fact]
    public async Task ExpiredPendingTargetDescriptorMismatchRemainsConflict()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var requiredLogicalExpiry = LineageTestData.Now +
                15 * 24 * 60 * 60;
            var initialRequest = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = requiredLogicalExpiry,
            };
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    initialRequest,
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var target = await UploadStateObjectAsync(
                inner,
                lease,
                selected,
                StateObjectClass.Candidate,
                predecessorIdentity: null,
                LineageTestData.LogicalExpiry,
                payloadMarker: 1,
                underRetained: true);
            var reset = LineageTestData.Reset(
                lease.Access,
                selected.LineageHeadIdentity,
                new string('e', 64));
            var request = LineageTestData.Request(lease.Access, reset) with
            {
                RequiredLogicalExpiresAtUnixSeconds = requiredLogicalExpiry,
            };
            var interrupted = await new LineageService(
                    new ProbedLineageStore(inner)
                    {
                        ThrowOnFirstDelete = true,
                    },
                    lease.Time)
                .ResolveAsync(
                    lease.Context,
                    request,
                    CancellationToken.None);
            Assert.False(interrupted.Succeeded);
            Assert.Equal(LineageCodes.Unavailable, interrupted.Code);

            var downloaded = await inner.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    target.Metadata,
                    LineageFormat.MaximumEnvelopeBytes),
                CancellationToken.None);
            Assert.True(downloaded.Succeeded);
            var envelope = downloaded.EncryptedBytes.ToArray();
            try
            {
                var deleted = await inner.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target.Metadata),
                    CancellationToken.None);
                Assert.True(deleted.Succeeded);
                var replacement = await inner.UploadImmutableAsync(
                    new OpaqueStoreUploadRequest(
                        target.Metadata.Reference.Name,
                        new OpaqueStoreCorrelationId(
                            LineageCryptography.CorrelationId(envelope)),
                        envelope,
                        new OpaqueStoreEncryptedObjectDigest(
                            OpaqueStoreHash.Sha256(envelope)),
                        target.Metadata.ExpiresAtUnixSeconds),
                    CancellationToken.None);
                Assert.True(replacement.Succeeded);
                Assert.NotNull(replacement.Metadata);
                Assert.NotEqual(
                    target.Metadata.Reference.ObjectId,
                    replacement.Metadata!.Reference.ObjectId);

                lease.Time.UnixSeconds =
                    replacement.Metadata.ExpiresAtUnixSeconds;
                var probe = new ProbedLineageStore(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: lease.Time));
                var recovered = await new LineageService(probe, lease.Time)
                    .ResolveAsync(
                        lease.Context,
                        request,
                        CancellationToken.None);

                Assert.False(recovered.Succeeded);
                Assert.Equal(LineageCodes.Conflict, recovered.Code);
                Assert.Null(recovered.Context);
                Assert.Equal(0, probe.UploadCalls);
                Assert.Equal(0, probe.DeleteCalls);
                var remaining = await inner.ListExactAsync(
                    new OpaqueStoreListRequest(
                        target.Metadata.Reference.Name,
                        LineageFormat.MaximumPhysicalPerClass),
                    CancellationToken.None);
                Assert.True(remaining.Succeeded);
                Assert.Single(remaining.Objects);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        });
    }

    [Fact]
    public async Task CompetingUnderRetainedIntentsRemainConflict()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var inner = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(inner, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            await UploadUnderRetainedResetIntentAsync(
                inner,
                lease,
                selected,
                new string('e', 64));
            await UploadUnderRetainedResetIntentAsync(
                inner,
                lease,
                selected,
                new string('f', 64));
            var resetName = await ResolveNameAsync(
                lease,
                StateObjectClass.Reset);
            var before = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(before.Succeeded);
            Assert.Equal(2, before.Objects.Length);

            var probe = new ProbedLineageStore(inner);
            var result = await new LineageService(probe, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Null(result.Context);
            Assert.Equal(0, probe.UploadCalls);
            Assert.Equal(0, probe.DeleteCalls);
            var after = await inner.ListExactAsync(
                new OpaqueStoreListRequest(
                    resetName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(after.Succeeded);
            Assert.Equal(before.Objects
                    .OrderBy(item => item.ObjectId.Value,
                        StringComparer.Ordinal),
                after.Objects.OrderBy(item => item.ObjectId.Value,
                    StringComparer.Ordinal));
        });
    }

    private static async Task UploadActiveOpaqueStateAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        byte payloadMarker)
    {
        _ = await UploadStateObjectAsync(
            store,
            lease,
            selected,
            StateObjectClass.Candidate,
            predecessorIdentity: null,
            LineageTestData.LogicalExpiry,
            payloadMarker,
            underRetained: false);
    }

    private static (string BaseScopeDigest, string Epoch, string SessionId)
        DeriveInitialCoordinates(
        LineageTestData.ContextLease lease)
    {
        Assert.True(LineageBaseScopeCodec.TryDigest(
            LineageTestData.Scope(),
            out var baseScopeDigest));
        Assert.True(LineageCryptography.TryDeriveInitialEpoch(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            out var epoch));
        Assert.True(LineageCryptography.TryDeriveSessionId(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            epoch,
            out var sessionId));
        return (baseScopeDigest, epoch, sessionId);
    }

    private static async Task<(
        StateControlHeaderV1 Header,
        OpaqueStoreObjectMetadata Metadata)> UploadStateObjectAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        StateObjectClass objectClass,
        string? predecessorIdentity,
        long logicalExpiry,
        byte payloadMarker,
        bool underRetained)
        => await UploadStateObjectAsync(
            store,
            lease,
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            objectClass,
            predecessorIdentity,
            logicalExpiry,
            payloadMarker,
            underRetained);

    private static async Task<(
        StateControlHeaderV1 Header,
        OpaqueStoreObjectMetadata Metadata)> UploadStateObjectAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        string baseScopeDigest,
        string epoch,
        string sessionId,
        StateObjectClass objectClass,
        string? predecessorIdentity,
        long logicalExpiry,
        byte payloadMarker,
        bool underRetained)
    {
        var name = await ResolveNameAsync(lease, objectClass);
        var draft = new StateControlHeaderDraft(
            baseScopeDigest,
            epoch,
            sessionId,
            objectClass,
            predecessorIdentity,
            SuccessorIdentity: null,
            "candidate-run",
            ProducingRunAttempt: payloadMarker,
            LineageTestData.Now,
            logicalExpiry,
            LineageTestData.Now + 8 * 24 * 60 * 60);
        byte[] payload = [payloadMarker];
        try
        {
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name,
                draft,
                payload,
                out var envelope,
                out var header,
                out var code), code);
            try
            {
                OpaqueStoreObjectMetadata metadata;
                if (underRetained)
                {
                    var uploaded = await store.UploadImmutableAsync(
                        new OpaqueStoreUploadRequest(
                            name,
                            new OpaqueStoreCorrelationId(
                                LineageCryptography.CorrelationId(envelope)),
                            envelope,
                            new OpaqueStoreEncryptedObjectDigest(
                                OpaqueStoreHash.Sha256(envelope)),
                            draft.RequiredPlatformExpiresAtUnixSeconds -
                                3_601),
                        CancellationToken.None);
                    Assert.True(uploaded.Succeeded);
                    Assert.NotNull(uploaded.Metadata);
                    Assert.True(uploaded.Metadata!.ExpiresAtUnixSeconds <
                        draft.RequiredPlatformExpiresAtUnixSeconds);
                    metadata = uploaded.Metadata;
                }
                else
                {
                    var uploaded = await new ScopedStateUploadProtocol(store)
                        .UploadAndReadBackAsync(
                            name,
                            envelope,
                            draft.RequiredPlatformExpiresAtUnixSeconds,
                            CancellationToken.None);
                    Assert.True(uploaded.Succeeded, uploaded.Code);
                    Assert.NotNull(uploaded.Metadata);
                    metadata = uploaded.Metadata!;
                }

                return (header!, metadata);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task UploadUnderRetainedResetIntentAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        string transitionEvidenceIdentity)
    {
        var targets = System.Collections.Immutable.ImmutableArray<
            LineageArtifactEvidence>.Empty;
        var intent = new LineageTransitionIntentV1(
            LineageTransitionIntentKind.Reset,
            selected.LineageHeadIdentity,
            selected.Epoch,
            transitionEvidenceIdentity,
            ExpiryBoundaryUnixSeconds: null,
            LineageTestData.Reviewed(),
            LineageCryptography.InventoryDigest(targets),
            targets,
            "workflow-run-42",
            ResetAuthorityRunAttempt: 1);
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            intent,
            out var payload));
        var name = await ResolveNameAsync(lease, StateObjectClass.Reset);
        var requiredExpiry = LineageTestData.Now +
            8 * 24 * 60 * 60;
        var draft = new StateControlHeaderDraft(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            StateObjectClass.Reset,
            selected.LineageHeadIdentity,
            SuccessorIdentity: null,
            "workflow-run-42",
            ProducingRunAttempt: 1,
            LineageTestData.Now,
            LineageTestData.LogicalExpiry,
            requiredExpiry);
        try
        {
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name,
                draft,
                payload,
                out var envelope,
                out _,
                out var code), code);
            try
            {
                var uploaded = await store.UploadImmutableAsync(
                    new OpaqueStoreUploadRequest(
                        name,
                        new OpaqueStoreCorrelationId(
                            LineageCryptography.CorrelationId(envelope)),
                        envelope,
                        new OpaqueStoreEncryptedObjectDigest(
                            OpaqueStoreHash.Sha256(envelope)),
                        requiredExpiry - 3_601),
                    CancellationToken.None);
                Assert.True(uploaded.Succeeded);
                Assert.NotNull(uploaded.Metadata);
                Assert.True(uploaded.Metadata!.ExpiresAtUnixSeconds <
                    requiredExpiry);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task UploadTransitionIntentAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        LineageTransitionIntentKind kind,
        string priorHeadIdentity,
        string transitionEvidenceIdentity,
        long? expiryBoundaryUnixSeconds,
        System.Collections.Immutable.ImmutableArray<
            LineageArtifactEvidence> targets,
        long logicalExpiry,
        long requiredPlatformExpiry)
    {
        var intent = new LineageTransitionIntentV1(
            kind,
            priorHeadIdentity,
            selected.Epoch,
            transitionEvidenceIdentity,
            expiryBoundaryUnixSeconds,
            LineageTestData.Reviewed(),
            LineageCryptography.InventoryDigest(targets),
            targets,
            kind == LineageTransitionIntentKind.Reset
                ? "workflow-run-42"
                : null,
            kind == LineageTransitionIntentKind.Reset ? 1 : null);
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            intent,
            out var payload));
        var objectClass = LineageTransitionIntentCodec.ObjectClass(kind);
        var name = await ResolveNameAsync(lease, objectClass);
        var draft = new StateControlHeaderDraft(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            objectClass,
            selected.LineageHeadIdentity,
            SuccessorIdentity: null,
            "workflow-run-42",
            ProducingRunAttempt: 1,
            LineageTestData.Now,
            logicalExpiry,
            requiredPlatformExpiry);
        try
        {
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name,
                draft,
                payload,
                out var envelope,
                out _,
                out var code), code);
            try
            {
                var uploaded = await new ScopedStateUploadProtocol(store)
                    .UploadAndReadBackAsync(
                        name,
                        envelope,
                        requiredPlatformExpiry,
                        CancellationToken.None);
                Assert.True(uploaded.Succeeded, uploaded.Code);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task UploadResetIntentAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        string priorHeadIdentity,
        long logicalExpiry,
        long requiredPlatformExpiry)
    {
        var targets = System.Collections.Immutable.ImmutableArray<
            LineageArtifactEvidence>.Empty;
        var intent = new LineageTransitionIntentV1(
            LineageTransitionIntentKind.Reset,
            priorHeadIdentity,
            selected.Epoch,
            new string('e', 64),
            ExpiryBoundaryUnixSeconds: null,
            LineageTestData.Reviewed(),
            LineageCryptography.InventoryDigest(targets),
            targets,
            "workflow-run-42",
            ResetAuthorityRunAttempt: 1);
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            intent,
            out var payload));
        var name = await ResolveNameAsync(lease, StateObjectClass.Reset);
        var draft = new StateControlHeaderDraft(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            StateObjectClass.Reset,
            selected.LineageHeadIdentity,
            SuccessorIdentity: null,
            "workflow-run-42",
            ProducingRunAttempt: 1,
            LineageTestData.Now,
            logicalExpiry,
            requiredPlatformExpiry);
        try
        {
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name,
                draft,
                payload,
                out var envelope,
                out _,
                out var code), code);
            try
            {
                var uploaded = await new ScopedStateUploadProtocol(store)
                    .UploadAndReadBackAsync(
                        name,
                        envelope,
                        requiredPlatformExpiry,
                        CancellationToken.None);
                Assert.True(uploaded.Succeeded, uploaded.Code);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task UploadEquivalentUnderRetainedInitialHeadAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        long producingRunAttempt)
    {
        var head = new LineageHeadV1(
            LineageTransitionKind.Initial,
            Ordinal: 0,
            LineageTestData.Reviewed(),
            PreviousEpoch: null,
            PreviousHeadIdentity: null,
            TransitionEvidenceIdentity: null,
            ExpiryBoundaryUnixSeconds: null,
            PhysicalPredecessors: [],
            PhysicalSuperseded: [],
            Superseded: [],
            CompletedCleanup: []);
        Assert.True(LineageHeadCodec.TryEncode(head, out var payload));
        var name = await ResolveNameAsync(
            lease,
            StateObjectClass.LineageHead);
        var requiredExpiry = LineageTestData.Now +
            StateRetentionRequirements.ScopedPlatformRequestSeconds;
        var draft = new StateControlHeaderDraft(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            StateObjectClass.LineageHead,
            PredecessorIdentity: null,
            SuccessorIdentity: null,
            "under-retained-copy",
            producingRunAttempt,
            LineageTestData.Now,
            LineageTestData.LogicalExpiry,
            requiredExpiry);
        try
        {
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name,
                draft,
                payload,
                out var envelope,
                out var header,
                out var code), code);
            Assert.Equal(selected.LineageHeadIdentity,
                header!.ObjectIdentity);
            try
            {
                var upload = await new ProbedLineageStore(store)
                {
                    UploadRequiredExpiryTransform = required =>
                        required - 3_601,
                }.UploadImmutableAsync(
                    new OpaqueStoreUploadRequest(
                        name,
                        new OpaqueStoreCorrelationId(
                            LineageCryptography.CorrelationId(envelope)),
                        envelope,
                        new OpaqueStoreEncryptedObjectDigest(
                            OpaqueStoreHash.Sha256(envelope)),
                        requiredExpiry),
                    CancellationToken.None);
                Assert.True(upload.Succeeded);
                Assert.NotNull(upload.Metadata);
                Assert.True(upload.Metadata!.ExpiresAtUnixSeconds <
                    requiredExpiry);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task UploadForeignTransitionObjectAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        byte payloadMarker)
    {
        Assert.True(LineageBaseScopeCodec.TryDigest(
            LineageTestData.Scope(),
            out var baseScopeDigest));
        var name = await ResolveNameAsync(lease, StateObjectClass.Reset);
        var draft = new StateControlHeaderDraft(
            baseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            StateObjectClass.Reset,
            selected.LineageHeadIdentity,
            SuccessorIdentity: null,
            "foreign-run",
            ProducingRunAttempt: payloadMarker,
            LineageTestData.Now,
            LineageTestData.LogicalExpiry,
            LineageTestData.Now + 8 * 24 * 60 * 60);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            lease.Context,
            lease.Access,
            name,
            draft,
            [payloadMarker],
            out var envelope,
            out _,
            out var code), code);
        try
        {
            var uploaded = await new ScopedStateUploadProtocol(store)
                .UploadAndReadBackAsync(
                    name,
                    envelope,
                    draft.RequiredPlatformExpiresAtUnixSeconds,
                    CancellationToken.None);
            Assert.True(uploaded.Succeeded, uploaded.Code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static async Task<OpaqueStoreName> ResolveNameAsync(
        LineageTestData.ContextLease lease,
        StateObjectClass objectClass)
    {
        await Task.Yield();
        Assert.True(LineageBaseScopeCodec.TryEncode(
            LineageTestData.Scope(),
            out var canonical));
        try
        {
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(objectClass),
                canonical,
                out var name));
            return name!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-lineage-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProbedLineageStore(IRestrictedStateStore inner)
        : IRestrictedStateStore
    {
        internal int IncompleteAtListCall { get; set; }
        internal bool ThrowOnFirstDelete { get; set; }
        internal int ThrowOnDeleteCall { get; set; }
        internal bool ReturnDeleteOutcomeUnknownOnce { get; set; }
        internal bool ReturnDeleteOutcomeUnknownAlways { get; set; }
        internal int StaleLineageAfterUploadCall { get; set; } = 2;
        internal Func<Task>? AfterFirstSuccessfulDelete { get; set; }
        internal Func<int, Task>? AfterUpload { get; set; }
        internal Func<long, long>? UploadRequiredExpiryTransform
        { get; set; }
        internal OpaqueStoreObjectReference? StaleLineageReference
        { get; set; }
        internal Func<
            OpaqueStoreObjectMetadata,
            OpaqueStoreObjectMetadata>? UploadMetadataTransform
        { get; set; }
        internal Func<
            OpaqueStoreObjectMetadata,
            OpaqueStoreObjectMetadata>? ReadMetadataTransform
        { get; set; }
        internal int UploadCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal int ReadBackCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal bool StaleLineageListInjected { get; private set; }
        internal List<OpaqueStoreName> UploadedNames { get; } = [];

        public async Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            if (ListCalls == IncompleteAtListCall)
            {
                return OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Incomplete);
            }

            var result = await inner.ListExactAsync(
                request,
                cancellationToken);
            if (!StaleLineageListInjected &&
                UploadCalls >= StaleLineageAfterUploadCall &&
                StaleLineageReference is not null &&
                request.Name == StaleLineageReference.Name &&
                result.Succeeded &&
                result.Objects.Length > 1 &&
                result.Objects.Contains(StaleLineageReference))
            {
                StaleLineageListInjected = true;
                return result with { Objects = [StaleLineageReference] };
            }

            return result;
        }

        public async Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadMetadataAsync(
                request,
                cancellationToken);
            return result.Metadata is not null &&
                ReadMetadataTransform is not null
                ? result with
                {
                    Metadata = ReadMetadataTransform(result.Metadata),
                }
                : result;
        }

        public async Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken)
        {
            if (ReadMetadataTransform is null)
            {
                return await inner.DownloadAsync(request, cancellationToken);
            }

            var actual = await inner.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(request.Expected.Reference),
                cancellationToken);
            if (!actual.Succeeded || actual.Metadata is null)
            {
                return OpaqueStoreDownloadResult.Fail(actual.Failure);
            }

            var downloaded = await inner.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    actual.Metadata,
                    request.MaximumBytes),
                cancellationToken);
            return downloaded.Succeeded
                ? downloaded with { Metadata = request.Expected }
                : downloaded;
        }

        public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            UploadCalls++;
            UploadedNames.Add(request.Name);
            var transformed = UploadRequiredExpiryTransform is null
                ? request
                : request with
                {
                    MinimumExpiresAtUnixSeconds =
                        UploadRequiredExpiryTransform(
                            request.MinimumExpiresAtUnixSeconds),
                };
            var result = await inner.UploadImmutableAsync(
                transformed,
                cancellationToken);
            if (AfterUpload is not null)
            {
                await AfterUpload(UploadCalls);
            }

            return result.Metadata is not null &&
                UploadMetadataTransform is not null
                ? result with
                {
                    Metadata = UploadMetadataTransform(result.Metadata),
                }
                : result;
        }

        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            ReadBackCalls++;
            return inner.ReadBackExactAsync(request, cancellationToken);
        }

        public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (DeleteCalls == ThrowOnDeleteCall)
            {
                throw new OperationCanceledException(
                    "simulated process termination during delete");
            }

            if (ThrowOnFirstDelete)
            {
                ThrowOnFirstDelete = false;
                throw new OperationCanceledException(
                    "simulated process termination before delete");
            }

            if (ReturnDeleteOutcomeUnknownOnce)
            {
                ReturnDeleteOutcomeUnknownOnce = false;
                return OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown);
            }

            if (ReturnDeleteOutcomeUnknownAlways)
            {
                return OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown);
            }

            var result = await inner.DeleteExactAsync(
                request,
                cancellationToken);
            if (DeleteCalls == 1 && AfterFirstSuccessfulDelete is not null)
            {
                await AfterFirstSuccessfulDelete();
            }

            return result;
        }
    }
}
