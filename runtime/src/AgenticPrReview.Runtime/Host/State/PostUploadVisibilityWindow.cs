using System.Globalization;

namespace AgenticPrReview.Runtime.Host.State;

internal enum StateReconciliationOwner
{
    LocatorRoot,
    LineageHead,
    LineageIntent,
    Candidate,
    PublicationIntent,
    Acceptance,
    PublicationFailure,
    Abandonment,
    Reset,
    ExpiryTransition,
    Cleanup,
}

internal enum StateReconciliationOutcome
{
    Committed,
    OutcomeUnknown,
    NotCommitted,
    ReconcileOnly,
}

internal enum StateReconciliationExactReadBack
{
    Matched,
    Failed,
    NotAvailable,
    NotApplicable,
}

internal enum StateReconciliationTerminal
{
    NotCommitted,
    TargetAbsent,
    Unavailable,
    Conflict,
    AuthenticationFailed,
    KeyUnavailable,
    RetentionFailed,
    CleanupFailed,
    Cancelled,
    Invalid,
}

internal sealed record StateReconciliationDiagnostic(
    StateReconciliationOwner Owner,
    StateReconciliationOutcome Outcome,
    StateReconciliationExactReadBack ExactReadBack,
    int Observations,
    StateReconciliationTerminal Terminal,
    int ScheduleIndex);

internal interface IStateReconciliationDiagnosticSink
{
    void Write(StateReconciliationDiagnostic diagnostic);
}

internal sealed class StateReconciliationStderrSink
    : IStateReconciliationDiagnosticSink
{
    internal static StateReconciliationStderrSink Instance { get; } = new();

    private StateReconciliationStderrSink()
    {
    }

    public void Write(StateReconciliationDiagnostic diagnostic) =>
        Console.Error.WriteLine(
            StateReconciliationDiagnosticFormatter.Format(diagnostic));
}

internal static class StateReconciliationDiagnosticFormatter
{
    internal const string Prefix = "APR_R4_E2P_STATE_RECONCILIATION ";

    internal static string Format(StateReconciliationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Observations is < 0 or > 32 ||
            diagnostic.ScheduleIndex is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnostic));
        }

        return Prefix + "{\"owner\":\"" + Owner(diagnostic.Owner) +
            "\",\"outcome\":\"" + Outcome(diagnostic.Outcome) +
            "\",\"exact_readback\":\"" +
            ExactReadBack(diagnostic.ExactReadBack) +
            "\",\"observations\":" +
            diagnostic.Observations.ToString(CultureInfo.InvariantCulture) +
            ",\"terminal\":\"" + Terminal(diagnostic.Terminal) +
            "\",\"schedule_index\":" +
            diagnostic.ScheduleIndex.ToString(CultureInfo.InvariantCulture) +
            "}";
    }

    private static string Owner(StateReconciliationOwner value) => value switch
    {
        StateReconciliationOwner.LocatorRoot => "locator_root",
        StateReconciliationOwner.LineageHead => "lineage_head",
        StateReconciliationOwner.LineageIntent => "lineage_intent",
        StateReconciliationOwner.Candidate => "candidate",
        StateReconciliationOwner.PublicationIntent => "publication_intent",
        StateReconciliationOwner.Acceptance => "acceptance",
        StateReconciliationOwner.PublicationFailure => "publication_failure",
        StateReconciliationOwner.Abandonment => "abandonment",
        StateReconciliationOwner.Reset => "reset",
        StateReconciliationOwner.ExpiryTransition => "expiry_transition",
        StateReconciliationOwner.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Outcome(StateReconciliationOutcome value) => value switch
    {
        StateReconciliationOutcome.Committed => "committed",
        StateReconciliationOutcome.OutcomeUnknown => "outcome_unknown",
        StateReconciliationOutcome.NotCommitted => "not_committed",
        StateReconciliationOutcome.ReconcileOnly => "reconcile_only",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ExactReadBack(
        StateReconciliationExactReadBack value) => value switch
    {
        StateReconciliationExactReadBack.Matched => "matched",
        StateReconciliationExactReadBack.Failed => "failed",
        StateReconciliationExactReadBack.NotAvailable => "not_available",
        StateReconciliationExactReadBack.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Terminal(StateReconciliationTerminal value) => value switch
    {
        StateReconciliationTerminal.NotCommitted => "not_committed",
        StateReconciliationTerminal.TargetAbsent => "target_absent",
        StateReconciliationTerminal.Unavailable => "unavailable",
        StateReconciliationTerminal.Conflict => "conflict",
        StateReconciliationTerminal.AuthenticationFailed => "authentication_failed",
        StateReconciliationTerminal.KeyUnavailable => "key_unavailable",
        StateReconciliationTerminal.RetentionFailed => "retention_failed",
        StateReconciliationTerminal.CleanupFailed => "cleanup_failed",
        StateReconciliationTerminal.Cancelled => "cancelled",
        StateReconciliationTerminal.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

internal sealed class PostUploadVisibilityWindow
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    private readonly TimeProvider timeProvider;
    private readonly IStateReconciliationDiagnosticSink? diagnosticSink;
    private readonly StateReconciliationOwner owner;
    private readonly StateReconciliationOutcome outcome;
    private readonly StateReconciliationExactReadBack exactReadBack;
    private int observations;
    private int scheduleIndex;
    private int diagnosticWritten;

    internal PostUploadVisibilityWindow(
        TimeProvider timeProvider,
        IStateReconciliationDiagnosticSink? diagnosticSink,
        StateReconciliationOwner owner,
        StateReconciliationOutcome outcome,
        StateReconciliationExactReadBack exactReadBack)
    {
        this.timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        this.diagnosticSink = diagnosticSink;
        this.owner = owner;
        this.outcome = outcome;
        this.exactReadBack = exactReadBack;
    }

    internal int Observations => observations;

    internal int ScheduleIndex => scheduleIndex;

    internal void RecordObservation()
    {
        if (observations >= 32)
        {
            throw new InvalidOperationException(
                "state_reconciliation_observation_limit_exceeded");
        }

        observations++;
    }

    internal async ValueTask<bool> WaitForNextObservationAsync()
    {
        if (scheduleIndex >= Delays.Length)
        {
            return false;
        }

        var delay = Delays[scheduleIndex++];
        await Task.Delay(delay, timeProvider, CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    internal void ReportFailure(StateReconciliationTerminal terminal)
    {
        if (Interlocked.Exchange(ref diagnosticWritten, 1) != 0)
        {
            return;
        }

        diagnosticSink?.Write(new StateReconciliationDiagnostic(
            owner,
            outcome,
            exactReadBack,
            observations,
            terminal,
            scheduleIndex));
    }
}
