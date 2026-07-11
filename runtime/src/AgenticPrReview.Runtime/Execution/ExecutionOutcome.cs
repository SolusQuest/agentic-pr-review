namespace AgenticPrReview.Runtime;

public sealed record ExecutionOutcome(RuntimeFinding[] Findings, RuntimeDiagnostic[] Diagnostics);
