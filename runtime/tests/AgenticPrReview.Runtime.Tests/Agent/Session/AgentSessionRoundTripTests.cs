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
            "d31fd4311a3b3d4ca8495a80f1fc826226bf2f83408d823e2f6746db040ee751",
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
        Assert.Null(restore.Code);
        Assert.Equal(
            artifact.SessionSha256,
            restore.RunRequest!.StablePlan.PriorSessionSha256);
        Assert.Equal(5, restore.RunRequest.InitialMessages.Length);
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
        var terminalResultMessage = Assert.Single(
            materialized.Messages,
            message => message.Role == "tool");
        var terminalResult = Assert.Single(
            terminalResultMessage.Contents);
        Assert.Equal("tool_result", terminalResult.Kind);
        Assert.Equal("finish0", terminalResult.CallId);
        Assert.Equal("{}", terminalResult.Text);
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
        Assert.Equal(
            [
                (
                    2607,
                    "d36212b450207a520faf72028ad0071633eac94723c341b8227134cf79e18cba"),
                (
                    4162,
                    "adad4064a383d8900d6b2ab2bf5730680465ef890dd00021b6d1046ae008e8b7"),
                (
                    6015,
                    "19eb842838bdc8c9c31270fffca9e6c4979b44876b27a868791bce6adba85e9f"),
            ],
            new[]
            {
                (
                    generation0.Artifact.Plaintext.Length,
                    generation0.Artifact.SessionSha256),
                (
                    generation1.Artifact.Plaintext.Length,
                    generation1.Artifact.SessionSha256),
                (
                    generation2.Artifact.Plaintext.Length,
                    generation2.Artifact.SessionSha256),
            });

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
            generation0.Artifact.SessionSha256,
            generation1.Artifact.Document.PriorSessionSha256);
        Assert.Equal(
            generation0.EnvelopeSha256,
            generation1.Artifact.Document.PredecessorStateSha256);
        Assert.Equal(
            RunJson(generation0.Artifact, 0),
            RunJson(generation1.Artifact, 0));
        Assert.Equal(
            RunJson(generation0.Artifact, 0),
            RunJson(generation2.Artifact, 0));
        Assert.Equal(
            RunJson(generation1.Artifact, 1),
            RunJson(generation2.Artifact, 1));

        var restored = Restore(
            generation2,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Equal(
            [
                "system",
                "user",
                "assistant",
                "tool",
                "user",
                "assistant",
                "tool",
                "user",
                "assistant",
                "tool",
                "user",
            ],
            restored.RunRequest!.InitialMessages.Select(message =>
                message.Role));
        Assert.Equal(
            ["g0", "g1", "g2", "next"],
            restored.RunRequest.InitialMessages
                .Where(message =>
                    StringComparer.Ordinal.Equals(message.Role, "user"))
                .Select(message =>
                    Assert.IsType<ProjectTextContent>(
                        Assert.Single(message.Contents)).Text));
        Assert.Equal(
            ["finish0", "finish1", "finish2"],
            restored.RunRequest.InitialMessages
                .Where(message =>
                    StringComparer.Ordinal.Equals(
                        message.Role,
                        "assistant"))
                .Select(message =>
                    Assert.Single(
                        message.Contents
                            .OfType<ProjectToolCallContent>())
                        .CallId));
        Assert.Equal(
            [
                ("opaque-0", "finish0", 2, 0),
                ("opaque-2", "finish2", 8, 0),
            ],
            restored.RunRequest.Continuation!.Items.Select(item =>
                (
                    item.Opaque,
                    item.AssociatedCallId!,
                    item.MessagePosition,
                    item.ContentPosition)));
        Assert.All(
            generation2.Artifact.Document.CompletedRuns,
            run => Assert.Equal(Identity(), run.ReviewedIdentity));
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
            [.. Controls(trusted), current]);
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
    public async Task FailedCancelledDeadlineBudgetAndPartialRunsProduceNoBytes()
    {
        var trusted = Trusted();
        var completed = await CompleteOneReadAsync(trusted);
        string[] failureCodes =
        [
            AgentFailureCodes.Cancelled,
            AgentFailureCodes.DeadlineExceeded,
            AgentFailureCodes.ModelLimit,
            AgentFailureCodes.TokenLimit,
            AgentFailureCodes.ToolLimit,
        ];
        foreach (var code in failureCodes)
        {
            var partialEvents = completed.Outcome.Events
                .TakeWhile(logical =>
                    logical is not AgentTerminalEvent)
                .ToImmutableArray();
            var failed = AgentRunOutcome.Failure(
                code,
                modelCalls: 1,
                toolCalls: 1,
                partialEvents);
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    completed.Run,
                    failed,
                    trusted,
                    completed.Run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.RecordInvalid,
                built.FailureCode);
            Assert.Null(built.Artifact);
        }
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
    public async Task StableControlPrefixIsDerivedOnlyFromPolicyBytes()
    {
        var trustedA = Trusted();
        var trustedB = trustedA with
        {
            TrustedPolicyBytes = "trusted policy B"u8.ToArray(),
        };
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trustedA,
            priorSessionSha256: null,
            out var materializedA));
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trustedB,
            priorSessionSha256: null,
            out var materializedB));
        Assert.Equal(
            "trusted policy A",
            Assert.IsType<ProjectTextContent>(
                Assert.Single(
                    Assert.Single(materializedA!.ControlMessages).Contents))
                .Text);
        Assert.Equal(
            "trusted policy B",
            Assert.IsType<ProjectTextContent>(
                Assert.Single(
                    Assert.Single(materializedB!.ControlMessages).Contents))
                .Text);
        Assert.NotEqual(
            materializedA.StablePlan.PolicySha256,
            materializedB.StablePlan.PolicySha256);

        ProjectChatMessage[][] invalidPrefixes =
        [
            [],
            [
                new ProjectChatMessage(
                    "system",
                    [new ProjectTextContent("altered")]),
            ],
            [
                .. materializedA.ControlMessages,
                new ProjectChatMessage(
                    "system",
                    [new ProjectTextContent("extra")]),
            ],
            [
                new ProjectChatMessage(
                    "system",
                    [new ProjectTextContent("extra")]),
                .. materializedA.ControlMessages,
            ],
        ];
        foreach (var prefix in invalidPrefixes)
        {
            var run = new AgentRunRequest(
                Identity(),
                materializedA.StablePlan,
                "session0",
                [.. prefix, User("review")]);
            var outcome = await Loop("finish0", reasoning: false).RunAsync(
                run,
                CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible);
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    run,
                    outcome,
                    trustedA,
                    run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.ScopeMismatch,
                built.FailureCode);
            Assert.Null(built.Artifact);
        }
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
    public async Task ScopeProducerAndTransitionPrecedeRawRecordConversion()
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        var unknownRecord = RawMutation(
            built.Artifact,
            "\"kind\":\"review_context\"",
            "\"kind\":\"unknown_record\"");
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            Restore(
                unknownRecord,
                built.EnvelopeSha256,
                trusted with { ProviderId = "other-provider" },
                AgentSessionHeadTransition.SameHead).Code);

        var producerMismatch = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                unknownRecord.Plaintext,
                new AgentSessionAcceptedState(
                    0,
                    unknownRecord.SessionSha256,
                    built.EnvelopeSha256,
                    new string('9', 40),
                    built.Artifact.Document.ProducerHeadSha,
                    PredecessorStateSha256: null),
                trusted,
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.SameHead,
                SyntheticContinuationCodec.Instance));
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            producerMismatch.Code);

        var unknownContent = RawMutation(
            built.Artifact,
            "\"kind\":\"continuation_slot\"",
            "\"kind\":\"unknown_content\"");
        Assert.Equal(
            AgentSessionCodes.TransitionRejected,
            Restore(
                unknownContent,
                built.EnvelopeSha256,
                trusted,
                AgentSessionHeadTransition.Unknown).Code);

        var malformedContinuation = RawMutation(
            built.Artifact,
            "\"item_id\":\"c0_0\"",
            "\"unknown\":0,\"item_id\":\"c0_0\"");
        Assert.Equal(
            AgentSessionCodes.ScopeMismatch,
            Restore(
                malformedContinuation,
                built.EnvelopeSha256,
                trusted with { BuildId = "other-build" },
                AgentSessionHeadTransition.SameHead).Code);
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

        foreach (var result in substitutions.Select(substitution =>
                     Restore(
                         built.Artifact,
                         built.EnvelopeSha256,
                         substitution,
                         AgentSessionHeadTransition.SameHead)))
        {
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
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "{\"namespace\":\"agentic-pr-review/agent-session\"",
                "{\"namespace\":\"agentic-pr-review/agent-session\",\"namespace\":\"agentic-pr-review/agent-session\"",
                StringComparison.Ordinal))),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "\"repository_id\":\"repo\"",
                "\"repository_id\":\"\\u0072epo\"",
                StringComparison.Ordinal))),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "\"review_target\":1",
                "\"review_target\":1.0",
                StringComparison.Ordinal))),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "\"review_target\":1",
                "\"review_target\":1e0",
                StringComparison.Ordinal))),
            Frame(Encoding.UTF8.GetBytes(jsonText.Replace(
                "\"review_target\":1",
                "\"review_target\":-0",
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
    public async Task RequiredRootFieldsRejectMissingNullTypeAndDomainMutations()
    {
        var built = await BuildGenerationAsync(
            Trusted(),
            previous: null,
            "g0",
            "finish0",
            reasoning: false);
        var json = Encoding.UTF8.GetString(
            built.Artifact.Plaintext[AgentSessionFormat.FramingBytes..]);
        (string Name, string WrongType, string OutOfDomain)[] vectors =
        [
            ("namespace", "0", "\"\""),
            ("discriminator", "0", "\"\""),
            ("session_id", "0", "\"\""),
            ("repository_id", "0", "\"\""),
            ("review_target", "\"1\"", "0"),
            ("workflow_identity", "0", "\"\""),
            ("provider_id", "0", "\"\""),
            ("model_id", "0", "\"\""),
            ("adapter_id", "0", "\"\""),
            ("policy_sha256", "0", "\"x\""),
            ("build_id", "0", "\"\""),
            ("toolset_sha256", "0", "\"x\""),
            ("limits_sha256", "0", "\"x\""),
            ("producer_base_sha", "0", "\"x\""),
            ("producer_head_sha", "0", "\"x\""),
            ("generation", "\"0\"", "-1"),
            ("completed_runs", "{}", "[]"),
        ];
        using var document = JsonDocument.Parse(json);
        foreach (var vector in vectors)
        {
            var raw = document.RootElement
                .GetProperty(vector.Name)
                .GetRawText();
            string[] mutations =
            [
                RemoveRootProperty(json, vector.Name, raw),
                ReplaceRootProperty(json, vector.Name, raw, "null"),
                ReplaceRootProperty(
                    json,
                    vector.Name,
                    raw,
                    vector.WrongType),
                ReplaceRootProperty(
                    json,
                    vector.Name,
                    raw,
                    vector.OutOfDomain),
            ];
            foreach (var mutation in mutations)
            {
                var plaintext = Frame(Encoding.UTF8.GetBytes(mutation));
                var restored = AgentSessionRestorer.Restore(
                    new AgentSessionRestoreInput(
                        AgentSessionLocatorFamily.Current,
                        AgentSessionRestoreIntent.Automatic,
                        ExplicitReset: false,
                        plaintext,
                        new AgentSessionAcceptedState(
                            0,
                            AgentCanonical.HashDomain(
                                AgentCanonical.SessionDomain,
                                plaintext),
                            built.EnvelopeSha256,
                            built.Artifact.Document.ProducerBaseSha,
                            built.Artifact.Document.ProducerHeadSha,
                            PredecessorStateSha256: null),
                        Trusted(),
                        "session0",
                        Identity(),
                        User("next"),
                        AgentSessionHeadTransition.SameHead,
                        SyntheticContinuationCodec.Instance));
                Assert.Equal(
                    AgentSessionCodes.CurrentMalformed,
                    restored.Code);
                Assert.Null(restored.RunRequest);
            }
        }

        var generation1 = await BuildGenerationAsync(
            Trusted(),
            built,
            "g1",
            "finish1",
            reasoning: false);
        var generation1Json = Encoding.UTF8.GetString(
            generation1.Artifact.Plaintext[
                AgentSessionFormat.FramingBytes..]);
        using var generation1Document =
            JsonDocument.Parse(generation1Json);
        foreach (var name in new[]
                 {
                     "predecessor_state_sha256",
                     "prior_session_sha256",
                 })
        {
            var raw = generation1Document.RootElement
                .GetProperty(name)
                .GetRawText();
            foreach (var mutation in new[]
                     {
                         RemoveRootProperty(generation1Json, name, raw),
                         ReplaceRootProperty(
                             generation1Json,
                             name,
                             raw,
                             "null"),
                         ReplaceRootProperty(
                             generation1Json,
                             name,
                             raw,
                             "0"),
                         ReplaceRootProperty(
                             generation1Json,
                             name,
                             raw,
                             "\"x\""),
                     })
            {
                var plaintext = Frame(Encoding.UTF8.GetBytes(mutation));
                var restored = AgentSessionRestorer.Restore(
                    new AgentSessionRestoreInput(
                        AgentSessionLocatorFamily.Current,
                        AgentSessionRestoreIntent.Automatic,
                        ExplicitReset: false,
                        plaintext,
                        new AgentSessionAcceptedState(
                            1,
                            AgentCanonical.HashDomain(
                                AgentCanonical.SessionDomain,
                                plaintext),
                            generation1.EnvelopeSha256,
                            generation1.Artifact.Document.ProducerBaseSha,
                            generation1.Artifact.Document.ProducerHeadSha,
                            generation1.Artifact.Document
                                .PredecessorStateSha256),
                        Trusted(),
                        "session0",
                        Identity(),
                        User("next"),
                        AgentSessionHeadTransition.SameHead,
                        SyntheticContinuationCodec.Instance));
                Assert.Equal(
                    AgentSessionCodes.CurrentMalformed,
                    restored.Code);
            }
        }
    }

    [Fact]
    public async Task Base64PayloadRejectsUrlUnpaddedAndWhitespaceForms()
    {
        var built = await BuildSizedContinuationAsync([2]);
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = built.Artifact!;
        var run = artifact.Document.CompletedRuns[0];
        var item = Assert.Single(run.Continuation.Items);
        byte[] payloadBytes = [0xfb, 0xff];
        var canonicalItem = item with
        {
            Encoding = "base64",
            Payload = "+/8=",
            PayloadBytes = payloadBytes,
            PayloadSha256 = AgentSessionCodec.ContinuationPayloadSha256(
                run.Continuation.CodecId,
                run.Continuation.CodecDiscriminator,
                item.ItemId,
                "base64",
                payloadBytes),
        };
        var canonical = Rewrite(
            artifact.Document,
            run with
            {
                Continuation = run.Continuation with
                {
                    Items = [canonicalItem],
                },
            });
        (string Old, string Replacement)[] mutations =
        [
            ("\"payload\":\"+/8=\"", "\"payload\":\"-_8=\""),
            ("\"payload\":\"+/8=\"", "\"payload\":\"+/8\""),
            ("\"payload\":\"+/8=\"", "\"payload\":\"+ /8=\""),
            ("\"encoding\":\"base64\"", "\"encoding\":\"base64url\""),
        ];
        foreach (var mutation in mutations)
        {
            var invalid = RawMutation(
                canonical,
                mutation.Old,
                mutation.Replacement);
            Assert.False(AgentSessionCodec.TryParse(
                invalid.Plaintext,
                out var parsed,
                out var failure));
            Assert.Null(parsed);
            Assert.Equal(
                AgentSessionCodes.ContinuationInvalid,
                failure);
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
    public async Task R3ToolSessionBuildsRestoresAndPreservesCanonicalRecords()
    {
        var trusted = Trusted();
        var built = await BuildGroundedR3GenerationAsync(trusted);
        var records = built.Artifact.Document.CompletedRuns[0].Records;
        var calls = records
            .OfType<AgentSessionAssistantMessageRecord>()
            .SelectMany(message => message.Contents)
            .OfType<AgentSessionToolCallContent>()
            .ToArray();
        Assert.Equal(
            [
                AgentToolRegistry.ListFilesName,
                AgentToolRegistry.ListChangedFilesName,
                AgentToolRegistry.ReadDiffName,
            ],
            calls.Select(call => call.Name));
        Assert.Equal(
            [
                "{\"prefix\":null,\"after\":null}",
                "{\"after\":null}",
                "{\"path\":\"src/a.cs\",\"start_hunk\":1,\"hunk_count\":20}",
            ],
            calls.Select(call => call.ArgumentsJson));

        var storedResults = records
            .OfType<AgentSessionToolResultRecord>()
            .ToDictionary(result => result.CallId, StringComparer.Ordinal);
        Assert.Equal(3, storedResults.Count);
        var restored = Restore(
            built,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        var restoredCalls = restored.RunRequest!.InitialMessages
            .SelectMany(message => message.Contents)
            .OfType<ProjectToolCallContent>()
            .Where(call => !StringComparer.Ordinal.Equals(
                call.Name,
                AgentToolRegistry.FinishReviewName))
            .ToArray();
        Assert.Equal(
            calls.Select(call =>
                (call.CallId, call.Name, call.ArgumentsJson)),
            restoredCalls.Select(call =>
                (call.CallId, call.Name, call.ArgumentsJson)));
        var restoredResults = restored.RunRequest.InitialMessages
            .SelectMany(message => message.Contents)
            .OfType<ProjectToolResultContent>()
            .Where(result => storedResults.ContainsKey(result.CallId))
            .ToArray();
        Assert.Equal(
            storedResults.Values.Select(result =>
                (result.CallId, result.ResultJson)),
            restoredResults.Select(result =>
                (result.CallId, result.Result)));
    }

    [Fact]
    public async Task StoredR3ToolArgumentsRequireCanonicalDefaultsAndNulls()
    {
        var trusted = Trusted();
        var built = await BuildGroundedR3GenerationAsync(trusted);
        foreach (var artifact in new[]
                 {
                     (CallId: "list0", Arguments: "{}"),
                     (CallId: "changed0", Arguments: "{}"),
                     (CallId: "diff0", Arguments: "{\"path\":\"src/a.cs\"}"),
                 }.Select(mutation => MutateToolCallArguments(
                     built.Artifact,
                     mutation.Arguments,
                     mutation.CallId)))
        {
            var restored = Restore(
                artifact,
                trusted,
                AgentSessionHeadTransition.SameHead);
            Assert.Equal(AgentSessionCodes.RecordInvalid, restored.Code);
            Assert.Null(restored.RunRequest);
        }
    }

    [Fact]
    public async Task R3ConstructionRejectsSelfConsistentImpossibleResultEvent()
    {
        var trusted = Trusted();
        var completed = await CompleteGroundedR3Async(trusted);
        var invalid = Sign(new ListFilesResult(
            "ok",
            Identity(),
            Prefix: null,
            After: null,
            ["src/a.cs", "src/a.cs"],
            Truncated: false,
            NextAfter: null,
            ObservationId: null));
        var canonical = ListFilesResultWriter.Write(invalid);
        var resultIndex = Enumerable.Range(
                0,
                completed.Outcome.Events.Length)
            .Single(index =>
                completed.Outcome.Events[index] is AgentToolResultEvent
                    result &&
                StringComparer.Ordinal.Equals(result.CallId, "list0"));
        var stored = Assert.IsType<AgentToolResultEvent>(
            completed.Outcome.Events[resultIndex]);
        var mutated = completed.Outcome with
        {
            Events = completed.Outcome.Events.SetItem(
                resultIndex,
                stored with
                {
                    ObservationId = invalid.ObservationId!,
                    ResultSha256 = AgentCanonical.HashRaw(canonical),
                    CanonicalResult = canonical.ToImmutableArray(),
                }),
        };
        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                completed.Run,
                mutated,
                trusted,
                completed.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(AgentSessionCodes.AssociationInvalid, built.FailureCode);
        Assert.Null(built.Artifact);
    }

    [Fact]
    public async Task R3UnknownDurableToolNameFailsBeforeReconstruction()
    {
        var trusted = Trusted();
        var built = await BuildGroundedR3GenerationAsync(trusted);
        var mutated = MutateToolName(
            built.Artifact,
            "list0",
            "list_files_alias");
        var restored = Restore(
            mutated,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(AgentSessionCodes.RecordInvalid, restored.Code);
        Assert.Null(restored.RunRequest);
    }

    [Fact]
    public void R3DurableAdmissionRejectsImpossibleFiniteDomainResults()
    {
        var listCall = new AgentSessionToolCallContent(
            0,
            "list0",
            AgentToolRegistry.ListFilesName,
            "{\"prefix\":null,\"after\":null}");
        var duplicateList = Sign(new ListFilesResult(
            "ok",
            Identity(),
            Prefix: null,
            After: null,
            ["src/a.cs", "src/a.cs"],
            Truncated: false,
            NextAfter: null,
            ObservationId: null));
        Assert.False(TryAdmitStored(listCall, duplicateList));

        var changedCall = new AgentSessionToolCallContent(
            0,
            "changed0",
            AgentToolRegistry.ListChangedFilesName,
            "{\"after\":null}");
        var invalidChange = new ReviewedChangedFile(
            "src/a.cs",
            PreviousPath: null,
            "modified",
            Additions: 1,
            Deletions: 1,
            Changes: 1,
            "available",
            new string('a', 64),
            SourceTruncated: false);
        var changed = Sign(new ListChangedFilesResult(
            "ok",
            Identity(),
            After: null,
            [invalidChange],
            Truncated: false,
            NextAfter: null,
            ObservationId: null));
        Assert.False(TryAdmitStored(changedCall, changed));

        var eofCall = new AgentSessionToolCallContent(
            0,
            "diff0",
            AgentToolRegistry.ReadDiffName,
            "{\"path\":\"src/a.cs\",\"start_hunk\":1,\"hunk_count\":20}");
        var eof = Sign(new ReadDiffResult(
            "eof",
            Identity(),
            "src/a.cs",
            new string('a', 64),
            SourceTruncated: false,
            RequestedStartHunk: 1,
            RequestedHunkCount: AgentLimits.ReadDiffHunks,
            ReturnedStartHunk: null,
            ReturnedEndHunk: null,
            Hunks: [],
            Truncated: false,
            NextStartHunk: null,
            ObservationId: null));
        Assert.False(TryAdmitStored(eofCall, eof));

        var finalCall = new AgentSessionToolCallContent(
            0,
            "diff0",
            AgentToolRegistry.ReadDiffName,
            "{\"path\":\"src/a.cs\",\"start_hunk\":200,\"hunk_count\":1}");
        var finalPage = Sign(new ReadDiffResult(
            "ok",
            Identity(),
            "src/a.cs",
            new string('a', 64),
            SourceTruncated: false,
            RequestedStartHunk: AgentLimits.DiffHunksPerFile,
            RequestedHunkCount: 1,
            ReturnedStartHunk: AgentLimits.DiffHunksPerFile,
            ReturnedEndHunk: AgentLimits.DiffHunksPerFile,
            Hunks: [ContextHunk(AgentLimits.DiffHunksPerFile)],
            Truncated: true,
            NextStartHunk: AgentLimits.DiffHunksPerFile + 1,
            ObservationId: null));
        Assert.False(TryAdmitStored(finalCall, finalPage));
    }

    [Fact]
    public async Task RestoredReadDiffEvidenceRejectsSparseAndPriorRunRanges()
    {
        var trusted = Trusted();
        var generation0 = await BuildGroundedR3GenerationAsync(trusted);
        var diffResult = generation0.Artifact.Document.CompletedRuns[0].Records
            .OfType<AgentSessionToolResultRecord>()
            .Single(result => StringComparer.Ordinal.Equals(
                result.CallId,
                "diff0"));
        var sparse = MutateTerminalEvidence(
            generation0.Artifact,
            runOrdinal: 0,
            new AgentEvidence(
                diffResult.ObservationId,
                "src/a.cs",
                10,
                12));
        var sparseRestore = Restore(
            sparse,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(AgentSessionCodes.AssociationInvalid, sparseRestore.Code);
        Assert.Null(sparseRestore.RunRequest);

        var generation1 = await BuildGenerationAsync(
            trusted,
            generation0,
            "later run",
            "finish1",
            reasoning: false);
        var priorRun = MutateTerminalEvidence(
            generation1.Artifact,
            runOrdinal: 1,
            new AgentEvidence(
                diffResult.ObservationId,
                "src/a.cs",
                10,
                10));
        var priorRestore = Restore(
            new BuiltGeneration(priorRun, generation1.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(AgentSessionCodes.AssociationInvalid, priorRestore.Code);
        Assert.Null(priorRestore.RunRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SelfConsistentReadResultSemanticMutationsAreRejected(
        int mutation)
    {
        var trusted = Trusted();
        var built = await BuildGroundedGenerationAsync(trusted);
        var baseline = new ReadFileResult(
            "ok",
            Identity(),
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
        var invalid = mutation switch
        {
            0 => baseline with
            {
                Status = "start_after_eof",
            },
            1 => baseline with
            {
                ReturnedEndLine = 2,
            },
            2 => baseline with
            {
                TruncationReason = "unapproved",
            },
            3 => baseline with
            {
                Lines = [new ReadFileLine(1, "line\0")],
            },
            4 => baseline with
            {
                Lines = [new ReadFileLine(1, "line\r")],
            },
            5 => baseline with
            {
                Lines = [new ReadFileLine(1, "line\n")],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(
                invalid,
                includeObservationId: false));
        var result = invalid with { ObservationId = observationId };
        var artifact = MutateGroundedResult(
            built.Artifact,
            Encoding.UTF8.GetString(ReadFileResultWriter.Write(result)),
            observationId,
            "src/a.cs",
            1);
        var restored = Restore(
            new BuiltGeneration(artifact, built.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.AssociationInvalid,
            restored.Code);
        Assert.Null(restored.RunRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task SelfConsistentSearchResultSemanticMutationsAreRejected(
        int mutation)
    {
        var trusted = Trusted();
        var built = await BuildGroundedSearchGenerationAsync(trusted);
        var firstMatch = new SearchMatch(
            "src/a.cs",
            new string('a', 64),
            1,
            "line");
        var baseline = new SearchTextResult(
            "ok",
            Identity(),
            AgentCanonical.QuerySha256("line"),
            Path: null,
            FilesScanned: 1,
            RawBytesScanned: 4,
            SkippedInvalidUtf8: 0,
            SkippedBinary: 0,
            SkippedLoneCr: 0,
            SkippedOversized: 0,
            [firstMatch],
            Truncated: false,
            TruncationReason: null,
            ObservationId: null);
        var invalid = mutation switch
        {
            0 => baseline with
            {
                Truncated = true,
                TruncationReason = "unapproved",
            },
            1 => baseline with
            {
                Matches = [firstMatch, firstMatch],
            },
            2 => baseline with
            {
                FilesScanned = 2,
                Matches =
                [
                    new SearchMatch(
                        "src/b.cs",
                        new string('b', 64),
                        1,
                        "line"),
                    firstMatch,
                ],
            },
            3 => baseline with
            {
                FilesScanned = AgentLimits.SearchFiles + 1,
            },
            4 => baseline with
            {
                Matches = [firstMatch with { Text = "line\0" }],
            },
            5 => baseline with
            {
                Matches = [firstMatch with { Text = "line\r" }],
            },
            6 => baseline with
            {
                Matches = [firstMatch with { Text = "line\n" }],
            },
            7 => baseline with
            {
                FilesScanned = 0,
                RawBytesScanned = 1,
                Matches = [],
            },
            8 => baseline with
            {
                RawBytesScanned = 0,
            },
            9 => baseline with
            {
                RawBytesScanned = AgentLimits.SearchFileBytes + 1,
            },
            10 => baseline with
            {
                Truncated = true,
                TruncationReason = "bytes_scanned",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.SearchObservationDomain,
            SearchTextResultWriter.Write(
                invalid,
                includeObservationId: false));
        var result = invalid with { ObservationId = observationId };
        var artifact = MutateGroundedResult(
            built.Artifact,
            Encoding.UTF8.GetString(SearchTextResultWriter.Write(result)),
            observationId,
            invalid.Matches.Length == 0
                ? firstMatch.Path
                : invalid.Matches[0].Path,
            invalid.Matches.Length == 0
                ? firstMatch.Line
                : invalid.Matches[0].Line);
        var restored = Restore(
            new BuiltGeneration(artifact, built.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(
            AgentSessionCodes.AssociationInvalid,
            restored.Code);
        Assert.Null(restored.RunRequest);
    }

    [Fact]
    public async Task ConstructionRejectsProducerImpossibleToolResults()
    {
        var trusted = Trusted();
        var readBaseline = new ReadFileResult(
            "ok",
            Identity(),
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
        foreach (var text in new[] { "line\0", "line\r", "line\n" })
        {
            var completed = await CompleteOneReadAsync(
                trusted,
                ReadExecution(readBaseline with
                {
                    Lines = [new ReadFileLine(1, text)],
                }));
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    completed.Run,
                    completed.Outcome,
                    trusted,
                    completed.Run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.AssociationInvalid,
                built.FailureCode);
            Assert.Null(built.Artifact);
        }

        var firstMatch = new SearchMatch(
            "src/a.cs",
            new string('a', 64),
            1,
            "line");
        var searchBaseline = new SearchTextResult(
            "ok",
            Identity(),
            AgentCanonical.QuerySha256("line"),
            Path: null,
            FilesScanned: 1,
            RawBytesScanned: 4,
            SkippedInvalidUtf8: 0,
            SkippedBinary: 0,
            SkippedLoneCr: 0,
            SkippedOversized: 0,
            [firstMatch],
            Truncated: false,
            TruncationReason: null,
            ObservationId: null);
        var impossibleSearchResults = new[]
        {
            searchBaseline with
            {
                Matches = [firstMatch with { Text = "line\0" }],
            },
            searchBaseline with
            {
                Matches = [firstMatch with { Text = "line\r" }],
            },
            searchBaseline with
            {
                Matches = [firstMatch with { Text = "line\n" }],
            },
            searchBaseline with
            {
                FilesScanned = 0,
                RawBytesScanned = 1,
                Matches = [],
            },
            searchBaseline with { RawBytesScanned = 0 },
            searchBaseline with
            {
                RawBytesScanned = AgentLimits.SearchFileBytes + 1,
            },
            searchBaseline with
            {
                Truncated = true,
                TruncationReason = "bytes_scanned",
            },
        };
        foreach (var impossible in impossibleSearchResults)
        {
            var completed = await CompleteOneSearchAsync(
                trusted,
                SearchExecution(impossible));
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    completed.Run,
                    completed.Outcome,
                    trusted,
                    completed.Run.InitialMessages.Length - 1,
                    SyntheticContinuationCodec.Instance,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.AssociationInvalid,
                built.FailureCode);
            Assert.Null(built.Artifact);
        }
    }

    [Fact]
    public async Task StoredToolArgumentsRequireNormalizedCanonicalBytes()
    {
        const string canonicalGlobalSearch =
            "{\"query\":\"line\",\"path\":null}";
        Assert.False(AgentToolArguments.TrySearchTextCanonical(
            "{\"query\":\"line\"}",
            out _));
        Assert.True(AgentToolArguments.TrySearchTextCanonical(
            canonicalGlobalSearch,
            out var canonicalSearch));
        Assert.Equal(
            canonicalGlobalSearch,
            Encoding.UTF8.GetString(canonicalSearch!.CanonicalBytes));

        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"src/a.cs\"}",
            out var providerRead));
        const string canonicalRead =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":400}";
        Assert.Equal(
            canonicalRead,
            Encoding.UTF8.GetString(providerRead!.CanonicalBytes));

        var trusted = Trusted();
        var read = await BuildGroundedGenerationAsync(trusted);
        var nonCanonicalRead = MutateToolCallArguments(
            read.Artifact,
            "{\"path\":\"src/a.cs\"}");
        var readRestore = Restore(
            new BuiltGeneration(
                nonCanonicalRead,
                read.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(AgentSessionCodes.RecordInvalid, readRestore.Code);
        Assert.Null(readRestore.RunRequest);

        var search = await BuildGroundedSearchGenerationAsync(trusted);
        var validSearchRestore = Restore(
            search,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(validSearchRestore.Succeeded, validSearchRestore.Code);
        Assert.NotNull(validSearchRestore.RunRequest);

        var nonCanonicalSearch = MutateToolCallArguments(
            search.Artifact,
            "{\"query\":\"line\"}");
        var searchRestore = Restore(
            new BuiltGeneration(
                nonCanonicalSearch,
                search.EnvelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.Equal(AgentSessionCodes.RecordInvalid, searchRestore.Code);
        Assert.Null(searchRestore.RunRequest);
    }

    [Fact]
    public async Task MultipleCallsRetainOnePhysicalAssistantMessage()
    {
        var trusted = Trusted();
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review")]);
        const string firstArguments =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
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
                            firstArguments),
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
            ["read1"] = ReadExecution("src/a.cs", 'a'),
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
        Assert.Single(
            records
                .OfType<AgentSessionToolResultRecord>()
                .Select(result => result.ObservationId)
                .Distinct(StringComparer.Ordinal));

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
    public async Task RepeatedObservationAcrossGenerationsRemainsGrounded()
    {
        var trusted = Trusted();
        var generation0 = await BuildGroundedGenerationAsync(trusted);
        var generation1 = await BuildGroundedGenerationAsync(
            trusted,
            generation0);

        var results = generation1.Artifact.Document.CompletedRuns
            .SelectMany(run => run.Records)
            .OfType<AgentSessionToolResultRecord>()
            .ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(results[0].ObservationId, results[1].ObservationId);

        var restored = Restore(
            generation1,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Equal(
            2,
            restored.RunRequest!.InitialMessages.Count(message =>
                StringComparer.Ordinal.Equals(message.Role, "tool") &&
                Assert.IsType<ProjectToolResultContent>(
                    Assert.Single(message.Contents)).CallId.StartsWith(
                        "read",
                        StringComparison.Ordinal)));
        Assert.Equal(
            2,
            restored.RunRequest.InitialMessages.Count(message =>
                StringComparer.Ordinal.Equals(message.Role, "tool") &&
                Assert.IsType<ProjectToolResultContent>(
                    Assert.Single(message.Contents)).CallId.StartsWith(
                        "finish",
                        StringComparison.Ordinal)));
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
            [.. Controls(trusted), User("review")]);
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
    public async Task CrossGenerationNonterminalPlacementRemainsExactAndClosed()
    {
        var trusted = Trusted();
        var previous = await BuildGenerationAsync(
            trusted,
            previous: null,
            "g0",
            "finish0",
            reasoning: true);
        Assert.True(
            AgentSessionRequestReconstruction.TryReconstructHistory(
                previous.Artifact.Document,
                SyntheticContinuationCodec.Instance,
                Controls(trusted).Length,
                out var history,
                out var continuation,
                out var failure),
            failure);
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, previous.Artifact.SessionSha256),
            "session0",
            [.. Controls(trusted), .. history!, User("g1")],
            continuation);
        const string readCallId = "read1";
        const string readArguments =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        var first = new ProjectContinuationItem(
            "readable-first",
            "opaque-first",
            "structured",
            readCallId,
            run.InitialMessages.Length,
            0);
        var second = new ProjectContinuationItem(
            "readable-second",
            "opaque-second",
            "structured",
            readCallId,
            run.InitialMessages.Length,
            1);
        var responses = new Queue<ProjectChatResponse>(
        [
            new ProjectChatResponse(
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
                            readCallId,
                            AgentToolRegistry.ReadFileName,
                            readArguments),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1,
                new ProjectContinuation(
                    "provider",
                    "model",
                    "adapter",
                    "session0",
                    [second, first])),
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "finish1",
                            AgentToolRegistry.FinishReviewName,
                            FinishJson),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(
                new Dictionary<string, AgentToolExecution>(
                    StringComparer.Ordinal)
                {
                    [readCallId] = ReadExecution("src/a.cs", 'a'),
                })).RunAsync(run, CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                run,
                outcome,
                trusted,
                run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                new AgentSessionPredecessor(
                    previous.Artifact.Plaintext,
                    previous.Artifact.SessionSha256,
                    previous.EnvelopeSha256,
                    previous.Artifact.Document.Generation,
                    previous.Artifact.Document.ProducerBaseSha,
                    previous.Artifact.Document.ProducerHeadSha,
                    previous.Artifact.Document.PredecessorStateSha256),
                AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(built.Artifact);
        Assert.Equal(
            [1, 0],
            artifact.Document.CompletedRuns[1].Continuation.Items
                .Select(item => item.ContentPosition));

        var restored = Restore(
            artifact,
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        var readItems = restored.RunRequest!.Continuation!.Items
            .Where(item => StringComparer.Ordinal.Equals(
                item.AssociatedCallId,
                readCallId))
            .ToArray();
        Assert.Equal(
            [1, 0],
            readItems.Select(item => item.ContentPosition));
        var readMessagePosition = Array.FindIndex(
            restored.RunRequest.InitialMessages,
            message => message.Contents
                .OfType<ProjectToolCallContent>()
                .Any(call => StringComparer.Ordinal.Equals(
                    call.CallId,
                    readCallId)));
        Assert.True(readMessagePosition >= 0);
        Assert.All(
            readItems,
            item => Assert.Equal(
                readMessagePosition,
                item.MessagePosition));

        var materialized = MinimalChatClient.Materialize(
            new ProjectChatRequest(
                restored.RunRequest.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                restored.RunRequest.Continuation,
                ThinkingRequired: true));
        var replayedAssistant = Assert.Single(
            materialized.Messages,
            message => message.Role == "assistant" &&
                message.Contents.Any(content =>
                    StringComparer.Ordinal.Equals(
                        content.CallId,
                        readCallId)));
        Assert.Equal(
            ["reasoning", "reasoning", "tool_call"],
            replayedAssistant.Contents.Select(content => content.Kind));
        Assert.Equal(
            ["readable-first", "readable-second"],
            replayedAssistant.Contents
                .OfType<MinimalChatContent>()
                .Where(content => content.Kind == "reasoning")
                .Select(content => content.Text));
        Assert.Equal(
            [0, 1, 2],
            replayedAssistant.Contents.Select(content => content.Position));

        var invalidPosition = restored.RunRequest.Continuation with
        {
            Items = restored.RunRequest.Continuation.Items
                .Select(item => ReferenceEquals(item, readItems[0])
                    ? item with
                    {
                        ContentPosition = AgentLimits.PartsPerMessage,
                    }
                    : item)
                .ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() =>
            MinimalChatClient.Materialize(new ProjectChatRequest(
                restored.RunRequest.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                invalidPosition,
                ThinkingRequired: true)));
        var invalidAssociation = restored.RunRequest.Continuation with
        {
            Items = restored.RunRequest.Continuation.Items
                .Select(item => ReferenceEquals(item, readItems[0])
                    ? item with { AssociatedCallId = "missing" }
                    : item)
                .ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() =>
            MinimalChatClient.Materialize(new ProjectChatRequest(
                restored.RunRequest.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                invalidAssociation,
                ThinkingRequired: true)));
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
                [.. Controls(trusted), User("review")]);
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
            new(
                "résumé",
                SyntheticContinuationCodec.StructuredOpaque(
                    "opaque-Ω",
                    "signature-Ω",
                    [
                        new("first", "雪\"\\\n"),
                        new("second", "value-2"),
                    ]),
                "structured"),
            new(string.Empty, string.Empty, "structured"),
        ];
        foreach (var fixture in fixtures)
        {
            var trusted = Trusted();
            var built = await BuildNonterminalContinuationGenerationAsync(
                trusted,
                fixture);
            var restored = Restore(
                built.Artifact,
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
    public void SyntheticStructuredCodecPreservesSignatureAndOrderedFields()
    {
        SyntheticField[] firstOrder =
        [
            new("first", "雪\"\\\n"),
            new("second", "value-2"),
        ];
        SyntheticField[] secondOrder =
        [
            firstOrder[1],
            firstOrder[0],
        ];
        var first = new AgentContinuationCodecValue(
            "résumé",
            SyntheticContinuationCodec.StructuredOpaque(
                "opaque-Ω",
                "signature-Ω",
                firstOrder),
            "structured");
        var second = first with
        {
            Opaque = SyntheticContinuationCodec.StructuredOpaque(
                "opaque-Ω",
                "signature-Ω",
                secondOrder),
        };

        Assert.True(SyntheticContinuationCodec.Instance.TryEncode(
            first,
            out var firstPayload));
        Assert.True(SyntheticContinuationCodec.Instance.TryEncode(
            second,
            out var secondPayload));
        Assert.False(firstPayload!.Bytes.SequenceEqual(secondPayload!.Bytes));
        Assert.True(SyntheticContinuationCodec.Instance.TryDecode(
            firstPayload.Encoding,
            firstPayload.Bytes,
            out var decoded));
        Assert.Equal(first, decoded);
        using (var document = JsonDocument.Parse(firstPayload.Bytes))
        {
            var root = document.RootElement;
            Assert.Equal(
                "signature-Ω",
                root.GetProperty("signature").GetString());
            Assert.Equal(
                ["first", "second"],
                root.GetProperty("fields")
                    .EnumerateArray()
                    .Select(field =>
                        field.GetProperty("name").GetString()));
        }

        var thirtyTwo = Enumerable.Range(0, 32)
            .Select(index => new SyntheticField(
                string.Concat("field-", index),
                string.Concat("value-", index)))
            .ToArray();
        var thirtyThree = thirtyTwo
            .Append(new SyntheticField("field-32", "value-32"))
            .ToArray();
        Assert.True(SyntheticContinuationCodec.Instance.TryEncode(
            first with
            {
                Opaque = SyntheticContinuationCodec.StructuredOpaque(
                    "opaque",
                    "signature",
                    thirtyTwo),
            },
            out _));
        Assert.False(SyntheticContinuationCodec.Instance.TryEncode(
            first with
            {
                Opaque = SyntheticContinuationCodec.StructuredOpaque(
                    "opaque",
                    "signature",
                    thirtyThree),
            },
            out _));
        Assert.True(SyntheticContinuationCodec.Instance.TryEncode(
            first with
            {
                Opaque = SyntheticContinuationCodec.StructuredOpaque(
                    "opaque",
                    "signature",
                    [new("field", new string('x', 8 * 1024))]),
            },
            out _));
        Assert.False(SyntheticContinuationCodec.Instance.TryEncode(
            first with
            {
                Opaque = SyntheticContinuationCodec.StructuredOpaque(
                    "opaque",
                    "signature",
                    [new("field", new string('x', 8 * 1024 + 1))]),
            },
            out _));
        Assert.False(SyntheticContinuationCodec.Instance.TryEncode(
            first with
            {
                Opaque = SyntheticContinuationCodec.StructuredOpaque(
                    "opaque",
                    "signature",
                    [new("duplicate", "a"), new("duplicate", "b")]),
            },
            out _));

        string[] malformed =
        [
            "{\"kind\":\"structured\",\"readable\":\"r\",\"framing\":\"structured\",\"opaque\":\"o\",\"signature\":\"s\",\"fields\":[]}",
            "{\"kind\":\"structured\",\"framing\":\"structured\",\"readable\":\"r\",\"opaque\":\"o\",\"signature\":\"s\",\"fields\":[],\"unknown\":0}",
            "{\"kind\":\"structured\",\"framing\":\"structured\",\"readable\":\"r\",\"opaque\":\"o\",\"signature\":\"s\",\"fields\":null}",
            "{\"kind\":\"structured\",\"framing\":\"structured\",\"readable\":\"r\",\"opaque\":\"o\",\"signature\":\"s\",\"fields\":[{\"value\":\"v\",\"name\":\"n\"}]}",
        ];
        foreach (var json in malformed)
        {
            Assert.False(SyntheticContinuationCodec.Instance.TryDecode(
                "utf8",
                Encoding.UTF8.GetBytes(json),
                out _));
        }

        Assert.False(SyntheticContinuationCodec.Instance.TryDecode(
            "base64",
            firstPayload.Bytes,
            out _));
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
            AgentSessionCodes.ConstructionLimit,
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
            AgentSessionCodes.ConstructionLimit,
            overAggregate.FailureCode);
        Assert.Null(overAggregate.Artifact);
    }

    [Fact]
    public async Task CumulativeSessionRecordCapIsExactAndNeverDropsHistory()
    {
        var trusted = Trusted();
        BuiltGeneration? predecessor = null;
        for (var index = 0; index < 19; index++)
        {
            predecessor = await BuildGenerationWithContinuationCountAsync(
                trusted,
                predecessor,
                string.Concat("predecessor-", index),
                string.Concat("finish", index),
                index < 6 ? 11 : 10);
        }

        var validOverPredecessor = Assert.IsType<BuiltGeneration>(
            predecessor);
        Assert.Equal(
            AgentLimits.SessionRecords - 3,
            validOverPredecessor.Artifact.Document.CompletedRuns.Sum(run =>
                run.Records.Length + run.Continuation.Items.Length));
        var predecessorBytes =
            validOverPredecessor.Artifact.Plaintext.ToArray();
        var appendAtBoundary = await AppendGenerationAsync(
            trusted,
            validOverPredecessor,
            "boundary-final",
            "finish19",
            continuationCount: 0);
        Assert.Equal(
            AgentSessionCodes.ConstructionLimit,
            appendAtBoundary.FailureCode);
        Assert.Null(appendAtBoundary.Artifact);
        Assert.Equal(
            predecessorBytes,
            validOverPredecessor.Artifact.Plaintext);

        var exact = AddTerminalContinuationItems(
            validOverPredecessor.Artifact.Document,
            count: 3);
        Assert.Equal(
            AgentLimits.SessionRecords,
            exact.CompletedRuns.Sum(run =>
                run.Records.Length + run.Continuation.Items.Length));
        Assert.True(
            AgentSessionValidation.TryValidateRecords(
                exact,
                SyntheticContinuationCodec.Instance,
                out var exactFailure),
            exactFailure);
        Assert.True(
            AgentSessionCodec.TryWrite(
                exact,
                out var exactArtifact,
                out var writeFailure),
            writeFailure);
        Assert.NotEmpty(exactArtifact!.Plaintext);

        var over = AddTerminalContinuationItems(
            validOverPredecessor.Artifact.Document,
            count: 4);
        Assert.Equal(
            AgentLimits.SessionRecords + 1,
            over.CompletedRuns.Sum(run =>
                run.Records.Length + run.Continuation.Items.Length));
        Assert.False(
            AgentSessionValidation.TryValidateRecords(
                over,
                SyntheticContinuationCodec.Instance,
                out var overFailure));
        Assert.Equal(AgentSessionCodes.RecordInvalid, overFailure);
    }

    [Fact]
    public async Task ConstructionRejectsHistoryWithoutNextMessageCapacity()
    {
        var trusted = Trusted();
        var generation0Run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("g0")]);
        var generation0Outcome = await CompleteToolRoundsAsync(
            generation0Run,
            [4, 4, 3, 3, 3, 3, 3],
            "finish0");
        var generation0 = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                generation0Run,
                generation0Outcome,
                trusted,
                generation0Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.True(generation0.Succeeded, generation0.FailureCode);
        var predecessorArtifact = Assert.IsType<AgentSessionArtifact>(
            generation0.Artifact);
        var predecessorBytes = predecessorArtifact.Plaintext.ToArray();
        var envelopeSha256 = new string('e', 64);
        var restored = Restore(
            new BuiltGeneration(predecessorArtifact, envelopeSha256),
            trusted,
            AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Equal(35, restored.RunRequest!.InitialMessages.Length);

        var generation1Outcome = await CompleteToolRoundsAsync(
            restored.RunRequest,
            [4, 4, 4, 4, 3, 2],
            "finish1");
        Assert.True(generation1Outcome.CompletedSessionEligible);
        var generation1 = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                restored.RunRequest,
                generation1Outcome,
                trusted,
                restored.RunRequest.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                new AgentSessionPredecessor(
                    predecessorArtifact.Plaintext,
                    predecessorArtifact.SessionSha256,
                    envelopeSha256,
                    predecessorArtifact.Document.Generation,
                    predecessorArtifact.Document.ProducerBaseSha,
                    predecessorArtifact.Document.ProducerHeadSha,
                    predecessorArtifact.Document.PredecessorStateSha256),
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.ConstructionLimit,
            generation1.FailureCode);
        Assert.Null(generation1.Artifact);
        Assert.Equal(predecessorBytes, predecessorArtifact.Plaintext);
    }

    [Fact]
    public async Task ConstructionReservesNextContextPartCapacity()
    {
        var trusted = Trusted();
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review")]);
        var responses = new Queue<ProjectChatResponse>();
        var executions = new Dictionary<string, AgentToolExecution>(
            StringComparer.Ordinal);
        int[] responseParts = [32, 32, 32, 32, 32, 32, 23];
        for (var responseIndex = 0;
            responseIndex < responseParts.Length;
            responseIndex++)
        {
            var callId = string.Concat("read", responseIndex);
            var contents = Enumerable.Range(
                    0,
                    responseParts[responseIndex] - 1)
                .Select(index => (ProjectChatContent)new ProjectTextContent(
                    string.Concat("p", responseIndex, "-", index)))
                .Append(new ProjectToolCallContent(
                    callId,
                    AgentToolRegistry.ReadFileName,
                    "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}"))
                .ToArray();
            responses.Enqueue(new ProjectChatResponse(
                new ProjectChatMessage("assistant", contents),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1));
            executions.Add(callId, ReadExecution("src/a.cs", 'a'));
        }

        responses.Enqueue(new ProjectChatResponse(
            new ProjectChatMessage(
                "assistant",
                Enumerable.Range(0, 31)
                    .Select(index =>
                        (ProjectChatContent)new ProjectTextContent(
                            string.Concat("terminal-", index)))
                    .Append(new ProjectToolCallContent(
                        "finish0",
                        AgentToolRegistry.FinishReviewName,
                        FinishJson))
                    .ToArray()),
            new ProjectChatUsage(1, 1),
            CapturedResponseBodyBytes: 1));
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
        Assert.Equal(
            AgentSessionCodes.ConstructionLimit,
            built.FailureCode);
        Assert.Null(built.Artifact);
    }

    [Fact]
    public async Task ConstructionReservesNextRequestByteCapacity()
    {
        var policy = new string('p', AgentLimits.ContentBytes);
        var trusted = Trusted() with
        {
            TrustedPolicyBytes = Encoding.UTF8.GetBytes(policy),
        };
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review")]);
        var largeTexts = Enumerable.Repeat(
                (ProjectChatContent)new ProjectTextContent(
                    new string('x', AgentLimits.ContentBytes)),
                7)
            .Append(new ProjectTextContent(new string('y', 32_500)))
            .ToArray();
        const string callId = "read0";
        const string readArguments =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        var nonterminal = new ProjectChatMessage(
            "assistant",
            [
                .. largeTexts,
                new ProjectToolCallContent(
                    callId,
                    AgentToolRegistry.ReadFileName,
                    readArguments),
            ]);
        var terminal = new ProjectChatMessage(
            "assistant",
            [
                .. largeTexts,
                new ProjectToolCallContent(
                    "finish0",
                    AgentToolRegistry.FinishReviewName,
                    FinishJson),
            ]);
        var execution = ReadExecution("src/a.cs", 'a');
        var outcome = await new AgentLoop(
            new QueueChatClient(new Queue<ProjectChatResponse>(
            [
                new ProjectChatResponse(
                    nonterminal,
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1),
                new ProjectChatResponse(
                    terminal,
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1),
            ])),
            new MappedToolExecutor(
                new Dictionary<string, AgentToolExecution>(
                    StringComparer.Ordinal)
                {
                    [callId] = execution,
                })).RunAsync(run, CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
        var resultMessage = new ProjectChatMessage(
            "tool",
            [
                new ProjectToolResultContent(
                    callId,
                    execution.ResultJson!),
            ]);
        var finalRequestBytes = AgentRequestWriter.Write(
            new ProjectChatRequest(
                [
                    .. run.InitialMessages,
                    nonterminal,
                    resultMessage,
                    terminal,
                    User("x"),
                ],
                AgentToolRegistry.Definitions.ToArray(),
                Continuation: null,
                ThinkingRequired: true));
        Assert.True(
            finalRequestBytes.Length > AgentLimits.RequestBytes,
            finalRequestBytes.Length.ToString(CultureInfo.InvariantCulture));

        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.ConstructionLimit,
            built.FailureCode);
        Assert.Null(built.Artifact);
    }

    [Fact]
    public async Task MalformedSuccessfulOutcomeBytesAndCodecFailuresAreContained()
    {
        var trusted = Trusted();
        var terminal = await CompleteAsync(
            trusted,
            priorSessionSha256: null,
            previous: null,
            currentText: "terminal containment",
            callId: "finish0",
            reasoning: false);
        var invalidCallOutcome = MutateCanonicalEventBytes(
            terminal.Outcome,
            mutateCall: true);
        var invalidCall = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                terminal.Run,
                invalidCallOutcome,
                trusted,
                terminal.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            invalidCall.FailureCode);
        Assert.Null(invalidCall.Artifact);

        var grounded = await CompleteOneReadAsync(trusted);
        var invalidResultOutcome = MutateCanonicalEventBytes(
            grounded.Outcome,
            mutateCall: false);
        var invalidResult = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                grounded.Run,
                invalidResultOutcome,
                trusted,
                grounded.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            invalidResult.FailureCode);
        Assert.Null(invalidResult.Artifact);

        var missingFindings = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                terminal.Run,
                MutateTerminalCanonicalBytes(
                    terminal.Outcome,
                    "{}"u8.ToArray()),
                trusted,
                terminal.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.Equal(
            AgentSessionCodes.RecordInvalid,
            missingFindings.FailureCode);
        Assert.Null(missingFindings.Artifact);

        var continuation = await CompleteAsync(
            trusted,
            priorSessionSha256: null,
            previous: null,
            currentText: "codec containment",
            callId: "finish0",
            reasoning: true);
        foreach (var codec in new IAgentContinuationCodec[]
                 {
                     InvalidUtf8ContinuationCodec.Instance,
                     ThrowingContinuationCodec.Instance,
                     RfcThrowingContinuationCodec.Instance,
                     KeyNotFoundThrowingContinuationCodec.Instance,
                  })
        {
            var result = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    continuation.Run,
                    continuation.Outcome,
                    trusted,
                    continuation.Run.InitialMessages.Length - 1,
                    codec,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.ContinuationInvalid,
                result.FailureCode);
            Assert.Null(result.Artifact);
        }

        var valid = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                continuation.Run,
                continuation.Outcome,
                trusted,
                continuation.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.True(valid.Succeeded, valid.FailureCode);
        var validArtifact = valid.Artifact!;
        foreach (var codec in new IAgentContinuationCodec[]
                 {
                     ThrowingContinuationCodec.Instance,
                     RfcThrowingContinuationCodec.Instance,
                     KeyNotFoundThrowingContinuationCodec.Instance,
                  })
        {
            var throwingRestore = AgentSessionRestorer.Restore(
                new AgentSessionRestoreInput(
                    AgentSessionLocatorFamily.Current,
                    AgentSessionRestoreIntent.Automatic,
                    ExplicitReset: false,
                    validArtifact.Plaintext,
                    new AgentSessionAcceptedState(
                        0,
                        validArtifact.SessionSha256,
                        new string('e', 64),
                        validArtifact.Document.ProducerBaseSha,
                        validArtifact.Document.ProducerHeadSha,
                        PredecessorStateSha256: null),
                    trusted,
                    "session0",
                    Identity(),
                    User("next"),
                    AgentSessionHeadTransition.SameHead,
                    codec));
            Assert.Equal(
                AgentSessionCodes.ContinuationInvalid,
                throwingRestore.Code);
            Assert.Null(throwingRestore.RunRequest);
        }

        foreach (var malformedPayload in new[]
                 {
                     "{}",
                     "{\"text\":\"missing kind\"}",
                     "{\"kind\":\"structured\",\"framing\":\"structured\",\"readable\":\"r\",\"opaque\":\"o\",\"signature\":\"s\"}",
                 })
        {
            var malformedCodec = new FixedPayloadContinuationCodec(
                malformedPayload);
            var malformedBuild = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    continuation.Run,
                    continuation.Outcome,
                    trusted,
                    continuation.Run.InitialMessages.Length - 1,
                    malformedCodec,
                    Predecessor: null,
                    AgentSessionHeadTransition.SameHead));
            Assert.Equal(
                AgentSessionCodes.ContinuationInvalid,
                malformedBuild.FailureCode);
            Assert.Null(malformedBuild.Artifact);

            var malformedArtifact = MutateContinuationPayload(
                validArtifact,
                malformedPayload);
            var malformedRestore = Restore(
                new BuiltGeneration(
                    malformedArtifact,
                    new string('e', 64)),
                trusted,
                AgentSessionHeadTransition.SameHead);
            Assert.Equal(
                AgentSessionCodes.ContinuationInvalid,
                malformedRestore.Code);
            Assert.Null(malformedRestore.RunRequest);
        }
    }

    [Fact]
    public void RepresentativeOldM4PayloadNeverReachesCurrentParser()
    {
        var oldPayload = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"ProviderSessionLedgerV1\",\"records\":[]}");
        foreach (var entry in new[]
                 {
                     (
                         Family: AgentSessionLocatorFamily.NonCurrent,
                         Intent: AgentSessionRestoreIntent.Automatic,
                         Expected: AgentSessionCodes.BootstrapIncompatible),
                     (
                         Family: AgentSessionLocatorFamily.NonCurrent,
                         Intent: AgentSessionRestoreIntent.Explicit,
                         Expected: AgentSessionCodes.ExplicitIncompatible),
                 })
        {
            var result = AgentSessionRestorer.Restore(
                new AgentSessionRestoreInput(
                    entry.Family,
                    entry.Intent,
                    ExplicitReset: false,
                    oldPayload,
                    AcceptedState: null,
                    Trusted(),
                    "session0",
                    Identity(),
                    User("next"),
                    AgentSessionHeadTransition.SameHead,
                    ThrowingContinuationCodec.Instance));
            Assert.Equal(entry.Expected, result.Code);
            Assert.Null(result.RunRequest);
        }

        var selectedCurrent = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Automatic,
                ExplicitReset: false,
                oldPayload,
                AcceptedState: null,
                Trusted(),
                "session0",
                Identity(),
                User("next"),
                AgentSessionHeadTransition.SameHead,
                ThrowingContinuationCodec.Instance));
        Assert.Equal(
            AgentSessionCodes.CurrentMalformed,
            selectedCurrent.Code);
    }

    [Fact]
    public async Task CorrectPriorHashCannotAuthorizeAlteredHistoryOrContinuation()
    {
        var trusted = Trusted();
        var previous = await BuildNonterminalContinuationGenerationAsync(
            trusted,
            new AgentContinuationCodecValue(
                "readable",
                "opaque",
                "structured"));
        Assert.True(AgentSessionRequestReconstruction.TryReconstructHistory(
            previous.Artifact.Document,
            SyntheticContinuationCodec.Instance,
            Controls(trusted).Length,
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
                .. Controls(trusted),
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
                .. Controls(trusted),
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
            [.. Controls(trusted), User("review")]);
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

    private static string ReplaceRootProperty(
        string json,
        string name,
        string oldValue,
        string newValue)
    {
        var oldProperty = string.Concat("\"", name, "\":", oldValue);
        var newProperty = string.Concat("\"", name, "\":", newValue);
        var mutated = json.Replace(
            oldProperty,
            newProperty,
            StringComparison.Ordinal);
        Assert.NotEqual(json, mutated);
        return mutated;
    }

    private static string RemoveRootProperty(
        string json,
        string name,
        string value)
    {
        var property = string.Concat("\"", name, "\":", value);
        var index = json.IndexOf(property, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var start = index > 0 && json[index - 1] == ','
            ? index - 1
            : index;
        var length = property.Length + (start == index ? 1 : 0);
        return json.Remove(start, length);
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

    private static AgentSessionArtifact MutateContinuationPayload(
        AgentSessionArtifact artifact,
        string payload)
    {
        var run = artifact.Document.CompletedRuns[0];
        var item = Assert.Single(run.Continuation.Items);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mutatedItem = item with
        {
            Encoding = "utf8",
            Payload = payload,
            PayloadBytes = payloadBytes,
            PayloadSha256 = AgentSessionCodec.ContinuationPayloadSha256(
                run.Continuation.CodecId,
                run.Continuation.CodecDiscriminator,
                item.ItemId,
                "utf8",
                payloadBytes),
        };
        return Rewrite(
            artifact.Document,
            run with
            {
                Continuation = run.Continuation with
                {
                    Items = [mutatedItem],
                },
            });
    }

    private static AgentSessionArtifact RawMutation(
        AgentSessionArtifact artifact,
        string oldValue,
        string newValue)
    {
        var json = Encoding.UTF8.GetString(
            artifact.Plaintext[AgentSessionFormat.FramingBytes..]);
        var mutated = json.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal);
        Assert.NotEqual(json, mutated);
        var plaintext = Frame(Encoding.UTF8.GetBytes(mutated));
        return new AgentSessionArtifact(
            plaintext,
            AgentCanonical.HashDomain(
                AgentCanonical.SessionDomain,
                plaintext),
            artifact.Document);
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
            [.. Controls(trusted), User("review")]);
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

    private static async Task<CompletedRun> CompleteOneReadAsync(
        AgentSessionTrustedRequest trusted,
        AgentToolExecution? execution = null)
    {
        const string readJson =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        var responses = new Queue<ProjectChatResponse>(
        [
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "read0",
                            AgentToolRegistry.ReadFileName,
                            readJson),
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
                            FinishJson),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review")]);
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(
                new Dictionary<string, AgentToolExecution>(
                    StringComparer.Ordinal)
                {
                    ["read0"] = execution ??
                         ReadExecution("src/a.cs", 'a'),
                })).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return new CompletedRun(run, outcome);
    }

    private static async Task<CompletedRun> CompleteOneSearchAsync(
        AgentSessionTrustedRequest trusted,
        AgentToolExecution execution)
    {
        const string searchJson = "{\"query\":\"line\"}";
        var responses = new Queue<ProjectChatResponse>(
        [
            new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectToolCallContent(
                            "search0",
                            AgentToolRegistry.SearchTextName,
                            searchJson),
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
                            FinishJson),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review-search")]);
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(
                new Dictionary<string, AgentToolExecution>(
                    StringComparer.Ordinal)
                {
                    ["search0"] = execution,
                })).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return new CompletedRun(run, outcome);
    }

    private static AgentRunOutcome MutateCanonicalEventBytes(
        AgentRunOutcome outcome,
        bool mutateCall)
    {
        var invalidBytes = ImmutableArray.Create((byte)0xff);
        var events = outcome.Events;
        if (mutateCall)
        {
            var eventIndex = Enumerable.Range(0, events.Length).First(
                index => events[index] is AgentToolCallEvent);
            var call = Assert.IsType<AgentToolCallEvent>(events[eventIndex]);
            var hash = StringComparer.Ordinal.Equals(
                call.Name,
                AgentToolRegistry.FinishReviewName)
                ? AgentCanonical.HashDomain(
                    AgentCanonical.TerminalDomain,
                    invalidBytes.AsSpan())
                : AgentCanonical.HashRaw(invalidBytes.AsSpan());
            events = events.SetItem(
                eventIndex,
                call with
                {
                    ArgumentsSha256 = hash,
                    CanonicalArguments = invalidBytes,
                });
            var messageIndex = Enumerable.Range(0, events.Length).First(
                index =>
                    events[index] is AgentMessageEvent message &&
                    message.Contents
                        .OfType<AgentToolCallReferencePart>()
                        .Any(reference =>
                            StringComparer.Ordinal.Equals(
                                reference.CallId,
                                call.CallId)));
            var message = Assert.IsType<AgentMessageEvent>(
                events[messageIndex]);
            events = events.SetItem(
                messageIndex,
                message with
                {
                    Contents = message.Contents
                        .Select(part =>
                            part is AgentToolCallReferencePart reference &&
                            StringComparer.Ordinal.Equals(
                                reference.CallId,
                                call.CallId)
                                ? reference with
                                {
                                    ArgumentsSha256 = hash,
                                }
                                : part)
                        .ToImmutableArray(),
                });
        }
        else
        {
            var eventIndex = Enumerable.Range(0, events.Length).First(
                index => events[index] is AgentToolResultEvent);
            var result = Assert.IsType<AgentToolResultEvent>(
                events[eventIndex]);
            events = events.SetItem(
                eventIndex,
                result with
                {
                    ResultSha256 = AgentCanonical.HashRaw(
                        invalidBytes.AsSpan()),
                    CanonicalResult = invalidBytes,
                });
        }

        return outcome with { Events = events };
    }

    private static AgentRunOutcome MutateTerminalCanonicalBytes(
        AgentRunOutcome outcome,
        byte[] canonicalBytes)
    {
        var immutableBytes = canonicalBytes.ToImmutableArray();
        var terminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            canonicalBytes);
        var events = outcome.Events;
        var callIndex = Enumerable.Range(0, events.Length).First(index =>
            events[index] is AgentToolCallEvent call &&
            StringComparer.Ordinal.Equals(
                call.Name,
                AgentToolRegistry.FinishReviewName));
        var call = Assert.IsType<AgentToolCallEvent>(events[callIndex]);
        events = events.SetItem(
            callIndex,
            call with
            {
                ArgumentsSha256 = terminalSha256,
                CanonicalArguments = immutableBytes,
            });
        var messageIndex = Enumerable.Range(0, events.Length).First(index =>
            events[index] is AgentMessageEvent message &&
            message.Contents
                .OfType<AgentToolCallReferencePart>()
                .Any(reference => StringComparer.Ordinal.Equals(
                    reference.CallId,
                    call.CallId)));
        var message = Assert.IsType<AgentMessageEvent>(events[messageIndex]);
        events = events.SetItem(
            messageIndex,
            message with
            {
                Contents = message.Contents.Select(part =>
                    part is AgentToolCallReferencePart reference &&
                    StringComparer.Ordinal.Equals(
                        reference.CallId,
                        call.CallId)
                        ? reference with
                        {
                            ArgumentsSha256 = terminalSha256,
                        }
                        : part).ToImmutableArray(),
            });
        var terminalIndex = Enumerable.Range(0, events.Length).First(index =>
            events[index] is AgentTerminalEvent);
        events = events.SetItem(
            terminalIndex,
            new AgentTerminalEvent(terminalSha256));
        return outcome with
        {
            Review = outcome.Review! with
            {
                TerminalSha256 = terminalSha256,
                CanonicalBytes = canonicalBytes,
            },
            Events = events,
        };
    }

    private static async Task<BuiltGeneration> BuildGroundedGenerationAsync(
        AgentSessionTrustedRequest trusted,
        BuiltGeneration? previous = null)
    {
        var identity = Identity();
        var generation = previous?.Artifact.Document.Generation + 1 ?? 0;
        var readCallId = string.Concat("read", generation);
        var finishCallId = string.Concat("finish", generation);
        ProjectChatMessage[] history = [];
        ProjectContinuation? continuation = null;
        AgentSessionPredecessor? predecessor = null;
        string? prior = null;
        if (previous is not null)
        {
            Assert.True(
                AgentSessionRequestReconstruction.TryReconstructHistory(
                    previous.Artifact.Document,
                    SyntheticContinuationCodec.Instance,
                    Controls(trusted).Length,
                    out var reconstructed,
                    out continuation,
                    out var reconstructionFailure),
                reconstructionFailure);
            history = reconstructed!;
            prior = previous.Artifact.SessionSha256;
            predecessor = new AgentSessionPredecessor(
                previous.Artifact.Plaintext,
                previous.Artifact.SessionSha256,
                previous.EnvelopeSha256,
                previous.Artifact.Document.Generation,
                previous.Artifact.Document.ProducerBaseSha,
                previous.Artifact.Document.ProducerHeadSha,
                previous.Artifact.Document.PredecessorStateSha256);
        }

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
                            readCallId,
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
                            finishCallId,
                            AgentToolRegistry.FinishReviewName,
                            Encoding.UTF8.GetString(terminalBytes)),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1),
        ]);
        var run = new AgentRunRequest(
            identity,
            Materialize(trusted, prior),
            "session0",
            [
                .. Controls(trusted),
                .. history,
                User(string.Concat("review-", generation)),
            ],
            continuation);
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new FixedToolExecutor(new AgentToolExecution(
                true,
                FailureCode: null,
                Encoding.UTF8.GetString(resultBytes),
                resultBytes,
                observation))).RunAsync(run, CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            trusted,
            run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            predecessor,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        return new BuiltGeneration(
            built.Artifact!,
            new string(generation == 0 ? 'e' : 'f', 64));
    }

    private static async Task<BuiltGeneration>
        BuildGroundedSearchGenerationAsync(
            AgentSessionTrustedRequest trusted)
    {
        const string searchJson = "{\"query\":\"line\"}";
        Assert.True(AgentToolArguments.TrySearchText(
            searchJson,
            out var searchArguments));
        var root = Directory.CreateTempSubdirectory(
            "apr89-session-search-");
        try
        {
            var sourceDirectory = Directory.CreateDirectory(
                Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory.FullName, "a.cs"),
                "line a\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory.FullName, "b.cs"),
                "line b\n",
                new UTF8Encoding(false));
            var executor = new SnapshotToolExecutor(
                new ReviewedSnapshot(
                    Identity(),
                    root.FullName,
                    ["src/a.cs", "src/b.cs"]),
                new VerifiedReviewedFileAccess());
            var prepared = new PreparedSearchTextCall(
                "search0",
                searchArguments!);
            Assert.Null(executor.Preflight(prepared));
            var preview = await executor.ExecuteAsync(
                prepared,
                CancellationToken.None);
            Assert.True(preview.Succeeded, preview.FailureCode);
            using var previewDocument = JsonDocument.Parse(
                preview.CanonicalResult!);
            var previewMatches = previewDocument.RootElement
                .GetProperty("matches");
            Assert.Equal(2, previewMatches.GetArrayLength());
            Assert.Equal(
                "src/a.cs",
                previewMatches[0].GetProperty("path").GetString());
            Assert.Equal(
                "src/b.cs",
                previewMatches[1].GetProperty("path").GetString());

            var observationId = preview.Observation!.ObservationId;
            var terminalBytes = AgentToolArguments.WriteFinishReview(
                "complete",
                [
                    new AgentFinding(
                        "high",
                        "grounded",
                        "grounded message",
                        [
                            new AgentEvidence(
                                observationId,
                                "src/a.cs",
                                1,
                                1),
                        ]),
                ]);
            var responses = new Queue<ProjectChatResponse>(
            [
                new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectToolCallContent(
                                "search0",
                                AgentToolRegistry.SearchTextName,
                                searchJson),
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
                Identity(),
                Materialize(trusted, prior: null),
                "session0",
                [.. Controls(trusted), User("review-search")]);
            var outcome = await new AgentLoop(
                new QueueChatClient(responses),
                executor).RunAsync(run, CancellationToken.None);
            Assert.True(
                outcome.CompletedSessionEligible,
                outcome.Diagnostic?.Code);
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
            return new BuiltGeneration(
                built.Artifact!,
                new string('e', 64));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<BuiltGeneration>
        BuildGroundedR3GenerationAsync(
            AgentSessionTrustedRequest trusted)
    {
        var completed = await CompleteGroundedR3Async(trusted);
        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                completed.Run,
                completed.Outcome,
                trusted,
                completed.Run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        return new BuiltGeneration(
            built.Artifact!,
            new string('e', 64));
    }

    private static async Task<CompletedRun> CompleteGroundedR3Async(
        AgentSessionTrustedRequest trusted)
    {
        var root = Directory.CreateTempSubdirectory(
            "apr102-session-r3-");
        try
        {
            var identity = Identity();
            var firstHunk = new ReviewedDiffHunk(
                oldStart: 11,
                oldCount: 1,
                newStart: 10,
                newCount: 1,
                [
                    new ReviewedDiffLine(
                        "deletion",
                        OldLine: 11,
                        NewLine: null,
                        "deleted line"),
                    new ReviewedDiffLine(
                        "addition",
                        OldLine: null,
                        NewLine: 10,
                        "line 10"),
                ]);
            var secondHunk = new ReviewedDiffHunk(
                oldStart: 12,
                oldCount: 0,
                newStart: 12,
                newCount: 1,
                [
                    new ReviewedDiffLine(
                        "addition",
                        OldLine: null,
                        NewLine: 12,
                        "line 12"),
                ]);
            var source = new ReviewedDiffSource(
                identity,
                "src/a.cs",
                previousPath: null,
                "modified",
                sourceTruncated: false,
                [firstHunk, secondHunk]);
            var change = new ReviewedChangedFile(
                "src/a.cs",
                PreviousPath: null,
                "modified",
                Additions: 2,
                Deletions: 1,
                Changes: 3,
                "available",
                source.PatchSha256,
                SourceTruncated: false);
            var executor = new SnapshotToolExecutor(
                new ReviewedSnapshot(
                    identity,
                    root.FullName,
                    ["src/a.cs"],
                    [change],
                    [source]),
                new VerifiedReviewedFileAccess());
            Assert.True(AgentToolArguments.TryReadDiff(
                "{\"path\":\"src/a.cs\"}",
                out var diffArguments));
            var preview = await executor.ExecuteAsync(
                new PreparedReadDiffCall("diff0", diffArguments!),
                CancellationToken.None);
            Assert.True(preview.Succeeded, preview.FailureCode);
            var terminalBytes = AgentToolArguments.WriteFinishReview(
                "complete",
                [
                    new AgentFinding(
                        "high",
                        "grounded",
                        "grounded message",
                        [
                            new AgentEvidence(
                                preview.Observation!.ObservationId,
                                "src/a.cs",
                                10,
                                10),
                        ]),
                ]);
            var responses = new Queue<ProjectChatResponse>(
            [
                new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectToolCallContent(
                                "list0",
                                AgentToolRegistry.ListFilesName,
                                "{}"),
                            new ProjectToolCallContent(
                                "changed0",
                                AgentToolRegistry.ListChangedFilesName,
                                "{}"),
                            new ProjectToolCallContent(
                                "diff0",
                                AgentToolRegistry.ReadDiffName,
                                "{\"path\":\"src/a.cs\"}"),
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
                [.. Controls(trusted), User("review-r3")]);
            var outcome = await new AgentLoop(
                new QueueChatClient(responses),
                executor).RunAsync(run, CancellationToken.None);
            Assert.True(
                outcome.CompletedSessionEligible,
                outcome.Diagnostic?.Code);
            return new CompletedRun(run, outcome);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static ListFilesResult Sign(ListFilesResult result)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ListFilesObservationDomain,
            ListFilesResultWriter.Write(
                result,
                includeObservationId: false));
        return result with { ObservationId = observationId };
    }

    private static ListChangedFilesResult Sign(
        ListChangedFilesResult result)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ListChangedFilesObservationDomain,
            ListChangedFilesResultWriter.Write(
                result,
                includeObservationId: false));
        return result with { ObservationId = observationId };
    }

    private static ReadDiffResult Sign(ReadDiffResult result)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            ReadDiffResultWriter.Write(
                result,
                includeObservationId: false));
        return result with { ObservationId = observationId };
    }

    private static bool TryAdmitStored(
        AgentSessionToolCallContent call,
        ListFilesResult result) =>
        TryAdmitStored(
            call,
            result.ObservationId!,
            ListFilesResultWriter.Write(result));

    private static bool TryAdmitStored(
        AgentSessionToolCallContent call,
        ListChangedFilesResult result) =>
        TryAdmitStored(
            call,
            result.ObservationId!,
            ListChangedFilesResultWriter.Write(result));

    private static bool TryAdmitStored(
        AgentSessionToolCallContent call,
        ReadDiffResult result) =>
        TryAdmitStored(
            call,
            result.ObservationId!,
            ReadDiffResultWriter.Write(result));

    private static bool TryAdmitStored(
        AgentSessionToolCallContent call,
        string observationId,
        byte[] canonical)
    {
        var result = new AgentSessionToolResultRecord(
            "result0",
            Sequence: 0,
            "message0",
            call.CallId,
            call.Name,
            observationId,
            Encoding.UTF8.GetString(canonical),
            "tool",
            "tool_result",
            "untrusted_tool_data");
        return AgentSessionToolObservationAdmission.TryAdmit(
            call,
            result,
            Identity(),
            canonical,
            out _);
    }

    private static ReviewedDiffHunk ContextHunk(int line) => new(
        line,
        1,
        line,
        1,
        [new ReviewedDiffLine("context", line, line, "line")]);

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

    private static AgentToolExecution ReadExecution(
        ReadFileResult withoutId)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(
                withoutId,
                includeObservationId: false));
        var result = withoutId with { ObservationId = observationId };
        var bytes = ReadFileResultWriter.Write(result);
        var returned = result.Lines.Length == 0
            ? ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
            : ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(
                    result.Path,
                    result.Lines.Select(line => line.Line)
                        .ToImmutableHashSet());
        return new AgentToolExecution(
            true,
            FailureCode: null,
            Encoding.UTF8.GetString(bytes),
            bytes,
            new AgentObservation(
                observationId,
                result.ReviewedIdentity,
                returned));
    }

    private static AgentToolExecution SearchExecution(
        SearchTextResult withoutId)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.SearchObservationDomain,
            SearchTextResultWriter.Write(
                withoutId,
                includeObservationId: false));
        var result = withoutId with { ObservationId = observationId };
        var bytes = SearchTextResultWriter.Write(result);
        var returned = result.Matches
            .GroupBy(match => match.Path, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Select(match => match.Line)
                    .ToImmutableHashSet(),
                StringComparer.Ordinal);
        return new AgentToolExecution(
            true,
            FailureCode: null,
            Encoding.UTF8.GetString(bytes),
            bytes,
            new AgentObservation(
                observationId,
                result.ReviewedIdentity,
                returned));
    }

    private static async Task<AgentRunOutcome> CompleteToolRoundsAsync(
        AgentRunRequest run,
        IReadOnlyList<int> callsPerResponse,
        string finishCallId)
    {
        var responses = new Queue<ProjectChatResponse>();
        var executions = new Dictionary<string, AgentToolExecution>(
            StringComparer.Ordinal);
        var callOrdinal = 0;
        foreach (var callCount in callsPerResponse)
        {
            var contents = new ProjectChatContent[callCount];
            for (var index = 0; index < callCount; index++)
            {
                var callId = string.Concat(
                    finishCallId,
                    "_read",
                    callOrdinal);
                contents[index] = new ProjectToolCallContent(
                    callId,
                    AgentToolRegistry.ReadFileName,
                    "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}");
                executions.Add(callId, ReadExecution("src/a.cs", 'a'));
                callOrdinal++;
            }

            responses.Enqueue(new ProjectChatResponse(
                new ProjectChatMessage("assistant", contents),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1));
        }

        responses.Enqueue(new ProjectChatResponse(
            new ProjectChatMessage(
                "assistant",
                [
                    new ProjectToolCallContent(
                        finishCallId,
                        AgentToolRegistry.FinishReviewName,
                        FinishJson),
                ]),
            new ProjectChatUsage(1, 1),
            CapturedResponseBodyBytes: 1));
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(executions)).RunAsync(
                run,
                CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
        return outcome;
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

    private static AgentSessionArtifact MutateGroundedResult(
        AgentSessionArtifact artifact,
        string resultJson,
        string observationId,
        string evidencePath,
        int evidenceLine)
    {
        var run = artifact.Document.CompletedRuns[0];
        var resultIndex = Enumerable.Range(0, run.Records.Length).First(
            index => run.Records[index] is AgentSessionToolResultRecord);
        var storedResult = Assert.IsType<AgentSessionToolResultRecord>(
            run.Records[resultIndex]);
        var terminalMessageIndex = Enumerable.Range(
                0,
                run.Records.Length)
            .First(index =>
                run.Records[index] is AgentSessionAssistantMessageRecord
                    message &&
                message.Contents.Any(content =>
                    content is AgentSessionTerminalCallContent));
        var terminalMessage = Assert.IsType<
            AgentSessionAssistantMessageRecord>(
                run.Records[terminalMessageIndex]);
        var terminal = Assert.Single(
            terminalMessage.Contents.OfType<
                AgentSessionTerminalCallContent>());
        Assert.True(AgentToolArguments.TryFinishReview(
            terminal.ArgumentsJson,
            out var original));
        var originalFinding = Assert.Single(original!.Findings);
        var terminalBytes = AgentToolArguments.WriteFinishReview(
            original.Summary,
            [
                originalFinding with
                {
                    Evidence =
                    [
                        new AgentEvidence(
                            observationId,
                            evidencePath,
                            evidenceLine,
                            evidenceLine),
                    ],
                },
            ]);
        var terminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            terminalBytes);
        using var terminalDocument = JsonDocument.Parse(terminalBytes);
        var findingsJson = terminalDocument.RootElement
            .GetProperty("findings")
            .GetRawText();
        var terminalContentIndex =
            terminalMessage.Contents.IndexOf(terminal);
        var outcomeIndex = Enumerable.Range(0, run.Records.Length).First(
            index => run.Records[index] is
                AgentSessionReviewOutcomeRecord);
        var outcome = Assert.IsType<AgentSessionReviewOutcomeRecord>(
            run.Records[outcomeIndex]);
        var records = run.Records
            .SetItem(
                resultIndex,
                storedResult with
                {
                    ObservationId = observationId,
                    ResultJson = resultJson,
                })
            .SetItem(
                terminalMessageIndex,
                terminalMessage with
                {
                    Contents = terminalMessage.Contents.SetItem(
                        terminalContentIndex,
                        terminal with
                        {
                            ArgumentsJson =
                                Encoding.UTF8.GetString(terminalBytes),
                            ArgumentsSha256 = terminalSha256,
                        }),
                })
            .SetItem(
                outcomeIndex,
                outcome with
                {
                    TerminalSha256 = terminalSha256,
                    FindingsJson = findingsJson,
                });
        return Rewrite(
            artifact.Document,
            run with { Records = records });
    }

    private static AgentSessionArtifact MutateToolCallArguments(
        AgentSessionArtifact artifact,
        string argumentsJson,
        string? callId = null)
    {
        var run = artifact.Document.CompletedRuns[0];
        var messageIndex = Enumerable.Range(0, run.Records.Length).First(
            index => run.Records[index] is
                AgentSessionAssistantMessageRecord message &&
                message.Contents.Any(content =>
                    content is AgentSessionToolCallContent));
        var message = Assert.IsType<AgentSessionAssistantMessageRecord>(
            run.Records[messageIndex]);
        var toolCalls = message.Contents
            .OfType<AgentSessionToolCallContent>();
        var call = callId is null
            ? Assert.Single(toolCalls)
            : Assert.Single(toolCalls, candidate =>
                StringComparer.Ordinal.Equals(candidate.CallId, callId));
        var callIndex = message.Contents.IndexOf(call);
        var mutatedCall = call with
        {
            ArgumentsJson = argumentsJson,
        };
        var records = run.Records.SetItem(
            messageIndex,
            message with
            {
                Contents = message.Contents.SetItem(
                    callIndex,
                    mutatedCall),
            });
        return Rewrite(
            artifact.Document,
            run with { Records = records });
    }

    private static AgentSessionArtifact MutateToolName(
        AgentSessionArtifact artifact,
        string callId,
        string name)
    {
        var run = artifact.Document.CompletedRuns[0];
        var messageIndex = Enumerable.Range(0, run.Records.Length).Single(
            index => run.Records[index] is
                AgentSessionAssistantMessageRecord message &&
                message.Contents
                    .OfType<AgentSessionToolCallContent>()
                    .Any(call => StringComparer.Ordinal.Equals(
                        call.CallId,
                        callId)));
        var message = Assert.IsType<AgentSessionAssistantMessageRecord>(
            run.Records[messageIndex]);
        var call = Assert.Single(
            message.Contents.OfType<AgentSessionToolCallContent>(),
            candidate => StringComparer.Ordinal.Equals(
                candidate.CallId,
                callId));
        var callIndex = message.Contents.IndexOf(call);
        var resultIndex = Enumerable.Range(0, run.Records.Length).Single(
            index => run.Records[index] is AgentSessionToolResultRecord
                result &&
                StringComparer.Ordinal.Equals(result.CallId, callId));
        var result = Assert.IsType<AgentSessionToolResultRecord>(
            run.Records[resultIndex]);
        var records = run.Records
            .SetItem(
                messageIndex,
                message with
                {
                    Contents = message.Contents.SetItem(
                        callIndex,
                        call with { Name = name }),
                })
            .SetItem(
                resultIndex,
                result with { Name = name });
        return Rewrite(
            artifact.Document,
            run with { Records = records });
    }

    private static AgentSessionArtifact MutateTerminalEvidence(
        AgentSessionArtifact artifact,
        int runOrdinal,
        AgentEvidence evidence)
    {
        var run = artifact.Document.CompletedRuns[runOrdinal];
        var terminalMessageIndex = Enumerable.Range(0, run.Records.Length)
            .Single(index =>
                run.Records[index] is AgentSessionAssistantMessageRecord
                    message &&
                message.Contents.Any(content =>
                    content is AgentSessionTerminalCallContent));
        var terminalMessage = Assert.IsType<
            AgentSessionAssistantMessageRecord>(
                run.Records[terminalMessageIndex]);
        var terminal = Assert.Single(
            terminalMessage.Contents.OfType<
                AgentSessionTerminalCallContent>());
        Assert.True(AgentToolArguments.TryFinishReview(
            terminal.ArgumentsJson,
            out var original));
        var terminalBytes = AgentToolArguments.WriteFinishReview(
            original!.Summary,
            [
                new AgentFinding(
                    "high",
                    "grounded",
                    "grounded message",
                    [evidence]),
            ]);
        var terminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            terminalBytes);
        using var terminalDocument = JsonDocument.Parse(terminalBytes);
        var findingsJson = terminalDocument.RootElement
            .GetProperty("findings")
            .GetRawText();
        var terminalContentIndex = terminalMessage.Contents.IndexOf(terminal);
        var outcomeIndex = Enumerable.Range(0, run.Records.Length)
            .Single(index =>
                run.Records[index] is AgentSessionReviewOutcomeRecord);
        var outcome = Assert.IsType<AgentSessionReviewOutcomeRecord>(
            run.Records[outcomeIndex]);
        var records = run.Records
            .SetItem(
                terminalMessageIndex,
                terminalMessage with
                {
                    Contents = terminalMessage.Contents.SetItem(
                        terminalContentIndex,
                        terminal with
                        {
                            ArgumentsJson =
                                Encoding.UTF8.GetString(terminalBytes),
                            ArgumentsSha256 = terminalSha256,
                        }),
                })
            .SetItem(
                outcomeIndex,
                outcome with
                {
                    TerminalSha256 = terminalSha256,
                    Summary = original.Summary,
                    FindingsJson = findingsJson,
                });
        return Rewrite(
            artifact.Document,
            run with { Records = records });
    }

    private static AgentLoop TwoReasoningLoop() =>
        new(
            new OneResponseChatClient(request =>
            {
                var messagePosition = request.Messages.Length;
                var first = new ProjectContinuationItem(
                    "readable-0",
                    SyntheticContinuationCodec.StructuredOpaque(
                        "opaque-0",
                        "signature-0",
                        [new("first", "1"), new("second", "2")]),
                    "structured",
                    "finish0",
                    messagePosition,
                    0);
                var second = new ProjectContinuationItem(
                    "readable-1",
                    SyntheticContinuationCodec.StructuredOpaque(
                        "opaque-1",
                        "signature-1",
                        [new("second", "2"), new("first", "1")]),
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

    private static async Task<BuiltGeneration>
        BuildNonterminalContinuationGenerationAsync(
            AgentSessionTrustedRequest trusted,
            AgentContinuationCodecValue value)
    {
        const string callId = "read0";
        const string readArguments =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":1}";
        var run = new AgentRunRequest(
            Identity(),
            Materialize(trusted, prior: null),
            "session0",
            [.. Controls(trusted), User("review")]);
        var item = new ProjectContinuationItem(
            value.Readable,
            value.Opaque,
            value.Framing,
            callId,
            run.InitialMessages.Length,
            0);
        var responses = new Queue<ProjectChatResponse>(
        [
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
                            callId,
                            AgentToolRegistry.ReadFileName,
                            readArguments),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1,
                new ProjectContinuation(
                    "provider",
                    "model",
                    "adapter",
                    "session0",
                    [item])),
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
        var outcome = await new AgentLoop(
            new QueueChatClient(responses),
            new MappedToolExecutor(
                new Dictionary<string, AgentToolExecution>(
                    StringComparer.Ordinal)
                {
                    [callId] = ReadExecution("src/a.cs", 'a'),
                })).RunAsync(run, CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
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
        return new BuiltGeneration(
            built.Artifact!,
            new string('e', 64));
    }

    private static AgentSessionDocument AddTerminalContinuationItems(
        AgentSessionDocument document,
        int count)
    {
        var runIndex = document.CompletedRuns.Length - 1;
        var run = document.CompletedRuns[runIndex];
        var terminalMessage = Assert.Single(
            run.Records.OfType<AgentSessionAssistantMessageRecord>(),
            message => message.Contents.Any(content =>
                content is AgentSessionTerminalCallContent));
        var terminal = Assert.IsType<AgentSessionTerminalCallContent>(
            terminalMessage.Contents[^1]);
        Assert.Equal(
            terminalMessage.Contents.Length - 1,
            terminal.ContentPosition);
        var template = run.Continuation.Items[0];
        var newItems =
            ImmutableArray.CreateBuilder<AgentSessionContinuationItem>(count);
        var newSlots =
            ImmutableArray.CreateBuilder<AgentSessionAssistantContent>(count);
        for (var index = 0; index < count; index++)
        {
            var itemId = string.Concat("limit-boundary-", index);
            var contentPosition = terminal.ContentPosition + index;
            var payloadBytes = template.PayloadBytes.ToArray();
            newItems.Add(template with
            {
                ItemId = itemId,
                PayloadBytes = payloadBytes,
                PayloadSha256 =
                    AgentSessionCodec.ContinuationPayloadSha256(
                        run.Continuation.CodecId,
                        run.Continuation.CodecDiscriminator,
                        itemId,
                        template.Encoding,
                        payloadBytes),
                MessageId = terminalMessage.Id,
                ContentPosition = contentPosition,
                AssociatedCallId = terminal.CallId,
            });
            newSlots.Add(new AgentSessionContinuationSlotContent(
                contentPosition,
                itemId));
        }

        var updatedMessage = terminalMessage with
        {
            Contents =
            [
                .. terminalMessage.Contents.Take(terminal.ContentPosition),
                .. newSlots,
                terminal with
                {
                    ContentPosition = terminal.ContentPosition + count,
                },
            ],
        };
        var updatedRun = run with
        {
            Records = run.Records
                .Select(record =>
                    StringComparer.Ordinal.Equals(
                        record.Id,
                        terminalMessage.Id)
                            ? (AgentSessionRecord)updatedMessage
                            : record)
                .ToImmutableArray(),
            Continuation = run.Continuation with
            {
                Items = [.. run.Continuation.Items, .. newItems],
            },
        };
        return document with
        {
            CompletedRuns = document.CompletedRuns.SetItem(
                runIndex,
                updatedRun),
        };
    }

    private static async Task<BuiltGeneration> BuildGenerationAsync(
        AgentSessionTrustedRequest trusted,
        BuiltGeneration? previous,
        string currentText,
        string callId,
        bool reasoning) =>
        await BuildGenerationWithContinuationCountAsync(
            trusted,
            previous,
            currentText,
            callId,
            reasoning ? 1 : 0);

    private static async Task<BuiltGeneration>
        BuildGenerationWithContinuationCountAsync(
            AgentSessionTrustedRequest trusted,
            BuiltGeneration? previous,
            string currentText,
            string callId,
            int continuationCount)
    {
        var built = await AppendGenerationAsync(
            trusted,
            previous,
            currentText,
            callId,
            continuationCount);
        Assert.True(built.Succeeded, built.FailureCode);
        return new BuiltGeneration(
            built.Artifact!,
            new string(
                "abcdef"[
                    (int)(built.Artifact!.Document.Generation % 6)],
                64));
    }

    private static async Task<AgentSessionBuildResult> AppendGenerationAsync(
        AgentSessionTrustedRequest trusted,
        BuiltGeneration? previous,
        string currentText,
        string callId,
        int continuationCount)
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
                    Controls(trusted).Length,
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

        var completed = await CompleteWithContinuationCountAsync(
            trusted,
            prior,
            (history ?? [], continuation),
            currentText,
            callId,
            continuationCount);
        return AgentSessionBuilder.Build(new AgentSessionBuildInput(
            completed.Run,
            completed.Outcome,
            trusted,
            completed.Run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            predecessor,
            AgentSessionHeadTransition.SameHead));
    }

    private static Task<CompletedRun> CompleteAsync(
        AgentSessionTrustedRequest trusted,
        string? priorSessionSha256,
        (ProjectChatMessage[] History, ProjectContinuation? Continuation)?
            previous,
        string currentText,
        string callId,
        bool reasoning) =>
        CompleteWithContinuationCountAsync(
            trusted,
            priorSessionSha256,
            previous,
            currentText,
            callId,
            reasoning ? 1 : 0);

    private static async Task<CompletedRun>
        CompleteWithContinuationCountAsync(
            AgentSessionTrustedRequest trusted,
            string? priorSessionSha256,
            (ProjectChatMessage[] History, ProjectContinuation? Continuation)?
                previous,
            string currentText,
            string callId,
            int continuationCount)
    {
        var plan = Materialize(trusted, priorSessionSha256);
        var run = new AgentRunRequest(
            Identity(),
            plan,
            "session0",
            [
                .. Controls(trusted),
                .. previous?.History ?? [],
                User(currentText),
            ],
            previous?.Continuation);
        var outcome = await LoopWithContinuationCount(
            callId,
            continuationCount).RunAsync(
            run,
            CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return new CompletedRun(run, outcome);
    }

    private static AgentLoop Loop(string callId, bool reasoning) =>
        LoopWithContinuationCount(callId, reasoning ? 1 : 0);

    private static AgentLoop LoopWithContinuationCount(
        string callId,
        int continuationCount) =>
        new(
            new OneResponseChatClient(request =>
            {
                var contents = new List<ProjectChatContent>();
                ProjectContinuation? continuation = null;
                if (continuationCount > 0)
                {
                    var items = Enumerable.Range(0, continuationCount)
                        .Select(index => new ProjectContinuationItem(
                            continuationCount == 1
                                ? string.Concat("readable-", callId[^1])
                                : string.Concat(
                                    "readable-",
                                    callId[^1],
                                    "-",
                                    index),
                            continuationCount == 1
                                ? string.Concat("opaque-", callId[^1])
                                : string.Concat(
                                    "opaque-",
                                    callId[^1],
                                    "-",
                                    index),
                            "structured",
                            callId,
                            request.Messages.Length,
                            index))
                        .ToArray();
                    contents.AddRange(items.Select(item =>
                        (ProjectChatContent)new ProjectReasoningContent(
                            item.Readable,
                            item.Opaque,
                            item.Framing,
                            item.AssociatedCallId,
                            item.MessagePosition,
                            item.ContentPosition)));
                    continuation = new ProjectContinuation(
                        "provider",
                        "model",
                        "adapter",
                        "session0",
                        items);
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

    private static ProjectChatMessage[] Controls(
        AgentSessionTrustedRequest trusted)
    {
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        return materialized!.ControlMessages;
    }

    private static AgentSessionTrustedRequest Trusted() =>
        new(
            "repo",
            1,
            "workflow@trusted-sha",
            "trusted policy A"u8.ToArray(),
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

    private sealed class InvalidUtf8ContinuationCodec
        : IAgentContinuationCodec
    {
        internal static InvalidUtf8ContinuationCodec Instance { get; } =
            new();

        public string CodecId => "r2-invalid-utf8";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = new AgentContinuationEncodedPayload(
                "utf8",
                [0xff]);
            return true;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = new AgentContinuationCodecValue(
                "readable-0",
                "opaque-0",
                "structured");
            return true;
        }
    }

    private sealed class ThrowingContinuationCodec
        : IAgentContinuationCodec
    {
        internal static ThrowingContinuationCodec Instance { get; } = new();

        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            throw new InvalidOperationException("synthetic codec failure");
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            throw new InvalidOperationException("synthetic codec failure");
        }
    }

    private sealed class RfcThrowingContinuationCodec
        : IAgentContinuationCodec
    {
        internal static RfcThrowingContinuationCodec Instance { get; } =
            new();

        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            throw new Rfc8785CanonicalizationException(
                Rfc8785RejectionReason.DuplicateProperty,
                "synthetic codec canonicalization failure");
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            throw new Rfc8785CanonicalizationException(
                Rfc8785RejectionReason.DuplicateProperty,
                "synthetic codec canonicalization failure");
        }
    }

    private sealed class KeyNotFoundThrowingContinuationCodec
        : IAgentContinuationCodec
    {
        internal static KeyNotFoundThrowingContinuationCodec Instance { get; } =
            new();

        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            throw new KeyNotFoundException("synthetic missing codec property");
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            throw new KeyNotFoundException("synthetic missing codec property");
        }
    }

    private sealed class FixedPayloadContinuationCodec(string payloadJson)
        : IAgentContinuationCodec
    {
        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = new AgentContinuationEncodedPayload(
                "utf8",
                Encoding.UTF8.GetBytes(payloadJson));
            return true;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value) =>
            SyntheticContinuationCodec.Instance.TryDecode(
                encoding,
                payload,
                out value);
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
                    if (!TryReadStructuredOpaque(
                            value.Opaque,
                            out var opaque,
                            out var signature,
                            out var fields))
                    {
                        payload = null;
                        return false;
                    }

                    writer.WriteString("structured");
                    writer.WriteProperty("framing");
                    writer.WriteString(value.Framing);
                    writer.WriteProperty("readable");
                    writer.WriteString(value.Readable);
                    writer.WriteProperty("opaque");
                    writer.WriteString(opaque!);
                    writer.WriteProperty("signature");
                    writer.WriteString(signature!);
                    writer.WriteProperty("fields");
                    writer.WriteArrayStart();
                    for (var index = 0; index < fields.Length; index++)
                    {
                        if (index > 0)
                        {
                            writer.WriteComma();
                        }

                        writer.WriteObjectStart();
                        writer.WriteProperty("name");
                        writer.WriteString(fields[index].Name);
                        writer.WriteProperty("value");
                        writer.WriteString(fields[index].Value);
                        writer.WriteObjectEnd();
                    }

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
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("kind", out var kind))
                {
                    return false;
                }

                var properties = root.EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray();
                switch (kind.GetString())
                {
                    case "readable" when properties.SequenceEqual(
                        ["kind", "text"]):
                        if (root.TryGetProperty("text", out var textProperty) &&
                            textProperty.GetString() is { } text)
                        {
                            value = new AgentContinuationCodecValue(
                                text,
                                string.Empty,
                                "readable");
                        }

                        break;
                    case "opaque" when properties.SequenceEqual(
                        ["kind", "opaque"]):
                        if (root.TryGetProperty(
                                "opaque",
                                out var opaqueProperty) &&
                            opaqueProperty.GetString() is { } opaque)
                        {
                            value = new AgentContinuationCodecValue(
                                string.Empty,
                                opaque,
                                "opaque");
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
                        ]):
                        if (root.TryGetProperty(
                                "readable",
                                out var readableProperty) &&
                            readableProperty.GetString() is { } readable &&
                            root.TryGetProperty(
                                "opaque",
                                out var structuredOpaqueProperty) &&
                            structuredOpaqueProperty.GetString() is
                            { } structuredOpaque &&
                            root.TryGetProperty(
                                "signature",
                                out var signatureProperty) &&
                            signatureProperty.GetString() is { } signature &&
                            root.TryGetProperty(
                                "framing",
                                out var framingProperty) &&
                            framingProperty.GetString() is { } framing &&
                            root.TryGetProperty(
                                "fields",
                                out var fieldsProperty) &&
                            ValidUtf8(framing, 1, 64) &&
                            TryReadFields(
                                fieldsProperty,
                                out var fields))
                        {
                            value = new AgentContinuationCodecValue(
                                readable,
                                signature.Length == 0 && fields.Length == 0
                                    ? structuredOpaque
                                    : StructuredOpaque(
                                        structuredOpaque,
                                        signature,
                                        fields),
                                framing);
                        }

                        break;
                }

                if (value is not null &&
                    TryEncode(value, out var canonical) &&
                    canonical is not null &&
                    StringComparer.Ordinal.Equals(
                        canonical.Encoding,
                        encoding) &&
                    payload.SequenceEqual(canonical.Bytes))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException)
            {
                return false;
            }

            return false;
        }

        internal static string StructuredOpaque(
            string opaque,
            string signature,
            IReadOnlyList<SyntheticField> fields)
        {
            var writer = new Rfc8785Writer(256);
            WriteStructuredOpaque(
                ref writer,
                opaque,
                signature,
                fields);
            return Encoding.UTF8.GetString(
                writer.ToImmutableArray().AsSpan());
        }

        private static bool TryReadStructuredOpaque(
            string value,
            out string? opaque,
            out string? signature,
            out ImmutableArray<SyntheticField> fields)
        {
            opaque = value;
            signature = string.Empty;
            fields = [];
            if (!value.StartsWith("{\"opaque\":", StringComparison.Ordinal))
            {
                return ValidUtf8(value, 0, AgentLimits.ContentBytes);
            }

            try
            {
                using var document = JsonDocument.Parse(value);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.EnumerateObject()
                        .Select(property => property.Name)
                        .SequenceEqual(["opaque", "signature", "fields"]) ||
                    root.GetProperty("opaque").GetString() is not { } parsedOpaque ||
                    root.GetProperty("signature").GetString() is not { } parsedSignature ||
                    !TryReadFields(root.GetProperty("fields"), out fields))
                {
                    return false;
                }

                var canonical = StructuredOpaque(
                    parsedOpaque,
                    parsedSignature,
                    fields);
                if (!StringComparer.Ordinal.Equals(canonical, value))
                {
                    return false;
                }

                opaque = parsedOpaque;
                signature = parsedSignature;
                return true;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private static bool TryReadFields(
            JsonElement element,
            out ImmutableArray<SyntheticField> fields)
        {
            fields = [];
            if (element.ValueKind != JsonValueKind.Array ||
                element.GetArrayLength() > 32)
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var builder = ImmutableArray.CreateBuilder<SyntheticField>(
                element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.EnumerateObject()
                        .Select(property => property.Name)
                        .SequenceEqual(["name", "value"]) ||
                    item.GetProperty("name").GetString() is not { } name ||
                    item.GetProperty("value").GetString() is not { } value ||
                    !ValidFieldName(name) ||
                    !names.Add(name) ||
                    !ValidUtf8(value, 0, 8 * 1024))
                {
                    return false;
                }

                builder.Add(new SyntheticField(name, value));
            }

            fields = builder.MoveToImmutable();
            return true;
        }

        private static void WriteStructuredOpaque(
            ref Rfc8785Writer writer,
            string opaque,
            string signature,
            IReadOnlyList<SyntheticField> fields)
        {
            writer.WriteObjectStart();
            writer.WriteProperty("opaque");
            writer.WriteString(opaque);
            writer.WriteProperty("signature");
            writer.WriteString(signature);
            writer.WriteProperty("fields");
            writer.WriteArrayStart();
            for (var index = 0; index < fields.Count; index++)
            {
                if (index > 0)
                {
                    writer.WriteComma();
                }

                writer.WriteObjectStart();
                writer.WriteProperty("name");
                writer.WriteString(fields[index].Name);
                writer.WriteProperty("value");
                writer.WriteString(fields[index].Value);
                writer.WriteObjectEnd();
            }

            writer.WriteArrayEnd();
            writer.WriteObjectEnd();
        }

        private static bool ValidFieldName(string value) =>
            value.Length is >= 1 and <= 64 &&
            value.All(character =>
                character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '_' or '.' or '-');

        private static bool ValidUtf8(
            string value,
            int minimumBytes,
            int maximumBytes)
        {
            try
            {
                var length = new UTF8Encoding(false, true).GetByteCount(value);
                return length >= minimumBytes && length <= maximumBytes;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
    }

    private sealed record SyntheticField(string Name, string Value);
}
