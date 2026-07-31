using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed partial class SnapshotToolExecutor(
    ReviewedSnapshot snapshot,
    IReviewedFileAccess fileAccess) : IAgentToolExecutor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Preflight(PreparedAgentToolCall call) =>
        call switch
        {
            PreparedListFilesCall list => ValidateListFiles(list.Arguments),
            PreparedListChangedFilesCall changed =>
                ValidateListChangedFiles(changed.Arguments),
            PreparedReadDiffCall diff => ValidateReadDiff(diff.Arguments),
            PreparedReadFileCall read => ValidatePath(read.Arguments.Path),
            PreparedSearchTextCall { Arguments.Path: { } path } =>
                ValidatePath(path),
            PreparedSearchTextCall => null,
            _ => AgentFailureCodes.UnknownTool,
        };

    public ValueTask<AgentToolExecution> ExecuteAsync(
        PreparedAgentToolCall call,
        CancellationToken cancellationToken) =>
        call switch
        {
            PreparedListFilesCall list => ValueTask.FromResult(
                ExecuteListFiles(list, cancellationToken)),
            PreparedListChangedFilesCall changed => ValueTask.FromResult(
                ExecuteListChangedFiles(changed, cancellationToken)),
            PreparedReadDiffCall diff => ValueTask.FromResult(
                ExecuteReadDiff(diff, cancellationToken)),
            PreparedReadFileCall read => ExecuteReadAsync(read, cancellationToken),
            PreparedSearchTextCall search => ExecuteSearchAsync(search, cancellationToken),
            _ => ValueTask.FromResult(AgentToolExecution.Failure(
                AgentFailureCodes.UnknownTool)),
        };

    private string? ValidatePath(string path)
    {
        if (!RepositoryPath.IsValid(path))
        {
            return AgentFailureCodes.ToolPathInvalid;
        }

        return snapshot.Contains(path)
            ? null
            : AgentFailureCodes.ToolPathNotTracked;
    }

    private static string? AccessFailure(ReviewedFileAccessStatus status) =>
        status switch
        {
            ReviewedFileAccessStatus.Success => null,
            ReviewedFileAccessStatus.Unsafe => AgentFailureCodes.ToolPathUnsafe,
            ReviewedFileAccessStatus.IoFailure => AgentFailureCodes.ToolIoFailed,
            _ => AgentFailureCodes.ToolIoFailed,
        };

    private static ClassifiedText Decode(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new ClassifiedText(AgentFailureCodes.ToolFileBinary, []);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new ClassifiedText(AgentFailureCodes.ToolFileInvalidUtf8, []);
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF &&
            text.Length > 0 &&
            text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' &&
                (index + 1 >= text.Length || text[index + 1] != '\n'))
            {
                return new ClassifiedText(AgentFailureCodes.ToolFileLoneCr, []);
            }
        }

        if (text.Length == 0)
        {
            return new ClassifiedText(null, []);
        }

        var split = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var length = split.Length;
        if (text.EndsWith('\n'))
        {
            length--;
        }

        return new ClassifiedText(null, split[..length].ToImmutableArray());
    }

    private sealed record ClassifiedText(
        string? FailureCode,
        ImmutableArray<string> Lines);
}
