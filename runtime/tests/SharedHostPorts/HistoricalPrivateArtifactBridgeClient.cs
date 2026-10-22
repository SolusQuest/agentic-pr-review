// Historical bridge fixture only; not compiled into the production runtime.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using System.Security.Principal;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

#if APR_HISTORICAL_UNIT_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalUnit;
#elif APR_HISTORICAL_FRAMEWORK_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalFramework;
#else
namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
#endif

internal sealed class PrivateArtifactBridgeClient(
    string endpoint,
    string buildDiscriminator,
    IArtifactBridgeConnectionFactory? connectionFactory = null)
{
    private readonly IArtifactBridgeConnectionFactory connectionFactory =
        connectionFactory ?? new ArtifactBridgeEndpointConnectionFactory();

    internal async Task<ArtifactBridgeResultDocument> ExchangeAsync<TCommand>(
        TCommand command,
        JsonTypeInfo<ActionHostPrivateCommandEnvelope<TCommand>> commandType,
        CancellationToken logicalCancellationToken)
        where TCommand : class, IActionHostPrivateCommandDocument
        => await ExchangeDocumentAsync<TCommand, ArtifactBridgeResultDocument>(command, commandType, logicalCancellationToken)
            .ConfigureAwait(false);

    internal Task<ArtifactUploadResultDocument> UploadAsync(
        ArtifactBridgeUploadCommandDocument command, CancellationToken cancellationToken) =>
        ExchangeDocumentAsync<ArtifactBridgeUploadCommandDocument, ArtifactUploadResultDocument>(command,
            RequireTypeInfo<ActionHostPrivateCommandEnvelope<ArtifactBridgeUploadCommandDocument>>(ArtifactBridgeJsonContext.Default), cancellationToken);

    private async Task<TResult> ExchangeDocumentAsync<TCommand, TResult>(TCommand command,
        JsonTypeInfo<ActionHostPrivateCommandEnvelope<TCommand>> commandType, CancellationToken logicalCancellationToken)
        where TCommand : class, IActionHostPrivateCommandDocument
        where TResult : class, IActionHostPrivateCommandResultDocument
    {
        logicalCancellationToken.ThrowIfCancellationRequested();
        var envelope = new ActionHostPrivateCommandEnvelope<TCommand>(
            buildDiscriminator,
            command);
        if (!ActionHostPrivateCommandCodec.TryWriteCommand(
                envelope,
                buildDiscriminator,
                commandType,
                out var commandBytes))
        {
            throw new ArtifactBridgeExchangeException(
                requestDispatched: false);
        }

        var frame = new byte[checked(commandBytes.Length + sizeof(int))];
        BinaryPrimitives.WriteUInt32BigEndian(
            frame,
            checked((uint)commandBytes.Length));
        commandBytes.CopyTo(frame, sizeof(int));
        var requestTerminator = new byte[sizeof(int)];
        var dispatched = false;
        try
        {
            await using var stream = await WithRequestDeadlineAsync(
                    token => connectionFactory.ConnectAsync(endpoint, token),
                    logicalCancellationToken)
                .ConfigureAwait(false);
            await WithRequestDeadlineAsync(
                    async token =>
                    {
                        await stream.WriteAsync(frame, token)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                        dispatched = true;
                        await stream.WriteAsync(requestTerminator, token)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                    },
                    logicalCancellationToken)
                .ConfigureAwait(false);

            var header = new byte[sizeof(int)];
            await ReadExactlyWithDeadlineAsync(
                    stream,
                    header,
                    logicalCancellationToken)
                .ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > ArtifactBridgeLimits.MaximumDocumentBytes)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }
            var resultBytes = new byte[checked((int)length)];
            await ReadExactlyWithDeadlineAsync(
                    stream,
                    resultBytes,
                    logicalCancellationToken)
                .ConfigureAwait(false);
            var trailing = new byte[1];
            var trailingCount = await WithRequestDeadlineAsync(
                    token => stream.ReadAsync(trailing, token),
                    logicalCancellationToken)
                .ConfigureAwait(false);
            if (trailingCount != 0)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }

            var resultType = RequireTypeInfo<
                ActionHostPrivateCommandResultEnvelope<
                    TResult>>(
                ArtifactBridgeJsonContext.Default);
            if (!ActionHostPrivateCommandCodec.TryReadResult(
                    resultBytes,
                    buildDiscriminator,
                    resultType,
                    out ActionHostPrivateCommandResultEnvelope<
                        TResult>? result) ||
                result?.Payload is null)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }
            return result.Payload;
        }
        catch (OperationCanceledException)
        {
            throw new ArtifactBridgeExchangeException(
                dispatched,
                cancelled: true);
        }
        catch (ArtifactBridgeExchangeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            SocketException or
            UnauthorizedAccessException or
            ObjectDisposedException)
        {
            throw new ArtifactBridgeExchangeException(dispatched);
        }
    }

    private static async Task ReadExactlyWithDeadlineAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken logicalCancellationToken)
    {
        await WithRequestDeadlineAsync(
                async token =>
                {
                    var offset = 0;
                    while (offset < destination.Length)
                    {
                        var read = await stream.ReadAsync(
                                destination[offset..],
                                token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new ArtifactBridgeExchangeException(
                                requestDispatched: true);
                        }
                        offset += read;
                    }
                },
                logicalCancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<T> WithRequestDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken logicalCancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            logicalCancellationToken);
        request.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        return await operation(request.Token).ConfigureAwait(false);
    }

    private static async Task WithRequestDeadlineAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken logicalCancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            logicalCancellationToken);
        request.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        await operation(request.Token).ConfigureAwait(false);
    }

    private static JsonTypeInfo<T> RequireTypeInfo<T>(
        ArtifactBridgeJsonContext context) =>
        context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new InvalidOperationException(
            "artifact_bridge_json_metadata_missing");
}
