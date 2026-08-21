using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal enum TrustedProofMutationOutcome
{
    Committed,
    MissingIdempotent,
    KnownNotSent,
    OutcomeUnknown,
}

internal sealed record TrustedProofCreateResult(
    TrustedProofMutationOutcome Outcome,
    TrustedProofIssueComment? Comment);

internal sealed class TrustedProofControlTransport : IDisposable
{
    private const int MaximumPages = 10;
    private readonly HttpClient client;
    private readonly TrustedProofControlCoordinates coordinates;

    private TrustedProofControlTransport(
        HttpClient client,
        TrustedProofControlCoordinates coordinates)
    {
        this.client = client;
        this.coordinates = coordinates;
    }

    internal static TrustedProofControlTransport Create(
        TrustedProofControlCoordinates coordinates,
        string token,
        HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "agentic-pr-review-r4-e2p");
        return new(client, coordinates);
    }

    internal async Task<IReadOnlyList<TrustedProofIssueComment>?> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<TrustedProofIssueComment>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var response = await client.GetAsync(
                $"repos/{coordinates.Repository}/issues/" +
                $"{coordinates.PullRequestNumber}/comments?per_page=100&page={page}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var items = await response.Content.ReadFromJsonAsync(
                TrustedProofControlJsonContext.Default
                    .TrustedProofIssueCommentArray,
                cancellationToken).ConfigureAwait(false);
            if (items is null)
            {
                return null;
            }

            result.AddRange(items);
            if (items.Length < 100)
            {
                return result;
            }
        }

        return null;
    }

    internal async Task<TrustedProofIssueComment?> GetAsync(
        long commentId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"repos/{coordinates.Repository}/issues/comments/{commentId}",
            cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(
                TrustedProofControlJsonContext.Default.TrustedProofIssueComment,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    internal async Task<TrustedProofCreateResult> CreateAsync(
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(
                    new TrustedProofCreateComment(body),
                    TrustedProofControlJsonContext.Default
                        .TrustedProofCreateComment));
            content.Headers.ContentType = new("application/json")
            {
                CharSet = "utf-8",
            };
            using var response = await client.PostAsync(
                $"repos/{coordinates.Repository}/issues/" +
                $"{coordinates.PullRequestNumber}/comments",
                content,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(
                    IsKnownNotSent(response.StatusCode)
                        ? TrustedProofMutationOutcome.KnownNotSent
                        : TrustedProofMutationOutcome.OutcomeUnknown,
                    null);
            }

            var comment = await response.Content.ReadFromJsonAsync(
                TrustedProofControlJsonContext.Default.TrustedProofIssueComment,
                cancellationToken).ConfigureAwait(false);
            return comment is null
                ? new(TrustedProofMutationOutcome.OutcomeUnknown, null)
                : new(TrustedProofMutationOutcome.Committed, comment);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            OperationCanceledException or JsonException)
        {
            return new(TrustedProofMutationOutcome.OutcomeUnknown, null);
        }
    }

    internal async Task<bool> HasWritePermissionAsync(
        string login,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"repos/{coordinates.Repository}/collaborators/" +
            $"{Uri.EscapeDataString(login)}/permission",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var permission = await response.Content.ReadFromJsonAsync(
            TrustedProofControlJsonContext.Default.TrustedProofPermission,
            cancellationToken).ConfigureAwait(false);
        return permission?.Permission is "write" or "admin";
    }

    internal async Task<TrustedProofMutationOutcome> DeleteAsync(
        long commentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.DeleteAsync(
                $"repos/{coordinates.Repository}/issues/comments/{commentId}",
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return TrustedProofMutationOutcome.Committed;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return TrustedProofMutationOutcome.MissingIdempotent;
            }

            return IsKnownNotSent(response.StatusCode)
                ? TrustedProofMutationOutcome.KnownNotSent
                : TrustedProofMutationOutcome.OutcomeUnknown;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            OperationCanceledException)
        {
            return TrustedProofMutationOutcome.OutcomeUnknown;
        }
    }

    public void Dispose() => client.Dispose();

    private static bool IsKnownNotSent(HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        return numericStatus is >= 400 and < 500 &&
            statusCode != HttpStatusCode.RequestTimeout &&
            numericStatus is not 409 and not 429;
    }
}
