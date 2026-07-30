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
            "state-classification-invalid" => RestoreDefect(
                command,
                noLineage: false,
                invalidAssociation: false,
                invalidClassification: true,
                stale: false),
            "state-association-invalid" => RestoreDefect(
                command,
                noLineage: false,
                invalidAssociation: true,
                invalidClassification: false,
                stale: false),
            "state-stale-replay" => RestoreDefect(
                command,
                noLineage: false,
                invalidAssociation: false,
                invalidClassification: false,
                stale: true),
            "state-cross-scope" => EnvelopeCrossScope(command),
            "state-oversize" => EnvelopeOversize(),
            "state-old-format-disguise" =>
                !RestrictedStateEnvelope.TryParse(
                    "{\"kind\":\"synthetic-non-current\"}"u8,
                    out _),
            "state-capacity-limit" => StateCapacity(command),
            "session-construction-limit" =>
                await ContinuationLimitAsync(),
            "continuation-limit" => await ContinuationLimitAsync(),
            _ => EnvelopeMutation(command, command.Case!),
        };
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

    private static bool EnvelopeMutation(
        ProofCommand command,
        string @case)
    {
        if (!TryCandidate(
                command,
                out var access,
                out var candidate))
        {
            return false;
        }

        byte[] bytes;
        if (@case == "state-truncation")
        {
            bytes = candidate!.Envelope[..^1];
        }
        else
        {
            bytes = candidate!.Envelope.ToArray();
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

        var keys = new SyntheticStateKeyResolver("issue88-proof");
        var decrypted = RestrictedStateEnvelope.TryDecrypt(
            access!,
            candidate!.Binding,
            bytes,
            keys,
            out var plaintext,
            out var code);
        return !decrypted &&
            plaintext is null &&
            code is RestrictedStateCodes.EnvelopeInvalid or
                RestrictedStateCodes.AuthenticationFailed or
                RestrictedStateCodes.KeyUnavailable;
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
        if (!TryCandidate(
                command,
                out var access,
                out var candidate))
        {
            return false;
        }

        var keys = new SyntheticStateKeyResolver("issue88-proof");
        var plaintext = new byte[AgentLimits.SessionPlaintextBytes + 1];
        return !RestrictedStateEnvelope.TryEncrypt(
                access!,
                candidate!.Binding,
                plaintext,
                keys,
                out var envelope,
                out var code) &&
            envelope is null &&
            StringComparer.Ordinal.Equals(
                code,
                RestrictedStateCodes.EnvelopeInvalid) &&
            keys.Accesses == 0;
    }

    private static async Task<bool> ContinuationLimitAsync()
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
