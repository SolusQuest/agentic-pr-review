namespace AgenticPrReview.Runtime.Host.State;

internal sealed class RecordingStateReconciliationDiagnosticSink
    : IStateReconciliationDiagnosticSink
{
    private readonly List<StateReconciliationDiagnostic> diagnostics = [];

    internal IReadOnlyList<StateReconciliationDiagnostic> Diagnostics
    {
        get
        {
            lock (diagnostics) return diagnostics.ToArray();
        }
    }

    public void Write(StateReconciliationDiagnostic diagnostic)
    {
        lock (diagnostics) diagnostics.Add(diagnostic);
    }
}

public sealed class StateReconciliationDiagnosticFormatterTests
{
    [Fact]
    public void WritesTheCanonicalNodeCarrierVector()
    {
        var formatted = StateReconciliationDiagnosticFormatter.Format(new(
            StateReconciliationOwner.LineageHead,
            StateReconciliationOutcome.Committed,
            StateReconciliationExactReadBack.Matched,
            Observations: 3,
            StateReconciliationTerminal.TargetAbsent,
            ScheduleIndex: 2));

        Assert.Equal(
            "APR_R4_E2P_STATE_RECONCILIATION {\"owner\":\"lineage_head\"," +
            "\"outcome\":\"committed\",\"exact_readback\":\"matched\"," +
            "\"observations\":3,\"terminal\":\"target_absent\"," +
            "\"schedule_index\":2}",
            formatted);
    }

    [Fact]
    public void PreservesIncompleteWithoutProtectedIdentifiers()
    {
        var formatted = StateReconciliationDiagnosticFormatter.Format(new(
            StateReconciliationOwner.Candidate,
            StateReconciliationOutcome.OutcomeUnknown,
            StateReconciliationExactReadBack.Failed,
            Observations: 1,
            StateReconciliationTerminal.Incomplete,
            ScheduleIndex: 0));

        Assert.Contains("\"terminal\":\"incomplete\"", formatted,
            StringComparison.Ordinal);
        Assert.DoesNotContain("artifact", formatted,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("digest", formatted,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", formatted,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", formatted,
            StringComparison.OrdinalIgnoreCase);
    }
}
