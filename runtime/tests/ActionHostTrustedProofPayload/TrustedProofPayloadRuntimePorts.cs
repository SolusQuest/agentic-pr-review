using System.Net;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// The native verifier may replace only these outer seams.  The payload owns
// every transport factory, its shared request ledger, and the composition.
internal sealed class TrustedProofPayloadRuntimePorts
{
    internal TrustedProofPayloadRuntimePorts(
        Func<HttpMessageHandler> createGitHubInnerHandler,
        Func<IActionHostGitObjectTransportFactory,
            IAcceptedStateProductionDependencies> createStateDependencies,
        Func<HttpMessageHandler, HttpMessageHandler>? wrapProviderHandler = null,
        TimeProvider? timeProvider = null,
        Func<string>? stagingParentFactory = null)
    {
        CreateGitHubInnerHandler = createGitHubInnerHandler ??
            throw new ArgumentNullException(nameof(createGitHubInnerHandler));
        CreateStateDependencies = createStateDependencies ??
            throw new ArgumentNullException(nameof(createStateDependencies));
        WrapProviderHandler = wrapProviderHandler ??
            (handler => handler);
        TimeProvider = timeProvider ?? TimeProvider.System;
        StagingParentFactory = stagingParentFactory;
    }

    internal Func<HttpMessageHandler> CreateGitHubInnerHandler { get; }

    internal Func<IActionHostGitObjectTransportFactory,
        IAcceptedStateProductionDependencies> CreateStateDependencies
    {
        get;
    }

    internal Func<HttpMessageHandler, HttpMessageHandler> WrapProviderHandler
    {
        get;
    }

    internal TimeProvider TimeProvider { get; }

    internal Func<string>? StagingParentFactory { get; }

    internal static TrustedProofPayloadRuntimePorts Production { get; } = new(
        static () => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            Credentials = null,
            MaxResponseDrainSize = 0,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
        },
        static github => new AcceptedStateProductionDependencies(github));
}
