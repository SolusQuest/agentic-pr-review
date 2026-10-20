namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

// Fixed synthetic-probe diagnostics only. Observations never establish lifecycle success.
internal enum TrustedLiveProbeStage
{
    CoreEntered, SignalReceived, CancellationReturned, LauncherStarted, ExitWaitStarted,
    CancellationObserved, KillStarted, KillReturned, Reaped, OutputAwait, OutputClosed,
    ErrorClosed, PhaseReturned, ScanStarted, ScanReturned, DeleteReturned, BuffersZeroed,
    CompletionStarted, CompletionWritten, CoreReturning,
}

internal sealed class TrustedLiveProbeObservation : IDisposable
{
    internal const string FileName = "supervisor-stages.log";
    private readonly FileStream stream;
    private readonly object gate = new();
    private readonly HashSet<TrustedLiveProbeStage> seen = [];
    private TrustedLiveProbeObservation(FileStream stream) => this.stream = stream;

    internal static TrustedLiveProbeObservation? TryCreate(string validatedProbeRoot)
    {
        try
        {
            return new(new FileStream(Path.Join(validatedProbeRoot, FileName),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        }
        catch { return null; }
    }

    internal void Record(TrustedLiveProbeStage stage)
    {
        try
        {
            lock (gate)
            {
                if (!Enum.IsDefined(stage) || !seen.Add(stage)) return;
                stream.Write(System.Text.Encoding.ASCII.GetBytes(stage.ToString() + "\n"));
                stream.Flush();
            }
        }
        catch { } // A diagnostic failure cannot change the observed lifecycle.
    }

    public void Dispose()
    {
        try { stream.Dispose(); }
        catch { }
    }
}
