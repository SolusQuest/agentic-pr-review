namespace AgenticPrReview.Runtime.Tests;

// These tests mutate process-global environment variables. xUnit runs test
// classes in parallel by default, so every such class must share this collection.
[CollectionDefinition(ProcessEnvironmentCollection.Name)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
