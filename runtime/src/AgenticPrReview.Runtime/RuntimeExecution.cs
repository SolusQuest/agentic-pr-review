using System.Text.Json;

namespace AgenticPrReview.Runtime;

public sealed record ExecutionOutcome(RuntimeFinding[] Findings, RuntimeDiagnostic[] Diagnostics);

public interface IRuntimeExecutor
{
    Task<ExecutionOutcome> ExecuteAsync(JsonElement input);
}

public sealed class DeterministicRuntimeExecutor : IRuntimeExecutor
{
    public Task<ExecutionOutcome> ExecuteAsync(JsonElement input) => Task.FromResult(new ExecutionOutcome([], []));
}

public sealed class ProviderFailureException : Exception;
