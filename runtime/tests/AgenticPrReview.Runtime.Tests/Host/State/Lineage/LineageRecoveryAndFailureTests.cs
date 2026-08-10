using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
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
    public async Task FreshProcessResumesExactEighthIntentWithoutDuplicateUpload()
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

            for (var index = 0; index < 7; index++)
            {
                await UploadHistoricalResetIntentAsync(
                    inner,
                    lease,
                    selected,
                    index);
            }

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
            Assert.Equal(8, afterCrash.Objects.Length);

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
            // upload a duplicate transition intent into the full family.
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

            await UploadHistoricalResetIntentAsync(
                inner,
                lease,
                selected,
                index: 0);
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
            Assert.Equal(LineageCodes.RetentionFailed, rejected.Code);

            var lowerRequest = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = LineageTestData.Now + 1,
            };
            var retried = await service.ResolveAsync(
                lease.Context,
                lowerRequest,
                CancellationToken.None);
            Assert.False(retried.Succeeded);
            Assert.Equal(LineageCodes.RetentionFailed, retried.Code);
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
            Assert.Equal(LineageCodes.CleanupFailed, result.Code);
            Assert.Equal(1, probe.DeleteCalls);
            Assert.Single(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    private static async Task UploadHistoricalResetIntentAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        int index)
    {
        var targets = System.Collections.Immutable.ImmutableArray<
            LineageArtifactEvidence>.Empty;
        var intent = new LineageTransitionIntentV1(
            LineageTransitionIntentKind.Reset,
            Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"prior-{index}"))),
            selected.Epoch,
            Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"request-{index}"))),
            ExpiryBoundaryUnixSeconds: null,
            LineageTestData.Reviewed(),
            LineageCryptography.InventoryDigest(targets),
            targets);
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            intent,
            out var payload));
        var name = await ResolveNameAsync(lease, StateObjectClass.Reset);
        var draft = new StateControlHeaderDraft(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            StateObjectClass.Reset,
            intent.PriorHeadIdentity,
            SuccessorIdentity: null,
            $"historical-{index}",
            ProducingRunAttempt: 1,
            LineageTestData.Now,
            LineageTestData.LogicalExpiry,
            LineageTestData.Now + 8 * 24 * 60 * 60);
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
                        draft.RequiredPlatformExpiresAtUnixSeconds,
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
        internal bool ReturnDeleteOutcomeUnknownOnce { get; set; }
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
                UploadCalls >= 2 &&
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

        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (ThrowOnFirstDelete)
            {
                ThrowOnFirstDelete = false;
                throw new OperationCanceledException(
                    "simulated process termination before delete");
            }

            if (ReturnDeleteOutcomeUnknownOnce)
            {
                ReturnDeleteOutcomeUnknownOnce = false;
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown));
            }

            return inner.DeleteExactAsync(request, cancellationToken);
        }
    }
}
