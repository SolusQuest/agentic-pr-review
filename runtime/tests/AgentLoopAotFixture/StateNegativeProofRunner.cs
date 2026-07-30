using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class StateNegativeProofRunner
{
    private static readonly HashSet<string> Cases =
        new(StringComparer.Ordinal)
        {
            "head-same",
            "head-unknown",
            "head-diverged",
            "head-unrelated",
            "state-no-lineage",
            "state-tamper",
            "state-truncation",
            "state-oversize",
            "state-classification-invalid",
            "state-association-invalid",
            "state-cross-scope",
            "state-stale-replay",
            "state-header-magic",
            "state-header-namespace",
            "state-header-discriminator",
            "state-header-algorithm",
            "state-header-key-id",
            "state-header-length",
            "state-old-format-disguise",
            "state-current-downgrade",
            "state-accepted-newer-present",
            "state-accepted-newer-hidden",
            "state-outcome-unknown-retry",
            "state-randomized-envelope-conflict",
            "state-cleanup-failure",
            "state-capacity-limit",
            "session-construction-limit",
            "continuation-limit",
        };

    internal static bool Handles(string @case) => Cases.Contains(@case);

    internal static async Task<bool> RunAsync(ProofCommand command)
    {
        return command.Case switch
        {
            "head-same" => RestoreTransition(
                command,
                ProofScenario.BootstrapIdentity(),
                AgentSessionHeadTransition.SameHead,
                shouldRestore: true),
            "head-unknown" => RestoreTransition(
                command,
                ProofScenario.ContinueIdentity(),
                AgentSessionHeadTransition.Unknown,
                shouldRestore: false),
            "head-diverged" => RestoreTransition(
                command,
                ProofScenario.ContinueIdentity(),
                AgentSessionHeadTransition.Diverged,
                shouldRestore: false),
            "head-unrelated" => RestoreTransition(
                command,
                ProofScenario.ContinueIdentity(),
                AgentSessionHeadTransition.Unrelated,
                shouldRestore: false),
            "state-no-lineage" => RestoreDefect(
                command,
                noLineage: true,
                invalidAssociation: false,
                invalidClassification: false,
                stale: false),
            "state-classification-invalid" =>
                RestoreSessionDefect(command, invalidClassification: true),
            "state-association-invalid" =>
                RestoreSessionDefect(command, invalidClassification: false),
            "state-stale-replay" => RestoreDefect(
                command,
                noLineage: false,
                invalidAssociation: false,
                invalidClassification: false,
                stale: true),
            "state-cross-scope" => EnvelopeCrossScope(command),
            "state-oversize" => EnvelopeOversize(),
            "state-old-format-disguise" =>
                RestoreEnvelopeMutation(command, command.Case!),
            "state-accepted-newer-present" =>
                await AcceptedNewerPresentAsync(command),
            "state-accepted-newer-hidden" =>
                await AcceptedNewerHiddenAsync(command),
            "state-outcome-unknown-retry" =>
                await OutcomeUnknownRetryAsync(command),
            "state-randomized-envelope-conflict" =>
                await RandomizedEnvelopeConflictAsync(command),
            "state-cleanup-failure" => await CleanupFailureAsync(command),
            "state-capacity-limit" => StateCapacity(command),
            "session-construction-limit" =>
                await SessionConstructionLimitAsync(),
            "continuation-limit" => await ContinuationLimitAsync(),
            _ => RestoreEnvelopeMutation(command, command.Case!),
        };
    }

    private static async Task<bool> AcceptedNewerPresentAsync(
        ProofCommand command)
    {
        var prepared = await PrepareNextAsync(command);
        if (prepared is null)
        {
            return false;
        }

        var accepted = prepared.Service.Accept(
            prepared.Access,
            prepared.Lineage,
            prepared.Receipt,
            prepared.Context,
            CancellationToken.None);
        if (accepted.Action != StateAction.Accepted ||
            accepted.Generation != 1)
        {
            return false;
        }

        var replay = prepared.Service.Restore(
            prepared.Access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                prepared.Lineage,
                prepared.Context),
            CancellationToken.None);
        return replay.Result.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                replay.Result.Code,
                RestrictedStateCodes.ReplayRejected) &&
            replay.Session is null;
    }

    private static async Task<bool> AcceptedNewerHiddenAsync(
        ProofCommand command)
    {
        var prepared = await PrepareNextAsync(command);
        if (prepared is null)
        {
            return false;
        }

        var accepted = prepared.Service.Accept(
            prepared.Access,
            prepared.Lineage,
            prepared.Receipt,
            prepared.Context,
            CancellationToken.None);
        if (accepted.Action != StateAction.Accepted ||
            accepted.Generation != 1 ||
            accepted.SessionSha256 is null ||
            accepted.EnvelopeSha256 is null)
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var read = local.Read(
            prepared.Access,
            CancellationToken.None);
        var older = read.Snapshot?.Accepted.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(
                candidate.EnvelopeSha256,
                prepared.Lineage.EnvelopeSha256));
        if (!read.Succeeded || older is null)
        {
            return false;
        }

        var hidden = local.CompareExchange(
            prepared.Access,
            read.Version!,
            new RestrictedStateSnapshot([older], null),
            CancellationToken.None);
        if (!hidden.Succeeded)
        {
            return false;
        }

        var acceptedLineage = new AcceptedLineage(
            prepared.Access.Scope,
            accepted.Generation.Value,
            accepted.SessionSha256,
            accepted.EnvelopeSha256,
            prepared.Lineage.EnvelopeSha256,
            ProofScenario.Now,
            ProofScenario.Now +
                RestrictedStateFormat.MaximumRetentionSeconds,
            TransitionAuthorized: true);
        var missing = prepared.Service.Restore(
            prepared.Access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                acceptedLineage,
                prepared.Context),
            CancellationToken.None);
        return missing.Result.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                missing.Result.Code,
                RestrictedStateCodes.CurrentMissing) &&
            missing.Session is null;
    }

    private static async Task<bool> OutcomeUnknownRetryAsync(
        ProofCommand command)
    {
        var prepared = await PrepareNextAsync(command);
        if (prepared is null)
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var before = local.Read(
            prepared.Access,
            CancellationToken.None).Snapshot?.Staging;
        var reconciled = prepared.Service.Reconcile(
            prepared.Access,
            prepared.Lineage,
            prepared.Receipt,
            prepared.Context,
            CancellationToken.None);
        var after = local.Read(
            prepared.Access,
            CancellationToken.None).Snapshot?.Staging;
        return before is not null &&
            after is not null &&
            reconciled.Action == StateAction.Idempotent &&
            StringComparer.Ordinal.Equals(
                reconciled.Code,
                RestrictedStateCodes.Idempotent) &&
            before.Envelope.AsSpan().SequenceEqual(after.Envelope) &&
            StringComparer.Ordinal.Equals(
                prepared.Receipt.ObjectIdentity,
                after.ObjectIdentity);
    }

    private static async Task<bool> RandomizedEnvelopeConflictAsync(
        ProofCommand command)
    {
        var prepared = await PrepareNextAsync(command);
        if (prepared is null)
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var staging = local.Read(
            prepared.Access,
            CancellationToken.None).Snapshot?.Staging;
        var keys = new SyntheticStateKeyResolver("issue88-proof");
        if (staging is null ||
            !RestrictedStateEnvelope.TryEncrypt(
                prepared.Access,
                staging.Binding,
                prepared.Artifact.Plaintext,
                keys,
                out var otherEnvelope,
                out _) ||
            otherEnvelope is null)
        {
            return false;
        }

        var otherEnvelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(otherEnvelope);
        var otherObjectIdentity =
            RestrictedStateEnvelope.ObjectIdentity(
                staging.Binding,
                staging.SessionSha256,
                otherEnvelopeSha);
        if (StringComparer.Ordinal.Equals(
                staging.EnvelopeSha256,
                otherEnvelopeSha) ||
            StringComparer.Ordinal.Equals(
                staging.ObjectIdentity,
                otherObjectIdentity))
        {
            return false;
        }

        var conflict = prepared.Service.Accept(
            prepared.Access,
            prepared.Lineage,
            new PreparedStateReceipt(
                staging.Binding.Generation,
                staging.SessionSha256,
                otherEnvelopeSha,
                otherObjectIdentity),
            prepared.Context,
            CancellationToken.None);
        return conflict.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                conflict.Code,
                RestrictedStateCodes.Conflict);
    }

    private static async Task<bool> CleanupFailureAsync(ProofCommand command)
    {
        var prepared = await PrepareNextAsync(command);
        if (prepared is null)
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var before = local.Read(
            prepared.Access,
            CancellationToken.None);
        if (!before.Succeeded ||
            before.Snapshot is null ||
            before.Version is null ||
            before.Snapshot.Accepted.Length != 1 ||
            before.Snapshot.Staging is null)
        {
            return false;
        }

        var failingStore = new CleanupFailureStore(local);
        var failingService = new RestrictedStateService(
            failingStore,
            new SyntheticStateKeyResolver("issue88-proof"),
            new AgentSessionRestrictedStateAdmission(),
            () => ProofScenario.Now);
        var result = failingService.Accept(
            prepared.Access,
            prepared.Lineage,
            prepared.Receipt,
            prepared.Context,
            CancellationToken.None);
        var after = local.Read(
            prepared.Access,
            CancellationToken.None);
        if (result.Action != StateAction.Failed ||
            !StringComparer.Ordinal.Equals(
                result.Code,
                RestrictedStateCodes.CleanupFailed) ||
            failingStore.AcceptWrites != 1 ||
            !after.Succeeded ||
            after.Snapshot is null ||
            after.Version is null ||
            !StringComparer.Ordinal.Equals(
                before.Version.Sha256,
                after.Version.Sha256) ||
            after.Snapshot.Accepted.Length != 1 ||
            after.Snapshot.Staging is null ||
            !StringComparer.Ordinal.Equals(
                after.Snapshot.Accepted[0].EnvelopeSha256,
                prepared.Lineage.EnvelopeSha256))
        {
            return false;
        }

        var restore = new RestrictedStateService(
            local,
            new SyntheticStateKeyResolver("issue88-proof"),
            new AgentSessionRestrictedStateAdmission(),
            () => ProofScenario.Now).Restore(
                prepared.Access,
                new RestrictedStateRestoreRequest(
                    RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit,
                    prepared.Lineage,
                    Context(
                        prepared.Lineage,
                        ProofScenario.ContinueIdentity(),
                        AgentSessionHeadTransition.VerifiedAhead,
                        envelopeSha256: null)),
                CancellationToken.None);
        return restore.Result.Action == StateAction.Restored &&
            restore.Session?.Value is not null &&
            StringComparer.Ordinal.Equals(
                restore.Result.EnvelopeSha256,
                prepared.Lineage.EnvelopeSha256) &&
            after.Snapshot.Staging is not null &&
            StringComparer.Ordinal.Equals(
                after.Snapshot.Staging.EnvelopeSha256,
                prepared.Receipt.EnvelopeSha256);
    }

    private static async Task<PreparedNextState?> PrepareNextAsync(
        ProofCommand command)
    {
        if (!TryContext(
                command,
                out var access,
                out var lineage,
                out var service,
                out _,
                out _))
        {
            return null;
        }

        var identity = ProofScenario.ContinueIdentity();
        var restoreContext = Context(
            lineage!,
            identity,
            AgentSessionHeadTransition.VerifiedAhead,
            envelopeSha256: null);
        var restored = service!.Restore(
            access!,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                restoreContext),
            CancellationToken.None);
        if (restored.Result.Action != StateAction.Restored ||
            restored.Session?.Value is null)
        {
            return null;
        }

        var admitted = restored.Session.Value;
        var run = admitted.RunRequest;
        var providerCanary = CanarySet.Provider(
            "issue88-proof-state-negative");
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [ProviderScripts.ContinueFinish]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var outcome = await new AgentLoop(
            new MinimalChatClient(backend),
            new NeverToolExecutor(),
            new SyntheticTimeProvider(ProofScenario.Now)).RunAsync(
                run,
                CancellationToken.None);
        if (!outcome.CompletedSessionEligible ||
            server.Captures.Count != 1)
        {
            return null;
        }

        var predecessor = new AgentSessionPredecessor(
            admitted.Artifact.Plaintext,
            lineage!.SessionSha256,
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
        if (!built.Succeeded || built.Artifact is null)
        {
            return null;
        }

        var stateContext = ProofState.SessionContext(
            built.Artifact,
            identity,
            AgentSessionHeadTransition.VerifiedAhead);
        var prepared = service.Prepare(
            access!,
            new RestrictedStatePrepareRequest(
                lineage,
                built.Artifact.Plaintext,
                stateContext),
            CancellationToken.None);
        if (prepared.Result.Action != StateAction.Prepared ||
            prepared.Receipt is null)
        {
            return null;
        }

        return new PreparedNextState(
            access!,
            lineage,
            service,
            stateContext,
            built.Artifact,
            prepared.Receipt);
    }

    private static bool RestoreTransition(
        ProofCommand command,
        ReviewedIdentity current,
        AgentSessionHeadTransition transition,
        bool shouldRestore)
    {
        if (!TryContext(
                command,
                out var access,
                out var lineage,
                out var service,
                out var store,
                out var admission))
        {
            return false;
        }

        var context = Context(
            lineage!,
            current,
            transition,
            envelopeSha256: null);
        var result = service!.Restore(
            access!,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                context),
            CancellationToken.None);
        return shouldRestore
            ? result.Result.Action == StateAction.Restored &&
                result.Session?.Value is not null &&
                store!.Calls == 1 &&
                admission!.Calls == 1
            : result.Result.Action == StateAction.Failed &&
                result.Session is null &&
                store!.Calls == 1 &&
                admission!.Calls == 1;
    }

    private static bool RestoreDefect(
        ProofCommand command,
        bool noLineage,
        bool invalidAssociation,
        bool invalidClassification,
        bool stale)
    {
        if (!TryContext(
                command,
                out var access,
                out var lineage,
                out var service,
                out var store,
                out var admission))
        {
            return false;
        }

        var selectedLineage = noLineage
            ? null
            : stale
                ? lineage! with
                {
                    AcceptedAtUnixSeconds = ProofScenario.Now - 2,
                    ExpiresAtUnixSeconds = ProofScenario.Now - 1,
                }
                : lineage;
        if (invalidAssociation)
        {
            selectedLineage = lineage! with
            {
                SessionSha256 = new string('f', 64),
            };
        }

        var context = Context(
            lineage!,
            ProofScenario.ContinueIdentity(),
            AgentSessionHeadTransition.VerifiedAhead,
            envelopeSha256: null);

        var result = service!.Restore(
            access!,
            new RestrictedStateRestoreRequest(
                invalidClassification
                    ? (RestrictedStateLocatorFamily)int.MaxValue
                    : RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                selectedLineage,
                context),
            CancellationToken.None);
        return result.Result.Action == StateAction.Failed &&
            result.Session is null;
    }

    private static bool RestoreSessionDefect(
        ProofCommand command,
        bool invalidClassification)
    {
        if (!TryContext(
                command,
                out var access,
                out var lineage,
                out var service,
                out var store,
                out var admission))
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var read = local.Read(access!, CancellationToken.None);
        var current = read.Snapshot?.Accepted.FirstOrDefault();
        var lineageBytes = File.ReadAllBytes(ProofPaths.Lineage(command));
        var keys = new SyntheticStateKeyResolver("issue88-proof");
        if (!read.Succeeded ||
            read.Version is null ||
            read.Snapshot is null ||
            current is null ||
            !RestrictedStateEnvelope.TryDecrypt(
                access!,
                current.Binding,
                current.Envelope,
                keys,
                out var plaintext,
                out _) ||
            plaintext is null ||
            !AgentSessionCodec.TryParse(
                plaintext,
                out var artifact,
                out _) ||
            artifact is null)
        {
            return false;
        }

        var runs = artifact.Document.CompletedRuns.ToArray();
        var mutated = false;
        for (var runIndex = 0;
            runIndex < runs.Length && !mutated;
            runIndex++)
        {
            var records = runs[runIndex].Records.ToArray();
            for (var recordIndex = 0;
                recordIndex < records.Length;
                recordIndex++)
            {
                if (invalidClassification)
                {
                    records[recordIndex] = WithClassification(
                        records[recordIndex],
                        "invalid-classification");
                    mutated = true;
                    break;
                }

                if (records[recordIndex] is
                    AgentSessionToolResultRecord toolResultRecord)
                {
                    records[recordIndex] = toolResultRecord with
                    {
                        SourceMessageId = "missing-assistant-message",
                    };
                    mutated = true;
                    break;
                }
            }

            if (mutated)
            {
                runs[runIndex] = runs[runIndex] with
                {
                    Records = records.ToImmutableArray(),
                };
            }
        }

        var document = artifact.Document with
        {
            CompletedRuns = runs.ToImmutableArray(),
        };
        if (!mutated ||
            !AgentSessionCodec.TryWrite(
                document,
                out var invalidArtifact,
                out _) ||
            invalidArtifact is null ||
            !RestrictedStateEnvelope.TryEncrypt(
                access!,
                current.Binding,
                invalidArtifact.Plaintext,
                keys,
                out var invalidEnvelope,
                out _) ||
            invalidEnvelope is null)
        {
            return false;
        }

        var invalidEnvelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(invalidEnvelope);
        var invalidCandidate = new RestrictedStateCandidate(
            current.Binding,
            invalidArtifact.SessionSha256,
            invalidEnvelopeSha,
            RestrictedStateEnvelope.ObjectIdentity(
                current.Binding,
                invalidArtifact.SessionSha256,
                invalidEnvelopeSha),
            invalidEnvelope);
        var replacement = read.Snapshot with
        {
            Accepted = read.Snapshot.Accepted.SetItem(0, invalidCandidate),
        };
        var injected = local.CompareExchange(
            access!,
            read.Version,
            replacement,
            CancellationToken.None);
        if (!injected.Committed || injected.Version is null)
        {
            return false;
        }

        var context = Context(
            lineage!,
            ProofScenario.ContinueIdentity(),
            AgentSessionHeadTransition.VerifiedAhead,
            envelopeSha256: null);
        var result = service!.Restore(
            access!,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                context),
            CancellationToken.None);
        var after = local.Read(access!, CancellationToken.None);
        return result.Result.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                result.Result.Code,
                RestrictedStateCodes.EnvelopeInvalid) &&
            result.Session is null &&
            store!.Calls == 1 &&
            admission!.Calls == 1 &&
            after.Succeeded &&
            after.Version is not null &&
            StringComparer.Ordinal.Equals(
                after.Version.Sha256,
                injected.Version.Sha256) &&
            File.ReadAllBytes(ProofPaths.Lineage(command))
                .AsSpan()
                .SequenceEqual(lineageBytes);
    }

    private static AgentSessionRecord WithClassification(
        AgentSessionRecord record,
        string classification) =>
        record switch
        {
            AgentSessionReviewContextRecord value =>
                value with { Classification = classification },
            AgentSessionAssistantMessageRecord value =>
                value with { Classification = classification },
            AgentSessionToolResultRecord value =>
                value with { Classification = classification },
            AgentSessionReviewOutcomeRecord value =>
                value with { Classification = classification },
            _ => throw new InvalidOperationException(),
        };

    private static bool RestoreEnvelopeMutation(
        ProofCommand command,
        string @case)
    {
        if (!TryContext(
                command,
                out var access,
                out var lineage,
                out var service,
                out var store,
                out var admission))
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var read = local.Read(access!, CancellationToken.None);
        var candidate = read.Snapshot?.Accepted.FirstOrDefault();
        var lineageBytes = File.ReadAllBytes(ProofPaths.Lineage(command));
        if (!read.Succeeded ||
            read.Version is null ||
            read.Snapshot is null ||
            candidate is null)
        {
            return false;
        }

        byte[] bytes;
        if (@case == "state-old-format-disguise")
        {
            bytes = "{\"kind\":\"synthetic-non-current\"}"u8.ToArray();
        }
        else if (@case == "state-truncation")
        {
            bytes = candidate.Envelope[..^1];
        }
        else
        {
            bytes = candidate.Envelope.ToArray();
            switch (@case)
            {
                case "state-tamper":
                    bytes[^1] ^= 0x01;
                    break;
                case "state-header-magic":
                case "state-current-downgrade":
                    bytes[0] ^= 0x01;
                    break;
                case "state-header-algorithm":
                    bytes[11] ^= 0x01;
                    break;
                case "state-header-namespace":
                    if (!FlipAscii(bytes, RestrictedStateFormat.Namespace))
                    {
                        return false;
                    }
                    break;
                case "state-header-discriminator":
                    if (!FlipAscii(
                            bytes,
                            RestrictedStateFormat.Discriminator))
                    {
                        return false;
                    }
                    break;
                case "state-header-key-id":
                    if (!FlipAscii(bytes, "synthetic-key"))
                    {
                        return false;
                    }
                    break;
                case "state-header-length":
                    if (!RestrictedStateEnvelope.TryParse(
                            bytes,
                            out var parsed) ||
                        parsed is null)
                    {
                        return false;
                    }

                    bytes[parsed.Header.Length - 1] ^= 0x01;
                    break;
                default:
                    return false;
            }
        }

        var envelopeSha = RestrictedStateEnvelope.EnvelopeSha256(bytes);
        var invalidCandidate = candidate with
        {
            EnvelopeSha256 = envelopeSha,
            ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(
                candidate.Binding,
                candidate.SessionSha256,
                envelopeSha),
            Envelope = bytes,
        };
        var replacement = read.Snapshot with
        {
            Accepted = read.Snapshot.Accepted.SetItem(0, invalidCandidate),
        };
        var injected = local.CompareExchange(
            access!,
            read.Version,
            replacement,
            CancellationToken.None);
        if (!injected.Committed || injected.Version is null)
        {
            return false;
        }

        var result = service!.Restore(
            access!,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                Context(
                    lineage!,
                    ProofScenario.ContinueIdentity(),
                    AgentSessionHeadTransition.VerifiedAhead,
                    envelopeSha256: null)),
            CancellationToken.None);
        var after = local.Read(access!, CancellationToken.None);
        var expectedCode = @case switch
        {
            "state-tamper" => RestrictedStateCodes.AuthenticationFailed,
            "state-header-key-id" => RestrictedStateCodes.KeyUnavailable,
            _ => RestrictedStateCodes.EnvelopeInvalid,
        };
        return result.Result.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                result.Result.Code,
                expectedCode) &&
            result.Session is null &&
            store!.Calls == 1 &&
            admission!.Calls == 0 &&
            after.Succeeded &&
            after.Version is not null &&
            StringComparer.Ordinal.Equals(
                after.Version.Sha256,
                injected.Version.Sha256) &&
            File.ReadAllBytes(ProofPaths.Lineage(command))
                .AsSpan()
                .SequenceEqual(lineageBytes);
    }

    private static bool EnvelopeCrossScope(ProofCommand command)
    {
        if (!TryCandidate(
                command,
                out var access,
                out var candidate))
        {
            return false;
        }

        var binding = candidate!.Binding with
        {
            Scope = candidate.Binding.Scope with
            {
                RepositoryId = "synthetic/cross-scope",
            },
        };
        var keys = new SyntheticStateKeyResolver("issue88-proof");
        return !RestrictedStateEnvelope.TryDecrypt(
                access!,
                binding,
                candidate.Envelope,
                keys,
                out var plaintext,
                out var code) &&
            plaintext is null &&
            StringComparer.Ordinal.Equals(
                code,
                RestrictedStateCodes.EnvelopeInvalid) &&
            keys.Accesses == 0;
    }

    private static bool EnvelopeOversize()
    {
        var bytes = new byte[AgentLimits.StateEnvelopeBytes + 1];
        return !RestrictedStateEnvelope.TryParse(bytes, out _);
    }

    private static bool StateCapacity(ProofCommand command)
    {
        var authorization = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out var access);
        if (authorization.Action != StateAction.Authorized ||
            access is null)
        {
            return false;
        }

        var local = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command));
        var read = local.Read(access, CancellationToken.None);
        var candidate = read.Snapshot?.Accepted.FirstOrDefault();
        if (!read.Succeeded ||
            read.Version is null ||
            candidate is null)
        {
            return false;
        }

        var overCapacity = new RestrictedStateSnapshot(
            [candidate, candidate, candidate],
            null);
        var rejectedWrite = local.CompareExchange(
            access,
            read.Version,
            overCapacity,
            CancellationToken.None);
        if (rejectedWrite.Committed ||
            rejectedWrite.Failure != RestrictedStateStoreFailure.Invalid)
        {
            return false;
        }

        var store = new FixedSnapshotStore(overCapacity);
        var service = new RestrictedStateService(
            store,
            new SyntheticStateKeyResolver("issue88-proof"),
            new AgentSessionRestrictedStateAdmission(),
            () => ProofScenario.Now);
        var result = service.Enumerate(access, CancellationToken.None);
        return result.Result.Action == StateAction.Failed &&
            StringComparer.Ordinal.Equals(
                result.Result.Code,
                RestrictedStateCodes.EnumerationInvalid) &&
            result.Candidates.IsEmpty &&
            store.ReadCalls == 1 &&
            local.Read(access, CancellationToken.None).Version?.Sha256 ==
                read.Version.Sha256;
    }

    private static async Task<bool> ContinuationLimitAsync()
    {
        var run = Run();
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
            {
                var position = request.Messages.Length;
                var item = new ProjectContinuationItem(
                    new string('r', 32 * 1024),
                    new string('o', 32 * 1024),
                    new string('f', 2 * 1024),
                    "finish-continuation",
                    position,
                    0);
                return new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                item.Readable,
                                item.Opaque,
                                item.Framing,
                                item.AssociatedCallId,
                                item.MessagePosition,
                                item.ContentPosition),
                            new ProjectToolCallContent(
                                "finish-continuation",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        ProofScenario.ProviderId,
                        ProofScenario.ModelId,
                        ProofScenario.AdapterId,
                        ProofScenario.SessionId,
                        [item]));
            }),
            new NeverToolExecutor()).RunAsync(
                run,
                CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            outcome.Diagnostic is
            {
                Code: AgentFailureCodes.ResponseInvalid,
                ModelCalls: 1,
                ToolCalls: 0,
            };
    }

    private static async Task<bool> SessionConstructionLimitAsync()
    {
        var run = Run();
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
            {
                var position = request.Messages.Length;
                var item = new ProjectContinuationItem(
                    (AgentLimits.ContinuationItemBytes + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty,
                    "sized",
                    "finish-continuation",
                    position,
                    0);
                return new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                item.Readable,
                                item.Opaque,
                                item.Framing,
                                item.AssociatedCallId,
                                item.MessagePosition,
                                item.ContentPosition),
                            new ProjectToolCallContent(
                                "finish-continuation",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        ProofScenario.ProviderId,
                        ProofScenario.ModelId,
                        ProofScenario.AdapterId,
                        ProofScenario.SessionId,
                        [item]));
            }),
            new NeverToolExecutor()).RunAsync(
                run,
                CancellationToken.None);
        if (!outcome.CompletedSessionEligible)
        {
            return false;
        }

        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                run,
                outcome,
                ProofScenario.Trusted(),
                run.InitialMessages.Length - 1,
                SizedContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        return !built.Succeeded &&
            built.Artifact is null &&
            StringComparer.Ordinal.Equals(
                built.FailureCode,
                AgentSessionCodes.ConstructionLimit);
    }

    private static bool TryContext(
        ProofCommand command,
        out AuthorizedStateAccess? access,
        out AcceptedLineage? lineage,
        out RestrictedStateService? service,
        out CountingStateStore? store,
        out CountingSessionAdmission? admission)
    {
        lineage = null;
        service = null;
        store = null;
        admission = null;
        var authorization = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out access);
        if (authorization.Action != StateAction.Authorized ||
            access is null ||
            !HostLineage.TryRead(
                command,
                access.Scope,
                out _,
                out lineage) ||
            lineage is null)
        {
            return false;
        }

        store = new CountingStateStore(
            new LocalRestrictedStateStore(ProofPaths.StateRoot(command)));
        admission = new CountingSessionAdmission(
            new AgentSessionRestrictedStateAdmission());
        service = new RestrictedStateService(
            store,
            new SyntheticStateKeyResolver("issue88-proof"),
            admission,
            () => ProofScenario.Now);
        return true;
    }

    private static bool TryCandidate(
        ProofCommand command,
        out AuthorizedStateAccess? access,
        out RestrictedStateCandidate? candidate)
    {
        candidate = null;
        var authorization = ProofState.Authorize(
            trusted: true,
            sameRepository: true,
            fork: false,
            out access);
        if (authorization.Action != StateAction.Authorized ||
            access is null)
        {
            return false;
        }

        var read = new LocalRestrictedStateStore(
            ProofPaths.StateRoot(command)).Read(
                access,
                CancellationToken.None);
        candidate = read.Snapshot?.Accepted.FirstOrDefault();
        return read.Succeeded && candidate is not null;
    }

    private static RestrictedStateSessionAdmissionContext Context(
        AcceptedLineage lineage,
        ReviewedIdentity current,
        AgentSessionHeadTransition transition,
        string? envelopeSha256) => new(
            ProofScenario.BootstrapIdentity().BaseSha,
            ProofScenario.BootstrapIdentity().HeadSha,
            lineage.Generation,
            lineage.ExpectedPredecessorEnvelopeSha256,
            new AgentSessionStateAdmissionContext(
                ProofScenario.Trusted(),
                ProofScenario.SessionId,
                current,
                ProofScenario.User("Synthetic negative restore context."),
                transition,
                SyntheticContinuationCodec.Instance,
                envelopeSha256));

    private static AgentRunRequest Run()
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
            ProofScenario.BootstrapIdentity(),
            materialized!.StablePlan,
            ProofScenario.SessionId,
            [
                .. materialized.ControlMessages,
                ProofScenario.User("Synthetic capacity context."),
            ]);
    }

    private static bool FlipAscii(byte[] bytes, string value)
    {
        var needle = Encoding.ASCII.GetBytes(value);
        var offset = bytes.AsSpan().IndexOf(needle);
        if (offset < 0)
        {
            return false;
        }

        bytes[offset] ^= 0x01;
        return true;
    }

    private const string FinishJson =
        "{\"summary\":\"Synthetic terminal.\",\"findings\":[]}";

    private sealed record PreparedNextState(
        AuthorizedStateAccess Access,
        AcceptedLineage Lineage,
        RestrictedStateService Service,
        RestrictedStateSessionAdmissionContext Context,
        AgentSessionArtifact Artifact,
        PreparedStateReceipt Receipt);

    private sealed class CleanupFailureStore(
        IRestrictedStateStore inner) : IRestrictedStateStore
    {
        internal int AcceptWrites { get; private set; }

        public RestrictedStateStoreRead Read(
            AuthorizedStateAccess access,
            CancellationToken cancellationToken) =>
            inner.Read(access, cancellationToken);

        public RestrictedStateStoreRawRead ReadRawVersion(
            AuthorizedStateAccess access,
            CancellationToken cancellationToken) =>
            inner.ReadRawVersion(access, cancellationToken);

        public RestrictedStateStoreWrite CompareExchange(
            AuthorizedStateAccess access,
            RestrictedStateSnapshotVersion expected,
            RestrictedStateSnapshot replacement,
            CancellationToken cancellationToken)
        {
            AcceptWrites++;
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Cleanup,
                Version: null,
                Committed: false);
        }

        public RestrictedStateStoreWrite CompareDelete(
            AuthorizedStateAccess access,
            RestrictedStateSnapshotVersion expected,
            CancellationToken cancellationToken) =>
            inner.CompareDelete(access, expected, cancellationToken);

        public RestrictedStateStoreWrite CompareDeleteRaw(
            AuthorizedStateAccess access,
            RestrictedStateRawVersion expected,
            CancellationToken cancellationToken) =>
            inner.CompareDeleteRaw(access, expected, cancellationToken);
    }

    private sealed class FixedSnapshotStore(
        RestrictedStateSnapshot snapshot) : IRestrictedStateStore
    {
        internal int ReadCalls { get; private set; }

        public RestrictedStateStoreRead Read(
            AuthorizedStateAccess access,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                snapshot,
                new RestrictedStateSnapshotVersion(
                    new string('0', 64),
                    Exists: true));
        }

        public RestrictedStateStoreRawRead ReadRawVersion(
            AuthorizedStateAccess access,
            CancellationToken cancellationToken) =>
            new(
                RestrictedStateStoreFailure.None,
                RestrictedStateRawVersion.Absent);

        public RestrictedStateStoreWrite CompareExchange(
            AuthorizedStateAccess access,
            RestrictedStateSnapshotVersion expected,
            RestrictedStateSnapshot replacement,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public RestrictedStateStoreWrite CompareDelete(
            AuthorizedStateAccess access,
            RestrictedStateSnapshotVersion expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public RestrictedStateStoreWrite CompareDeleteRaw(
            AuthorizedStateAccess access,
            RestrictedStateRawVersion expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class OneResponseChatClient(
        Func<ProjectChatRequest, ProjectChatResponse> response)
        : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class SizedContinuationCodec : IAgentContinuationCodec
    {
        internal static SizedContinuationCodec Instance { get; } = new();

        public string CodecId => "sized";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            if (!int.TryParse(
                    value.Readable,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var size) ||
                size < 0)
            {
                return false;
            }

            payload = new AgentContinuationEncodedPayload(
                "base64",
                new byte[size]);
            return true;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            return false;
        }
    }
}
