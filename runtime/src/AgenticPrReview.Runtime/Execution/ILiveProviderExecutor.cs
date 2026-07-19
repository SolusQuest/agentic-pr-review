namespace AgenticPrReview.Runtime;

/// <summary>
/// Provider-neutral seam for the process-boundary live executor. #55 wires only
/// the deterministic synthetic implementation; #52 can supply a real adapter
/// without changing context, output, or publication contracts.
/// </summary>
internal interface ILiveProviderExecutor
{
    ProviderExecutionObservation Execute(ReviewInput input, string inputHash);
}

internal sealed record ProviderExecutionObservation(
    string Summary,
    string[] Limitations,
    string Mode);

internal sealed class SyntheticLiveProviderExecutor : ILiveProviderExecutor
{
    public ProviderExecutionObservation Execute(ReviewInput input, string inputHash) =>
        new(
            "Synthetic live runtime completed without findings.",
            ["No live provider was invoked."],
            "live-provider");
}
