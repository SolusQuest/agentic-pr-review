using AgenticPrReview.Runtime.Host.Publishing.Recovery;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;

public sealed class PublicationRecoveryClassifierTests
{
    [Fact]
    public void D10MatrixMapsToClosedActions()
    {
        AssertAction(
            Empty() with { CandidateCount = 0 },
            PublicationRecoveryAction.NoPendingWork);
        AssertAction(
            Current() with
            {
                AcceptanceCount = 1,
                StickyReadbackCount = 1,
                RecoveryCount = 1,
                Marker = PublicationMarkerObservation.Exact,
                AcceptanceMatchesRecovery = true,
            },
            PublicationRecoveryAction.ReturnCommitted);
        AssertAction(
            Current() with { Marker = PublicationMarkerObservation.Exact },
            PublicationRecoveryAction.CompleteAcceptance);
        AssertAction(
            Current(),
            PublicationRecoveryAction.ResumeBeforeIntent);
        AssertAction(
            Current() with { IntentCount = 1 },
            PublicationRecoveryAction.StickyOutcomeUnknown);
        AssertAction(
            Current() with
            {
                IntentCount = 1,
                FailureCount = 1,
                HasExactKnownNotWrittenFailure = true,
            },
            PublicationRecoveryAction.ResumeKnownNotWritten);
        AssertAction(
            Current() with
            {
                FailureCount = 1,
                HasOutcomeUnknownFailure = true,
            },
            PublicationRecoveryAction.StickyOutcomeUnknown);
        AssertAction(
            Current() with
            {
                CandidateMatchesCurrentHead = false,
                Marker = PublicationMarkerObservation.Absent,
            },
            PublicationRecoveryAction.AbandonStaleCandidate);
    }

    [Theory]
    [InlineData(CrashCut.AfterCandidateUpload,
        (int)PublicationRecoveryAction.ResumeBeforeIntent)]
    [InlineData(CrashCut.AfterIntentUpload,
        (int)PublicationRecoveryAction.StickyOutcomeUnknown)]
    [InlineData(CrashCut.AfterStickyRequest,
        (int)PublicationRecoveryAction.StickyOutcomeUnknown)]
    [InlineData(CrashCut.AfterStickyReadback,
        (int)PublicationRecoveryAction.CompleteAcceptance)]
    [InlineData(CrashCut.AfterAcceptanceUpload,
        (int)PublicationRecoveryAction.ReturnCommitted)]
    [InlineData(CrashCut.AfterAcceptanceReadback,
        (int)PublicationRecoveryAction.ReturnCommitted)]
    [InlineData(CrashCut.AfterCleanup,
        (int)PublicationRecoveryAction.NoPendingWork)]
    public void EveryExternalCrashCutConvergesTruthfully(
        CrashCut cut,
        int expected)
    {
        AssertAction(At(cut), (PublicationRecoveryAction)expected);
    }

    [Fact]
    public void AmbiguousIncompleteOrLostOwnershipAlwaysFailsClosed()
    {
        AssertConflict(Current() with { EnumerationComplete = false });
        AssertConflict(Current() with { OwnershipRetained = false });
        AssertConflict(Current() with { CandidateCount = 2 });
        AssertConflict(Current() with { IntentCount = 2 });
        AssertConflict(Current() with
        {
            Marker = PublicationMarkerObservation.Ambiguous,
        });
        AssertConflict(Current() with
        {
            Anchors = PublicationRecoveryAnchorState.Unresolved,
        });
        AssertConflict(Current() with
        {
            CandidateMatchesCurrentHead = false,
            IntentCount = 1,
        });
        AssertConflict(Current() with
        {
            CandidateMatchesCurrentHead = false,
            Marker = PublicationMarkerObservation.Incomplete,
        });
    }

    [Fact]
    public void OnlyNoPendingWorkAllowsProviderAndOnlyKnownNotWrittenRetries()
    {
        var none = PublicationRecoveryClassifier.Classify(Empty());
        Assert.True(none.AllowsProvider);
        Assert.False(none.AllowsStickyWrite);

        var unknown = PublicationRecoveryClassifier.Classify(
            Current() with { IntentCount = 1 });
        Assert.False(unknown.AllowsProvider);
        Assert.False(unknown.AllowsStickyWrite);

        var retry = PublicationRecoveryClassifier.Classify(Current() with
        {
            FailureCount = 1,
            HasExactKnownNotWrittenFailure = true,
        });
        Assert.True(retry.AllowsStickyWrite);
        Assert.False(retry.AllowsProvider);
    }

    [Fact]
    public void TerminalRecoveryBecomesSupersededOnlyAfterDurableSuccessorProof()
    {
        var prior = Empty() with
        {
            RecoveryCount = 1,
            AcceptanceCount = 1,
            IsSupersededTerminalRecovery = true,
        };
        AssertConflict(prior);

        var superseded = PublicationRecoveryClassifier.Classify(prior with
        {
            HasDurableSuccessorRecovery = true,
        });
        Assert.Equal(
            PublicationRecoveryAction.NoPendingWork,
            superseded.Action);
        Assert.Equal(
            PublicationRecoveryLifecycleState.SupersededTerminalRecovery,
            superseded.Lifecycle);
        Assert.True(superseded.AllowsProvider);
        Assert.True(superseded.AllowsSupersededCleanup);

        var cleanupDebt = PublicationRecoveryClassifier.Classify(Empty() with
        {
            Anchors = PublicationRecoveryAnchorState.CleanupDebt,
        });
        Assert.Equal(
            PublicationRecoveryLifecycleState.CompletedCleanupDebt,
            cleanupDebt.Lifecycle);
        Assert.True(cleanupDebt.AllowsProvider);
    }

    private static PublicationRecoveryInventory At(CrashCut cut) => cut switch
    {
        CrashCut.AfterCandidateUpload => Current(),
        CrashCut.AfterIntentUpload or CrashCut.AfterStickyRequest =>
            Current() with { IntentCount = 1 },
        CrashCut.AfterStickyReadback => Current() with
        {
            IntentCount = 1,
            StickyReadbackCount = 1,
            RecoveryCount = 1,
            Marker = PublicationMarkerObservation.Exact,
        },
        CrashCut.AfterAcceptanceUpload or
            CrashCut.AfterAcceptanceReadback => Current() with
        {
            IntentCount = 1,
            StickyReadbackCount = 1,
            RecoveryCount = 1,
            AcceptanceCount = 1,
            AcceptanceMatchesRecovery = true,
            Marker = PublicationMarkerObservation.Exact,
        },
        CrashCut.AfterCleanup => Empty(),
        _ => throw new ArgumentOutOfRangeException(nameof(cut)),
    };

    private static PublicationRecoveryInventory Current() => Empty() with
    {
        CandidateCount = 1,
        CandidateMatchesCurrentHead = true,
        HasStoredValidatedPublication = true,
        RecordsMatchCandidate = true,
    };

    private static PublicationRecoveryInventory Empty() => new(
        EnumerationComplete: true,
        OwnershipRetained: true,
        CandidateCount: 0,
        CandidateMatchesCurrentHead: false,
        HasStoredValidatedPublication: false,
        IntentCount: 0,
        StickyReadbackCount: 0,
        FailureCount: 0,
        AbandonmentCount: 0,
        AcceptanceCount: 0,
        RecoveryCount: 0,
        RecordsMatchCandidate: true,
        AcceptanceMatchesRecovery: false,
        HasExactKnownNotWrittenFailure: false,
        HasOutcomeUnknownFailure: false,
        Marker: PublicationMarkerObservation.Absent,
        Anchors: PublicationRecoveryAnchorState.None);

    private static void AssertAction(
        PublicationRecoveryInventory inventory,
        PublicationRecoveryAction expected) =>
        Assert.Equal(
            expected,
            PublicationRecoveryClassifier.Classify(inventory).Action);

    private static void AssertConflict(
        PublicationRecoveryInventory inventory) =>
        AssertAction(inventory, PublicationRecoveryAction.Conflict);

    public enum CrashCut
    {
        AfterCandidateUpload,
        AfterIntentUpload,
        AfterStickyRequest,
        AfterStickyReadback,
        AfterAcceptanceUpload,
        AfterAcceptanceReadback,
        AfterCleanup,
    }
}
