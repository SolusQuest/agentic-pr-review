using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofVerifier;

internal sealed class VerifierRecordingHandler(
    string scenarioRoot,
    string channel,
    HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly object Gate = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var dispatchTimestamp = Stopwatch.GetTimestamp();
        var recordTrustedRequest = channel is "github" or "control" &&
            request.RequestUri is { } requestUri && !string.Equals(
                requestUri.Host, "codeload.github.com",
                StringComparison.OrdinalIgnoreCase);
        var trustedSequence = recordTrustedRequest
            ? Increment("verifier-" + channel + "-event-sequence")
            : 0;
        var sequence = channel == "provider"
            ? Increment("provider-request-count")
            : 0;
        if (channel == "provider")
        {
            File.WriteAllText(
                Path.Join(scenarioRoot, "provider-sequence"),
                sequence.ToString(CultureInfo.InvariantCulture));
        }

        var requestShape = channel == "provider"
            ? await ReadProviderRequestShapeAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : "-\t-\t-\t-\t-";

        var response = await base.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (recordTrustedRequest)
        {
            RecordTrustedProofRequest(request, response, dispatchTimestamp,
                trustedSequence);
        }
        lock (Gate)
        {
            File.AppendAllText(
                Path.Join(scenarioRoot, "verifier-" + channel + "-trace.tsv"),
                string.Join(
                    '\t',
                    sequence.ToString(CultureInfo.InvariantCulture),
                    request.Method.Method,
                    request.RequestUri?.AbsolutePath ?? "-",
                    ((int)response.StatusCode).ToString(
                        CultureInfo.InvariantCulture),
                    requestShape) + "\n");
        }

        return response;
    }

    // Persist only canonical, secret-free facts.  URI queries, headers,
    // bodies, and credentials are intentionally outside the witness format.
    private void RecordTrustedProofRequest(
        HttpRequestMessage request,
        HttpResponseMessage response,
        long dispatchTimestamp,
        int localSequence)
    {
        if (request.RequestUri is not { } uri)
        {
            throw new InvalidOperationException("trusted request uri is missing");
        }

        if (!request.Options.TryGetValue(
                TrustedProofOperationRequestAccounting.WitnessDomainOption,
                out string? witnessedDomain) ||
            witnessedDomain is not ("host_head_source_rest" or
                "host_other_github_rest" or "trusted_control_rest") ||
            (channel == "control" && witnessedDomain != "trusted_control_rest"))
        {
            throw new InvalidOperationException(
                "github witness is missing canonical request domain");
        }
        var domain = witnessedDomain;
        var method = request.Method.Method;
        if (method is not ("GET" or "HEAD" or "OPTIONS" or "POST" or "PUT" or
                "PATCH" or "DELETE"))
        {
            throw new InvalidOperationException("unexpected verifier request method");
        }
        var points = method is "GET" or "HEAD" or "OPTIONS" ? 1 : 5;
        var responseClass = TrustedProofOperationRequestAccounting
            .WitnessResponseClass(TrustedProofOperationRequestAccounting
                .ResponseClassify(response));
        lock (Gate)
        {
            File.AppendAllText(
                Path.Join(scenarioRoot, "verifier-" + channel + "-requests.tsv"),
                string.Join('\t',
                    "verifier_" + channel,
                    localSequence.ToString(CultureInfo.InvariantCulture),
                    domain,
                    "github_rest",
                    method,
                    points.ToString(CultureInfo.InvariantCulture),
                    dispatchTimestamp.ToString(CultureInfo.InvariantCulture),
                    responseClass) + "\n");
        }
    }

    private static async Task<string> ReadProviderRequestShapeAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return "0\t0\t0\t0\t-";
        }

        var bytes = await request.Content.ReadAsByteArrayAsync(
            cancellationToken).ConfigureAwait(false);
        var marker = TrustedProofDeterministicDeepSeekHandler
            .ContinuationMarker;
        var text = Encoding.UTF8.GetString(bytes);
        var markerCount = 0;
        for (var offset = 0;
            (offset = text.IndexOf(marker, offset,
                StringComparison.Ordinal)) >= 0;
            offset += marker.Length)
        {
            markerCount++;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var messages = document.RootElement.GetProperty("messages");
            var carriers = messages.EnumerateArray().Count(message =>
                message.TryGetProperty("role", out var role) &&
                role.GetString() == "assistant" &&
                message.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String &&
                reasoning.GetString()!.Contains(marker,
                    StringComparison.Ordinal));
            var calls = messages.EnumerateArray()
                .Where(message =>
                    message.TryGetProperty("role", out var role) &&
                    role.GetString() == "assistant" &&
                    message.TryGetProperty("tool_calls", out _))
                .SelectMany(message =>
                    message.GetProperty("tool_calls").EnumerateArray())
                .Take(8)
                .Select(call => (
                    Id: call.GetProperty("id").GetString()!,
                    Arguments: call.GetProperty("function")
                        .GetProperty("arguments").GetString()!))
                .ToArray();
            var resultIds = messages.EnumerateArray()
                .Where(message =>
                    message.TryGetProperty("role", out var role) &&
                    role.GetString() == "tool" &&
                    message.TryGetProperty("tool_call_id", out _))
                .Select(message => message.GetProperty("tool_call_id")
                    .GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var complete = calls.Count(call => resultIds.Contains(call.Id));
            var argumentDigests = string.Join(',', calls.Select(call =>
                Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(call.Arguments)))
                    .ToLowerInvariant()));
            return string.Join('\t',
                markerCount.ToString(CultureInfo.InvariantCulture),
                carriers.ToString(CultureInfo.InvariantCulture),
                messages.GetArrayLength().ToString(CultureInfo.InvariantCulture),
                complete.ToString(CultureInfo.InvariantCulture),
                argumentDigests.Length == 0 ? "-" : argumentDigests);
        }
        catch (JsonException)
        {
            return string.Join('\t',
                markerCount.ToString(CultureInfo.InvariantCulture),
                "-1",
                "-1",
                "-1",
                "-");
        }
    }

    private int Increment(string name)
    {
        lock (Gate)
        {
            var path = Path.Join(scenarioRoot, name);
            var current = File.Exists(path) && int.TryParse(
                File.ReadAllText(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0;
            current++;
            File.WriteAllText(path, current.ToString(CultureInfo.InvariantCulture));
            return current;
        }
    }
}
