using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal sealed class ActionHostWorkflowDispatchInputsDocument
{
    [JsonPropertyName("pr-number")]
    public long? PullRequestNumber { get; set; }
}

internal sealed class ActionHostEventDocument
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("workflow_run")]
    public ActionHostGitHubWorkflowRunDocument? WorkflowRun { get; set; }

    [JsonPropertyName("inputs")]
    public ActionHostWorkflowDispatchInputsDocument? Inputs { get; set; }

    [JsonPropertyName("repository")]
    public ActionHostGitHubRepositoryIdentityDocument? Repository { get; set; }

    [JsonPropertyName("sender")]
    public ActionHostGitHubActorDocument? Sender { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ActionHostEventDocument))]
internal sealed partial class ActionHostEventJsonContext :
    JsonSerializerContext;

internal sealed class ActionHostExactPathEventReader : IActionHostEventReader
{
    public async Task<ActionHostEventReadResult> ReadAsync(
        string exactPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(exactPath))
        {
            return ActionHostEventReadResult.Failed();
        }

        try
        {
            await using var stream = new FileStream(
                exactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or >
                ActionHostAuthorizationBounds.MaximumEventBytes)
            {
                return ActionHostEventReadResult.Failed();
            }

            using var captured = new MemoryStream(
                checked((int)stream.Length));
            var buffer = new byte[16 * 1024];
            while (captured.Length <=
                ActionHostAuthorizationBounds.MaximumEventBytes)
            {
                var remaining = checked(
                    ActionHostAuthorizationBounds.MaximumEventBytes + 1 -
                    (int)captured.Length);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    var bytes = captured.ToArray();
                    return bytes.Length == 0
                        ? ActionHostEventReadResult.Failed()
                        : ActionHostEventReadResult.Captured(bytes);
                }

                await captured.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            return ActionHostEventReadResult.Failed();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return ActionHostEventReadResult.Failed();
        }
    }
}

internal static class ActionHostEventParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool TryParse(
        byte[]? bytes,
        out ActionHostEventFact? fact,
        out bool unsupported)
    {
        fact = null;
        unsupported = false;
        if (bytes is null ||
            bytes.Length is <= 0 or >
                ActionHostAuthorizationBounds.MaximumEventBytes ||
            bytes.Length >= 3 &&
                bytes[0] == 0xef &&
                bytes[1] == 0xbb &&
                bytes[2] == 0xbf)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            var document = JsonSerializer.Deserialize(
                bytes,
                ActionHostEventJsonContext.Default.ActionHostEventDocument);
            if (document is null ||
                !TryMap(document.Repository, out var repository) ||
                !TryMap(document.Sender, out var sender))
            {
                return false;
            }

            if (document.WorkflowRun is not null && document.Inputs is not null)
            {
                return false;
            }

            if (document.WorkflowRun is not null)
            {
                if (!ActionHostGitHubDocumentMapper.TryMap(
                        document.WorkflowRun,
                        out var workflowRun))
                {
                    return false;
                }

                fact = new(
                    ActionHostAuthorizationRoute.WorkflowRun,
                    repository!,
                    sender!,
                    document.Action,
                    workflowRun,
                    null);
                return true;
            }

            if (document.Inputs is not null)
            {
                fact = new(
                    ActionHostAuthorizationRoute.WorkflowDispatch,
                    repository!,
                    sender!,
                    document.Action,
                    null,
                    document.Inputs.PullRequestNumber);
                return true;
            }

            unsupported = true;
            return false;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryMap(
        ActionHostGitHubRepositoryIdentityDocument? document,
        out ActionHostGitHubRepositoryIdentity? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            !ActionHostGitHubDocumentMapper.IsRepositoryName(document.FullName))
        {
            return false;
        }

        fact = new(document.Id, document.FullName!);
        return true;
    }

    private static bool TryMap(
        ActionHostGitHubActorDocument? document,
        out ActionHostGitHubActorFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            string.IsNullOrWhiteSpace(document.Login) ||
            document.Login.Length > 255 ||
            document.Login.Any(char.IsControl))
        {
            return false;
        }

        fact = new(document.Id, document.Login);
        return true;
    }
}
