using System.Buffers.Binary;
using System.Collections;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkHost
{
    private static readonly string[] ExpectedEnvironment =
    [
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        "NO_COLOR",
        "TMPDIR",
    ];

    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux() || !HasClosedEnvironment())
        {
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        using var sigterm = Register(PosixSignal.SIGTERM, cancellation);
        using var sigint = Register(PosixSignal.SIGINT, cancellation);
        var bytes = await ReadFrameAsync(
            Console.OpenStandardInput(),
            ActionHostContractBounds.MaximumLaunchDocumentBytes,
            CancellationToken.None).ConfigureAwait(false);
        if (bytes is null ||
            !ActionHostJsonCodec.TryReadLaunch(
                bytes,
                out var launch,
                out _) ||
            launch is null)
        {
            return 1;
        }

        var scenarioRoot = Path.GetDirectoryName(launch.EventJsonPath);
        if (string.IsNullOrEmpty(scenarioRoot) ||
            !Directory.Exists(scenarioRoot))
        {
            return 1;
        }

        await File.WriteAllTextAsync(
            Path.Join(scenarioRoot, "host.pid"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            Path.Join(scenarioRoot, "host-environment.keys"),
            ExpectedEnvironment,
            CancellationToken.None).ConfigureAwait(false);
        RecordObservation(scenarioRoot, "repository", "host.launch");
        RecordObservation(scenarioRoot, "workflow-source", "host.launch");
        if (launch.Inputs.StateKey is not null)
        {
            RecordObservation(scenarioRoot, "state-key-current",
                "state.cryptographic-memory");
        }
        if (launch.Inputs.PreviousStateKey is not null)
        {
            RecordObservation(scenarioRoot, "state-key-previous",
                "state.cryptographic-memory");
        }

        var mode = File.ReadAllText(Path.Join(scenarioRoot, "mode")).Trim();
        if (mode == "cancel-before-side-effect")
        {
            await File.WriteAllTextAsync(
                Path.Join(scenarioRoot, "cancel-before-side-effect-ready"),
                "1",
                CancellationToken.None).ConfigureAwait(false);
            await WaitForCancellationAsync(cancellation.Token)
                .ConfigureAwait(false);
        }

        if (File.Exists(Path.Join(scenarioRoot, "stall-after-signal")))
        {
            await WaitForCancellationAsync(cancellation.Token)
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(Path.Join(scenarioRoot, "wait-for-crash")))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 1;
        }

        Func<HttpMessageHandler> githubHandlers =
            () => new FrameworkGitHubHandler(scenarioRoot);
        var github = new ActionHostGitHubAuthorizationTransportFactory(
            githubHandlers);
        var publisher = new BoundedGitHubPublisherTransportFactory(
            githubHandlers);
        var provider = new ActionHostDeepSeekProviderRunnerFactory(
            credential => DeepSeekTransport.CreateForTesting(
                credential,
                new FrameworkProviderHandler(scenarioRoot),
                TimeSpan.FromSeconds(10)));
        var dependencies = new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            new FrameworkStateDependencies(scenarioRoot, github),
            publisher,
            provider,
            new FrameworkTimeProvider(),
            () => Path.Join(scenarioRoot, "host-staging"),
            new PostAcceptanceInlinePublisherHook(publisher));
        var completion = await new ActionHostComposition(dependencies)
            .RunAsync(launch, cancellation.Token)
            .ConfigureAwait(false);
        if (!ActionHostJsonCodec.TryWriteCompletion(completion, out var output))
        {
            return 1;
        }

        await WriteFrameAsync(
            Console.OpenStandardOutput(),
            output,
            CancellationToken.None).ConfigureAwait(false);
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

    private static bool HasClosedEnvironment()
    {
        var names = Environment.GetEnvironmentVariables()
            .Keys
            .Cast<object>()
            .Select(value => value.ToString() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return names.SequenceEqual(ExpectedEnvironment, StringComparer.Ordinal) &&
            Environment.GetEnvironmentVariable("NO_COLOR") == "1" &&
            Environment.GetEnvironmentVariable("DOTNET_NOLOGO") == "1" &&
            Environment.GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT") ==
                "1" &&
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("TMPDIR"));
    }

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
        await output.WriteAsync(header, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(document, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).SetResult(),
            completion);
        await completion.Task.ConfigureAwait(false);
    }

    private static void RecordObservation(
        string scenarioRoot,
        string canaryClass,
        string sink) => File.AppendAllText(
            Path.Join(scenarioRoot, "canary-observations.tsv"),
            canaryClass + "\t" + sink + "\n");

    private sealed class FrameworkTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    }
}
