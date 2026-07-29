using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static class AgentToolRegistry
{
    internal const string ReadFileName = "read_file";
    internal const string SearchTextName = "search_text";
    internal const string FinishReviewName = "finish_review";

    internal const string ReadFileDescription =
        "Read a bounded line range from one tracked UTF-8 file in the reviewed snapshot.";
    internal const string SearchTextDescription =
        "Search for a case-sensitive literal in tracked UTF-8 files in the reviewed snapshot.";
    internal const string FinishReviewDescription =
        "Finish the review with validated grounded findings.";

    internal const string ReadFileSchema =
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"line_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":400}},\"required\":[\"path\"],\"additionalProperties\":false}";
    internal const string SearchTextSchema =
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}";
    internal const string FinishReviewSchema =
        "{\"type\":\"object\",\"properties\":{\"summary\":{\"type\":\"string\"},\"findings\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{\"severity\":{\"type\":\"string\",\"enum\":[\"critical\",\"high\",\"medium\",\"low\"]},\"title\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"},\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{\"observation_id\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"end_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647}},\"required\":[\"observation_id\",\"path\",\"start_line\",\"end_line\"],\"additionalProperties\":false}}},\"required\":[\"severity\",\"title\",\"message\",\"evidence\"],\"additionalProperties\":false}}},\"required\":[\"summary\",\"findings\"],\"additionalProperties\":false}";

    internal static ImmutableArray<ProjectToolDefinition> Definitions { get; } =
    [
        new(ReadFileName, ReadFileDescription, ReadFileSchema),
        new(SearchTextName, SearchTextDescription, SearchTextSchema),
        new(FinishReviewName, FinishReviewDescription, FinishReviewSchema),
    ];
}

internal sealed record ReadFileArguments(
    string Path,
    int StartLine,
    int LineCount,
    byte[] CanonicalBytes);

internal sealed record SearchTextArguments(
    string Query,
    string? Path,
    byte[] CanonicalBytes);

internal sealed record FinishReviewArguments(
    string Summary,
    ImmutableArray<AgentFinding> Findings,
    byte[] CanonicalBytes);

internal static class AgentToolArguments
{
    internal static bool TryReadFile(
        string json,
        out ReadFileArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json);
        if (input is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                input,
                AgentToolJsonContext.Default.ReadFileArgumentsDto);
            if (dto?.Path is null ||
                !RepositoryPath.IsValid(dto.Path) ||
                dto.StartLine is < 1 ||
                dto.LineCount is < 1 or > AgentLimits.ReadFileLines)
            {
                return false;
            }

            var start = dto.StartLine ?? 1;
            var count = dto.LineCount ?? AgentLimits.ReadFileLines;
            if (!MatchesReadInput(input, dto.Path, start, count))
            {
                return false;
            }

            var canonical = WriteReadFile(dto.Path, start, count, true, true);
            arguments = new ReadFileArguments(dto.Path, start, count, canonical);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    internal static bool TrySearchText(
        string json,
        out SearchTextArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json);
        if (input is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                input,
                AgentToolJsonContext.Default.SearchTextArgumentsDto);
            if (dto?.Query is null)
            {
                return false;
            }

            var queryBytes = Encoding.UTF8.GetByteCount(dto.Query);
            if (queryBytes is < 1 or > AgentLimits.QueryBytes ||
                dto.Query.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
                AgentTextValidation.IsOnlyFixedWhitespace(dto.Query) ||
                (dto.Path is not null && !RepositoryPath.IsValid(dto.Path)))
            {
                return false;
            }

            var absent = WriteSearchText(dto.Query, null, false);
            var present = dto.Path is null
                ? null
                : WriteSearchText(dto.Query, dto.Path, true);
            if (!input.AsSpan().SequenceEqual(absent) &&
                (present is null || !input.AsSpan().SequenceEqual(present)))
            {
                return false;
            }

            var canonical = WriteSearchText(dto.Query, dto.Path, true);
            arguments = new SearchTextArguments(dto.Query, dto.Path, canonical);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    internal static bool TryFinishReview(
        string json,
        out FinishReviewArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json, AgentLimits.TerminalBytes);
        if (input is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                input,
                AgentToolJsonContext.Default.FinishReviewArgumentsDto);
            if (dto?.Summary is null || dto.Findings is null)
            {
                return false;
            }

            var findings = ImmutableArray.CreateBuilder<AgentFinding>(dto.Findings.Length);
            foreach (var finding in dto.Findings)
            {
                if (finding is null ||
                    finding.Severity is null ||
                    finding.Title is null ||
                    finding.Message is null ||
                    finding.Evidence is null)
                {
                    return false;
                }

                var evidence = ImmutableArray.CreateBuilder<AgentEvidence>(
                    finding.Evidence.Length);
                foreach (var item in finding.Evidence)
                {
                    if (item is null ||
                        item.ObservationId is null ||
                        item.Path is null)
                    {
                        return false;
                    }

                    evidence.Add(new AgentEvidence(
                        item.ObservationId,
                        item.Path,
                        item.StartLine,
                        item.EndLine));
                }

                findings.Add(new AgentFinding(
                    finding.Severity,
                    finding.Title,
                    finding.Message,
                    evidence.MoveToImmutable()));
            }

            var canonical = WriteFinishReview(dto.Summary, findings);
            if (!input.AsSpan().SequenceEqual(canonical))
            {
                return false;
            }

            arguments = new FinishReviewArguments(
                dto.Summary,
                findings.MoveToImmutable(),
                canonical);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    internal static byte[] WriteFinishReview(
        string summary,
        IEnumerable<AgentFinding> findings)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("summary");
        writer.WriteString(summary);
        writer.WriteProperty("findings");
        writer.WriteArrayStart();
        var findingIndex = 0;
        foreach (var finding in findings)
        {
            if (findingIndex++ > 0)
            {
                writer.WriteComma();
            }

            writer.WriteObjectStart();
            writer.WriteProperty("severity");
            writer.WriteString(finding.Severity);
            writer.WriteProperty("title");
            writer.WriteString(finding.Title);
            writer.WriteProperty("message");
            writer.WriteString(finding.Message);
            writer.WriteProperty("evidence");
            writer.WriteArrayStart();
            for (var evidenceIndex = 0;
                evidenceIndex < finding.Evidence.Length;
                evidenceIndex++)
            {
                if (evidenceIndex > 0)
                {
                    writer.WriteComma();
                }

                var evidence = finding.Evidence[evidenceIndex];
                writer.WriteObjectStart();
                writer.WriteProperty("observation_id");
                writer.WriteString(evidence.ObservationId);
                writer.WriteProperty("path");
                writer.WriteString(evidence.Path);
                writer.WriteProperty("start_line");
                writer.WriteNumber(evidence.StartLine);
                writer.WriteProperty("end_line");
                writer.WriteNumber(evidence.EndLine);
                writer.WriteObjectEnd();
            }

            writer.WriteArrayEnd();
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    private static byte[]? StrictInputBytes(
        string json,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            return bytes.Length <= maximumBytes ? bytes : null;
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static bool MatchesReadInput(
        ReadOnlySpan<byte> input,
        string path,
        int startLine,
        int lineCount)
    {
        return input.SequenceEqual(WriteReadFile(path, startLine, lineCount, false, false)) ||
            input.SequenceEqual(WriteReadFile(path, startLine, lineCount, true, false)) ||
            input.SequenceEqual(WriteReadFile(path, startLine, lineCount, false, true)) ||
            input.SequenceEqual(WriteReadFile(path, startLine, lineCount, true, true));
    }

    private static byte[] WriteReadFile(
        string path,
        int startLine,
        int lineCount,
        bool includeStart,
        bool includeCount)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        writer.WriteProperty("path");
        writer.WriteString(path);
        if (includeStart)
        {
            writer.WriteProperty("start_line");
            writer.WriteNumber(startLine);
        }

        if (includeCount)
        {
            writer.WriteProperty("line_count");
            writer.WriteNumber(lineCount);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    private static byte[] WriteSearchText(
        string query,
        string? path,
        bool includePath)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        writer.WriteProperty("query");
        writer.WriteString(query);
        if (includePath)
        {
            writer.WriteProperty("path");
            if (path is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteString(path);
            }
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

internal static class AgentTextValidation
{
    internal static bool IsOnlyFixedWhitespace(string value)
    {
        var any = false;
        foreach (var rune in value.EnumerateRunes())
        {
            any = true;
            if (!IsFixedWhitespace(rune.Value))
            {
                return false;
            }
        }

        return any;
    }

    private static bool IsFixedWhitespace(int scalar) =>
        scalar is 0x0009 or 0x000A or 0x000B or 0x000C or 0x000D or
            0x0020 or 0x0085 or
            0x00A0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or
            0x3000 ||
        scalar is >= 0x2000 and <= 0x200A;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ReadFileArgumentsDto
{
    [JsonPropertyName("path")]
    [JsonPropertyOrder(0)]
    public string? Path { get; set; }

    [JsonPropertyName("start_line")]
    [JsonPropertyOrder(1)]
    public int? StartLine { get; set; }

    [JsonPropertyName("line_count")]
    [JsonPropertyOrder(2)]
    public int? LineCount { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SearchTextArgumentsDto
{
    [JsonPropertyName("query")]
    [JsonPropertyOrder(0)]
    public string? Query { get; set; }

    [JsonPropertyName("path")]
    [JsonPropertyOrder(1)]
    public string? Path { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FinishReviewArgumentsDto
{
    [JsonPropertyName("summary")]
    [JsonPropertyOrder(0)]
    public string? Summary { get; set; }

    [JsonPropertyName("findings")]
    [JsonPropertyOrder(1)]
    public FinishReviewFindingDto?[]? Findings { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FinishReviewFindingDto
{
    [JsonPropertyName("severity")]
    [JsonPropertyOrder(0)]
    public string? Severity { get; set; }

    [JsonPropertyName("title")]
    [JsonPropertyOrder(1)]
    public string? Title { get; set; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(2)]
    public string? Message { get; set; }

    [JsonPropertyName("evidence")]
    [JsonPropertyOrder(3)]
    public FinishReviewEvidenceDto?[]? Evidence { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FinishReviewEvidenceDto
{
    [JsonPropertyName("observation_id")]
    [JsonPropertyOrder(0)]
    public string? ObservationId { get; set; }

    [JsonPropertyName("path")]
    [JsonPropertyOrder(1)]
    public string? Path { get; set; }

    [JsonPropertyName("start_line")]
    [JsonPropertyOrder(2)]
    public int StartLine { get; set; }

    [JsonPropertyName("end_line")]
    [JsonPropertyOrder(3)]
    public int EndLine { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReadFileArgumentsDto))]
[JsonSerializable(typeof(SearchTextArgumentsDto))]
[JsonSerializable(typeof(FinishReviewArgumentsDto))]
internal sealed partial class AgentToolJsonContext : JsonSerializerContext;
