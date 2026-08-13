using System.Collections.Immutable;
using System.Reflection;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class RetainedStateTransactionContractTests
{
    [Fact]
    public void CandidateRetentionUsesExactUnifiedBoundaries()
    {
        const long now = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryCandidate(
            now,
            out var logical,
            out var platform));

        Assert.Equal(
            now + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            now + StateRetentionRequirements.ScopedPlatformRequestSeconds,
            platform);
        Assert.True(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds,
            now));
        Assert.False(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds - 1,
            now));
    }

    [Fact]
    public void AcceptanceReceiptRequiresTwoLogicalWindows()
    {
        const long acceptedAt = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryAcceptance(
            acceptedAt,
            out var logical,
            out var platform));

        Assert.Equal(
            acceptedAt + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            acceptedAt +
                2 * StateRetentionRequirements.LogicalWindowSeconds,
            platform);
    }

    [Fact]
    public void RetentionOverflowFailsClosed()
    {
        Assert.False(RetainedStateRetention.TryCandidate(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryAcceptance(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryOpaque(
            RestrictedStateFormat.MaximumUnixSeconds,
            RestrictedStateFormat.MaximumUnixSeconds,
            out _));
    }

    [Fact]
    public void CleanupRecordIsCanonicalAndOperationBound()
    {
        var first = Metadata("candidate-name", "2");
        var second = Metadata("acceptance-name", "1");
        Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64),
            [first, second],
            out var value));
        Assert.NotNull(value);
        Assert.Equal(second, value!.Targets[0]);
        Assert.Equal(first, value.Targets[1]);
        Assert.True(RetainedStateCleanupRecordCodec.TryEncode(
            value,
            out var bytes));
        Assert.True(RetainedStateCleanupRecordCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(value.TerminalAcceptanceIdentity,
            decoded!.TerminalAcceptanceIdentity);
        Assert.Equal(value.BaseScopeDigest, decoded.BaseScopeDigest);
        Assert.Equal(value.Epoch, decoded.Epoch);
        Assert.Equal(value.SessionId, decoded.SessionId);
        Assert.Equal(value.PreCleanupInventoryDigest,
            decoded.PreCleanupInventoryDigest);
        Assert.Equal(value.OperationIdentity, decoded.OperationIdentity);
        Assert.True(value.Targets.SequenceEqual(decoded.Targets));

        var tampered = bytes.ToArray();
        tampered[^1] ^= 1;
        Assert.False(RetainedStateCleanupRecordCodec.TryDecode(
            tampered,
            out _));
    }

    [Fact]
    public void OpaqueWriteAnchorIsCanonicalTamperBoundAndExactlyBounded()
    {
        var envelope = Enumerable.Range(0, 257)
            .Select(index => (byte)index)
            .ToArray();
        var value = Anchor(envelope);
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryEncode(
            value,
            out var bytes));
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(value.CandidateObjectIdentity,
            decoded!.CandidateObjectIdentity);
        Assert.Equal(value.OperationIdentity, decoded.OperationIdentity);
        Assert.Equal(value.ObjectClass, decoded.ObjectClass);
        Assert.Equal(value.PredecessorIdentity,
            decoded.PredecessorIdentity);
        Assert.Equal(value.SuccessorIdentity, decoded.SuccessorIdentity);
        Assert.Equal(value.SemanticRequiredExpiresAtUnixSeconds,
            decoded.SemanticRequiredExpiresAtUnixSeconds);
        Assert.Equal(value.RequiredPlatformExpiresAtUnixSeconds,
            decoded.RequiredPlatformExpiresAtUnixSeconds);
        Assert.Equal(value.ProducingRunIdentity,
            decoded.ProducingRunIdentity);
        Assert.Equal(value.ProducingRunAttempt,
            decoded.ProducingRunAttempt);
        Assert.Equal(value.TargetName, decoded.TargetName);
        Assert.Equal(value.TargetObjectIdentity,
            decoded.TargetObjectIdentity);
        Assert.True(value.TargetEnvelope.AsSpan().SequenceEqual(
            decoded.TargetEnvelope.AsSpan()));
        Assert.Equal(value.TargetEnvelopeSha256,
            decoded.TargetEnvelopeSha256);
        Assert.Equal(value.DispatchPhase, decoded.DispatchPhase);
        Assert.Equal(value.TargetPayloadSha256,
            decoded.TargetPayloadSha256);

        var tampered = bytes.ToArray();
        var envelopeOffset = tampered.AsSpan().IndexOf(envelope);
        Assert.True(envelopeOffset >= 0);
        tampered[envelopeOffset] ^= 1;
        Assert.False(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            tampered,
            out _));

        var low = 1;
        var high = LineageFormat.MaximumEnvelopeBytes;
        var maximumEnvelopeBytes = 0;
        byte[] maximumEncoding = [];
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (RetainedStateOpaqueWriteAnchorCodec.TryEncode(
                    Anchor(new byte[middle]),
                    out var encoded))
            {
                maximumEnvelopeBytes = middle;
                maximumEncoding = encoded;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        Assert.Equal(LineageFormat.MaximumPayloadBytes,
            maximumEncoding.Length);
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryDecode(
            maximumEncoding,
            out _));
        Assert.False(RetainedStateOpaqueWriteAnchorCodec.TryEncode(
            Anchor(new byte[maximumEnvelopeBytes + 1]),
            out _));
    }

    [Fact]
    public void PublicationCapabilityCarriesExactOutcomeAndRendering()
    {
        var outcome = R4PublicationTestData.Outcome(summary: "exact A");
        Assert.True(R4PreparedPublication.TryCreate(
            outcome,
            R4PublicationTestData.Scope,
            out var prepared));
        Assert.NotNull(prepared);
        Assert.True(prepared!.TryProject(
            out var projectedOutcome,
            out var rendered,
            out var scope));
        Assert.Same(outcome, projectedOutcome);
        Assert.Equal(R4PublicationTestData.Scope, scope);
        Assert.Contains("exact A", rendered!.Comment, StringComparison.Ordinal);
        Assert.Equal("[PRIVATE]", prepared.ToString());
    }

    [Fact]
    public void RetainedStateCapabilitiesDoNotExposeOrSelfIssueAuthority()
    {
        var capabilities = new[]
        {
            typeof(RetainedStatePreparedCandidate),
            typeof(RetainedStatePersistedCandidate),
            typeof(RetainedStateOwnership),
            typeof(RetainedStateOpaqueWriteAttempt),
            typeof(RetainedStateOpaqueRecord),
            typeof(RetainedStateOpaquePayloadExtraction),
            typeof(RetainedStateAcceptancePreparation),
            typeof(RetainedStateAcceptanceRecoveryDurability),
            typeof(RetainedStateAcceptanceEvidence),
            typeof(RetainedStateAcceptanceAttempt),
            typeof(RetainedStatePredecessorCopyAttempt),
            typeof(RetainedStatePendingCandidateEvidence),
            typeof(RetainedStateP5CleanupAuthorization),
            typeof(VerifiedRetainedStateAcceptance),
            typeof(RetainedStateCleanupAuthorization),
            typeof(RetainedStateAuthorityLease),
            typeof(RetainedStateObservation),
        };
        foreach (var capability in capabilities)
        {
            Assert.DoesNotContain(
                capability.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                property => property.PropertyType ==
                    typeof(RetainedStateTransactionAuthority));
        }

        var recoveryMethods = new[]
        {
            typeof(RestrictedStateService).GetMethod(
                "BindRetainedStateAcceptanceRecoveryAsync",
                BindingFlags.Static | BindingFlags.NonPublic),
            typeof(RestrictedStateService).GetMethod(
                "RecoverRetainedStateAcceptancePreparationAsync",
                BindingFlags.Static | BindingFlags.NonPublic),
        };
        Assert.All(recoveryMethods, method =>
        {
            Assert.NotNull(method);
            Assert.Contains(
                method!.GetParameters(),
                parameter => parameter.ParameterType ==
                    typeof(RetainedStateOpaquePayloadExtraction));
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType ==
                    typeof(ImmutableArray<byte>));
        });

        var issuerFactories = capabilities
            .SelectMany(type => type.GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic))
            .Where(method => method.Name == "Create")
            .ToArray();
        Assert.NotEmpty(issuerFactories);
        Assert.All(issuerFactories, method => Assert.Equal(
            typeof(object),
            method.GetParameters()[0].ParameterType));

        var serviceConstructor = Assert.Single(
            typeof(RetainedStateTransactionService).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        var exception = Assert.Throws<TargetInvocationException>(() =>
            serviceConstructor.Invoke([new object()]));
        Assert.IsType<ArgumentException>(exception.InnerException);

        var persistenceConstructor = Assert.Single(
            typeof(RetainedStatePersistence).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(
            typeof(object),
            persistenceConstructor.GetParameters()[0].ParameterType);
        var authorityFactory = Assert.Single(
            typeof(RetainedStateTransactionAuthority).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "TryCreate");
        Assert.Equal(
            typeof(object),
            authorityFactory.GetParameters()[0].ParameterType);
    }

    private static OpaqueStoreObjectMetadata Metadata(
        string name,
        string id) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(name),
                new OpaqueStoreObjectId(id)),
            new OpaqueStoreProducingRun("900", 2),
            new OpaqueStoreArchiveDigest(new string('a', 64)),
            new OpaqueStoreEncryptedObjectDigest(new string('b', 64)),
            1_800_000_000,
            100);

    private static RetainedStateOpaqueWriteAnchor Anchor(byte[] envelope) =>
        new(
            new string('a', 64),
            new string('b', 64),
            StateObjectClass.PublicationIntent,
            new string('a', 64),
            new string('c', 64),
            1_700_000_000,
            1_800_000_000,
            "900",
            2,
            new OpaqueStoreName("target"),
            new string('d', 64),
            ImmutableArray.CreateRange(envelope),
            OpaqueStoreHash.Sha256(envelope),
            RetainedStateOpaqueWriteAnchorPhase.PreparedBeforeTargetDispatch,
            new string('e', 64));
}
