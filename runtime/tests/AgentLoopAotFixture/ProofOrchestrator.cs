using System.Collections.Immutable;
using System.Collections;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProofOrchestrator
{
    internal static async Task<int> BootstrapAsync(ProofCommand command)
    {
        var authorization = ProofState.EnterAuthorizedComposition(
            trusted: true,
            sameRepository: true,
            fork: false,
            _ => true,
            out var access,
            out var entered);
        if (authorization.Action != StateAction.Authorized ||
            access is null ||
            !entered)
        {
            WriteFailureCode(command, "bootstrap_authorization", 0);
            return Fail();
        }

        WriteStartupNonce(command);
        var keyResolver = new SyntheticStateKeyResolver("issue88-proof");
        var localStore = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var store = new CountingStateStore(localStore);
        var admission = new CountingSessionAdmission(
            new AgentSessionRestrictedStateAdmission());
        var service = new RestrictedStateService(
            store,
            keyResolver,
            admission,
            () => ProofScenario.Now);
        var bootstrapContext = new RestrictedStateSessionAdmissionContext(
            ProofScenario.BootstrapIdentity().BaseSha,
            ProofScenario.BootstrapIdentity().HeadSha,
            0,
            null,
            new AgentSessionStateAdmissionContext(
                ProofScenario.Trusted(),
                ProofScenario.SessionId,
                ProofScenario.BootstrapIdentity(),
                ProofScenario.User("Synthetic bootstrap context."),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance,
                null));
        var restored = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Absent,
                RestrictedStateRestoreIntent.Automatic,
                null,
                bootstrapContext),
            CancellationToken.None);
        if (restored.Result.Action != StateAction.Bootstrap ||
            !StringComparer.Ordinal.Equals(
                restored.Result.Code,
                RestrictedStateCodes.Absent) ||
            store.Calls != 0 ||
            keyResolver.Accesses != 0 ||
            admission.Calls != 0)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "bootstrap_restore_",
                    restored.Result.Action,
                    "_",
                    restored.Result.Code,
                    "_store",
                    store.Calls,
                    "_key",
                    keyResolver.Accesses,
                    "_session",
                    admission.Calls),
                0);
            return Fail();
        }

        Directory.CreateDirectory(ProofPaths.StateRoot(command));
        var trusted = ProofScenario.Trusted();
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized))
        {
            WriteFailureCode(command, "bootstrap_materialize", 0);
            return Fail();
        }

        var identity = ProofScenario.BootstrapIdentity();
        var context = ProofScenario.User(
            "Review the synthetic snapshot. Use read_file and each R3 observation tool before finishing.");
        var run = new AgentRunRequest(
            identity,
            materialized!.StablePlan,
            ProofScenario.SessionId,
            [.. materialized.ControlMessages, context]);
        var canaries = ProofCanaryValues.Create("issue88-proof");
        var providerCanary = canaries.Provider;
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                ProviderScripts.BootstrapR3Observations,
                ProviderScripts.BootstrapFinish,
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var chat = new MinimalChatClient(backend);
        var diffSource = ProofScenario.BootstrapDiffSource(identity);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath],
            [ProofScenario.BootstrapChangedFile(diffSource)],
            [diffSource]);
        var loop = new AgentLoop(
            chat,
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now));
        var outcome = await loop.RunAsync(run, CancellationToken.None);
        if (!outcome.CompletedSessionEligible ||
            outcome.Review is null ||
            server.Captures.Count != 2)
        {
            WriteFailureCode(
                command,
                outcome.Diagnostic?.Code ?? "agent_outcome_incomplete",
                server.Captures.Count);
            return Fail();
        }

        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                run,
                outcome,
                trusted,
                run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        if (!built.Succeeded || built.Artifact is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "bootstrap_session_",
                    built.FailureCode ?? "missing"),
                server.Captures.Count);
            return Fail();
        }

        var stateContext = ProofState.SessionContext(
            built.Artifact,
            identity,
            AgentSessionHeadTransition.SameHead);
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                built.Artifact.Plaintext,
                stateContext),
            CancellationToken.None);
        if (prepared.Result.Action != StateAction.Prepared ||
            prepared.Receipt is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "bootstrap_prepare_",
                    prepared.Result.Code),
                server.Captures.Count);
            return Fail();
        }

        var accepted = service.Accept(
            access,
            null,
            prepared.Receipt,
            stateContext,
            CancellationToken.None);
        if (accepted.Action != StateAction.Accepted ||
            accepted.Generation != 0 ||
            accepted.SessionSha256 is null ||
            accepted.EnvelopeSha256 is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "bootstrap_accept_",
                    accepted.Code),
                server.Captures.Count);
            return Fail();
        }

        var lineage = HostLineage.Create(
            access.Scope,
            accepted.Generation.Value,
            accepted.SessionSha256,
            accepted.EnvelopeSha256,
            null);
        ProofFiles.WriteNew(
            ProofPaths.Lineage(command),
            ProofJson.Write(lineage));
        var output = Output(
            "bootstrap",
            outcome,
            built.Artifact,
            run.StablePlan,
            server.Captures,
            "automatic_absent",
            "none",
            [
                "authorization_before_capabilities",
                "automatic_bootstrap_no_store",
                "real_snapshot_read_file_and_r3_tools_grounded_finish",
                "completed_session_only",
                "encrypted_state_accepted",
                "provider_canary_authorization_only",
                "nine_canary_channel_scan",
            ]);
        var outputBytes = ProofJson.Write(output);
        if (!CanaryScanner.VerifyPositive(
                canaries,
                server.Endpoint,
                server.Captures,
                ForbiddenCanaryChannels(
                    command,
                    built.Artifact,
                    outcome,
                    prepared.Receipt,
                    outputBytes)))
        {
            CryptographicOperations.ZeroMemory(canaries.StateKey);
            WriteFailureCode(command, "bootstrap_canary_scan", 0);
            return Fail();
        }

        CryptographicOperations.ZeroMemory(canaries.StateKey);
        ProofFiles.WriteNew(command.Output, [.. outputBytes, (byte)'\n']);
        Console.WriteLine(ProofCodes.BootstrapOk);
        return 0;
    }

    internal static async Task<int> ContinueAsync(ProofCommand command)
    {
        var authorization = ProofState.EnterAuthorizedComposition(
            trusted: true,
            sameRepository: true,
            fork: false,
            _ => true,
            out var access,
            out var entered);
        if (authorization.Action != StateAction.Authorized ||
            access is null ||
            !entered ||
            !HostLineage.TryRead(
                command,
                access.Scope,
                out var lineageFile,
                out var lineage) ||
            lineageFile is null ||
            lineage is null)
        {
            WriteFailureCode(command, "continue_authorization_or_lineage", 0);
            return Fail();
        }

        WriteStartupNonce(command);
        var keyResolver = new SyntheticStateKeyResolver("issue88-proof");
        var store = new CountingStateStore(
            new LocalRestrictedStateStore(ProofPaths.StateRoot(command)));
        var admission = new CountingSessionAdmission(
            new AgentSessionRestrictedStateAdmission());
        var service = new RestrictedStateService(
            store,
            keyResolver,
            admission,
            () => ProofScenario.Now);
        var identity = ProofScenario.ContinueIdentity();
        var restoreContext = new RestrictedStateSessionAdmissionContext(
            ProofScenario.BootstrapIdentity().BaseSha,
            ProofScenario.BootstrapIdentity().HeadSha,
            lineage.Generation,
            lineage.ExpectedPredecessorEnvelopeSha256,
            new AgentSessionStateAdmissionContext(
                ProofScenario.Trusted(),
                ProofScenario.SessionId,
                identity,
                ProofScenario.User(
                    "Continue the synthetic review using only restored history."),
                AgentSessionHeadTransition.VerifiedAhead,
                SyntheticContinuationCodec.Instance,
                null));
        var restored = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                restoreContext),
            CancellationToken.None);
        if (restored.Result.Action != StateAction.Restored ||
            restored.Session?.Value is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "continue_restore_",
                    restored.Result.Code),
                0);
            return Fail();
        }

        var admitted = restored.Session.Value;
        var run = admitted.RunRequest;
        var canaries = ProofCanaryValues.Create("issue88-proof");
        var providerCanary = canaries.Provider;
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [ProviderScripts.ContinueFinish]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var loop = new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now));
        var outcome = await loop.RunAsync(run, CancellationToken.None);
        if (!outcome.CompletedSessionEligible ||
            outcome.Review is null ||
            !outcome.Review.Summary.Contains(
                ProofScenario.PriorOnlyFact,
                StringComparison.Ordinal) ||
            server.Captures.Count != 1)
        {
            WriteFailureCode(
                command,
                outcome.Diagnostic?.Code ?? "continue_outcome_incomplete",
                server.Captures.Count);
            return Fail();
        }

        var predecessor = new AgentSessionPredecessor(
            admitted.Artifact.Plaintext,
            lineage.SessionSha256,
            lineage.EnvelopeSha256,
            lineage.Generation,
            ProofScenario.BootstrapIdentity().BaseSha,
            ProofScenario.BootstrapIdentity().HeadSha,
            lineage.ExpectedPredecessorEnvelopeSha256);
        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                run,
                outcome,
                ProofScenario.Trusted(),
                run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                predecessor,
                AgentSessionHeadTransition.VerifiedAhead));
        if (!built.Succeeded ||
            built.Artifact is null ||
            built.Artifact.Document.Generation != 1 ||
            !StringComparer.Ordinal.Equals(
                built.Artifact.Document.PredecessorStateSha256,
                lineage.EnvelopeSha256))
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "continue_session_",
                    built.FailureCode ?? "identity"),
                server.Captures.Count);
            return Fail();
        }

        var stateContext = ProofState.SessionContext(
            built.Artifact,
            identity,
            AgentSessionHeadTransition.VerifiedAhead);
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                built.Artifact.Plaintext,
                stateContext),
            CancellationToken.None);
        if (prepared.Result.Action != StateAction.Prepared ||
            prepared.Receipt is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "continue_prepare_",
                    prepared.Result.Code),
                server.Captures.Count);
            return Fail();
        }

        var accepted = service.Accept(
            access,
            lineage,
            prepared.Receipt,
            stateContext,
            CancellationToken.None);
        if (accepted.Action != StateAction.Accepted ||
            accepted.Generation != 1 ||
            accepted.SessionSha256 is null ||
            accepted.EnvelopeSha256 is null)
        {
            WriteFailureCode(
                command,
                string.Concat(
                    "continue_accept_",
                    accepted.Code),
                server.Captures.Count);
            return Fail();
        }

        var nextLineage = HostLineage.Create(
            access.Scope,
            accepted.Generation.Value,
            accepted.SessionSha256,
            accepted.EnvelopeSha256,
            lineage.EnvelopeSha256);
        ProofFiles.ReplaceAtomically(
            ProofPaths.Lineage(command),
            ProofJson.Write(nextLineage));
        var output = Output(
            "continue",
            outcome,
            built.Artifact,
            run.StablePlan,
            server.Captures,
            "verified_ahead",
            "present",
            [
                "authorization_before_restore",
                "independent_host_lineage",
                "producer_provenance_preserved",
                "dynamic_head_verified_ahead",
                "exact_continuation_reconstructed",
                "prior_only_fact_used",
                "next_generation_accepted",
                "nine_canary_channel_scan",
            ]);
        var outputBytes = ProofJson.Write(output);
        if (!CanaryScanner.VerifyPositive(
                canaries,
                server.Endpoint,
                server.Captures,
                ForbiddenCanaryChannels(
                    command,
                    built.Artifact,
                    outcome,
                    prepared.Receipt,
                    outputBytes)))
        {
            CryptographicOperations.ZeroMemory(canaries.StateKey);
            WriteFailureCode(command, "continue_canary_scan", 0);
            return Fail();
        }

        CryptographicOperations.ZeroMemory(canaries.StateKey);
        ProofFiles.WriteNew(command.Output, [.. outputBytes, (byte)'\n']);
        Console.WriteLine(ProofCodes.ContinueOk);
        return 0;
    }

    private static ProofOutput Output(
        string phase,
        AgentRunOutcome outcome,
        AgentSessionArtifact artifact,
        StableAgentPlan stablePlan,
        IReadOnlyList<ProviderCapture> captures,
        string transition,
        string predecessor,
        ImmutableArray<string> evidence) => new(
        "apr-agent-loop-proof",
        phase,
        "passed",
        captures.Count,
        outcome.Events.OfType<AgentToolCallEvent>().Count(),
        ThinkingRequired: true,
        artifact.Document.Generation,
        transition,
        StableSessionIdentitySha256(artifact),
        predecessor,
        AgentCanonical.StablePlanSha256(stablePlan),
        AgentCanonical.LimitsSha256(),
        AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions),
        captures.Select(capture =>
            AgentCanonical.HashRaw(capture.Body)).ToImmutableArray(),
        evidence);

    private static string StableSessionIdentitySha256(
        AgentSessionArtifact artifact)
    {
        var document = artifact.Document with
        {
            PredecessorStateSha256 =
                artifact.Document.PredecessorStateSha256 is null
                    ? null
                    : new string('0', 64),
        };
        if (!AgentSessionCodec.TryWrite(
                document,
                out var normalized,
                out _) ||
            normalized is null)
        {
            throw new InvalidOperationException(
                "The admitted session could not be normalized.");
        }

        return normalized.SessionSha256;
    }

    private static IEnumerable<byte[]> ForbiddenCanaryChannels(
        ProofCommand command,
        AgentSessionArtifact artifact,
        AgentRunOutcome outcome,
        PreparedStateReceipt receipt,
        byte[] output)
    {
        yield return artifact.Plaintext;
        yield return output;
        yield return System.Text.Encoding.UTF8.GetBytes(string.Join(
            "\n",
            receipt.Generation.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            receipt.SessionSha256,
            receipt.EnvelopeSha256,
            receipt.ObjectIdentity));

        foreach (var logicalEvent in outcome.Events)
        {
            switch (logicalEvent)
            {
                case AgentToolCallEvent call:
                    yield return call.CanonicalArguments.ToArray();
                    break;
                case AgentToolResultEvent result:
                    yield return result.CanonicalResult.ToArray();
                    break;
            }
        }

        foreach (DictionaryEntry entry in
            Environment.GetEnvironmentVariables())
        {
            yield return System.Text.Encoding.UTF8.GetBytes(string.Concat(
                entry.Key,
                "=",
                entry.Value));
        }

        var fixedArtifacts = new[]
        {
            ProofPaths.StartupNonce(command),
            ProofPaths.Lineage(command),
            Path.Join(
                ProofPaths.RepositoryRoot(command),
                ProofScenario.ReviewedPath),
        };
        foreach (var path in fixedArtifacts.Where(File.Exists))
        {
            yield return System.Text.Encoding.UTF8.GetBytes(
                Path.GetFileName(path));
            yield return File.ReadAllBytes(path);
        }

        var stateRoot = ProofPaths.StateRoot(command);
        if (Directory.Exists(stateRoot))
        {
            foreach (var path in Directory.EnumerateFiles(
                stateRoot,
                "*",
                SearchOption.AllDirectories))
            {
                yield return System.Text.Encoding.UTF8.GetBytes(
                    Path.GetFileName(path));
                yield return File.ReadAllBytes(path);
            }
        }
    }

    private static void WriteStartupNonce(ProofCommand command)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        try
        {
            ProofFiles.WriteNew(ProofPaths.StartupNonce(command), nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static int Fail()
    {
        Console.Error.WriteLine(ProofCodes.ProofFailed);
        return 1;
    }

    private static void WriteFailureCode(
        ProofCommand command,
        string code,
        int modelCalls)
    {
        var report = string.Concat(
            code,
            "\nmodel_calls=",
            modelCalls.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "\n");
        ProofFiles.WriteNew(
            Path.Join(command.Root, "failure.code"),
            System.Text.Encoding.ASCII.GetBytes(report));
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "No non-terminal tool call is expected.");
    }
}
