using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofVerifier;

internal static class TrustedProofVerifierHost
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

        var inputBytes = await ReadBoundedInputAsync(
            Console.OpenStandardInput(),
            ActionHostContractBounds.MaximumLaunchDocumentBytes + 4)
            .ConfigureAwait(false);
        if (inputBytes is null ||
            !TryReadLaunch(inputBytes, out var launch) ||
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

        if (!await RejectsReorderedContinuationAsync().ConfigureAwait(false))
        {
            return 1;
        }
        await File.WriteAllTextAsync(
            Path.Join(scenarioRoot, "provider-reordered-history-rejected"),
            "1\n").ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Join(scenarioRoot, "host.pid"),
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            Path.Join(scenarioRoot, "host-environment.keys"),
            ExpectedEnvironment).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenarioRoot, "compiled-payload-identity.tsv"),
            string.Join('\t',
                TrustedProofPayloadHost.ProofKind,
                TrustedProofPayloadBuildIdentity.SourceCommit,
                TrustedProofPayloadBuildIdentity.SourceTree) + "\n")
            .ConfigureAwait(false);

        Func<HttpMessageHandler> handlers = () => new VerifierRecordingHandler(
            scenarioRoot,
            "github",
            new FrameworkGitHubHandler(
                scenarioRoot,
                launch.PayloadSha256,
                TrustedProofV2WorkflowAdmission.Render));
        var github = new ActionHostGitHubAuthorizationTransportFactory(handlers);
        var publisher = new BoundedGitHubPublisherTransportFactory(handlers);

        using var cancellation = new CancellationTokenSource();
        using var sigterm = Register(PosixSignal.SIGTERM, cancellation);
        using var sigint = Register(PosixSignal.SIGINT, cancellation);
        using var coordinator = CreateStaleCoordinator(
            scenarioRoot,
            launch,
            handlers);
        var provider = new ActionHostDeepSeekProviderRunnerFactory(
            credential => DeepSeekTransport.CreateForTesting(
                credential,
                new VerifierRecordingHandler(
                    scenarioRoot,
                    "provider",
                    new TrustedProofDeterministicDeepSeekHandler(
                        credential.Value,
                        coordinator?.Signal)),
                TimeSpan.FromSeconds(10)));
        var dependencies = new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            new FrameworkStateDependencies(scenarioRoot, github),
            publisher,
            provider,
            new VerifierTimeProvider(),
            () => Path.Join(scenarioRoot, "host-staging"),
            new PostAcceptanceInlinePublisherHook(publisher),
            TrustedProofV2WorkflowAdmission.Instance);
        var coordinatorTask = coordinator?.CoordinateAsync(cancellation.Token);
        await using var input = new MemoryStream(inputBytes, writable: false);
        var result = await TrustedProofPayloadHost.RunAsync(
            input,
            Console.OpenStandardOutput(),
            dependencies,
            cancellation.Token).ConfigureAwait(false);
        cancellation.Cancel();
        if (coordinatorTask is not null &&
            !await coordinatorTask.ConfigureAwait(false) &&
            result == 0)
        {
            return 1;
        }

        return result;
    }

    private static TrustedProofStaleWindowCoordinator? CreateStaleCoordinator(
        string scenarioRoot,
        ActionHostLaunchContract launch,
        Func<HttpMessageHandler> handlers)
    {
        if (!StringComparer.Ordinal.Equals(
                File.ReadAllText(Path.Join(scenarioRoot, "mode")).Trim(),
                "stale"))
        {
            return null;
        }

        if (launch.Inputs.GitHubToken is null ||
            launch.Inputs.PullRequestNumber is not > 0)
        {
            throw new InvalidOperationException();
        }

        var coordinates = new TrustedProofControlCoordinates(
            FrameworkCanaries.ProofControlRepository,
            launch.RepositoryId,
            launch.Inputs.PullRequestNumber.Value,
            FrameworkGitHubHandler.HeadSha,
            TrustedProofStaleWindowCoordinator.StaleOperationId,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.RunId,
            launch.RunAttempt);
        return TrustedProofStaleWindowCoordinator.CreateForVerifier(
            coordinates,
            TrustedProofControlTransport.Create(
                coordinates,
                launch.Inputs.GitHubToken.ExportForPrivateLaunch(),
            handlers()));
    }

    private static async Task<bool> RejectsReorderedContinuationAsync()
    {
        const string credential = "verifier-private-provider-probe";
        var messages = new List<string>();
        using (var bootstrap = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential)))
        {
            for (var ordinal = 0; ordinal < 6; ordinal++)
            {
                using var response = await bootstrap.SendAsync(
                    ProviderRequest(credential, messages),
                    CancellationToken.None).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                var responseBytes = await response.Content.ReadAsByteArrayAsync()
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(responseBytes);
                var message = document.RootElement.GetProperty("choices")[0]
                    .GetProperty("message");
                var function = message.GetProperty("tool_calls")[0]
                    .GetProperty("function");
                var callId = message.GetProperty("tool_calls")[0]
                    .GetProperty("id").GetString()!;
                var name = function.GetProperty("name").GetString();
                var messageJson = message.GetRawText();
                if (name == "list_changed_files")
                {
                    messageJson = messageJson.Replace(
                        "\"arguments\":\"{}\"",
                        "\"arguments\":\"{\\\"after\\\":null}\"",
                        StringComparison.Ordinal);
                }
                else if (name == "list_files")
                {
                    messageJson = messageJson.Replace(
                        "\"arguments\":\"{}\"",
                        "\"arguments\":\"{\\\"prefix\\\":null," +
                            "\\\"after\\\":null}\"",
                        StringComparison.Ordinal);
                }

                messages.Add(messageJson);
                messages.Add(
                    "{\"role\":\"tool\",\"tool_call_id\":\"" + callId +
                    "\",\"content\":\"{\\\"result\\\":\\\"accepted\\\"}\"}");
            }
        }

        var firstCall = messages[0];
        var firstResult = messages[1];
        messages[0] = messages[2];
        messages[1] = messages[3];
        messages[2] = firstCall;
        messages[3] = firstResult;
        using var restored = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));
        using var rejected = await restored.SendAsync(
            ProviderRequest(credential, messages),
            CancellationToken.None).ConfigureAwait(false);
        return rejected.StatusCode == HttpStatusCode.BadRequest;
    }

    private static HttpRequestMessage ProviderRequest(
        string credential,
        IReadOnlyList<string> messages)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.deepseek.com/chat/completions")
        {
            Content = new StringContent(
                $"{{\"messages\":[{string.Join(',', messages)}]}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        return request;
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
            Environment.GetEnvironmentVariable(
                "DOTNET_CLI_TELEMETRY_OPTOUT") == "1" &&
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("TMPDIR"));
    }

    private static async Task<byte[]?> ReadBoundedInputAsync(
        Stream input,
        int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return output.Length == 0 ? null : output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private static bool TryReadLaunch(
        byte[] frame,
        out ActionHostLaunchContract? launch)
    {
        launch = null;
        if (frame.Length < 5)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        return length == frame.Length - 4 &&
            ActionHostJsonCodec.TryReadLaunch(frame[4..], out launch, out _) &&
            launch is not null &&
            StringComparer.Ordinal.Equals(
                launch.BuildDiscriminator,
                TrustedProofPayloadHost.PayloadBuildDiscriminator);
    }

    private sealed class VerifierTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    }
}
