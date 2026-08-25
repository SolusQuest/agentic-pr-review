using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal static class TrustedProofPayloadHost
{
    internal const string ProofKind =
        "apr-r4-e2p-trusted-proof-payload-v2";
    internal const string ProofRole = "r4-e2p";
    internal const string PayloadBuildDiscriminator = "r4-w2";

    internal static async Task<int> RunAsync()
    {
        using var signals = new CancellationTokenSource();
        using var sigterm = Register(PosixSignal.SIGTERM, signals);
        using var sigint = Register(PosixSignal.SIGINT, signals);
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var bytes = await ReadFrameAsync(
            input,
            ActionHostContractBounds.MaximumLaunchDocumentBytes,
            CancellationToken.None).ConfigureAwait(false);
        if (!TryReadLaunch(bytes, out var launch))
        {
            return 1;
        }

        using var coordinator = await TrustedProofStaleWindowCoordinator
            .ResolveAsync(launch!, signals.Token).ConfigureAwait(false);
        var dependencies = TrustedProofPayloadComposition.CreateProductionLike(
            coordinator?.Signal);
        var coordinatorTask = coordinator?.CoordinateAsync(signals.Token);
        var completion = await new ActionHostComposition(dependencies)
            .RunAsync(launch!, signals.Token)
            .ConfigureAwait(false);
        signals.Cancel();
        if (coordinatorTask is not null &&
            !await coordinatorTask.ConfigureAwait(false) &&
            completion.ProcessExitCode == 0)
        {
            return 1;
        }

        return await WriteCompletionAsync(output, completion)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        Stream input,
        Stream output,
        ActionHostCompositionDependencies dependencies,
        CancellationToken cancellationToken)
    {
        using var signals = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            signals.Token);
        using var sigterm = Register(PosixSignal.SIGTERM, signals);
        using var sigint = Register(PosixSignal.SIGINT, signals);
        var bytes = await ReadFrameAsync(
            input,
            ActionHostContractBounds.MaximumLaunchDocumentBytes,
            cancellationToken).ConfigureAwait(false);
        if (!TryReadLaunch(bytes, out var launch))
        {
            return 1;
        }

        var completion = await new ActionHostComposition(dependencies)
            .RunAsync(launch!, linked.Token)
            .ConfigureAwait(false);
        return await WriteCompletionAsync(output, completion)
            .ConfigureAwait(false);
    }

    private static bool TryReadLaunch(
        byte[]? bytes,
        out ActionHostLaunchContract? launch)
    {
        launch = null;
        return bytes is not null &&
            ActionHostJsonCodec.TryReadLaunch(bytes, out launch, out _) &&
            launch is not null &&
            StringComparer.Ordinal.Equals(
                launch.BuildDiscriminator,
                PayloadBuildDiscriminator);
    }

    private static async Task<int> WriteCompletionAsync(
        Stream output,
        ActionHostCompletion completion)
    {
        if (!ActionHostJsonCodec.TryWriteCompletion(completion, out var document))
        {
            return 1;
        }

        await WriteFrameAsync(output, document, CancellationToken.None)
            .ConfigureAwait(false);
        return completion.ProcessExitCode;
    }

    private static PosixSignalRegistration Register(
        PosixSignal signal,
        CancellationTokenSource cancellation) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

    private static async Task<byte[]?> ReadFrameAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(input, header, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 || length > maximumBytes)
        {
            return null;
        }

        var document = new byte[length];
        if (!await ReadExactlyAsync(input, document, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        var trailing = new byte[1];
        return await input.ReadAsync(trailing, cancellationToken)
                .ConfigureAwait(false) == 0
            ? document
            : null;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task WriteFrameAsync(
        Stream output,
        byte[] document,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            header,
            checked((uint)document.Length));
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
