using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal interface IArtifactBridgeConnectionFactory
{
    void CompleteUploadRequest(Stream stream)
    {
        if (stream is not NetworkStream network) throw new IOException("upload_half_close_unavailable");
        network.Socket.Shutdown(SocketShutdown.Send);
    }

    ValueTask<Stream> ConnectAsync(
        string endpoint,
        CancellationToken cancellationToken);
}

internal sealed class ArtifactBridgeEndpointConnectionFactory
    : IArtifactBridgeConnectionFactory
{
    public async ValueTask<Stream> ConnectAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\";
            if (!endpoint.StartsWith(prefix, StringComparison.Ordinal) ||
                endpoint.Length <= prefix.Length)
            {
                throw new IOException("artifact_bridge_endpoint_invalid");
            }

            var stream = new NamedPipeClientStream(
                ".",
                endpoint[prefix.Length..],
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);
            try
            {
                await stream.ConnectAsync(cancellationToken)
                    .ConfigureAwait(false);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(endpoint),
                    cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class ArtifactBridgeExchangeException(
    bool requestDispatched,
    bool cancelled = false) : IOException("artifact_bridge_exchange_failed")
{
    internal bool RequestDispatched { get; } = requestDispatched;

    internal bool Cancelled { get; } = cancelled;
}
