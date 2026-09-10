namespace AgenticPrReview.Runtime.Tests;

// These tests mutate process-global environment variables or Console streams.
// Exclude other collections too: their output must not enter a captured stream.
[CollectionDefinition(ProcessEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
