using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

// This is the one rate-limit taxonomy admitted by every GitHub transport.
// Callers may collapse it to their public failure vocabulary, but must retain
// the classification in their proof receipt/evidence before doing so.
internal enum ActionHostGitHubRateLimitClassification
{
    None,
    Permission,
    Primary,
    Secondary,
    Combined,
    Invalid,
}

internal static class ActionHostGitHubRateLimitClassifier
{
    internal const int MaximumErrorBodyBytes = 4 * 1024;
    private const int MaximumMessageCharacters = 512;
    private const int MaximumHeaderValue = 1_000_000;
    private const long MaximumResetEpoch = 4_102_444_800;

    internal static async ValueTask<ActionHostGitHubRateLimitClassification> ClassifyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        long? currentUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var rateStatus = response.StatusCode is HttpStatusCode.Forbidden or
            HttpStatusCode.TooManyRequests;
        if (!rateStatus || response.Content is null)
        {
            return Classify(response.StatusCode, response.Headers, null,
                currentUnixSeconds);
        }
        var content = response.Content;
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is < 0 or > MaximumErrorBodyBytes)
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
        try
        {
            await using var input = await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var remainingBound = checked(MaximumErrorBodyBytes + 1 -
                    (int)output.Length);
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingBound)),
                    cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumErrorBodyBytes)
                {
                    return ActionHostGitHubRateLimitClassification.Invalid;
                }

                output.Write(buffer, 0, read);
            }
            var body = output.ToArray();
            if (declaredLength is { } expectedLength &&
                body.LongLength != expectedLength)
            {
                return ActionHostGitHubRateLimitClassification.Invalid;
            }
            var replacement = new ByteArrayContent(body);
            foreach (var header in content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = replacement;
            content.Dispose();
            return Classify(response.StatusCode, response.Headers, body,
                currentUnixSeconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or
            IOException or InvalidOperationException)
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
    }

    internal static ActionHostGitHubRateLimitClassification Classify(
        HttpStatusCode status,
        HttpResponseHeaders headers,
        byte[]? errorBody,
        long? currentUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!TryHeader(headers, "x-ratelimit-remaining", 0, out var remaining,
                out var remainingPresent) ||
            !TryHeader(headers, "x-ratelimit-limit", 1, out var limit,
                out var limitPresent) ||
            !TryReset(headers, out var reset, out var resetPresent) ||
            !TryHeader(headers, "retry-after", 0, out var retryAfter,
                out var retryAfterPresent))
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
        var now = currentUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < 0 || (remainingPresent && limitPresent && remaining > limit) ||
            (resetPresent && (!remainingPresent || reset <= now)))
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
        var rateStatus = status is HttpStatusCode.Forbidden or
            HttpStatusCode.TooManyRequests;
        if (!rateStatus)
        {
            return retryAfterPresent
                ? ActionHostGitHubRateLimitClassification.Invalid
                : ActionHostGitHubRateLimitClassification.None;
        }
        if (!TrySecondary(errorBody, out var secondary))
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
        if (remainingPresent && remaining == 0 && !resetPresent)
        {
            return ActionHostGitHubRateLimitClassification.Invalid;
        }
        var primary = remainingPresent && remaining == 0 && resetPresent;
        secondary |= retryAfterPresent;
        if (primary && secondary) return ActionHostGitHubRateLimitClassification.Combined;
        if (primary) return ActionHostGitHubRateLimitClassification.Primary;
        if (secondary) return ActionHostGitHubRateLimitClassification.Secondary;
        // A plain 403 is a permission denial. A plain 429 does not identify
        // either the primary or secondary limit and therefore carries
        // incomplete rate evidence.
        return status == HttpStatusCode.TooManyRequests
            ? ActionHostGitHubRateLimitClassification.Invalid
            : ActionHostGitHubRateLimitClassification.Permission;
    }

    private static bool TryHeader(HttpResponseHeaders headers, string name,
        int minimum, out int value, out bool present)
    {
        value = 0;
        present = headers.TryGetValues(name, out var raw);
        if (!present) return true;
        var values = raw!.ToArray();
        return values.Length == 1 && int.TryParse(values[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out value) && value >= minimum &&
            value <= MaximumHeaderValue;
    }

    private static bool TryReset(HttpResponseHeaders headers, out long value,
        out bool present)
    {
        value = 0;
        present = headers.TryGetValues("x-ratelimit-reset", out var raw);
        if (!present) return true;
        var values = raw!.ToArray();
        return values.Length == 1 && long.TryParse(values[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out value) && value > 0 &&
            value <= MaximumResetEpoch;
    }

    private static bool TrySecondary(byte[]? body, out bool secondary)
    {
        secondary = false;
        if (body is null || body.Length == 0) return true;
        if (body.Length > MaximumErrorBodyBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var messages = document.RootElement.EnumerateObject()
                .Where(property => property.NameEquals("message")).ToArray();
            if (messages.Length == 0) return true;
            if (messages.Length != 1 || messages[0].Value.ValueKind != JsonValueKind.String)
                return false;
            var message = messages[0].Value.GetString();
            if (message is null || message.Length == 0 ||
                message.Length > MaximumMessageCharacters ||
                message.Any(character => character <= '\u001f' || character == '\u007f'))
                return false;
            secondary = Regex.IsMatch(message,
                @"\bsecondary rate limit(?:ed|s)?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
                    RegexOptions.ECMAScript);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
