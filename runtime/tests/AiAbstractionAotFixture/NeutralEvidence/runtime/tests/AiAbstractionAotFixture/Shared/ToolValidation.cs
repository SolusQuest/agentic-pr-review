using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class ToolValidation
{
    internal static string ExecuteRead(
        ProjectToolCall call,
        FirstFixtureInput input,
        string scenario)
    {
        RequireTool(call, FixtureConstants.ReadCallId, "read_file");
        using var document = ParseClosedArguments(
            call.ArgumentsJson,
            ["path", "startLine", "endLine"]);
        var root = document.RootElement;
        var path = RequireString(root, "path");
        var startLine = RequireInt32(root, "startLine");
        var endLine = RequireInt32(root, "endLine");
        if (path.Length == 0 || startLine < 1 || endLine < startLine)
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }

        var file = input.Files.SingleOrDefault(item => item.Path == path) ??
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        var lines = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (endLine > lines.Length)
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }

        var result = string.Join(
            "\n",
            Enumerable.Range(startLine, endLine - startLine + 1)
                .Select(line => $"{line}:{lines[line - 1]}"));
        if (scenario == "oversized-tool-result")
        {
            result = new string('x', FixtureConstants.MaxToolResultBytes + 1);
        }
        EnsureBoundedResult(result);
        return result;
    }

    internal static string ExecuteSearch(
        ProjectToolCall call,
        FirstFixtureInput input)
    {
        RequireTool(call, FixtureConstants.SearchCallId, "search_text");
        using var document = ParseClosedArguments(call.ArgumentsJson, ["path", "query"]);
        var root = document.RootElement;
        var path = RequireString(root, "path");
        var query = RequireString(root, "query");
        if (path.Length == 0 || query.Length == 0 || query != input.SearchQuery)
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }

        var file = input.Files.SingleOrDefault(item => item.Path == path) ??
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        var matches = file.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains(query, StringComparison.Ordinal))
            .Select(item => $"{path}:{item.index + 1}:{item.line}")
            .ToArray();
        var result = string.Join("\n", matches);
        EnsureBoundedResult(result);
        return result;
    }

    internal static TerminalReview ExecuteFinish(
        ProjectToolCall call,
        string expectedCallId,
        string expectedSummary)
    {
        RequireTool(call, expectedCallId, "finish_review");
        using var document = ParseClosedArguments(call.ArgumentsJson, ["summary", "findings"]);
        var root = document.RootElement;
        var summary = RequireString(root, "summary");
        var findings = root.GetProperty("findings");
        if (summary != expectedSummary ||
            findings.ValueKind != JsonValueKind.Array ||
            findings.GetArrayLength() != 0)
        {
            throw new FixtureFailure("APR_AI_TERMINAL");
        }
        return new TerminalReview(summary, []);
    }

    private static void RequireTool(
        ProjectToolCall call,
        string expectedCallId,
        string expectedName)
    {
        if (call.CallId != expectedCallId)
        {
            throw new FixtureFailure("APR_AI_ASSOCIATION");
        }
        if (call.Name != expectedName)
        {
            throw new FixtureFailure("APR_AI_TOOL_NAME");
        }
    }

    private static JsonDocument ParseClosedArguments(
        string json,
        string[] expectedProperties)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException)
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                document.Dispose();
                throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
            }
        }
        if (!names.SetEquals(expectedProperties))
        {
            document.Dispose();
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }
        return document;
    }

    private static string RequireString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }
        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new FixtureFailure("APR_AI_TOOL_ARGUMENT");
        }
        return result;
    }

    private static void EnsureBoundedResult(string result)
    {
        if (Encoding.UTF8.GetByteCount(result) > FixtureConstants.MaxToolResultBytes)
        {
            throw new FixtureFailure("APR_AI_TOOL_RESULT_OVERSIZED");
        }
    }
}

internal sealed record ProjectToolCall(
    string CallId,
    string Name,
    string ArgumentsJson);
