using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
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
        const int sequence = 0;
        const string requestShape = "-\t-\t-\t-\t-";

        var response = await base.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (recordTrustedRequest)
        {
            await RecordTrustedProofRequestAsync(request, response,
                dispatchTimestamp, trustedSequence, cancellationToken)
                .ConfigureAwait(false);
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
    private async Task RecordTrustedProofRequestAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        long dispatchTimestamp,
        int localSequence,
        CancellationToken cancellationToken)
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
        var classified = await TrustedProofOperationRequestAccounting
            .ResponseClassifyAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var responseClass = TrustedProofOperationRequestAccounting
            .WitnessResponseClass(classified);
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

// Verifier-owned observation at the typed callback boundary. It never observes or
// claims HTTP dispatch; the inner fixed script cannot write these counters.
internal sealed class VerifierRecordingChatClient(string scenarioRoot, IProjectChatClient inner) : IProjectChatClient
{
    private static readonly object Gate = new();
    internal const string ObservationDomain = "apr-historical-authored-typed-chat-v1";
    private bool? restored;
    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var ordinal = Increment();
        var outcome = "rejected";
        var shape = "-\t-\t-\t-\t-\t-";
        try
        {
            shape = Observe(request, ordinal);
            var response = await inner.GetResponseAsync(request, token).ConfigureAwait(false);
            outcome = "succeeded";
            return response;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "failed"; throw; }
        finally
        {
            lock (Gate)
                File.AppendAllText(Path.Join(scenarioRoot, "verifier-typed-chat-trace.tsv"),
                    string.Join('\t', ObservationDomain, ordinal.ToString(CultureInfo.InvariantCulture), outcome, shape) + "\n");
        }
    }
    private int Increment()
    {
        lock (Gate)
        {
            var path = Path.Join(scenarioRoot, "synthetic-chat-call-count");
            var ordinal = File.Exists(path) ? int.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture) + 1 : 1;
            File.WriteAllText(path, ordinal.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Join(scenarioRoot, "synthetic-chat-sequence"), ordinal.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Join(scenarioRoot, "typed-chat-observation-domain"), ObservationDomain);
            return ordinal;
        }
    }
    private string Observe(ProjectChatRequest request, int ordinal)
    {
        var marker = TrustedProofDeterministicDeepSeekHandler.ContinuationMarker;
        var calls = request.Messages.SelectMany(message => message.Contents.OfType<ProjectToolCallContent>()).ToArray();
        var results = request.Messages.SelectMany(message => message.Contents.OfType<ProjectToolResultContent>()).ToArray();
        if (calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length ||
            !calls.Select(call => call.CallId).SequenceEqual(results.Select(result => result.CallId), StringComparer.Ordinal))
            throw new ProjectChatNormalizationException();
        restored ??= calls.Length == 6;
        var expectedCalls = (restored == true ? 6 : 0) + ordinal - 1;
        string[] expectedIds = ["e2p-list-changed", "e2p-list-files", "e2p-read-diff", "e2p-read-file", "e2p-search", "e2p-finish",
            "e2p-continuation-read-file", "e2p-continuation-search"];
        string[] expectedArguments =
        [
            "{\"after\":null}", "{\"prefix\":null,\"after\":null}",
            "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1,\"hunk_count\":20}",
            "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}",
            "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}",
            "{\"summary\":\"Trusted proof complete.\",\"findings\":[]}",
            "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}",
            "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}",
        ];
        if (expectedCalls > expectedIds.Length || calls.Length != expectedCalls ||
            !calls.Select(call => call.CallId).SequenceEqual(expectedIds.Take(expectedCalls), StringComparer.Ordinal) ||
            !calls.Select(call => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson))))
                .SequenceEqual(expectedArguments.Take(expectedCalls).Select(value => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))), StringComparer.Ordinal))
            throw new ProjectChatNormalizationException();
        var protocol = request.Messages.Where(message => message.Contents.Any(part =>
            part is ProjectToolCallContent or ProjectToolResultContent)).ToArray();
        if (protocol.Length != calls.Length * 2) throw new ProjectChatNormalizationException();
        for (var index = 0; index < calls.Length; index++)
            if (protocol[index * 2] is not { Role: "assistant" } assistant ||
                assistant.Contents.OfType<ProjectToolCallContent>().ToArray() is not [var call] ||
                protocol[index * 2 + 1] is not { Role: "tool", Contents: [ProjectToolResultContent result] } ||
                call.CallId != result.CallId || string.IsNullOrWhiteSpace(result.Result))
                throw new ProjectChatNormalizationException();
        var carriers = request.Continuation?.Items.Where(item => item.Readable.Contains(marker, StringComparison.Ordinal)).ToArray() ?? [];
        var occurrences = request.Messages.Sum(message => message.Contents.Sum(part => part switch
        {
            ProjectTextContent text => Count(text.Text, marker), ProjectToolCallContent call => Count(call.ArgumentsJson, marker),
            ProjectToolResultContent result => Count(result.Result, marker), _ => 0,
        }));
        if (occurrences != 0 || carriers.Length > 1) throw new ProjectChatNormalizationException();
        var inline = request.Messages.SelectMany((message, position) => message.Contents.OfType<ProjectReasoningContent>()
            .Where(part => part.Text.Contains(marker, StringComparison.Ordinal)).Select(part => (position, part))).ToArray();
        if (inline.Length > 1 || inline.Length > carriers.Length) throw new ProjectChatNormalizationException();
        occurrences = carriers.Sum(item => Count(item.Readable, marker));
        if (occurrences != carriers.Length || occurrences != (expectedCalls >= 3 ? 1 : 0)) throw new ProjectChatNormalizationException();
        if (carriers is [var carrier])
        {
            if (carrier.MessagePosition < 0 || carrier.MessagePosition >= request.Messages.Length || carrier.ContentPosition != 0 ||
                carrier.AssociatedCallId is not null || carrier.Opaque != "" || carrier.Framing != DeepSeekAdapterContext.ReasoningFraming ||
                request.Messages[carrier.MessagePosition].Role != "assistant" ||
                request.Messages[carrier.MessagePosition].Contents.OfType<ProjectToolCallContent>().ToArray() is not [var markerCall] ||
                markerCall.CallId != "e2p-read-diff" || markerCall.Name != "read_diff" ||
                inline is [var existing] && (existing.position != carrier.MessagePosition || existing.part.Text != carrier.Readable ||
                    existing.part.MessagePosition != carrier.MessagePosition || existing.part.Position != 0 ||
                    existing.part.AssociatedCallId is not null || existing.part.Opaque != "" || existing.part.Framing != carrier.Framing))
                throw new ProjectChatNormalizationException();
        }
        var trace = AgentRequestWriter.Write(request);
        string hash;
        try
        {
            foreach (var forbidden in new[] { FrameworkCanaries.ProviderKey, FrameworkCanaries.GitHubToken,
                FrameworkCanaries.StateKey, FrameworkCanaries.PreviousStateKey, FrameworkCanaries.PublicResult })
                if (trace.AsSpan().IndexOf(Encoding.UTF8.GetBytes(forbidden)) >= 0) throw new ProjectChatNormalizationException();
            hash = Convert.ToHexStringLower(SHA256.HashData(trace));
        }
        finally { CryptographicOperations.ZeroMemory(trace); }
        var arguments = string.Join(',', calls.Select(call => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson)))));
        return string.Join('\t', occurrences, carriers.Length, request.Messages.Length, results.Length,
            arguments.Length == 0 ? "-" : arguments, hash);
    }
    private static int Count(string value, string marker)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0; index += marker.Length) count++;
        return count;
    }
}
