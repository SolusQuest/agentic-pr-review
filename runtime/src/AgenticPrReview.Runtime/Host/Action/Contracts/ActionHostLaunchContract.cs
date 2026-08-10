using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHost.Contracts;

internal enum ActionHostCancellationState
{
    Active = 1,
    Requested = 2,
}

internal enum ActionHostContractError
{
    None = 0,
    InvalidLaunch,
    InvalidCompletion,
    InvalidPrivateCommand,
}

internal sealed class ActionHostLaunchContract
{
    private ActionHostLaunchContract(
        ActionHostInputs inputs,
        string eventJsonPath,
        string eventJsonSha256,
        string repositoryName,
        long repositoryId,
        long runId,
        int runAttempt,
        string workflowPath,
        string workflowRef,
        string workflowSha,
        string actionSourceSha,
        string payloadSha256,
        string buildDiscriminator,
        ActionHostCancellationState cancellation,
        string artifactBridgeEndpoint)
    {
        Inputs = inputs;
        EventJsonPath = eventJsonPath;
        EventJsonSha256 = eventJsonSha256;
        RepositoryName = repositoryName;
        RepositoryId = repositoryId;
        RunId = runId;
        RunAttempt = runAttempt;
        WorkflowPath = workflowPath;
        WorkflowRef = workflowRef;
        WorkflowSha = workflowSha;
        ActionSourceSha = actionSourceSha;
        PayloadSha256 = payloadSha256;
        BuildDiscriminator = buildDiscriminator;
        Cancellation = cancellation;
        ArtifactBridgeEndpoint = artifactBridgeEndpoint;
    }

    internal ActionHostInputs Inputs { get; }

    internal string EventJsonPath { get; }

    internal string EventJsonSha256 { get; }

    internal string RepositoryName { get; }

    internal long RepositoryId { get; }

    internal long RunId { get; }

    internal int RunAttempt { get; }

    internal string WorkflowPath { get; }

    internal string WorkflowRef { get; }

    internal string WorkflowSha { get; }

    internal string ActionSourceSha { get; }

    internal string PayloadSha256 { get; }

    internal string BuildDiscriminator { get; }

    internal ActionHostCancellationState Cancellation { get; }

    internal string ArtifactBridgeEndpoint { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;

    internal static bool TryCreate(
        ActionHostInputs? inputs,
        string? eventJsonPath,
        string? eventJsonSha256,
        string? repositoryName,
        long repositoryId,
        long runId,
        int runAttempt,
        string? workflowPath,
        string? workflowRef,
        string? workflowSha,
        string? actionSourceSha,
        string? payloadSha256,
        string? buildDiscriminator,
        ActionHostCancellationState cancellation,
        string? artifactBridgeEndpoint,
        out ActionHostLaunchContract? contract)
    {
        contract = null;
        if (inputs is null ||
            !ActionHostContractValidation.IsBoundedPrivateStructure(
                eventJsonPath,
                ActionHostContractBounds.MaximumEventJsonPathBytes) ||
            !ActionHostContractValidation.IsSha256(eventJsonSha256) ||
            !ActionHostContractValidation.IsRepositoryName(repositoryName) ||
            repositoryId <= 0 ||
            runId <= 0 ||
            runAttempt <= 0 ||
            !ActionHostContractValidation.IsBoundedPrivateStructure(
                workflowPath,
                ActionHostContractBounds.MaximumWorkflowPathBytes) ||
            !ActionHostContractValidation.IsBoundedPrivateStructure(
                workflowRef,
                ActionHostContractBounds.MaximumWorkflowRefBytes) ||
            !ActionHostContractValidation.IsCommitSha(workflowSha) ||
            !ActionHostContractValidation.IsCommitSha(actionSourceSha) ||
            !ActionHostContractValidation.IsSha256(payloadSha256) ||
            !ActionHostContractValidation.IsBuildDiscriminator(
                buildDiscriminator) ||
            cancellation is not (
                ActionHostCancellationState.Active or
                ActionHostCancellationState.Requested) ||
            !ActionHostContractValidation.IsBoundedPrivateStructure(
                artifactBridgeEndpoint,
                ActionHostContractBounds.MaximumBridgeEndpointBytes))
        {
            return false;
        }

        contract = new ActionHostLaunchContract(
            inputs,
            eventJsonPath!,
            eventJsonSha256!,
            repositoryName!,
            repositoryId,
            runId,
            runAttempt,
            workflowPath!,
            workflowRef!,
            workflowSha!,
            actionSourceSha!,
            payloadSha256!,
            buildDiscriminator!,
            cancellation,
            artifactBridgeEndpoint!);
        return true;
    }
}

internal interface IActionHostPrivateCommandDocument
{
    ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;
}

internal interface IActionHostPrivateCommandResultDocument
{
    ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;
}

internal interface IActionHostPrivateEnvelope
{
    string BuildDiscriminator { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostPrivateCommandEnvelope<TCommand>(
    string BuildDiscriminator,
    TCommand? Payload)
    : IActionHostPrivateEnvelope
    where TCommand : class, IActionHostPrivateCommandDocument
{
    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;

    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostPrivateCommandResultEnvelope<TResult>(
    string BuildDiscriminator,
    TResult? Payload)
    : IActionHostPrivateEnvelope
    where TResult : class, IActionHostPrivateCommandResultDocument
{
    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;

    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostNoPrivateCommandDocument
    : IActionHostPrivateCommandDocument;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostNoPrivateCommandResultDocument
    : IActionHostPrivateCommandResultDocument;
