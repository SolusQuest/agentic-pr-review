using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageContractAndCodecTests
{
    [Fact]
    public void ObjectClassRegistryIsClosedAndExact()
    {
        Assert.Equal(
        [
            "locator_root",
            "lineage_head",
            "candidate",
            "publication_intent",
            "acceptance",
            "publication_failure",
            "abandonment",
            "reset",
            "expiry_transition",
            "cleanup",
        ],
            StateObjectClasses.All
                .Select(StateObjectClasses.ToWireName)
                .ToArray());
        Assert.Equal(9, StateObjectClasses.Scoped.Length);
        Assert.DoesNotContain(
            StateObjectClass.LocatorRoot,
            StateObjectClasses.Scoped);
    }

    [Fact]
    public void BaseScopeIsCanonicalCompleteAndExcludesTransitionShas()
    {
        var scope = LineageTestData.Scope();
        Assert.True(LineageBaseScopeCodec.TryEncode(scope, out var first));
        Assert.True(LineageBaseScopeCodec.TryEncode(scope, out var second));
        Assert.Equal(first, second);
        Assert.True(LineageBaseScopeCodec.TryDigest(scope, out var digest));
        Assert.Equal(64, digest.Length);
        Assert.Equal(
            "f1d896572a6db4be6d6ed021cab4c1c2bcbae31080444e77b4aa1d4ec5a1a004",
            digest);

        var properties = typeof(LineageBaseScope).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("BaseSha", properties);
        Assert.DoesNotContain("HeadSha", properties);

        var variants = new[]
        {
            scope with { RepositoryId = "owner/other" },
            scope with { TrustedWorkflowIdentity = "workflow:other" },
            scope with { TrustedSourceIdentity = "source:other" },
            scope with { PullRequestNumber = 155 },
            scope with { Provider = "provider-2" },
            scope with { Model = "model-2" },
            scope with { Adapter = "adapter-2" },
            scope with { ConfigSha256 = new string('e', 64) },
            scope with { InstructionSha256 = new string('e', 64) },
            scope with { ToolsetSha256 = new string('e', 64) },
            scope with { LimitsSha256 = new string('e', 64) },
            scope with { PayloadBuildIdentity = "payload-v2" },
        };
        foreach (var variant in variants)
        {
            Assert.True(LineageBaseScopeCodec.TryDigest(
                variant,
                out var changed));
            Assert.NotEqual(digest, changed);
        }

        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);
    }

    [Fact]
    public void EpochAndSessionAreDeterministicAndTransitionBound()
    {
        using var lease = LineageTestData.Context();
        Assert.True(LineageBaseScopeCodec.TryDigest(
            LineageTestData.Scope(),
            out var baseScopeDigest));
        Assert.True(LineageCryptography.TryDeriveInitialEpoch(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            out var epoch));
        Assert.True(LineageCryptography.TryDeriveInitialEpoch(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            out var retry));
        Assert.Equal(epoch, retry);
        Assert.True(LineageCryptography.TryDeriveSessionId(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            epoch,
            out var session));
        Assert.Matches("^[a-f0-9]{64}$", session);
        Assert.Equal(
            "b07333646ae2dcb2dc805c6a2a409a7554e9cb9905b7d59f23327c4d48d46c4a",
            epoch);
        Assert.Equal(
            "5013a5c9374f190f539b92e302bfdcd0a73a9f50b8b9d48f11785430333400c6",
            session);

        Assert.True(LineageBaseScopeCodec.TryDigest(
            LineageTestData.Scope() with { ConfigSha256 = new string('e', 64) },
            out var changedScopeDigest));
        Assert.True(LineageCryptography.TryDeriveInitialEpoch(
            lease.Context,
            lease.Access,
            changedScopeDigest,
            out var changedScopeEpoch));
        Assert.NotEqual(epoch, changedScopeEpoch);

        Assert.True(LineageCryptography.TryDeriveResetEpoch(
            lease.Context,
            lease.Access,
            baseScopeDigest,
            new string('3', 64),
            new string('4', 64),
            "workflow-run-42",
            1,
            out var reset));
        Assert.NotEqual(epoch, reset);
    }

    [Fact]
    public void EnvelopeAuthenticatesNameAndKeepsLogicalIdentityAcrossRefresh()
    {
        using var lease = LineageTestData.Context();
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
            out var session));
        var name = new OpaqueStoreName("apr-state-test-envelope");
        var payload = new byte[] { 0, 1, 2, 3, 255 };
        var draft = new StateControlHeaderDraft(
            baseScopeDigest,
            epoch,
            session,
            StateObjectClass.Cleanup,
            PredecessorIdentity: null,
            SuccessorIdentity: null,
            "run-one",
            ProducingRunAttempt: 1,
            LineageTestData.Now,
            LineageTestData.LogicalExpiry,
            LineageTestData.Now + 8 * 24 * 60 * 60);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            lease.Context,
            lease.Access,
            name,
            draft,
            payload,
            out var first,
            out var firstHeader,
            out var code), code);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            lease.Context,
            lease.Access,
            name,
            draft with
            {
                ProducingRunIdentity = "run-two",
                ProducingRunAttempt = 2,
                CreatedAtUnixSeconds = LineageTestData.Now + 1,
                RequiredPlatformExpiresAtUnixSeconds =
                    LineageTestData.Now + 9 * 24 * 60 * 60,
            },
            payload,
            out var refreshed,
            out var refreshedHeader,
            out code), code);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(
            firstHeader!.ObjectIdentity,
            refreshedHeader!.ObjectIdentity);

        Assert.True(StateControlEnvelopeV1Codec.TryDecrypt(
            lease.Context,
            lease.Access,
            name,
            first,
            out var opened,
            out var openedPayload,
            out code), code);
        Assert.Equal(firstHeader, opened);
        Assert.Equal(payload, openedPayload);

        Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(
            lease.Context,
            lease.Access,
            new OpaqueStoreName("apr-state-other-name"),
            first,
            out _,
            out var rejectedPayload,
            out code));
        Assert.Equal(LineageCodes.AuthenticationFailed, code);
        Assert.Empty(rejectedPayload);

        var tampered = first.ToArray();
        tampered[^(LineageFormat.TagBytes - 2)] ^= 0x40;
        Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(
            lease.Context,
            lease.Access,
            name,
            tampered,
            out _,
            out rejectedPayload,
            out code));
        Assert.Equal(LineageCodes.AuthenticationFailed, code);

        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(refreshed);
        CryptographicOperations.ZeroMemory(openedPayload);
        CryptographicOperations.ZeroMemory(tampered);
    }

    [Fact]
    public void HeadAndIntentCodecsAreBoundedClosedAndClassOwned()
    {
        var evidence = new LineageArtifactEvidence(
            "apr-state-name",
            "object-1",
            "transport-run",
            1,
            new string('a', 64),
            new string('b', 64),
            LineageTestData.SentinelExpiry,
            Size: 128);
        var head = new LineageHeadV1(
            LineageTransitionKind.Reset,
            Ordinal: 1,
            LineageTestData.Reviewed(),
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            ExpiryBoundaryUnixSeconds: null,
            [evidence],
            [],
            [evidence with { ObjectId = "object-2" }],
            [],
            ResetAuthorityRunIdentity: "reset-run",
            ResetAuthorityRunAttempt: 2);
        Assert.True(LineageHeadCodec.TryEncode(head, out var encoded));
        Assert.True(LineageHeadCodec.TryDecode(encoded, out var decoded));
        Assert.True(LineageHeadCodec.Equivalent(head, decoded!));

        var targets = ImmutableArray.Create(evidence);
        var intent = new LineageTransitionIntentV1(
            LineageTransitionIntentKind.Reset,
            new string('4', 64),
            new string('5', 64),
            new string('6', 64),
            ExpiryBoundaryUnixSeconds: null,
            LineageTestData.Reviewed(),
            LineageCryptography.InventoryDigest(targets),
            targets,
            ResetAuthorityRunIdentity: "reset-run",
            ResetAuthorityRunAttempt: 2);
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            intent,
            out var intentBytes));
        Assert.True(LineageTransitionIntentCodec.TryDecode(
            StateObjectClass.Reset,
            intentBytes,
            out var restored));
        Assert.True(LineageTransitionIntentCodec.TryEncode(
            restored,
            out var restoredIntentBytes));
        Assert.Equal(intentBytes, restoredIntentBytes);
        Assert.False(LineageTransitionIntentCodec.TryDecode(
            StateObjectClass.Cleanup,
            intentBytes,
            out _));

        CryptographicOperations.ZeroMemory(encoded);
        CryptographicOperations.ZeroMemory(intentBytes);
        CryptographicOperations.ZeroMemory(restoredIntentBytes);
    }
}
