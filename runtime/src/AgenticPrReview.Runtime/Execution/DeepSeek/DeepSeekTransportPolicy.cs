using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal static class DeepSeekTransportPolicy
{
    internal const string Endpoint =
        "https://api.deepseek.com/chat/completions";
    internal const int CredentialMaxBytes = 256;
    internal const int RequestBodyMaxBytes = 1_048_576;
    internal const int SuccessBodyMaxBytes = 1_048_576;
    internal const int ErrorBodyDiscardMaxBytes = 8_192;
    internal const int RequestRejectedCount = RequestBodyMaxBytes + 1;
    internal const int ResponseTooLargeCount = SuccessBodyMaxBytes + 1;
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(120);
}

internal enum DeepSeekTransportOutcome
{
    RequestRejected,
    Success,
    ResponseTooLarge,
    HttpFailure,
    ConnectTimeout,
    ProviderTimeout,
    TransportFailure,
}

internal enum DeepSeekHttpStatusClass
{
    BadRequest,
    Unauthorized,
    PaymentRequired,
    NotFound,
    RequestTimeout,
    UnprocessableContent,
    TooManyRequests,
    Other4xx,
    Other5xx,
}

internal sealed class DeepSeekTransportResult
{
    private readonly ImmutableArray<byte> _body;

    private DeepSeekTransportResult(
        DeepSeekTransportOutcome outcome,
        int? status,
        DeepSeekHttpStatusClass? statusClass,
        int? actualCount,
        int? capturedCount,
        int? discardedErrorCount,
        ImmutableArray<byte> body)
    {
        if (!ValidState(
                outcome,
                status,
                statusClass,
                actualCount,
                capturedCount,
                discardedErrorCount,
                body))
        {
            throw new ArgumentException(
                "The DeepSeek transport result state is invalid.");
        }

        Outcome = outcome;
        Status = status;
        StatusClass = statusClass;
        ActualCount = actualCount;
        CapturedCount = capturedCount;
        DiscardedErrorCount = discardedErrorCount;
        _body = body;
    }

    internal DeepSeekTransportOutcome Outcome { get; }
    internal int? Status { get; }
    internal DeepSeekHttpStatusClass? StatusClass { get; }
    internal int? ActualCount { get; }
    internal int? CapturedCount { get; }
    internal int? DiscardedErrorCount { get; }
    internal bool HasBody => !_body.IsDefault;
    internal ImmutableArray<byte> Body => _body;

    internal static DeepSeekTransportResult RequestRejected() => new(
        DeepSeekTransportOutcome.RequestRejected,
        null,
        null,
        DeepSeekTransportPolicy.RequestRejectedCount,
        null,
        null,
        default);

    internal static DeepSeekTransportResult Success(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length > DeepSeekTransportPolicy.SuccessBodyMaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                "The success body exceeds the transport result cap.");
        }

        return new DeepSeekTransportResult(
            DeepSeekTransportOutcome.Success,
            200,
            null,
            null,
            body.Length,
            null,
            ImmutableArray.CreateRange(body));
    }

    internal static DeepSeekTransportResult ResponseTooLarge() => new(
        DeepSeekTransportOutcome.ResponseTooLarge,
        null,
        null,
        null,
        DeepSeekTransportPolicy.ResponseTooLargeCount,
        null,
        default);

    internal static DeepSeekTransportResult HttpFailure(
        DeepSeekHttpStatusClass statusClass,
        int discardedErrorCount)
    {
        if (!Enum.IsDefined(statusClass))
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusClass),
                "The HTTP status class is not defined.");
        }

        if (discardedErrorCount is < 0 or
            > DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discardedErrorCount),
                "The discarded error count is outside the result cap.");
        }

        return new DeepSeekTransportResult(
            DeepSeekTransportOutcome.HttpFailure,
            null,
            statusClass,
            null,
            null,
            discardedErrorCount,
            default);
    }

    internal static DeepSeekTransportResult ConnectTimeout() => new(
        DeepSeekTransportOutcome.ConnectTimeout,
        null,
        null,
        null,
        null,
        null,
        default);

    internal static DeepSeekTransportResult ProviderTimeout() => new(
        DeepSeekTransportOutcome.ProviderTimeout,
        null,
        null,
        null,
        null,
        null,
        default);

    internal static DeepSeekTransportResult TransportFailure() => new(
        DeepSeekTransportOutcome.TransportFailure,
        null,
        null,
        null,
        null,
        null,
        default);

    public override string ToString() => Outcome switch
    {
        DeepSeekTransportOutcome.RequestRejected =>
            $"request_rejected(actual_count={ActualCount})",
        DeepSeekTransportOutcome.Success =>
            $"success(status={Status},captured_count={CapturedCount})",
        DeepSeekTransportOutcome.ResponseTooLarge =>
            $"response_too_large(captured_count={CapturedCount})",
        DeepSeekTransportOutcome.HttpFailure =>
            $"http_failure(status_class={StatusClass}," +
            $"discarded_error_count={DiscardedErrorCount})",
        DeepSeekTransportOutcome.ConnectTimeout => "connect_timeout",
        DeepSeekTransportOutcome.ProviderTimeout => "provider_timeout",
        DeepSeekTransportOutcome.TransportFailure => "transport_failure",
        _ => nameof(DeepSeekTransportResult),
    };

    private static bool ValidState(
        DeepSeekTransportOutcome outcome,
        int? status,
        DeepSeekHttpStatusClass? statusClass,
        int? actualCount,
        int? capturedCount,
        int? discardedErrorCount,
        ImmutableArray<byte> body) =>
        outcome switch
        {
            DeepSeekTransportOutcome.RequestRejected =>
                status is null &&
                statusClass is null &&
                actualCount == DeepSeekTransportPolicy.RequestRejectedCount &&
                capturedCount is null &&
                discardedErrorCount is null &&
                body.IsDefault,
            DeepSeekTransportOutcome.Success =>
                status == 200 &&
                statusClass is null &&
                actualCount is null &&
                capturedCount is >= 0 and
                    <= DeepSeekTransportPolicy.SuccessBodyMaxBytes &&
                !body.IsDefault &&
                capturedCount == body.Length &&
                discardedErrorCount is null,
            DeepSeekTransportOutcome.ResponseTooLarge =>
                status is null &&
                statusClass is null &&
                actualCount is null &&
                capturedCount ==
                    DeepSeekTransportPolicy.ResponseTooLargeCount &&
                discardedErrorCount is null &&
                body.IsDefault,
            DeepSeekTransportOutcome.HttpFailure =>
                status is null &&
                statusClass is not null &&
                Enum.IsDefined(statusClass.Value) &&
                actualCount is null &&
                capturedCount is null &&
                discardedErrorCount is >= 0 and
                    <= DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes &&
                body.IsDefault,
            DeepSeekTransportOutcome.ConnectTimeout or
            DeepSeekTransportOutcome.ProviderTimeout or
            DeepSeekTransportOutcome.TransportFailure =>
                status is null &&
                statusClass is null &&
                actualCount is null &&
                capturedCount is null &&
                discardedErrorCount is null &&
                body.IsDefault,
            _ => false,
        };
}
