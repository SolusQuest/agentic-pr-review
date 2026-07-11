namespace AgenticPrReview.Runtime;

public interface IRuntimeExecutor
{
    Task<ExecutionOutcome> ExecuteAsync(ReviewInput input);
}
