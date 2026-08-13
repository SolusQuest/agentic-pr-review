using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;

public sealed class PublicationRecoveryCodecTests
{
    private const long Now = 1_800_000_000;

    [Fact]
    public void EveryRecordRoundTripsAndRejectsOneByteTampering()
    {
        var binding = Binding();
        Assert.True(PublicationIntentV1Codec.TryCreate(
            binding,
            Now,
            out var intent));
        AssertRoundTrip(
            intent!,
            PublicationIntentV1Codec.TryEncode,
            PublicationIntentV1Codec.TryDecode);

        var receipt = Receipt();
        Assert.True(StickyReadbackRecordV1Codec.TryCreate(
            binding,
            receipt,
            Now + 1,
            out var readback));
        AssertRoundTrip(
            readback!,
            StickyReadbackRecordV1Codec.TryEncode,
            StickyReadbackRecordV1Codec.TryDecode);
        Assert.True(readback!.TryRehydrate(out var rehydrated));
        Assert.Equal(receipt.CommentId, rehydrated!.CommentId);

        Assert.True(PublicationFailureV1Codec.TryCreate(
            binding,
            BoundedGitHubPublisherOutcome.KnownNotWritten,
            StickyPublicationReason.Deadline,
            Now + 2,
            out var failure));
        AssertRoundTrip(
            failure!,
            PublicationFailureV1Codec.TryEncode,
            PublicationFailureV1Codec.TryDecode);

        Assert.True(AbandonmentV1Codec.TryCreate(
            binding,
            Hex('9'),
            Now + 3,
            out var abandonment));
        AssertRoundTrip(
            abandonment!,
            AbandonmentV1Codec.TryEncode,
            AbandonmentV1Codec.TryDecode);

        var handoff = ImmutableArray.CreateRange(
            Enumerable.Range(0, 96).Select(static value => (byte)value));
        Assert.True(RecoveryRecordV1Codec.TryCreate(
            binding,
            readback.RecordIdentity,
            handoff,
            Now + 900,
            out var recovery));
        var recoveryRecord = Assert.IsType<RecoveryRecordV1>(recovery);
        Assert.True(RecoveryRecordV1Codec.TryEncode(
            recoveryRecord,
            out var recoveryBytes,
            out var handoffOffset,
            out var handoffLength));
        Assert.Equal(handoff.Length, handoffLength);
        Assert.True(recoveryBytes.AsSpan(handoffOffset, handoffLength)
            .SequenceEqual(handoff.AsSpan()));
        Assert.True(RecoveryRecordV1Codec.TryDecode(
            recoveryBytes,
            out var decodedRecovery,
            out var decodedOffset,
            out var decodedLength));
        var decoded = Assert.IsType<RecoveryRecordV1>(decodedRecovery);
        Assert.Equal(recoveryRecord.Binding, decoded.Binding);
        Assert.Equal(
            recoveryRecord.StickyReadbackRecordIdentity,
            decoded.StickyReadbackRecordIdentity);
        Assert.True(recoveryRecord.AcceptanceRecoveryHandoff.AsSpan()
            .SequenceEqual(
                decoded.AcceptanceRecoveryHandoff.AsSpan()));
        Assert.Equal(
            recoveryRecord.MinimumSemanticExpiresAtUnixSeconds,
            decoded.MinimumSemanticExpiresAtUnixSeconds);
        Assert.Equal(recoveryRecord.RecordIdentity, decoded.RecordIdentity);
        Assert.Equal(handoffOffset, decodedOffset);
        Assert.Equal(handoffLength, decodedLength);

        recoveryBytes[handoffOffset + 1] ^= 1;
        Assert.False(RecoveryRecordV1Codec.TryDecode(
            recoveryBytes,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void StickyReadbackRequiresAnExactP2Receipt()
    {
        var binding = Binding();
        var mismatched = binding with { BodySha256 = Hex('8') };
        Assert.False(StickyReadbackRecordV1Codec.TryCreate(
            mismatched,
            Receipt(),
            Now,
            out _));
        Assert.False(PublicationFailureV1Codec.TryCreate(
            binding,
            BoundedGitHubPublisherOutcome.WrittenAndReadBack,
            StickyPublicationReason.None,
            Now,
            out _));
    }

    [Fact]
    public void BindingFieldsParticipateInTheRecordIdentity()
    {
        Assert.True(PublicationIntentV1Codec.TryCreate(
            Binding(),
            Now,
            out var original));
        Assert.True(PublicationIntentV1Codec.TryCreate(
            Binding() with { CandidateObjectIdentity = Hex('8') },
            Now,
            out var changed));
        Assert.NotEqual(original!.RecordIdentity, changed!.RecordIdentity);
    }

    [Fact]
    public void RetentionUsesExactPreStickyAndMinimumEightDayRules()
    {
        Assert.True(PublicationRecoveryRetention.TryCompute(
            Now,
            Now + 604_800,
            out var semantic,
            out var requested));
        Assert.Equal(Now + 604_800 + 900, semantic);
        Assert.Equal(Now + 691_200, requested);
        Assert.True(PublicationRecoveryRetention.CoversReturnedExpiry(
            requested,
            requested));
        Assert.True(PublicationRecoveryRetention.CoversReturnedExpiry(
            requested + 1,
            requested));
        Assert.False(PublicationRecoveryRetention.CoversReturnedExpiry(
            requested - 1,
            requested));
    }

    [Fact]
    public void ClosedP2OutcomeMappingNeverInventsAReceiptOrRetry()
    {
        var written = PublicationTransportOutcomeMapper.Map(
            BoundedGitHubPublisherOutcome.WrittenAndReadBack,
            StickyPublicationReason.None,
            Receipt());
        Assert.Equal(
            PublicationTransportTransition.PersistStickyReadback,
            written.Transition);
        Assert.NotNull(written.Receipt);

        var retry = PublicationTransportOutcomeMapper.Map(
            BoundedGitHubPublisherOutcome.KnownNotWritten,
            StickyPublicationReason.Deadline,
            receipt: null);
        Assert.True(retry.AllowsRetry);

        foreach (var outcome in new[]
        {
            BoundedGitHubPublisherOutcome.CancelledBeforeSend,
            BoundedGitHubPublisherOutcome.OutcomeUnknown,
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
        })
        {
            var reason = outcome ==
                BoundedGitHubPublisherOutcome.CancelledBeforeSend
                ? StickyPublicationReason.Cancelled
                : StickyPublicationReason.ReconciliationIncomplete;
            var decision = PublicationTransportOutcomeMapper.Map(
                outcome,
                reason,
                receipt: null);
            Assert.False(decision.AllowsRetry);
            Assert.Null(decision.Receipt);
        }
    }

    private static void AssertRoundTrip<T>(
        T value,
        TryEncoder<T> encode,
        TryDecoder<T> decode) where T : class
    {
        Assert.True(encode(value, out var bytes));
        Assert.True(decode(bytes, out var decoded));
        Assert.Equal(value, decoded);
        bytes[^1] ^= 1;
        Assert.False(decode(bytes, out _));
    }

    private static PublicationRecoveryBindingV1 Binding() => new(
        Hex('1'),
        Hex('2'),
        Hex('3'),
        Hex('4'),
        Hex('5'),
        new string('a', 40),
        Hex('6'),
        Hex('7'));

    private static StickyCommentPublisher.StickyPublicationReceipt Receipt()
    {
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                repositoryId: 42,
                pullRequestNumber: 161,
                commentId: 99,
                commentUrl:
                    "https://github.com/SolusQuest/agentic-pr-review/" +
                    "pull/161#issuecomment-99",
                scopeSha256: Hex('6'),
                bodySha256: Hex('7'),
                headSha: new string('a', 40),
                out var receipt));
        return receipt!;
    }

    private static string Hex(char value) => new(value, 64);

    private delegate bool TryEncoder<T>(T value, out byte[] bytes);
    private delegate bool TryDecoder<T>(
        ReadOnlySpan<byte> bytes,
        out T? value);
}
