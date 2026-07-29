using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed class AgentSessionRoundTripTests
{
    private const string FinishJson =
        "{\"summary\":\"complete\",\"findings\":[]}";

    [Fact]
    public void MinimalFrozenVectorsAreByteExact()
    {
        var record = new AgentSessionReviewContextRecord(
            "r0",
            0,
            new ReviewedIdentity(
                "repo",
                1,
                new string('0', 40),
                new string('1', 40)),
            "x",
            "user",
            "text",
            "untrusted_review_data");

        Assert.Equal(
            "{\"kind\":\"review_context\",\"id\":\"r0\",\"sequence\":0,\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"text\":\"x\",\"role\":\"user\",\"framing\":\"text\",\"classification\":\"untrusted_review_data\"}",
            Encoding.UTF8.GetString(
                AgentSessionCodec.WriteRecordBytes(record)));
        Assert.Equal(
            "e9a9e4ff63897f3b91af639ccf3973c54b62c2c56875d4426578ee70b0e73612",
            AgentSessionCodec.ContinuationPayloadSha256(
                "r2-synthetic",
                "current-1",
                "c0",
                "utf8",
                "x"u8));
    }

    [Fact]
    public async Task GenerationZeroBuildsAndRestoresEquivalentRequest()
    {
        var trusted = Trusted();
        var completed = await CompleteAsync(
            trusted,
            priorSessionSha256: null,
            previous: null,
            currentText: "review generation zero",
            callId: "finish0",
            reasoning: true);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            completed.Run,
            completed.Outcome,
            trusted,
            completed.Run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));

        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(built.Artifact);
        Assert.Equal(
            "c0236fe3c10bfedbced813778507d8c9cca7e0a84f0b4a749535bb01c2e95b6a",
            artifact.SessionSha256);
        Assert.Equal(2627, artifact.Plaintext.Length);
        Assert.Equal("APRSES01", Encoding.ASCII.GetString(
            artifact.Plaintext,
            0,
            8));
        Assert.Single(artifact.Document.CompletedRuns);
        Assert.Single(
            artifact.Document.CompletedRuns[0].Continuation.Items);
        Assert.True(AgentSessionCodec.TryParse(
            artifact.Plaintext,
            out var parsed,
            out var parseFailure),
            parseFailure);
        Assert.Equal(
            artifact.SessionSha256,
            parsed!.SessionSha256);

        var envelopeSha = new string('e', 64);
        var restore = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                artifact.Plaintext,
                new AgentSessionAcceptedState(
                    0,
                    artifact.SessionSha256,
                    envelopeSha,
                    completed.Run.ReviewedIdentity.BaseSha,
                    completed.Run.ReviewedIdentity.HeadSha,
                    PredecessorStateSha256: null),
                trusted,
                completed.Run.SessionId,
                completed.Run.ReviewedIdentity,
                User("review generation one"),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance));

        Assert.True(restore.Succeeded, restore.Code);
        Assert.Equal(
            artifact.SessionSha256,
            restore.RunRequest!.StablePlan.PriorSessionSha256);
        Assert.Equal(4, restore.RunRequest.InitialMessages.Length);
        Assert.Single(restore.RunRequest.Continuation!.Items);
        var materialized = MinimalChatClient.Materialize(
            new ProjectChatRequest(
                restore.RunRequest.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                restore.RunRequest.Continuation,
                ThinkingRequired: true));
        var priorAssistant = Assert.Single(
            materialized.Messages,
            message => message.Role == "assistant");
        Assert.Equal(
            ["reasoning", "tool_call"],
            priorAssistant.Contents.Select(content => content.Kind));
        Assert.Equal("opaque-0", priorAssistant.Contents[0].Opaque);
    }

    [Fact]
    public async Task GenerationsRetainPriorRunsAndAppendOnlyCurrentContinuation()
    {
        var trusted = Trusted();
        var generation0 = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        var generation1 = await BuildGenerationAsync(
            trusted,
            generation0,
            "g1",
            "finish1",
            reasoning: false);
        var generation2 = await BuildGenerationAsync(
            trusted,
            generation1,
            "g2",
            "finish2",
            reasoning: true);

        Assert.Equal(2, generation2.Artifact.Document.Generation);
        Assert.Equal(3, generation2.Artifact.Document.CompletedRuns.Length);
        Assert.Single(
            generation2.Artifact.Document.CompletedRuns[0]
                .Continuation.Items);
        Assert.Empty(
            generation2.Artifact.Document.CompletedRuns[1]
                .Continuation.Items);
        Assert.Single(
            generation2.Artifact.Document.CompletedRuns[2]
                .Continuation.Items);
        Assert.Equal(
            generation1.Artifact.SessionSha256,
            generation2.Artifact.Document.PriorSessionSha256);
        Assert.Equal(
            generation1.EnvelopeSha256,
            generation2.Artifact.Document.PredecessorStateSha256);
        Assert.Equal(
            RunJson(generation0.Artifact, 0),
            RunJson(generation1.Artifact, 0));
        Assert.Equal(
            RunJson(generation0.Artifact, 0),
            RunJson(generation2.Artifact, 0));
        Assert.Equal(
            RunJson(generation1.Artifact, 1),
            RunJson(generation2.Artifact, 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(AgentLimits.PartsPerMessage)]
    public async Task MultipartCurrentContextCompletesLoopButProducesNoSession(
        int partCount)
    {
        var trusted = Trusted();
        var plan = Materialize(trusted, prior: null);
        var current = new ProjectChatMessage(
            "user",
            Enumerable.Range(0, partCount)
                .Select(index => (ProjectChatContent)new ProjectTextContent(
                    string.Concat("part-", index)))
                .ToArray());
        var run = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [.. trusted.ControlMessages, current]);
        var outcome = await Loop("finish0", reasoning: false).RunAsync(
            run,
            CancellationToken.None);

        Assert.True(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.Equal(AgentSessionCodes.RecordInvalid, built.FailureCode);
        Assert.Null(built.Artifact);
    }

    [Fact]
    public async Task HostPolicyAndActualControlPrefixMustMatch()
    {
        var trusted = Trusted();
        var plan = Materialize(trusted, prior: null);
        var run = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [
                new ProjectChatMessage(
                    "system",
                    [new ProjectTextContent("different control B")]),
                User("review"),
            ]);
        var outcome = await Loop("finish0", reasoning: false).RunAsync(
            run,
            CancellationToken.None);

        Assert.True(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.Equal(AgentSessionCodes.ScopeMismatch, built.FailureCode);
        Assert.Null(built.Artifact);
    }

    [Fact]
    public async Task CurrentBytesCannotDowngradeToBootstrap()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var altered = built.Artifact.Plaintext.ToArray();
        altered[0] = (byte)'X';
        var accepted = new AgentSessionAcceptedState(
            0,
            built.Artifact.SessionSha256,
            built.EnvelopeSha256,
            Identity().BaseSha,
            Identity().HeadSha,
            PredecessorStateSha256: null);
        var restored = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                altered,
                accepted,
                trusted,
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance));

        Assert.Equal(AgentSessionCodes.CurrentMalformed, restored.Code);
        Assert.False(restored.Succeeded);
    }

    [Theory]
    [InlineData(
        0,
        0,
        AgentSessionCodes.BootstrapAbsent)]
    [InlineData(
        0,
        1,
        AgentSessionCodes.ExplicitMissing)]
    [InlineData(
        1,
        0,
        AgentSessionCodes.BootstrapIncompatible)]
    [InlineData(
        1,
        1,
        AgentSessionCodes.ExplicitIncompatible)]
    public void HostLocatorTableDoesNotParseCandidateBytes(
        int familyValue,
        int intentValue,
        string expected)
    {
        var family = (AgentSessionLocatorFamily)familyValue;
        var intent = (AgentSessionRestoreIntent)intentValue;
        var result = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                family,
                intent,
                ExplicitReset: false,
                "not a session"u8.ToArray(),
                AcceptedState: null,
                Trusted(),
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.Unknown,
                SyntheticContinuationCodec.Instance));

        Assert.Equal(expected, result.Code);
    }

    [Fact]
    public async Task RestoreFailurePrecedenceIsDeterministic()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        var accepted = new AgentSessionAcceptedState(
            0,
            built.Artifact.SessionSha256,
            built.EnvelopeSha256,
            built.Artifact.Document.ProducerBaseSha,
            built.Artifact.Document.ProducerHeadSha,
            PredecessorStateSha256: null);

        var explicitReset = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Explicit,
                ExplicitReset: true,
                "malformed"u8.ToArray(),
                accepted,
                trusted,
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.Unknown,
                SyntheticContinuationCodec.Instance));
        Assert.Equal(AgentSessionCodes.ResetExplicit, explicitReset.Code);

        var oversized = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                new byte[AgentLimits.SessionPlaintextBytes + 1],
                AcceptedState: null,
                trusted,
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.Unknown,
                SyntheticContinuationCodec.Instance));
        Assert.Equal(AgentSessionCodes.CurrentOversized, oversized.Code);

        var nonCurrent = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.NonCurrent,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                built.Artifact.Plaintext,
                accepted,
                trusted,
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance));
        Assert.Equal(
            AgentSessionCodes.BootstrapIncompatible,
            nonCurrent.Code);

        var run = built.Artifact.Document.CompletedRuns[0];
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records[0]);
        var invalidRecord = Rewrite(
            built.Artifact.Document,
            run with
            {
                Records = run.Records.SetItem(
                    0,
                    context with { Classification = "policy" }),
            });
        var scopeBeforeRecord = Restore(
            invalidRecord,
            built.EnvelopeSha256,
            trusted with { ProviderId = "other-provider" },
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            scopeBeforeRecord.Code);

        var item = Assert.Single(run.Continuation.Items);
        var invalidContinuation = Rewrite(
            built.Artifact.Document,
            run with
            {
                Continuation = run.Continuation with
                {
                    Items = run.Continuation.Items.SetItem(
                        0,
                        item with
                        {
                            PayloadSha256 = new string('0', 64),
                        }),
                },
            });
        var transitionBeforeContinuation = Restore(
            invalidContinuation,
            built.EnvelopeSha256,
            trusted,
            AgentSessionHeadTransition.Unknown);
        Assert.Equal(
            AgentSessionCodes.TransitionRejected,
            transitionBeforeContinuation.Code);
    }

    [Fact]
    public async Task StableScopeSubstitutionAlwaysFailsClosed()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        AgentSessionTrustedRequest[] substitutions =
        [
            trusted with { RepositoryId = "other-repo" },
            trusted with { ReviewTarget = 2 },
            trusted with { WorkflowIdentity = "other-workflow" },
            trusted with
            {
                TrustedPolicyBytes = "other policy"u8.ToArray(),
            },
            trusted with { ProviderId = "other-provider" },
            trusted with { ModelId = "other-model" },
            trusted with { AdapterId = "other-adapter" },
            trusted with { BuildId = "other-build" },
        ];

        foreach (var substitution in substitutions)
        {
            var result = Restore(
                built.Artifact,
                built.EnvelopeSha256,
                substitution,
                AgentSessionHeadTransition.SameHead);
            Assert.Equal(AgentSessionCodes.ScopeMismatch, result.Code);
            Assert.Null(result.RunRequest);
        }

        var sessionSubstitution = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                built.Artifact.Plaintext,
                new AgentSessionAcceptedState(
                    0,
                    built.Artifact.SessionSha256,
                    built.EnvelopeSha256,
                    built.Artifact.Document.ProducerBaseSha,
                    built.Artifact.Document.ProducerHeadSha,
                    PredecessorStateSha256: null),
                trusted,
                "other-session",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance));
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            sessionSubstitution.Code);
    }

    [Fact]
    public async Task CanonicalWritersEnforceExactRecordAndSessionCaps()
    {
        var built = await BuildGenerationAsync(
            Trusted(),
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            built.Artifact.Document.CompletedRuns[0].Records[0]);
        var emptyRecord = context with { Text = string.Empty };
        var emptyRecordLength =
            AgentSessionCodec.WriteRecordBytes(emptyRecord).Length;
        var exactRecord = context with
        {
            Text = new string(
                'x',
                AgentLimits.SessionRecordBytes - emptyRecordLength),
        };
        Assert.Equal(
            AgentLimits.SessionRecordBytes,
            AgentSessionCodec.WriteRecordBytes(exactRecord).Length);
        Assert.Empty(AgentSessionCodec.WriteRecordBytes(
            exactRecord with
            {
                Text = string.Concat(exactRecord.Text, "x"),
            }));

        var emptyWorkflow = built.Artifact.Document with
        {
            WorkflowIdentity = string.Empty,
        };
        Assert.True(AgentSessionCodec.TryWrite(
            emptyWorkflow,
            out var baseArtifact,
            out var baseFailure),
            baseFailure);
        var exactWorkflowLength =
            AgentLimits.SessionPlaintextBytes -
            baseArtifact!.Plaintext.Length;
        var exactDocument = emptyWorkflow with
        {
            WorkflowIdentity = new string('w', exactWorkflowLength),
        };
        Assert.True(AgentSessionCodec.TryWrite(
            exactDocument,
            out var exactArtifact,
            out var exactFailure),
            exactFailure);
        Assert.Equal(
            AgentLimits.SessionPlaintextBytes,
            exactArtifact!.Plaintext.Length);

        Assert.False(AgentSessionCodec.TryWrite(
            exactDocument with
            {
                WorkflowIdentity = string.Concat(
                    exactDocument.WorkflowIdentity,
                    "w"),
            },
            out var overArtifact,
            out var overFailure));
        Assert.Null(overArtifact);
        Assert.Equal(
            AgentSessionCodes.ConstructionLimit,
            overFailure);
    }

    [Fact]
    public async Task CanonicalFramingAndClosedRootMutationsFail()
    {
        var built = await BuildGenerationAsync(
            Trusted(),
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var plaintext = built.Artifact.Plaintext;
        var json = plaintext[AgentSessionFormat.FramingBytes..];
        var jsonText = Encoding.UTF8.GetString(json);
        var mutations = new[]
        {
            plaintext[..^1],
            [.. plaintext, (byte)'\n'],
            Frame([.. "\uFEFF"u8, .. json]),
            Frame([.. " "u8, .. json]),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "{\"namespace\":\"agentic-pr-review/agent-session\",\"discriminator\":\"r2-current-1\"",
                "{\"discriminator\":\"r2-current-1\",\"namespace\":\"agentic-pr-review/agent-session\"",
                StringComparison.Ordinal))),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "{\"namespace\":",
                "{\"unknown\":0,\"namespace\":",
                StringComparison.Ordinal))),
        };

        foreach (var mutation in mutations)
        {
            Assert.False(AgentSessionCodec.TryParse(
                mutation,
                out var parsed,
                out var failure));
            Assert.Null(parsed);
            Assert.Equal(AgentSessionCodes.CurrentMalformed, failure);
        }
    }

    [Fact]
    public async Task ClosedKindsOrderingOrdinalsAndAssociationsRejectMutation()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        var json = Encoding.UTF8.GetString(
            built.Artifact.Plaintext[AgentSessionFormat.FramingBytes..]);
        var unknownKind = Frame(Encoding.UTF8.GetBytes(json.Replace(
            "\"kind\":\"review_context\"",
            "\"kind\":\"unknown\"",
            StringComparison.Ordinal)));
        Assert.False(AgentSessionCodec.TryParse(
            unknownKind,
            out _,
            out var unknownKindFailure));
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            unknownKindFailure);

        var unknownField = Frame(Encoding.UTF8.GetBytes(json.Replace(
            "\"kind\":\"review_context\",\"id\"",
            "\"kind\":\"review_context\",\"unknown\":0,\"id\"",
            StringComparison.Ordinal)));
        Assert.False(AgentSessionCodec.TryParse(
            unknownField,
            out _,
            out var unknownFieldFailure));
        Assert.Equal(
            AgentSessionCodes.CurrentMalformed,
            unknownFieldFailure);

        var run = built.Artifact.Document.CompletedRuns[0];
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records[0]);
        var duplicateIdentifier = Rewrite(
            built.Artifact.Document,
            run with
            {
                Records = run.Records.SetItem(
                    0,
                    context with { Id = run.RunId }),
            });
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            Restore(
                duplicateIdentifier,
                trusted,
                AgentSessionHeadTransition.SameHead).Code);

        var assistant = Assert.IsType<
            AgentSessionAssistantMessageRecord>(run.Records[1]);
        var wrongOrdinal = Rewrite(
            built.Artifact.Document,
            run with
            {
                Records = run.Records.SetItem(
                    1,
                    assistant with { MessageOrdinal = 1 }),
            });
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            Restore(
                wrongOrdinal,
                trusted,
                AgentSessionHeadTransition.SameHead).Code);

        var slot = Assert.IsType<AgentSessionContinuationSlotContent>(
            assistant.Contents[0]);
        var movedSlot = Rewrite(
            built.Artifact.Document,
            run with
            {
                Records = run.Records.SetItem(
                    1,
                    assistant with
                    {
                        Contents = assistant.Contents.SetItem(
                            0,
                            slot with { ContentPosition = 1 }),
                    }),
            });
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            Restore(
                movedSlot,
                trusted,
                AgentSessionHeadTransition.SameHead).Code);

        var grounded = await BuildGroundedGenerationAsync(trusted);
        var groundedRun =
            grounded.Artifact.Document.CompletedRuns[0];
        var result = Assert.IsType<AgentSessionToolResultRecord>(
            groundedRun.Records[2]);
        var brokenAssociation = Rewrite(
            grounded.Artifact.Document,
            groundedRun with
            {
                Records = groundedRun.Records.SetItem(
                    2,
                    result with { SourceMessageId = "moved" }),
            });
        Assert.Equal(
            AgentSessionCodes.AssociationInvalid,
            Restore(
                brokenAssociation,
                grounded.EnvelopeSha256,
                trusted,
                AgentSessionHeadTransition.SameHead).Code);
    }

    [Fact]
    public async Task ClassificationAndContinuationMutationsFailWithStableCodes()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        var run = built.Artifact.Document.CompletedRuns[0];
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records[0]);
        var relabeled = Rewrite(
            built.Artifact.Document,
            run with
            {
                Records = run.Records.SetItem(
                    0,
                    context with { Classification = "policy" }),
            });
        var relabeledRestore = Restore(
            relabeled,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.ClassificationInvalid,
            relabeledRestore.Code);

        var item = Assert.Single(run.Continuation.Items);
        var badContinuation = Rewrite(
            built.Artifact.Document,
            run with
            {
                Continuation = run.Continuation with
                {
                    Items = run.Continuation.Items.SetItem(
                        0,
                        item with
                        {
                            PayloadSha256 = new string('0', 64),
                        }),
                },
            });
        var continuationRestore = Restore(
            badContinuation,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.ContinuationInvalid,
            continuationRestore.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SelfConsistentButUngroundedTerminalIsRejected(
        int mutation)
    {
        var trusted = Trusted();
        var built = await BuildGroundedGenerationAsync(trusted);
        var invalidArtifact = MutateTerminal(
            built.Artifact,
            mutation);
        var restored = Restore(
            new BuiltGeneration(
                invalidArtifact,
                built.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);

        Assert.Equal(
            AgentSessionCodes.AssociationInvalid,
            restored.Code);
        Assert.Null(restored.RunRequest);
    }

    [Fact]
    public async Task MultipleCallsRetainOnePhysicalAssistantMessage()
    {
        var trusted = Trusted();
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. trusted.ControlMessages, User("review")]);
        const string firstArguments =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        const string secondArguments =
            "{\"path\":\"src/b.cs\",\"start_line\":1,\"line_count\":1}";
        var continuationItem = new ProjectContinuationItem(
            "readable-grouped",
            "opaque-grouped",
            "structured",
            "read0",
            run.InitialMessages.Length,
            1);
        var responses = new Queue<ProjectChatResponse>(
        [
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectTextContent("checking two files"),
                        new ProjectReasoningContent(
                            continuationItem.Readable,
                            continuationItem.Opaque,
                            continuationItem.Framing,
                            continuationItem.AssociatedCallId,
                            continuationItem.MessagePosition,
                            continuationItem.ContentPosition),
                        new ProjectToolCallContent(
                            "read0",
                            AgentToolRegistry.ReadFileName,
                            firstArguments),
                        new ProjectToolCallContent(
                            "read1",
                            AgentToolRegistry.ReadFileName,
                            secondArguments),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1,
                new ProjectContinuation(
                    "provider",
                    "model",
                    "adapter",
                    "session0",
                    [continuationItem])),
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "finish0",
                            AgentToolRegistry.FinishReviewName,
                            FinishJson),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var executions = new Dictionary<string, AgentToolExecution>(
            StringComparer.Ordinal)
        {
            ["read0"] = ReadExecution("src/a.cs", 'a'),
            ["read1"] = ReadExecution("src/b.cs", 'b'),
        };
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(executions)).RunAsync(
                run,
                CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);

        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(
            built.Artifact);
        var records = artifact.Document.CompletedRuns[0].Records;
        var grouped = Assert.IsType<AgentSessionAssistantMessageRecord>(
            records[1]);
        Assert.Equal(
            ["read0", "read1"],
            grouped.Contents
                .OfType<AgentSessionToolCallContent>()
                .Select(call => call.CallId));
        Assert.Collection(
            records.Skip(2).Take(2),
            record => Assert.Equal(
                grouped.Id,
                Assert.IsType<AgentSessionToolResultRecord>(
                    record).SourceMessageId),
            record => Assert.Equal(
                grouped.Id,
                Assert.IsType<AgentSessionToolResultRecord>(
                    record).SourceMessageId));

        var restored = Restore(
            artifact,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        var materialized = MinimalChatClient.Materialize(
            new ProjectChatRequest(
                restored.RunRequest!.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                restored.RunRequest.Continuation,
                ThinkingRequired: true));
        var priorAssistant = materialized.Messages.First(
            message => message.Role == "assistant");
        Assert.Equal(
            ["text", "reasoning", "tool_call", "tool_call"],
            priorAssistant.Contents.Select(content => content.Kind));
    }

    [Fact]
    public async Task DurableContinuationOrderAndPhysicalPlacementAreIndependent()
    {
        var trusted = Trusted();
        var plan = Materialize(trusted, prior: null);
        var run = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [.. trusted.ControlMessages, User("review")]);
        var outcome = await TwoReasoningLoop().RunAsync(
            run,
            CancellationToken.None);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));

        Assert.True(built.Succeeded, built.FailureCode);
        Assert.Equal(
            [1, 0],
            built.Artifact!.Document.CompletedRuns[0]
                .Continuation.Items
                .Select(item => item.ContentPosition));
        var restored = Restore(
            new BuiltGeneration(
                built.Artifact,
                new string('e', 64)),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Equal(
            [1, 0],
            restored.RunRequest!.Continuation!.Items
                .Select(item => item.ContentPosition));

        var native = MinimalChatClient.Materialize(
            new ProjectChatRequest(
                restored.RunRequest.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                restored.RunRequest.Continuation,
                ThinkingRequired: true));
        var assistant = Assert.Single(
            native.Messages,
            message => message.Role == "assistant");
        Assert.Equal(
            ["reasoning", "reasoning", "tool_call"],
            assistant.Contents.Select(content => content.Kind));
        Assert.Equal("readable-0", assistant.Contents[0].Text);
        Assert.Equal("readable-1", assistant.Contents[1].Text);
        Assert.Equal([0, 1, 2], assistant.Contents.Select(
            content => content.Position));
    }

    [Fact]
    public async Task EveryContinuationSlotPositionRoundTripsPhysically()
    {
        var trusted = Trusted();
        for (var slotPosition = 0;
            slotPosition < AgentLimits.PartsPerMessage;
            slotPosition++)
        {
            var plan = Materialize(trusted, prior: null);
            var callId = string.Concat("finish", slotPosition);
            var run = new AgentRunRequest(
                Identity(),
                plan,
                "session0",
                [.. trusted.ControlMessages, User("review")]);
            var loop = new AgentLoop(
                new OneResponseChatClient(request =>
                {
                    var messagePosition = request.Messages.Length;
                    var terminalPosition =
                        slotPosition == AgentLimits.PartsPerMessage - 1
                            ? AgentLimits.PartsPerMessage - 2
                            : AgentLimits.PartsPerMessage - 1;
                    var item = new ProjectContinuationItem(
                        string.Concat("readable-", slotPosition),
                        string.Concat("opaque-", slotPosition),
                        "structured",
                        callId,
                        messagePosition,
                        slotPosition);
                    var contents = Enumerable.Range(
                            0,
                            AgentLimits.PartsPerMessage)
                        .Select(position =>
                            position == slotPosition
                                ? (ProjectChatContent)
                                    new ProjectReasoningContent(
                                        item.Readable,
                                        item.Opaque,
                                        item.Framing,
                                        item.AssociatedCallId,
                                        item.MessagePosition,
                                        item.ContentPosition)
                                : position == terminalPosition
                                    ? new ProjectToolCallContent(
                                        callId,
                                        AgentToolRegistry.FinishReviewName,
                                        FinishJson)
                                    : new ProjectTextContent(
                                        string.Concat("text-", position)))
                        .ToArray();
                    return new ProjectChatResponse(
                        new ProjectChatMessage("assistant", contents),
                        new ProjectChatUsage(1, 1),
                        CapturedResponseBodyBytes: 1,
                        new ProjectContinuation(
                            "provider",
                            "model",
                            "adapter",
                            "session0",
                            [item]));
                }),
                new NeverToolExecutor());
            var outcome = await loop.RunAsync(
                run,
                CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible);
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    run,
                    outcome,
                    trusted,
                    run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.True(built.Succeeded, built.FailureCode);
            var restored = Restore(
                Assert.IsType<AgentSessionArtifact>(built.Artifact),
                trusted,
                AgentSessionHeadTransition.SameHead);
            Assert.True(restored.Succeeded, restored.Code);

            var materialized = MinimalChatClient.Materialize(
                new ProjectChatRequest(
                    restored.RunRequest!.InitialMessages,
                    AgentToolRegistry.Definitions.ToArray(),
                    restored.RunRequest.Continuation,
                    ThinkingRequired: true));
            var assistant = Assert.Single(
                materialized.Messages,
                message => message.Role == "assistant");
            Assert.Equal(
                "reasoning",
                assistant.Contents[slotPosition].Kind);
            Assert.Equal(
                Enumerable.Range(0, AgentLimits.PartsPerMessage),
                assistant.Contents.Select(content => content.Position));
        }
    }

    [Fact]
    public async Task SyntheticContinuationVariantsRoundTripValueExactly()
    {
        AgentContinuationCodecValue[] fixtures =
        [
            new("雪\"\\\n", string.Empty, "readable"),
            new(string.Empty, "signed:AA==\nredacted", "opaque"),
            new("résumé", "signature-Ω", "structured"),
            new(string.Empty, string.Empty, "structured"),
        ];
        foreach (var fixture in fixtures)
        {
            var trusted = Trusted();
            var run = new AgentRunRequest(
                Identity(),
                Materialize(trusted, prior: null),
                "session0",
                [.. trusted.ControlMessages, User("review")]);
            var item = new ProjectContinuationItem(
                fixture.Readable,
                fixture.Opaque,
                fixture.Framing,
                "finish0",
                run.InitialMessages.Length,
                0);
            var outcome = await new AgentLoop(
                new OneResponseChatClient(_ =>
                    new ProjectChatResponse(
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
                                    "finish0",
                                    AgentToolRegistry.FinishReviewName,
                                    FinishJson),
                            ]),
                        new ProjectChatUsage(1, 1),
                        CapturedResponseBodyBytes: 1,
                        new ProjectContinuation(
                            "provider",
                            "model",
                            "adapter",
                            "session0",
                            [item]))),
                new NeverToolExecutor()).RunAsync(
                    run,
                    CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible);
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    run,
                    outcome,
                    trusted,
                    run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.True(built.Succeeded, built.FailureCode);
            var restored = Restore(
                Assert.IsType<AgentSessionArtifact>(built.Artifact),
                trusted,
                AgentSessionHeadTransition.SameHead);
            Assert.True(restored.Succeeded, restored.Code);
            var restoredItem = Assert.Single(
                restored.RunRequest!.Continuation!.Items);
            Assert.Equal(fixture.Readable, restoredItem.Readable);
            Assert.Equal(fixture.Opaque, restoredItem.Opaque);
            Assert.Equal(fixture.Framing, restoredItem.Framing);
        }
    }

    [Fact]
    public async Task ContinuationItemAndAggregateCapsAreExact()
    {
        var exactItem = await BuildSizedContinuationAsync(
            [AgentLimits.ContinuationItemBytes]);
        Assert.True(exactItem.Succeeded, exactItem.FailureCode);

        var overItem = await BuildSizedContinuationAsync(
            [AgentLimits.ContinuationItemBytes + 1]);
        Assert.Equal(
            AgentSessionCodes.ContinuationInvalid,
            overItem.FailureCode);
        Assert.Null(overItem.Artifact);

        var exactAggregate = await BuildSizedContinuationAsync(
        [
            AgentLimits.ContinuationItemBytes,
            AgentLimits.ContinuationItemBytes,
            AgentLimits.ContinuationItemBytes,
            AgentLimits.ContinuationItemBytes,
        ]);
        Assert.True(
            exactAggregate.Succeeded,
            exactAggregate.FailureCode);

        var overAggregate = await BuildSizedContinuationAsync(
        [
            52_429,
            52_429,
            52_429,
            52_429,
            52_429,
        ]);
        Assert.Equal(
            AgentSessionCodes.ContinuationInvalid,
            overAggregate.FailureCode);
        Assert.Null(overAggregate.Artifact);
    }

    [Fact]
    public async Task CorrectPriorHashCannotAuthorizeAlteredHistoryOrContinuation()
    {
        var trusted = Trusted();
        var previous = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        Assert.True(AgentSessionRequestReconstruction.TryReconstructHistory(
            previous.Artifact.Document,
            SyntheticContinuationCodec.Instance,
            trusted.ControlMessages.Length,
            out var history,
            out var continuation,
            out var failure),
            failure);
        var predecessor = new AgentSessionPredecessor(
            previous.Artifact.Plaintext,
            previous.Artifact.SessionSha256,
            previous.EnvelopeSha256,
            0,
            previous.Artifact.Document.ProducerBaseSha,
            previous.Artifact.Document.ProducerHeadSha,
            PredecessorStateSha256: null);
        var plan = Materialize(
            trusted,
            previous.Artifact.SessionSha256);

        var alteredMessages = history!.ToArray();
        var priorContext = Assert.IsType<ProjectTextContent>(
            alteredMessages[0].Contents[0]);
        alteredMessages[0] = User(
            string.Concat(priorContext.Text, " altered"));
        var alteredHistoryRun = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [
                .. trusted.ControlMessages,
                .. alteredMessages,
                User("next"),
            ],
            continuation);
        var alteredHistoryOutcome = await Loop(
            "finish1",
            reasoning: false).RunAsync(
            alteredHistoryRun,
            CancellationToken.None);
        Assert.True(alteredHistoryOutcome.CompletedSessionEligible);
        var alteredHistoryBuild = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                alteredHistoryRun,
                alteredHistoryOutcome,
                trusted,
                alteredHistoryRun.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                predecessor,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            alteredHistoryBuild.FailureCode);

        var originalItem = Assert.Single(continuation!.Items);
        var alteredContinuation = continuation with
        {
            Items =
            [
                originalItem with
                {
                    Opaque = string.Concat(
                        originalItem.Opaque,
                        "-altered"),
                },
            ],
        };
        var alteredContinuationRun = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [
                .. trusted.ControlMessages,
                .. history!,
                User("next"),
            ],
            alteredContinuation);
        var alteredContinuationOutcome = await Loop(
            "finish1",
            reasoning: false).RunAsync(
            alteredContinuationRun,
            CancellationToken.None);
        Assert.True(alteredContinuationOutcome.CompletedSessionEligible);
        var alteredContinuationBuild = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                alteredContinuationRun,
                alteredContinuationOutcome,
                trusted,
                alteredContinuationRun.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                predecessor,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.ContinuationInvalid,
            alteredContinuationBuild.FailureCode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RejectedHeadTransitionsNeverReturnARequest(
        int transitionValue)
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var result = Restore(
            built,
            trusted,
            (AgentSessionHeadTransition)transitionValue);

        Assert.Equal(AgentSessionCodes.TransitionRejected, result.Code);
        Assert.Null(result.RunRequest);
    }

    [Fact]
    public async Task VerifiedAheadAdmitsDistinctCurrentHeadWithoutRewritingProducer()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var current = Identity() with
        {
            BaseSha = new string('2', 40),
            HeadSha = new string('3', 40),
        };
        var accepted = new AgentSessionAcceptedState(
            0,
            built.Artifact.SessionSha256,
            built.EnvelopeSha256,
            built.Artifact.Document.ProducerBaseSha,
            built.Artifact.Document.ProducerHeadSha,
            PredecessorStateSha256: null);
        AgentSessionRestoreResult Invoke(AgentSessionHeadTransition transition) =>
            AgentSessionRestorer.Restore(new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                built.Artifact.Plaintext,
                accepted,
                trusted,
                "session0",
                current,
                User("next"),
                transition,
                SyntheticContinuationCodec.Instance));

        var rejected = Invoke(AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.TransitionRejected,
            rejected.Code);
        var restored = Invoke(AgentSessionHeadTransition.VerifiedAhead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Equal(current, restored.RunRequest!.ReviewedIdentity);
        Assert.Equal(
            Identity().HeadSha,
            restored.Artifact!.Document.ProducerHeadSha);
    }

    [Fact]
    public async Task StablePlanAndInitialEventPrefixAreCrossChecked()
    {
        var trusted = Trusted();
        var correctPlan = Materialize(trusted, prior: null);
        var wrongPlan = correctPlan with
        {
            PolicySha256 = new string('2', 64),
        };
        var wrongRun = new AgentRunRequest(
            Identity(),
            wrongPlan,
            "session0",
            [.. trusted.ControlMessages, User("review")]);
        var wrongOutcome = await Loop(
            "finish0",
            reasoning: false).RunAsync(
            wrongRun,
            CancellationToken.None);
        Assert.True(wrongOutcome.CompletedSessionEligible);
        var wrongBuild = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                wrongRun,
                wrongOutcome,
                trusted,
                wrongRun.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            wrongBuild.FailureCode);

        var correctRun = wrongRun with { StablePlan = correctPlan };
        var correctOutcome = await Loop(
            "finish0",
            reasoning: false).RunAsync(
            correctRun,
            CancellationToken.None);
        var initialMessage = Assert.IsType<AgentMessageEvent>(
            correctOutcome.Events[1]);
        var mutatedOutcome = correctOutcome with
        {
            Events = correctOutcome.Events.SetItem(
                1,
                initialMessage with { Role = "user" }),
        };
        var eventBuild = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                correctRun,
                mutatedOutcome,
                trusted,
                correctRun.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            eventBuild.FailureCode);
    }

    private static byte[] Frame(byte[] json)
    {
        var framed = new byte[AgentSessionFormat.FramingBytes + json.Length];
        "APRSES01"u8.CopyTo(framed);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            framed.AsSpan(8, 4),
            checked((uint)json.Length));
        json.CopyTo(framed, AgentSessionFormat.FramingBytes);
        return framed;
    }

    private static string RunJson(
        AgentSessionArtifact artifact,
        int runOrdinal)
    {
        using var document = JsonDocument.Parse(
            artifact.Plaintext[AgentSessionFormat.FramingBytes..]);
        return document.RootElement
            .GetProperty("completed_runs")[runOrdinal]
            .GetRawText();
    }

    private static AgentSessionArtifact Rewrite(
        AgentSessionDocument document,
        AgentSessionCompletedRun run)
    {
        var rewritten = document with
        {
            CompletedRuns = document.CompletedRuns.SetItem(
                run.RunOrdinal,
                run),
        };
        Assert.True(AgentSessionCodec.TryWrite(
            rewritten,
            out var artifact,
            out var failure),
            failure);
        return artifact!;
    }

    private static AgentSessionRestoreResult Restore(
        BuiltGeneration built,
        AgentSessionTrustedRequest trusted,
        AgentSessionHeadTransition transition) =>
        Restore(built.Artifact, built.EnvelopeSha256, trusted, transition);

    private static AgentSessionRestoreResult Restore(
        AgentSessionArtifact artifact,
        AgentSessionTrustedRequest trusted,
        AgentSessionHeadTransition transition) =>
        Restore(
            artifact,
            new string('e', 64),
            trusted,
            transition);

    private static AgentSessionRestoreResult Restore(
        AgentSessionArtifact artifact,
        string envelopeSha256,
        AgentSessionTrustedRequest trusted,
        AgentSessionHeadTransition transition) =>
        AgentSessionRestorer.Restore(new AgentSessionRestoreInput(
            AgentSessionLocatorFamily.Current,
            AgentSessionRestoreIntent.Automatic,
            ExplicitReset: false,
            artifact.Plaintext,
            new AgentSessionAcceptedState(
                artifact.Document.Generation,
                artifact.SessionSha256,
                envelopeSha256,
                artifact.Document.ProducerBaseSha,
                artifact.Document.ProducerHeadSha,
                artifact.Document.PredecessorStateSha256),
            trusted,
            artifact.Document.SessionId,
            Identity(),
            User("next"),
            transition,
            SyntheticContinuationCodec.Instance));

    private static async Task<AgentSessionBuildResult>
        BuildSizedContinuationAsync(int[] payloadSizes)
    {
        var trusted = Trusted();
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. trusted.ControlMessages, User("review")]);
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
            {
                var messagePosition = request.Messages.Length;
                var items = payloadSizes.Select((size, index) =>
                    new ProjectContinuationItem(
                        size.ToString(CultureInfo.InvariantCulture),
                        string.Empty,
                        "sized",
                        "finish0",
                        messagePosition,
                        index)).ToArray();
                var contents = items
                    .Select(item => (ProjectChatContent)
                        new ProjectReasoningContent(
                            item.Readable,
                            item.Opaque,
                            item.Framing,
                            item.AssociatedCallId,
                            item.MessagePosition,
                            item.ContentPosition))
                    .Append(new ProjectToolCallContent(
                        "finish0",
                        AgentToolRegistry.FinishReviewName,
                        FinishJson))
                    .ToArray();
                return new ProjectChatResponse(
                    new ProjectChatMessage("assistant", contents),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        "provider",
                        "model",
                        "adapter",
                        "session0",
                        items));
            }),
            new NeverToolExecutor()).RunAsync(
                run,
                CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SizedContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
    }

    private static async Task<BuiltGeneration> BuildGroundedGenerationAsync(
        AgentSessionTrustedRequest trusted)
    {
        var identity = Identity();
        const string readJson =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        Assert.True(AgentToolArguments.TryReadFile(
            readJson,
            out var readArguments));
        var withoutId = new ReadFileResult(
            "ok",
            identity,
            "src/a.cs",
            new string('a', 64),
            1,
            1,
            1,
            1,
            [new ReadFileLine(1, "line")],
            Truncated: false,
            TruncationReason: null,
            ObservationId: null);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(
                withoutId,
                includeObservationId: false));
        var readResult = withoutId with
        {
            ObservationId = observationId,
        };
        var resultBytes = ReadFileResultWriter.Write(readResult);
        var observation = new AgentObservation(
            observationId,
            identity,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("src/a.cs", ImmutableHashSet.Create(1)));
        var finding = new AgentFinding(
            "high",
            "grounded",
            "grounded message",
            [
                new AgentEvidence(
                    observationId,
                    "src/a.cs",
                    1,
                    1),
            ]);
        var terminalBytes = AgentToolArguments.WriteFinishReview(
            "complete",
            [finding]);
        var responses = new Queue<ProjectChatResponse>(
        [
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "read0",
                            AgentToolRegistry.ReadFileName,
                            Encoding.UTF8.GetString(
                                readArguments!.CanonicalBytes)),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "finish0",
                            AgentToolRegistry.FinishReviewName,
                            Encoding.UTF8.GetString(terminalBytes)),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var run = new AgentRunRequest(
            identity,
            Materialize(trusted, prior: null),
            "session0",
            [.. trusted.ControlMessages, User("review")]);
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new FixedToolExecutor(new AgentToolExecution(
                true,
                FailureCode: null,
                Encoding.UTF8.GetString(resultBytes),
                resultBytes,
                observation))).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        return new BuiltGeneration(
            built.Artifact!,
            new string('e', 64));
    }

    private static AgentToolExecution ReadExecution(
        string path,
        char rawHashCharacter)
    {
        var withoutId = new ReadFileResult(
            "ok",
            Identity(),
            path,
            new string(rawHashCharacter, 64),
            1,
            1,
            1,
            1,
            [new ReadFileLine(1, "line")],
            Truncated: false,
            TruncationReason: null,
            ObservationId: null);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(
                withoutId,
                includeObservationId: false));
        var result = withoutId with
        {
            ObservationId = observationId,
        };
        var resultBytes = ReadFileResultWriter.Write(result);
        return new AgentToolExecution(
            true,
            FailureCode: null,
            Encoding.UTF8.GetString(resultBytes),
            resultBytes,
            new AgentObservation(
                observationId,
                Identity(),
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(path, ImmutableHashSet.Create(1))));
    }

    private static AgentSessionArtifact MutateTerminal(
        AgentSessionArtifact artifact,
        int mutation)
    {
        var run = artifact.Document.CompletedRuns[0];
        var terminalMessage = Assert.IsType<
            AgentSessionAssistantMessageRecord>(run.Records[^2]);
        var terminal = Assert.Single(
            terminalMessage.Contents.OfType<
                AgentSessionTerminalCallContent>());
        Assert.True(AgentToolArguments.TryFinishReview(
            terminal.ArgumentsJson,
            out var original));
        var originalFinding = Assert.Single(original!.Findings);
        var originalEvidence = Assert.Single(originalFinding.Evidence);
        ImmutableArray<AgentFinding> findings = mutation switch
        {
            0 =>
            [
                originalFinding with
                {
                    Evidence =
                    [
                        originalEvidence with
                        {
                            ObservationId = new string('f', 64),
                        },
                    ],
                },
            ],
            1 =>
            [
                originalFinding with
                {
                    Evidence =
                    [
                        originalEvidence with { Path = "src/b.cs" },
                    ],
                },
            ],
            2 =>
            [
                originalFinding with
                {
                    Evidence =
                    [
                        originalEvidence with
                        {
                            StartLine = 2,
                            EndLine = 2,
                        },
                    ],
                },
            ],
            3 => [originalFinding, originalFinding],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var invalidBytes = AgentToolArguments.WriteFinishReview(
            original.Summary,
            findings);
        var invalidJson = Encoding.UTF8.GetString(invalidBytes);
        var invalidSha = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            invalidBytes);
        using var invalidDocument = JsonDocument.Parse(invalidBytes);
        var invalidFindings = invalidDocument.RootElement
            .GetProperty("findings")
            .GetRawText();
        var mutatedTerminal = terminal with
        {
            ArgumentsJson = invalidJson,
            ArgumentsSha256 = invalidSha,
        };
        var terminalIndex = terminalMessage.Contents.IndexOf(terminal);
        var mutatedMessage = terminalMessage with
        {
            Contents = terminalMessage.Contents.SetItem(
                terminalIndex,
                mutatedTerminal),
        };
        var outcome = Assert.IsType<AgentSessionReviewOutcomeRecord>(
            run.Records[^1]);
        var mutatedOutcome = outcome with
        {
            TerminalSha256 = invalidSha,
            Summary = original.Summary,
            FindingsJson = invalidFindings,
        };
        return Rewrite(
            artifact.Document,
            run with
            {
                Records = run.Records
                    .SetItem(run.Records.Length - 2, mutatedMessage)
                    .SetItem(run.Records.Length - 1, mutatedOutcome),
            });
    }

    private static AgentLoop TwoReasoningLoop() =>
        new(
            new OneResponseChatClient(request =>
            {
                var messagePosition = request.Messages.Length;
                var first = new ProjectContinuationItem(
                    "readable-0",
                    "opaque-0",
                    "structured",
                    "finish0",
                    messagePosition,
                    0);
                var second = new ProjectContinuationItem(
                    "readable-1",
                    "opaque-1",
                    "structured",
                    "finish0",
                    messagePosition,
                    1);
                return new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                first.Readable,
                                first.Opaque,
                                first.Framing,
                                first.AssociatedCallId,
                                first.MessagePosition,
                                first.ContentPosition),
                            new ProjectReasoningContent(
                                second.Readable,
                                second.Opaque,
                                second.Framing,
                                second.AssociatedCallId,
                                second.MessagePosition,
                                second.ContentPosition),
                            new ProjectToolCallContent(
                                "finish0",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        "provider",
                        "model",
                        "adapter",
                        "session0",
                        [second, first]));
            }),
            new NeverToolExecutor());

    private static async Task<BuiltGeneration> BuildGenerationAsync(
        AgentSessionTrustedRequest trusted,
        BuiltGeneration? previous,
        string currentText,
        string callId,
        bool reasoning)
    {
        ProjectChatMessage[]? history = null;
        ProjectContinuation? continuation = null;
        AgentSessionPredecessor? predecessor = null;
        string? prior = null;
        if (previous is not null)
        {
            var stable = Materialize(
                trusted,
                previous.Artifact.SessionSha256);
            Assert.True(
                AgentSessionRequestReconstruction.TryReconstructHistory(
                    previous.Artifact.Document,
                    SyntheticContinuationCodec.Instance,
                    trusted.ControlMessages.Length,
                    out history,
                    out continuation,
                    out var failure),
                failure);
            prior = previous.Artifact.SessionSha256;
            predecessor = new AgentSessionPredecessor(
                previous.Artifact.Plaintext,
                previous.Artifact.SessionSha256,
                previous.EnvelopeSha256,
                previous.Artifact.Document.Generation,
                previous.Artifact.Document.ProducerBaseSha,
                previous.Artifact.Document.ProducerHeadSha,
                previous.Artifact.Document.PredecessorStateSha256);
            Assert.Equal(prior, stable.PriorSessionSha256);
        }

        var completed = await CompleteAsync(
            trusted,
            prior,
            (history ?? [], continuation),
            currentText,
            callId,
            reasoning);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            completed.Run,
            completed.Outcome,
            trusted,
            completed.Run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            predecessor,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        return new BuiltGeneration(
            built.Artifact!,
            new string(
                (char)('a' + built.Artifact!.Document.Generation),
                64));
    }

    private static async Task<CompletedRun> CompleteAsync(
        AgentSessionTrustedRequest trusted,
        string? priorSessionSha256,
        (ProjectChatMessage[] History, ProjectContinuation? Continuation)?
            previous,
        string currentText,
        string callId,
        bool reasoning)
    {
        var plan = Materialize(trusted, priorSessionSha256);
        var run = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [
                .. trusted.ControlMessages,
                .. previous?.History ?? [],
                User(currentText),
            ],
            previous?.Continuation);
        var outcome = await Loop(callId, reasoning).RunAsync(
            run,
            CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return new CompletedRun(run, outcome);
    }

    private static AgentLoop Loop(string callId, bool reasoning) =>
        new(
            new OneResponseChatClient(request =>
            {
                var contents = new List<ProjectChatContent>();
                ProjectContinuation? continuation = null;
                if (reasoning)
                {
                    var item = new ProjectContinuationItem(
                        string.Concat("readable-", callId[^1]),
                        string.Concat("opaque-", callId[^1]),
                        "structured",
                        callId,
                        request.Messages.Length,
                        0);
                    contents.Add(new ProjectReasoningContent(
                        item.Readable,
                        item.Opaque,
                        item.Framing,
                        item.AssociatedCallId,
                        item.MessagePosition,
                        item.ContentPosition));
                    continuation = new ProjectContinuation(
                        "provider",
                        "model",
                        "adapter",
                        "session0",
                        [item]);
                }

                contents.Add(new ProjectToolCallContent(
                    callId,
                    AgentToolRegistry.FinishReviewName,
                    FinishJson));
                return new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        contents.ToArray()),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    continuation);
            }),
            new NeverToolExecutor());

    private static StableAgentPlan Materialize(
        AgentSessionTrustedRequest trusted,
        string? prior)
    {
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            prior,
            out var materialized));
        return materialized!.StablePlan;
    }

    private static AgentSessionTrustedRequest Trusted() =>
        new(
            "repo",
            1,
            "workflow@trusted-sha",
            "trusted policy A"u8.ToArray(),
            [
                new ProjectChatMessage(
                    "system",
                    [new ProjectTextContent("trusted control A")]),
            ],
            "build",
            "provider",
            "model",
            "adapter");

    private static ReviewedIdentity Identity() =>
        new(
            "repo",
            1,
            new string('0', 40),
            new string('1', 40));

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    private sealed record CompletedRun(
        AgentRunRequest Run,
        AgentRunOutcome Outcome);

    private sealed record BuiltGeneration(
        AgentSessionArtifact Artifact,
        string EnvelopeSha256);

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
            throw new InvalidOperationException("No non-terminal tool expected.");
    }

    private sealed class QueueChatClient(
        Queue<ProjectChatResponse> responses) : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private sealed class FixedToolExecutor(
        AgentToolExecution execution) : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(execution);
    }

    private sealed class MappedToolExecutor(
        IReadOnlyDictionary<string, AgentToolExecution> executions)
        : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) =>
            executions.ContainsKey(call.CallId)
                ? null
                : "agent_tool_invalid";

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(executions[call.CallId]);
    }

    private sealed class SizedContinuationCodec
        : IAgentContinuationCodec
    {
        internal static SizedContinuationCodec Instance { get; } = new();

        public string CodecId => "r2-sized";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            if (!int.TryParse(
                    value.Readable,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
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
            if (!StringComparer.Ordinal.Equals(encoding, "base64") ||
                payload.IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }

            value = new AgentContinuationCodecValue(
                payload.Length.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                "sized");
            return true;
        }
    }

    private sealed class SyntheticContinuationCodec
        : IAgentContinuationCodec
    {
        internal static SyntheticContinuationCodec Instance { get; } = new();

        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            var writer = new Rfc8785Writer(256);
            writer.WriteObjectStart();
            writer.WriteProperty("kind");
            switch (value.Framing)
            {
                case "readable" when value.Opaque.Length == 0:
                    writer.WriteString("readable");
                    writer.WriteProperty("text");
                    writer.WriteString(value.Readable);
                    break;
                case "opaque" when value.Readable.Length == 0:
                    writer.WriteString("opaque");
                    writer.WriteProperty("opaque");
                    writer.WriteString(value.Opaque);
                    break;
                case "structured":
                    writer.WriteString("structured");
                    writer.WriteProperty("framing");
                    writer.WriteString(value.Framing);
                    writer.WriteProperty("readable");
                    writer.WriteString(value.Readable);
                    writer.WriteProperty("opaque");
                    writer.WriteString(value.Opaque);
                    writer.WriteProperty("signature");
                    writer.WriteString(string.Empty);
                    writer.WriteProperty("fields");
                    writer.WriteArrayStart();
                    writer.WriteArrayEnd();
                    break;
                default:
                    payload = null;
                    return false;
            }

            writer.WriteObjectEnd();
            payload = new AgentContinuationEncodedPayload(
                "utf8",
                writer.ToImmutableArray().ToArray());
            return true;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            if (!StringComparer.Ordinal.Equals(encoding, "utf8"))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;
                var properties = root.EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray();
                switch (root.GetProperty("kind").GetString())
                {
                    case "readable" when properties.SequenceEqual(
                        ["kind", "text"]):
                        var text = root.GetProperty("text").GetString();
                        if (text is not null)
                        {
                            value = new AgentContinuationCodecValue(
                                text,
                                string.Empty,
                                "readable");
                            return true;
                        }

                        break;
                    case "opaque" when properties.SequenceEqual(
                        ["kind", "opaque"]):
                        var opaque = root.GetProperty("opaque").GetString();
                        if (opaque is not null)
                        {
                            value = new AgentContinuationCodecValue(
                                string.Empty,
                                opaque,
                                "opaque");
                            return true;
                        }

                        break;
                    case "structured" when properties.SequenceEqual(
                        [
                            "kind",
                            "framing",
                            "readable",
                            "opaque",
                            "signature",
                            "fields",
                        ]) &&
                        root.GetProperty("signature").GetString() ==
                            string.Empty &&
                        root.GetProperty("fields").GetArrayLength() == 0:
                        var readable =
                            root.GetProperty("readable").GetString();
                        var structuredOpaque =
                            root.GetProperty("opaque").GetString();
                        var framing =
                            root.GetProperty("framing").GetString();
                        if (readable is not null &&
                            structuredOpaque is not null &&
                            framing is not null)
                        {
                            value = new AgentContinuationCodecValue(
                                readable,
                                structuredOpaque,
                                framing);
                            return true;
                        }

                        break;
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException)
            {
            }

            return false;
        }
    }
}
