namespace AgenticPrReview.Runtime;

public sealed class DeterministicRuntimeExecutor : IRuntimeExecutor
{
    public Task<ExecutionOutcome> ExecuteAsync(ReviewInput input) => Task.FromResult(new ExecutionOutcome([], []));
}
