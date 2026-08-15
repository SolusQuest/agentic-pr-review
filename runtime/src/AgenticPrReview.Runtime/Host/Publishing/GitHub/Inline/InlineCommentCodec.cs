using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;

internal enum InlineMarkerInspectionKind
{
    NoMarker = 1,
    Valid,
    Invalid,
}

internal sealed record InlineMarkerInspection(
    InlineMarkerInspectionKind Kind,
    string? InlineKey);

internal static class InlineCommentMarker
{
    private const string Prefix =
        "<!-- agentic-pr-review:r4:inline:v1 inline_key=";
    private const string Suffix = " -->";

    internal static string Append(string body, string inlineKey)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsLowerHexSha256(inlineKey) ||
            R4Markdown.ValidateBodyText(body) != R4BodyTextValidation.Valid)
        {
            throw new ArgumentException("The inline marker input is invalid.");
        }

        return $"{body}\n\n{Prefix}{inlineKey}{Suffix}";
    }

    internal static InlineMarkerInspection Inspect(string? body)
    {
        if (body is null)
        {
            return new(InlineMarkerInspectionKind.Invalid, null);
        }

        if (!body.Contains("agentic-pr-review:r4:inline",
                StringComparison.Ordinal))
        {
            return new(InlineMarkerInspectionKind.NoMarker, null);
        }

        if (R4Markdown.ValidateBodyText(body) != R4BodyTextValidation.Valid)
        {
            return new(InlineMarkerInspectionKind.Invalid, null);
        }

        var first = body.IndexOf(Prefix, StringComparison.Ordinal);
        if (first < 0)
        {
            return new(InlineMarkerInspectionKind.Invalid, null);
        }

        if (first != body.LastIndexOf(Prefix, StringComparison.Ordinal) ||
            first < 2 || body.AsSpan(first - 2, 2) is not "\n\n")
        {
            return new(InlineMarkerInspectionKind.Invalid, null);
        }

        var marker = body[first..];
        if (!marker.EndsWith(Suffix, StringComparison.Ordinal) ||
            marker.Length != Prefix.Length + 64 + Suffix.Length)
        {
            return new(InlineMarkerInspectionKind.Invalid, null);
        }

        var key = marker.Substring(Prefix.Length, 64);
        return IsLowerHexSha256(key)
            ? new(InlineMarkerInspectionKind.Valid, key)
            : new(InlineMarkerInspectionKind.Invalid, null);
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record InlineReviewCommentMutationDocument(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("body")] string Body);

internal sealed record InlineBatchReviewMutationDocument(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("comments")]
    InlineReviewCommentMutationDocument[] Comments);

internal sealed record InlineIndividualCommentMutationDocument(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("body")] string Body);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(InlineBatchReviewMutationDocument))]
[JsonSerializable(typeof(InlineIndividualCommentMutationDocument))]
internal sealed partial class InlineCommentJsonContext : JsonSerializerContext;

internal static class InlineCommentSerializer
{
    private const string ReviewBody =
        "Agentic PR Review inline findings for the accepted review.";
    private const string OmittedMessage =
        "Full finding text is available in the accepted sticky review.";
    private static readonly InlineCommentJsonContext Context = new(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });

    internal static bool TryRender(
        AuthorizedInlinePublicationRequest request,
        out IReadOnlyList<RenderedInlineComment>? comments)
    {
        comments = null;
        try
        {
            var rendered = new List<RenderedInlineComment>(
                request.CandidateMap.Candidates.Length);
            foreach (var candidate in request.CandidateMap.Candidates)
            {
                var finding = candidate.FindingIdentity.Finding;
                var full = "### " + R4Markdown.Escape(finding.Severity) +
                    ": " + R4Markdown.Escape(finding.Title) + "\n\n" +
                    R4Markdown.Escape(finding.Message);
                var body = InlineCommentMarker.Append(full,
                    candidate.InlineKey);
                if (!TryIndividual(request, candidate, body,
                        out var individual))
                {
                    body = InlineCommentMarker.Append(OmittedMessage,
                        candidate.InlineKey);
                    if (!TryIndividual(request, candidate, body,
                            out individual))
                    {
                        return false;
                    }
                }

                rendered.Add(new(candidate, body, individual!));
            }

            comments = rendered;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or EncoderFallbackException or
            ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryBatch(
        AuthorizedInlinePublicationRequest request,
        IReadOnlyList<RenderedInlineComment> comments,
        out byte[]? bytes)
    {
        bytes = null;
        try
        {
            var document = new InlineBatchReviewMutationDocument(
                request.Authorization.PullRequest.HeadSha,
                ReviewBody,
                "COMMENT",
                comments.Select(static comment =>
                    new InlineReviewCommentMutationDocument(
                        comment.Candidate.Path,
                        comment.Candidate.Line,
                        "RIGHT",
                        comment.Body)).ToArray());
            var candidate = JsonSerializer.SerializeToUtf8Bytes(
                document,
                Context.InlineBatchReviewMutationDocument);
            if (candidate.Length is 0 or >
                BoundedGitHubPublisherPolicy.MaximumInlineBatchRequestBytes)
            {
                return false;
            }

            bytes = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryIndividual(
        AuthorizedInlinePublicationRequest request,
        AgenticPrReview.Runtime.Host.Publishing.Inline.InlineCandidate candidate,
        string body,
        out byte[]? bytes)
    {
        bytes = null;
        var document = new InlineIndividualCommentMutationDocument(
            request.Authorization.PullRequest.HeadSha,
            candidate.Path,
            candidate.Line,
            "RIGHT",
            body);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            document,
            Context.InlineIndividualCommentMutationDocument);
        if (serialized.Length is 0 or >
            BoundedGitHubPublisherPolicy.MaximumIndividualInlineRequestBytes)
        {
            return false;
        }

        bytes = serialized;
        return true;
    }
}

internal static class InlineBatchValidationParser
{
    private const string Message = "Validation Failed";
    private const string DocumentationUrl =
        "https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request";

    internal static bool IsExactKnownNotSent(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var seenMessage = false;
            var seenDocumentationUrl = false;
            var seenErrors = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                if (reader.ValueIsEscaped)
                {
                    return false;
                }

                if (reader.ValueTextEquals("message"))
                {
                    if (seenMessage || !reader.Read() ||
                        reader.TokenType != JsonTokenType.String ||
                        reader.ValueIsEscaped ||
                        !reader.ValueTextEquals(Message))
                    {
                        return false;
                    }

                    seenMessage = true;
                }
                else if (reader.ValueTextEquals("documentation_url"))
                {
                    if (seenDocumentationUrl || !reader.Read() ||
                        reader.TokenType != JsonTokenType.String ||
                        reader.ValueIsEscaped ||
                        !reader.ValueTextEquals(DocumentationUrl))
                    {
                        return false;
                    }

                    seenDocumentationUrl = true;
                }
                else if (reader.ValueTextEquals("errors"))
                {
                    if (seenErrors || !ReadExactErrors(ref reader))
                    {
                        return false;
                    }

                    seenErrors = true;
                }
                else
                {
                    return false;
                }
            }

            return seenMessage && seenDocumentationUrl && seenErrors &&
                reader.TokenType == JsonTokenType.EndObject && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ReadExactErrors(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray ||
            !reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        var resource = false;
        var field = false;
        var code = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            if (reader.ValueIsEscaped)
            {
                return false;
            }

            if (reader.ValueTextEquals("resource"))
            {
                if (resource || !ReadExact(ref reader, "PullRequestReview"))
                {
                    return false;
                }

                resource = true;
            }
            else if (reader.ValueTextEquals("field"))
            {
                if (field || !ReadExact(ref reader, "comments"))
                {
                    return false;
                }

                field = true;
            }
            else if (reader.ValueTextEquals("code"))
            {
                if (code || !ReadExact(ref reader, "invalid"))
                {
                    return false;
                }

                code = true;
            }
            else
            {
                return false;
            }
        }

        return resource && field && code &&
            reader.TokenType == JsonTokenType.EndObject && reader.Read() &&
            reader.TokenType == JsonTokenType.EndArray;
    }

    private static bool ReadExact(ref Utf8JsonReader reader, string value) =>
        reader.Read() && reader.TokenType == JsonTokenType.String &&
        !reader.ValueIsEscaped &&
        reader.ValueTextEquals(value);
}
