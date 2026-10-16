using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.Tests.Agent;

public sealed class Cr1DevelopmentProfileTests
{
    // These are numeric witnesses, not invented package serialization results.
    private static readonly Cr1RetainedInventory FreshReservation = new(6, 20, 14, 139_264);

    [Fact]
    public void FreshAndRestoredWitnessesKeepSeparateCumulativeInventories()
    {
        Assert.Equal(Cr1DevelopmentRefusal.None, Admit(default, FreshReservation));
        var saved = new Cr1RetainedInventory(6, 20, 14, 49_152);
        var next = FreshReservation with { ProviderInputs = 13 };
        Assert.Equal(Cr1DevelopmentRefusal.None, Admit(saved, next));
        Assert.Equal(27, saved.ProviderInputs + next.ProviderInputs);
        Assert.Equal(40, saved.Records + next.Records);
        Assert.Equal(12, saved.Attempts + next.Attempts);
        Assert.Equal(188_416, saved.RetainedBytes + next.RetainedBytes);
        Assert.Equal(new Cr1RetainedInventory(6, 20, 14, 49_152), saved);
    }

    [Theory]
    [InlineData(58, 44, 18, 57_344, (int)Cr1DevelopmentRefusal.None)]
    [InlineData(59, 44, 18, 57_344, (int)Cr1DevelopmentRefusal.AttemptCapacityExceeded)]
    [InlineData(58, 45, 18, 57_344, (int)Cr1DevelopmentRefusal.RecordCapacityExceeded)]
    [InlineData(58, 44, 19, 57_344, (int)Cr1DevelopmentRefusal.ProviderInputCapacityExceeded)]
    [InlineData(58, 44, 18, 57_345, (int)Cr1DevelopmentRefusal.RetainedByteCapacityExceeded)]
    public void ExactAndOneOverBoundariesRefuseWithoutClamping(
        long attempts, long records, long inputs, long bytes, int expected) =>
        Assert.Equal((Cr1DevelopmentRefusal)expected, Admit(new(attempts, records, inputs, bytes), FreshReservation));

    [Fact]
    public void ANewReviewDoesNotRenewCumulativeAttemptOrInputCapacity()
    {
        Assert.Equal(Cr1DevelopmentRefusal.AttemptCapacityExceeded,
            Admit(new(64, 20, 14, 0), FreshReservation));
        Assert.Equal(Cr1DevelopmentRefusal.ProviderInputCapacityExceeded,
            Admit(new(6, 20, 32, 0), FreshReservation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(long.MaxValue)]
    public void UnsupportedCurrentCallRequestsAreRejected(long calls) =>
        Assert.Equal(Cr1DevelopmentRefusal.CurrentWorkExceeded,
            Cr1DevelopmentProfile.Admit(Cr1DevelopmentProfile.Id, calls, default,
                FreshReservation with { Attempts = calls }));

    [Fact]
    public void ConfirmedNoSendCannotMakeAnUnreservedAttemptDisappear() =>
        Assert.Equal(Cr1DevelopmentRefusal.CurrentWorkExceeded,
            Admit(default, FreshReservation with { Attempts = 5 }));

    [Fact]
    public void UnknownProfileAndNegativeInventoriesReject()
    {
        Assert.Equal(Cr1DevelopmentRefusal.UnknownProfile,
            Cr1DevelopmentProfile.Admit("current", 6, default, FreshReservation));
        foreach (var invalid in new Cr1RetainedInventory[]
        {
            new(-1, 0, 0, 0), new(0, -1, 0, 0), new(0, 0, -1, 0), new(0, 0, 0, -1),
        })
        {
            Assert.Equal(Cr1DevelopmentRefusal.InvalidInventory, Admit(invalid, FreshReservation));
            Assert.Equal(Cr1DevelopmentRefusal.InvalidInventory, Admit(default, invalid));
        }
    }

    [Fact]
    public void UntrustedLargeInventoriesCannotOverflowIntoAdmission()
    {
        Assert.Equal(Cr1DevelopmentRefusal.AttemptCapacityExceeded,
            Admit(new(long.MaxValue, 0, 0, 0), FreshReservation));
        Assert.Equal(Cr1DevelopmentRefusal.RecordCapacityExceeded,
            Admit(default, FreshReservation with { Records = long.MaxValue }));
        Assert.Equal(Cr1DevelopmentRefusal.ProviderInputCapacityExceeded,
            Admit(new(0, 0, long.MaxValue, 0), FreshReservation));
        Assert.Equal(Cr1DevelopmentRefusal.RetainedByteCapacityExceeded,
            Admit(default, FreshReservation with { RetainedBytes = long.MaxValue }));
    }

    [Fact]
    public void DevelopmentAdmissionLeavesProductionAndRetainedProfilesUnchanged()
    {
        var before = AgentLimits.Registry;
        Assert.Equal(Cr1DevelopmentRefusal.None, Admit(default, FreshReservation));
        Assert.Equal(before, AgentLimits.Registry);
        Assert.Equal(64, AgentLimits.ModelCalls);
        Assert.Equal(128, AgentLimits.ModelCallsCeiling);
        Assert.Equal(8192, AgentLimits.SessionRecords);
        Assert.Equal(16 * 1024 * 1024, AgentLimits.SessionPlaintextBytes);
        Assert.Equal(64, AgentLimits.RegistryFor(AgentLimitProfile.Output8192)[0].Value);
        Assert.Equal(64, AgentLimits.RegistryFor(AgentLimitProfile.Output65536)[0].Value);
    }

    private static Cr1DevelopmentRefusal Admit(Cr1RetainedInventory saved, Cr1RetainedInventory growth) =>
        Cr1DevelopmentProfile.Admit(Cr1DevelopmentProfile.Id, 6, saved, growth);
}
