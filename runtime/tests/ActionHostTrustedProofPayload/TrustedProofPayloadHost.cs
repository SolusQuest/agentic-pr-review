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
        if (!TrustedProofRequestBudgetProfile.TrySelectProduction(
                Environment.GetEnvironmentVariable, out var requestBudgetProfile) ||
            requestBudgetProfile is null)
        {
            return 1;
        }
        using var signals = new CancellationTokenSource();
        using var sigterm = Register(PosixSignal.SIGTERM, signals);
        using var sigint = Register(PosixSignal.SIGINT, signals);
        return await RunCoreAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                static _ => Task.FromResult(
                    TrustedProofPayloadRuntimePorts.Production),
                signals.Token,
                requestBudgetProfile)
            .ConfigureAwait(false);
    }

    // This is deliberately the only payload execution path.  The native
    // verifier supplies narrow outer ports; it cannot construct a composition,
    // a coordinator, or an unmetered GitHub transport.
    internal static async Task<int> RunCoreAsync(
        Stream input,
        Stream output,
        Func<ActionHostLaunchContract,
            Task<TrustedProofPayloadRuntimePorts>> createPortsAsync,
        CancellationToken cancellationToken,
        TrustedProofRequestBudgetProfile? requestBudgetProfile = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(createPortsAsync);
        requestBudgetProfile ??= TrustedProofRequestBudgetProfile.Measurement;
        var bytes = await ReadFrameAsync(
            input,
            ActionHostContractBounds.MaximumLaunchDocumentBytes,
            cancellationToken).ConfigureAwait(false);
        if (!TryReadLaunch(bytes, out var launch))
        {
            return 1;
        }

        var ports = await createPortsAsync(launch!).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(ports);

        var githubBudget = new TrustedProofGitHubRequestBudget(
            TrustedProofGitHubRequestBudget.MaximumAuthenticatedRestRequests,
            TrustedProofGitHubRequestBudget.MaximumAnonymousCodeloadRequests,
            ports.CreateGitHubInnerHandler,
            requestBudgetProfile.RemainingTailGuard);
        var controlBudget = new TrustedProofControlRequestBudget(
            remainingTailGuard: requestBudgetProfile.RemainingTailGuard);
        TrustedProofStaleWindowCoordinator? coordinator = null;
        Task<bool>? coordinatorTask = null;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        try
        {
            coordinator = await TrustedProofStaleWindowCoordinator
                .ResolveAsync(
                    launch!,
                    githubBudget.CreateHandler,
                    controlBudget,
                    operation.Token).ConfigureAwait(false);
            var dependencies = TrustedProofPayloadComposition.CreateProductionLike(
                githubBudget,
                ports,
                coordinator?.Signal);
            coordinatorTask = coordinator?.CoordinateAsync(operation.Token);
            var completion = await new ActionHostComposition(dependencies)
                .RunAsync(launch!, operation.Token)
                .ConfigureAwait(false);
            if (completion.ProcessExitCode != 0)
            {
                operation.Cancel();
            }

            var coordinatorSucceeded = coordinatorTask is null ||
                await coordinatorTask.ConfigureAwait(false);
            if (!coordinatorSucceeded && completion.ProcessExitCode == 0)
            {
                return 1;
            }

            return await WriteCompletionAsync(output, completion)
                .ConfigureAwait(false);
        }
        finally
        {
            operation.Cancel();
            if (coordinatorTask is not null && !coordinatorTask.IsCompleted)
            {
                try
                {
                    await coordinatorTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the primary Host failure while observing the
                    // canceled coordinator task before its transport is disposed.
                }
            }
            coordinator?.Dispose();
            githubBudget.WriteReceipt(Console.Error);
            controlBudget.WriteReceipt(Console.Error);
        }
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
