using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record FinishReviewArguments(
    string Summary,
    ImmutableArray<AgentFinding> Findings,
    byte[] CanonicalBytes);

internal sealed record PreparedFinishReviewCall(
    string CallId,
    FinishReviewArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.FinishReviewName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TryFinishReview(
        string json,
        out FinishReviewArguments? arguments) =>
        TryFinishReview(json, allowProviderSpelling: false, out arguments);

    internal static bool TryFinishReviewProvider(
        string json,
        out FinishReviewArguments? arguments) =>
        TryFinishReview(json, allowProviderSpelling: true, out arguments);

    private static bool TryFinishReview(
        string json,
        bool allowProviderSpelling,
        out FinishReviewArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json, AgentLimits.TerminalBytes);
        if (input is null)
        {
            return false;
        }
        var providerComparison = allowProviderSpelling
            ? ProviderComparisonBytes(input, AgentLimits.TerminalBytes)
            : null;
        if (allowProviderSpelling && providerComparison is null)
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
            if (!MatchesInput(
                    input,
                    providerComparison,
                    canonical,
                    AgentLimits.TerminalBytes))
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
