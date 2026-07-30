using System.Text;
using System.Net;
using System.Net.Sockets;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class NegativeProofRunner
{
    private static readonly HashSet<string> EndpointCases =
        new(StringComparer.Ordinal)
        {
            "endpoint-https",
            "endpoint-host",
            "endpoint-path",
            "endpoint-userinfo",
            "endpoint-query",
            "endpoint-fragment",
            "endpoint-encoded-host",
        };

    internal static async Task<int> RunAsync(ProofCommand command)
    {
        var @case = command.Case!;
        var passed = @case switch
        {
            "authorization-untrusted" => Authorization(
                trusted: false,
                sameRepository: true,
                fork: false),
            "authorization-fork" => Authorization(
                trusted: true,
                sameRepository: true,
                fork: true),
            "authorization-cross-repository" => Authorization(
                trusted: true,
                sameRepository: false,
                fork: false),
            "missing-state-automatic" => MissingState(
                command,
                RestrictedStateLocatorFamily.Absent,
                RestrictedStateRestoreIntent.Automatic,
                StateAction.Bootstrap,
                RestrictedStateCodes.Absent),
            "bootstrap-non-current" => BootstrapNonCurrent(command),
            "explicit-incompatible" => ExplicitIncompatible(command),
            "caller-cancellation" => await CallerCancellationAsync(),
            "deadline-before-model" => await DeadlineBeforeModelAsync(),
            "deadline-after-tool" => await DeadlineAfterToolAsync(command),
            "chat-sync-failure" => await ChatFailureAsync(
                new SynchronousFailureChatClient()),
            "chat-async-failure" => await ChatFailureAsync(
                new AsynchronousFailureChatClient()),
            "transport-cancellation" => await ChatFailureAsync(
                new TransportCancellationChatClient()),
            "request-cap" => await RequestBytesAsync(
                command,
                AgentLimits.RequestBytes,
                shouldSucceed: true),
            "request-cap-plus-one" => await RequestBytesAsync(
                command,
                AgentLimits.RequestBytes + 1,
                shouldSucceed: false),
            "response-cap" => await ResponseBytesAsync(
                command,
                AgentLimits.ResponseBytes,
                shouldSucceed: true),
            "response-cap-plus-one" => await ResponseBytesAsync(
                command,
                AgentLimits.ResponseBytes + 1,
                shouldSucceed: false),
            "response-length-disagreement" =>
                await ResponseLengthDisagreementAsync(command),
            "token-limit" => await TokenLimitAsync(command),
            "model-limit" => await ModelLimitAsync(command),
            "tool-limit" => await ToolLimitAsync(command),
            "tool-result-cap" => await ToolResultLimitAsync(
                command,
                aggregate: false),
            "tool-result-aggregate" => await ToolResultLimitAsync(
                command,
                aggregate: true),
            "redirect" => await RedirectAsync(
                command,
                crossHost: false),
            "redirect-cross-host" => await RedirectAsync(
                command,
                crossHost: true),
            "method-get" => await RawRequestRejectedAsync(
                command,
                "method"),
            "header-default" => await RawRequestRejectedAsync(
                command,
                "User-Agent"),
            "header-cookie" => await RawRequestRejectedAsync(
                command,
                "Cookie"),
            "header-proxy" => await RawRequestRejectedAsync(
                command,
                "Proxy-Authorization"),
            "header-trace" => await RawRequestRejectedAsync(
                command,
                "traceparent"),
            "header-github" => await RawRequestRejectedAsync(
                command,
                "X-GitHub-Token"),
            "header-ambient" => await RawRequestRejectedAsync(
                command,
                "X-Ambient"),
            "path-rejection" => await ToolRejectionAsync(
                command,
                unknownTool: false),
            "tool-rejection" => await ToolRejectionAsync(
                command,
                unknownTool: true),
            "invalid-terminal" => await InvalidTerminalAsync(command),
            "post-tool-chat-failure" =>
                await PostToolChatFailureAsync(command),
            _ when EndpointCases.Contains(@case) => Endpoint(@case),
            _ when StateNegativeProofRunner.Handles(@case) =>
                await StateNegativeProofRunner.RunAsync(command),
            _ => false,
        };
        if (!passed)
        {
            Console.Error.WriteLine(ProofCodes.ProofFailed);
            return 1;
        }

        var report = string.Concat(
            ProofCodes.NegativeOk,
            " ",
            @case,
            "\n");
        ProofFiles.WriteNew(command.Output, Encoding.ASCII.GetBytes(report));
        Console.Write(report);
        return 0;
    }

    private static bool Authorization(
        bool trusted,
        bool sameRepository,
        bool fork)
    {
        var result = ProofState.Authorize(
            trusted,
            sameRepository,
            fork,
            out var access);
        return result.Action == StateAction.Denied &&
            StringComparer.Ordinal.Equals(
                result.Code,
                RestrictedStateCodes.AccessDenied) &&
            access is null;
    }

    private static bool BootstrapNonCurrent(ProofCommand command) =>
        MissingState(
            command,
            RestrictedStateLocatorFamily.NonCurrent,
            RestrictedStateRestoreIntent.Automatic,
            StateAction.Bootstrap,
            RestrictedStateCodes.Absent);

    private static bool ExplicitIncompatible(ProofCommand command) =>
        MissingState(
            command,
            RestrictedStateLocatorFamily.NonCurrent,
            RestrictedStateRestoreIntent.Explicit,
            StateAction.Failed,
            RestrictedStateCodes.ExplicitMissing);

    private static bool MissingState(
        ProofCommand command,
        RestrictedStateLocatorFamily family,
        RestrictedStateRestoreIntent intent,
        StateAction action,
        string code)
    {
        var authorized = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out var access);
        if (authorized.Action != StateAction.Authorized || access is null)
        {
            return false;
        }

        var store = new CountingStateStore(
            new LocalRestrictedStateStore(ProofPaths.StateRoot(command)));
        var keys = new SyntheticStateKeyResolver(
            string.Concat("issue88-negative-", command.Case));
        var admission = new CountingSessionAdmission(
            new AgentSessionRestrictedStateAdmission());
        var service = new RestrictedStateService(
            store,
            keys,
            admission,
            () => ProofScenario.Now);
        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                family,
                intent,
                null,
                PlaceholderStateContext()),
            CancellationToken.None);
        return result.Result.Action == action &&
            StringComparer.Ordinal.Equals(result.Result.Code, code) &&
            store.Calls == 0 &&
            keys.Accesses == 0 &&
            admission.Calls == 0;
    }

    private static async Task<bool> CallerCancellationAsync()
    {
        var chat = new CountingChatClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await new AgentLoop(
            chat,
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), cancellation.Token);
        return !outcome.CompletedSessionEligible &&
            StringComparer.Ordinal.Equals(
                outcome.Diagnostic?.Code,
                AgentFailureCodes.Cancelled) &&
            chat.Invocations == 0;
    }

    private static async Task<bool> DeadlineBeforeModelAsync()
    {
        var chat = new CountingChatClient();
        var time = new SyntheticTimeProvider(
            ProofScenario.Now,
            advanceAfterFirstRead:
                TimeSpan.FromSeconds(AgentLimits.DeadlineSeconds + 1));
        var outcome = await new AgentLoop(
            chat,
            new NeverToolExecutor(),
            time).RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.DeadlineExceeded,
                modelCalls: 0,
                toolCalls: 0) &&
            chat.Invocations == 0;
    }

    private static async Task<bool> DeadlineAfterToolAsync(
        ProofCommand command)
    {
        var authorized = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out var access);
        if (authorized.Action != StateAction.Authorized || access is null)
        {
            return false;
        }

        var identity = ProofScenario.BootstrapIdentity();
        var time = new SyntheticTimeProvider(ProofScenario.Now);
        var providerCanary = CanarySet.Provider(
            "issue88-negative-deadline-after-tool");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [ProviderScripts.BootstrapRead]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath]);
        var realTool = new SnapshotToolExecutor(
            snapshot,
            new VerifiedReviewedFileAccess());
        var tool = new AdvancingToolExecutor(realTool, time);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            tool,
            time).RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.DeadlineExceeded,
                modelCalls: 1,
                toolCalls: 1) &&
            server.Captures.Count == 1 &&
            tool.Executions == 1 &&
            !File.Exists(ProofPaths.Lineage(command)) &&
            !Directory.Exists(ProofPaths.StateRoot(command));
    }

    private static async Task<bool> ChatFailureAsync(IProjectChatClient chat)
    {
        var outcome = await new AgentLoop(
            chat,
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.ChatFailed,
                modelCalls: 1,
                toolCalls: 0);
    }

    private static async Task<bool> RequestBytesAsync(
        ProofCommand command,
        int targetBytes,
        bool shouldSucceed)
    {
        var run = SizedRun(targetBytes);
        var providerCanary = CanarySet.Provider(
            string.Concat("issue88-negative-", command.Case));
        IReadOnlyList<Func<byte[], ProviderServerResponse>> scripts =
            shouldSucceed
                ? [body => ProviderScripts.DirectFinish(body)]
                : [];
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            scripts);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(run, CancellationToken.None);
        return shouldSucceed
            ? outcome.CompletedSessionEligible &&
                server.Captures.Count == 1
            : !outcome.CompletedSessionEligible &&
                HasDiagnostic(
                    outcome,
                    AgentFailureCodes.RequestTooLarge,
                    modelCalls: 0,
                    toolCalls: 0) &&
                server.Captures.Count == 0;
    }

    private static async Task<bool> ResponseBytesAsync(
        ProofCommand command,
        int targetBytes,
        bool shouldSucceed)
    {
        var providerCanary = CanarySet.Provider(
            string.Concat("issue88-negative-", command.Case));
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                body => ProviderScripts.DirectFinishAtBytes(
                    body,
                    targetBytes),
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return shouldSucceed
            ? outcome.CompletedSessionEligible &&
                server.Captures.Count == 1
            : !outcome.CompletedSessionEligible &&
                HasDiagnostic(
                    outcome,
                    AgentFailureCodes.ResponseTooLarge,
                    modelCalls: 1,
                    toolCalls: 0) &&
                server.Captures.Count == 1;
    }

    private static async Task<bool> ResponseLengthDisagreementAsync(
        ProofCommand command)
    {
        var providerCanary = CanarySet.Provider(
            "issue88-negative-response-length");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                body =>
                {
                    var response = ProviderScripts.DirectFinish(body);
                    return response with
                    {
                        DeclaredContentLength = response.Body.Length + 1,
                    };
                },
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            StringComparer.Ordinal.Equals(
                outcome.Diagnostic?.Code,
                AgentFailureCodes.ChatFailed) &&
            server.Captures.Count == 1;
    }

    private static async Task<bool> TokenLimitAsync(ProofCommand command)
    {
        var providerCanary = CanarySet.Provider(
            "issue88-negative-token-limit");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                body => ProviderScripts.DirectFinishWithUsage(
                    body,
                    AgentLimits.InputTokens + 1,
                    0),
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.TokenLimit,
                modelCalls: 1,
                toolCalls: 0) &&
            server.Captures.Count == 1;
    }

    private static async Task<bool> ModelLimitAsync(ProofCommand command)
    {
        var identity = ProofScenario.BootstrapIdentity();
        var providerCanary = CanarySet.Provider(
            "issue88-negative-model-limit");
        var scripts = Enumerable.Range(0, AgentLimits.ModelCalls)
            .Select<int, Func<byte[], ProviderServerResponse>>(ordinal =>
                body => ProviderScripts.ReadCalls(
                    body,
                    ProofScenario.ReviewedPath,
                    ordinal,
                    1))
            .ToArray();
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            scripts);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath]);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.ModelLimit,
                AgentLimits.ModelCalls,
                AgentLimits.ModelCalls) &&
            server.Captures.Count == AgentLimits.ModelCalls;
    }

    private static async Task<bool> ToolLimitAsync(ProofCommand command)
    {
        var identity = ProofScenario.BootstrapIdentity();
        var providerCanary = CanarySet.Provider(
            "issue88-negative-tool-limit");
        Func<byte[], ProviderServerResponse>[] scripts =
        [
            body => ProviderScripts.ReadCalls(body, ProofScenario.ReviewedPath, 0, 8),
            body => ProviderScripts.ReadCalls(body, ProofScenario.ReviewedPath, 8, 8),
            body => ProviderScripts.ReadCalls(body, ProofScenario.ReviewedPath, 16, 8),
            body => ProviderScripts.ReadCalls(body, ProofScenario.ReviewedPath, 24, 1),
        ];
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            scripts);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath]);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.ToolLimit,
                modelCalls: 4,
                toolCalls: AgentLimits.ToolCalls) &&
            server.Captures.Count == 4;
    }

    private static async Task<bool> ToolResultLimitAsync(
        ProofCommand command,
        bool aggregate)
    {
        var authorized = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out var access);
        if (authorized.Action != StateAction.Authorized || access is null)
        {
            return false;
        }

        const string path = "reviewed/large.txt";
        var repositoryRoot = ProofPaths.RepositoryRoot(command);
        if (aggregate)
        {
            Directory.CreateDirectory(Path.Join(repositoryRoot, "reviewed"));
            File.WriteAllText(
                Path.Join(repositoryRoot, "reviewed", "large.txt"),
                string.Concat(new string('x', 30_000), "\n"),
                new UTF8Encoding(false));
        }
        var providerCanary = CanarySet.Provider(
            aggregate
                ? "issue88-negative-tool-result-aggregate"
                : "issue88-negative-tool-result-cap");
        Func<byte[], ProviderServerResponse>[] scripts = aggregate
            ?
            [
                body => ProviderScripts.ReadCalls(body, path, 0, 8),
                body => ProviderScripts.ReadCalls(body, path, 8, 1),
            ]
            :
            [
                body => ProviderScripts.ReadCalls(body, path, 0, 1),
            ];
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            scripts);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var identity = ProofScenario.BootstrapIdentity();
        IAgentToolExecutor tool;
        if (aggregate)
        {
            var snapshot = new ReviewedSnapshot(
                identity,
                repositoryRoot,
                [path]);
            tool = new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess());
        }
        else
        {
            tool = new OversizedResultToolExecutor();
        }

        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            tool,
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.ToolResultLimit,
                modelCalls: aggregate ? 2 : 1,
                toolCalls: aggregate ? 9 : 1) &&
            server.Captures.Count == (aggregate ? 2 : 1) &&
            !File.Exists(ProofPaths.Lineage(command)) &&
            !Directory.Exists(ProofPaths.StateRoot(command));
    }

    private static async Task<bool> RedirectAsync(
        ProofCommand command,
        bool crossHost)
    {
        var providerCanary = CanarySet.Provider(
            crossHost
                ? "issue88-negative-redirect-cross-host"
                : "issue88-negative-redirect");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                _ => new ProviderServerResponse(
                    "{}"u8.ToArray(),
                    StatusCode: 302,
                    Location: crossHost
                        ? "http://127.0.0.1:9/v1/chat/completions"
                        : "/v1/chat/completions"),
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            StringComparer.Ordinal.Equals(
                outcome.Diagnostic?.Code,
                AgentFailureCodes.ChatFailed) &&
            server.Captures.Count == 1;
    }

    private static async Task<bool> RawRequestRejectedAsync(
        ProofCommand command,
        string mutation)
    {
        var providerCanary = CanarySet.Provider(
            string.Concat("issue88-negative-", command.Case));
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [body => ProviderScripts.DirectFinish(body)]);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(
            IPAddress.Loopback,
            server.Endpoint.Port);
        await using (var stream = client.GetStream())
        {
            var method = StringComparer.Ordinal.Equals(mutation, "method")
                ? "GET"
                : "POST";
            var extra = StringComparer.Ordinal.Equals(mutation, "method")
                ? string.Empty
                : string.Concat(
                    mutation,
                    ": synthetic-forbidden\r\n");
            var request = string.Concat(
                method,
                " /v1/chat/completions HTTP/1.1\r\n",
                "Host: 127.0.0.1:",
                server.Endpoint.Port,
                "\r\nAuthorization: Bearer ",
                providerCanary,
                "\r\nAccept: application/json\r\n",
                "Content-Type: application/json; charset=utf-8\r\n",
                "Content-Length: 2\r\n",
                extra,
                "\r\n{}");
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(request));
            await stream.FlushAsync();
        }

        await server.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        return server.Rejected && server.Captures.Count == 0;
    }

    private static bool Endpoint(string @case)
    {
        var value = @case switch
        {
            "endpoint-https" =>
                "https://127.0.0.1:1234/v1/chat/completions",
            "endpoint-host" =>
                "http://localhost:1234/v1/chat/completions",
            "endpoint-path" =>
                "http://127.0.0.1:1234/v1/other",
            "endpoint-userinfo" =>
                "http://user@127.0.0.1:1234/v1/chat/completions",
            "endpoint-query" =>
                "http://127.0.0.1:1234/v1/chat/completions?q=1",
            "endpoint-fragment" =>
                "http://127.0.0.1:1234/v1/chat/completions#fragment",
            "endpoint-encoded-host" =>
                "http://127%2e0%2e0%2e1:1234/v1/chat/completions",
            _ => throw new InvalidOperationException(),
        };
        return !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            !ProviderEndpoint.IsAllowed(endpoint);
    }

    private static async Task<bool> ToolRejectionAsync(
        ProofCommand command,
        bool unknownTool)
    {
        var identity = ProofScenario.BootstrapIdentity();
        var providerCanary = CanarySet.Provider(
            string.Concat("issue88-negative-", command.Case));
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                unknownTool
                    ? ProviderScripts.UnknownTool
                    : body => ProviderScripts.ReadCalls(
                        body,
                        "untracked.txt",
                        0,
                        1),
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath]);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                unknownTool
                    ? AgentFailureCodes.UnknownTool
                    : AgentFailureCodes.ToolPathNotTracked,
                modelCalls: 1,
                toolCalls: 0) &&
            server.Captures.Count == 1 &&
            !File.Exists(ProofPaths.Lineage(command)) &&
            !Directory.Exists(ProofPaths.StateRoot(command));
    }

    private static async Task<bool> InvalidTerminalAsync(
        ProofCommand command)
    {
        var providerCanary = CanarySet.Provider(
            "issue88-negative-invalid-terminal");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [ProviderScripts.InvalidTerminal]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.TerminalInvalid,
                modelCalls: 1,
                toolCalls: 1) &&
            server.Captures.Count == 1 &&
            !File.Exists(ProofPaths.Lineage(command)) &&
            !Directory.Exists(ProofPaths.StateRoot(command));
    }

    private static async Task<bool> PostToolChatFailureAsync(
        ProofCommand command)
    {
        var identity = ProofScenario.BootstrapIdentity();
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath]);
        var chat = new ToolThenFailureChatClient();
        var outcome = await new AgentLoop(
            chat,
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now))
            .RunAsync(Run(identity), CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            HasDiagnostic(
                outcome,
                AgentFailureCodes.ChatFailed,
                modelCalls: 2,
                toolCalls: 1) &&
            chat.Invocations == 2 &&
            !File.Exists(ProofPaths.Lineage(command)) &&
            !Directory.Exists(ProofPaths.StateRoot(command));
    }

    private static bool HasDiagnostic(
        AgentRunOutcome outcome,
        string code,
        int modelCalls,
        int toolCalls)
    {
        var diagnostic = outcome.Diagnostic;
        return diagnostic is not null &&
            StringComparer.Ordinal.Equals(diagnostic.Code, code) &&
            diagnostic.ModelCalls == modelCalls &&
            diagnostic.ToolCalls == toolCalls;
    }

    private static AgentRunRequest Run(
        ReviewedIdentity? identity = null)
    {
        var trusted = ProofScenario.Trusted();
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized))
        {
            throw new InvalidOperationException();
        }

        return new AgentRunRequest(
            identity ?? ProofScenario.BootstrapIdentity(),
            materialized!.StablePlan,
            ProofScenario.SessionId,
            [
                .. materialized.ControlMessages,
                ProofScenario.User("Synthetic negative review context."),
            ]);
    }

    private static AgentRunRequest SizedRun(int targetBytes)
    {
        var trusted = ProofScenario.Trusted();
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized))
        {
            throw new InvalidOperationException();
        }

        const int messagesCount = 8;
        const int partsPerMessage = 32;
        const int baseTextLength = 3_900;
        var messages = Enumerable.Range(0, messagesCount)
            .Select(messageIndex => new ProjectChatMessage(
                "user",
                Enumerable.Range(
                        0,
                        messageIndex == messagesCount - 1
                            ? partsPerMessage - 1
                            : partsPerMessage)
                    .Select(_ => (ProjectChatContent)new ProjectTextContent(
                        new string('x', baseTextLength)))
                    .ToArray()))
            .ToArray();
        var request = new ProjectChatRequest(
            messages,
            AgentToolRegistry.Definitions.ToArray(),
            null,
            ThinkingRequired: true);
        var currentBytes = AgentRequestWriter.Write(request).Length;
        var adjustment = targetBytes - currentBytes;
        var finalLength = baseTextLength + adjustment;
        if (finalLength is < 1 or > AgentLimits.ContentBytes)
        {
            throw new InvalidOperationException(
                "The synthetic request target is not constructible.");
        }

        var last = messages[^1];
        last.Contents[^1] = new ProjectTextContent(
            new string('x', finalLength));
        request = request with { Messages = messages };
        if (AgentRequestWriter.Write(request).Length != targetBytes)
        {
            throw new InvalidOperationException(
                "The synthetic request target did not converge.");
        }

        return new AgentRunRequest(
            ProofScenario.BootstrapIdentity(),
            materialized!.StablePlan,
            ProofScenario.SessionId,
            messages);
    }

    private static RestrictedStateSessionAdmissionContext
        PlaceholderStateContext() => new(
            ProofScenario.BootstrapIdentity().BaseSha,
            ProofScenario.BootstrapIdentity().HeadSha,
            0,
            null,
            new AgentSessionStateAdmissionContext(
                ProofScenario.Trusted(),
                ProofScenario.SessionId,
                ProofScenario.BootstrapIdentity(),
                ProofScenario.User("Synthetic placeholder context."),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance,
                null));

    private sealed class CountingChatClient : IProjectChatClient
    {
        internal int Invocations { get; private set; }

        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [new ProjectTextContent("not reached")]),
                new ProjectChatUsage(0, 0),
                1));
        }
    }

    private sealed class SynchronousFailureChatClient : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            throw new IOException("synthetic");
    }

    private sealed class AsynchronousFailureChatClient : IProjectChatClient
    {
        public async Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new IOException("synthetic");
        }
    }

    private sealed class TransportCancellationChatClient : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<ProjectChatResponse>(
                new CancellationToken(canceled: true));
    }

    private sealed class ToolThenFailureChatClient : IProjectChatClient
    {
        internal int Invocations { get; private set; }

        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            Invocations++;
            if (Invocations == 1)
            {
                return Task.FromResult(new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectToolCallContent(
                                "read-before-failure",
                                AgentToolRegistry.ReadFileName,
                                "{\"path\":\"reviewed/fact.txt\"}"),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1));
            }

            throw new IOException("synthetic post-tool failure");
        }
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class AdvancingToolExecutor(
        IAgentToolExecutor inner,
        SyntheticTimeProvider time) : IAgentToolExecutor
    {
        internal int Executions { get; private set; }

        public string? Preflight(PreparedAgentToolCall call) =>
            inner.Preflight(call);

        public async ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken)
        {
            Executions++;
            var result = await inner.ExecuteAsync(call, cancellationToken);
            time.Advance(
                TimeSpan.FromSeconds(AgentLimits.DeadlineSeconds + 1));
            return result;
        }
    }

    private sealed class OversizedResultToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken)
        {
            var bytes = Enumerable.Repeat(
                (byte)'x',
                AgentLimits.ToolResultBytes + 1).ToArray();
            return ValueTask.FromResult(new AgentToolExecution(
                true,
                null,
                new string('x', bytes.Length),
                bytes,
                null));
        }
    }
}
