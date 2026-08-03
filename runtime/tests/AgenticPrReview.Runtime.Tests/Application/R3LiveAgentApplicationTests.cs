using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed partial class R3LiveAgentApplicationTests
{
    private const string RepositoryFact =
        "R3_LIVE_TOOL_ONLY_FACT_7d4c9f";
    private const string ProviderSecret =
        "provider-secret-must-not-enter-body";
    private const string StateSecret =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private static readonly ReviewedIdentity Identity = new(
        "owner/repository",
        107,
        new string('a', 40),
        new string('b', 40));

    [Fact]
    public async Task BootstrapRunsThroughSixToolAgentLoopAndReturnsExactCandidate()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var secrets = new CountingSecretSource(
            new R3LiveAgentSecrets(ProviderSecret, StateSecret));
        var handler = new GroundedReviewHandler(ProviderSecret);
        var transportFactory = new TestingTransportFactory(handler);
        var commitCoordinator = new CapturingStateCommitCoordinator();
        var dependencies = new R3LiveAgentDependencies(
            secrets,
            new R3LiveAgentStateRestorer(),
            transportFactory,
            new CountingFileAccessFactory(),
            commitCoordinator,
            TimeProvider.System);
        var request = Request(
            snapshot.Path,
            Path.Join(snapshot.Path, "state-does-not-exist"));

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(request, CancellationToken.None);

        Assert.True(
            StringComparer.Ordinal.Equals(
                R3LiveAgentCodes.Completed,
                execution.Result.Code),
            handler.Failure?.ToString() ?? execution.Result.Code);
        Assert.Equal(2, execution.Result.ModelCalls);
        Assert.Equal(2, execution.Result.ToolCalls);
        Assert.NotNull(execution.Result.StablePlanSha256);
        Assert.NotNull(execution.Result.TerminalSha256);
        var candidate = Assert.IsType<LiveAgentCandidate>(
            commitCoordinator.Candidate);
        Assert.True(candidate.Outcome.CompletedSessionEligible);
        Assert.Null(candidate.Predecessor);
        Assert.Same(
            DeepSeekReasoningContinuationCodec.Instance,
            candidate.ContinuationCodec);
        Assert.Equal(AgentSessionHeadTransition.SameHead, candidate.Transition);
        Assert.Equal(1, candidate.CurrentReviewContextIndex);
        Assert.Equal(Identity, candidate.Run.ReviewedIdentity);
        Assert.Equal(2, candidate.Run.InitialMessages.Length);
        Assert.Equal("system", candidate.Run.InitialMessages[0].Role);
        Assert.Equal("user", candidate.Run.InitialMessages[1].Role);
        Assert.Equal(request.AuthorizedScope,
            commitCoordinator.Access!.Scope);
        Assert.Equal(1, secrets.CallCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(
            RepositoryFact,
            handler.Requests[0],
            StringComparison.Ordinal);
        Assert.Contains(
            RepositoryFact,
            handler.Requests[1],
            StringComparison.Ordinal);
        Assert.All(handler.Requests, body =>
        {
            Assert.DoesNotContain(ProviderSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(StateSecret, body, StringComparison.Ordinal);
        });
        Assert.Equal(
            AgentToolRegistry.Definitions.Select(definition => definition.Name),
            handler.FirstRequestToolNames);
        Assert.Equal(6, handler.FirstRequestToolNames.Count);
    }

    [Fact]
    public async Task LiveAgentVerifierInnerAuthorizationDenialPrecedesNetworkActivation()
    {
        var secrets = new CountingSecretSource(
            new R3LiveAgentSecrets(ProviderSecret, StateSecret));
        var restorer = new ThrowingStateRestorer();
        var transport = new ThrowingTransportFactory();
        var files = new ThrowingFileAccessFactory();
        var dependencies = new R3LiveAgentDependencies(
            secrets,
            restorer,
            transport,
            files,
            new CapturingStateCommitCoordinator(),
            new ThrowingTimeProvider());
        var valid = Request(
            snapshotRoot: null!,
            stateRoot: null!);
        var denied = new R3LiveAgentRequest(
            valid.AuthorizedScope with { BuildId = "other-build" },
            valid.IsTrustedWorkflow,
            valid.IsSameRepository,
            valid.IsForkOrigin,
            valid.StateLocatorFamily,
            valid.StateRestoreIntent,
            valid.AcceptedLineage,
            valid.StateAdmissionContext,
            valid.StateRoot,
            valid.SnapshotRoot,
            valid.TrackedFiles,
            valid.ChangedFiles,
            valid.DiffSources);

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(denied, CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.AccessDenied, execution.Result.Code);
        Assert.Equal(0, secrets.CallCount);
        Assert.Equal(0, restorer.CallCount);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task LiveAgentVerifierQualityFailureAfterCommitPreservesAcceptedTruth()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var corpusPath = Path.Join(
            AppContext.BaseDirectory,
            "fixtures",
            "agent",
            "r3-quality",
            "corpus.json");
        Assert.True(R3QualityCorpusParser.TryParse(
            File.ReadAllBytes(corpusPath),
            out var corpus,
            out var parseFailure),
            parseFailure?.ToString());
        var sourceCase = Assert.Single(
            corpus!.Cases,
            item => item.Kind == R3QualityCaseKind.MustFind);
        var failingCase = sourceCase with
        {
            ReviewedIdentity = Identity,
            InitialContext = "Review the selected snapshot.",
            ProcessOneContext = null,
        };
        var inner = new CapturingStateCommitCoordinator();
        var observer = new VerifierCommitObserver(
            VerifierScenario.MustFind,
            failingCase,
            R3QualityFreshProcessTwoInputSet.Capture([]));
        observer.SetInner(inner);
        var dependencies = new R3LiveAgentDependencies(
            new CountingSecretSource(
                new R3LiveAgentSecrets(ProviderSecret, StateSecret)),
            new R3LiveAgentStateRestorer(),
            new TestingTransportFactory(
                new GroundedReviewHandler(ProviderSecret)),
            new CountingFileAccessFactory(),
            observer,
            TimeProvider.System);

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(
                Request(
                    snapshot.Path,
                    Path.Join(snapshot.Path, "state-does-not-exist")),
                CancellationToken.None);

        Assert.Equal(R3LiveAgentCodes.Completed, execution.Result.Code);
        Assert.True(execution.Result.HandoffReady);
        Assert.Equal(1, observer.DelegationCount);
        Assert.Equal(1, inner.CallCount);
        Assert.True(observer.CommitResult!.HandoffReady);
        Assert.True(
            observer.Outcome is
            {
                Status: "failed",
                Classification: "quality",
                Code: R3QualityCodes.RequiredToolMissing,
            },
            string.Join(
                "|",
                observer.Outcome?.Status,
                observer.Outcome?.Classification,
                observer.Outcome?.Code,
                observer.Outcome?.SourceCode));
        Assert.False(observer.ProofPassed);
    }

    [Fact]
    public async Task EveryStableScopeFieldAndTrustFactIsIndependentlyDenied()
    {
        var valid = Request(snapshotRoot: null!, stateRoot: null!);
        var hash = new string('e', 64);
        var deniedScopes = new[]
        {
            valid.AuthorizedScope with { RepositoryId = "other/repository" },
            valid.AuthorizedScope with { WorkflowIdentity = "other-workflow" },
            valid.AuthorizedScope with { ReviewTarget = 108 },
            valid.AuthorizedScope with { SessionId = "other-session" },
            valid.AuthorizedScope with { ProviderId = "other-provider" },
            valid.AuthorizedScope with { ModelId = "other-model" },
            valid.AuthorizedScope with { AdapterId = "other-adapter" },
            valid.AuthorizedScope with { PolicySha256 = hash },
            valid.AuthorizedScope with { LimitsSha256 = hash },
            valid.AuthorizedScope with { ToolsetSha256 = hash },
            valid.AuthorizedScope with { BuildId = "other-build" },
        };

        foreach (var scope in deniedScopes)
        {
            var execution = await DeniedExecution(
                valid,
                scope,
                isTrustedWorkflow: true,
                isSameRepository: true,
                isForkOrigin: false);
            Assert.Equal(
                RestrictedStateCodes.AccessDenied,
                execution.Result.Code);
        }

        Assert.Equal(RestrictedStateCodes.AccessDenied,
            (await DeniedExecution(
                valid,
                valid.AuthorizedScope,
                isTrustedWorkflow: false,
                isSameRepository: true,
                isForkOrigin: false)).Result.Code);
        Assert.Equal(RestrictedStateCodes.AccessDenied,
            (await DeniedExecution(
                valid,
                valid.AuthorizedScope,
                isTrustedWorkflow: true,
                isSameRepository: false,
                isForkOrigin: false)).Result.Code);
        Assert.Equal(RestrictedStateCodes.AccessDenied,
            (await DeniedExecution(
                valid,
                valid.AuthorizedScope,
                isTrustedWorkflow: true,
                isSameRepository: true,
                isForkOrigin: true)).Result.Code);
    }

    [Fact]
    public async Task StateFailureNeverDowngradesToBootstrap()
    {
        var restorer = new FixedStateRestorer(
            new RestrictedStateRestoreResult(
                StateResult.Create(
                    StateAction.Failed,
                    RestrictedStateCodes.LineageMismatch),
                null));
        var transport = new ThrowingTransportFactory();
        var files = new ThrowingFileAccessFactory();
        var application = new R3LiveAgentApplication(
            new R3LiveAgentDependencies(
                new CountingSecretSource(new R3LiveAgentSecrets(
                    ProviderSecret,
                    StateSecret)),
                restorer,
                transport,
                files,
                new CapturingStateCommitCoordinator(),
                TimeProvider.System));

        var execution = await application.RunAsync(
            Request(snapshotRoot: "missing-snapshot", stateRoot: "state-root"),
            CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.LineageMismatch,
            execution.Result.Code);
        Assert.Equal(1, restorer.CallCount);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task InvalidSnapshotAndCancellationProduceNoCandidateOrProviderCall()
    {
        using var snapshot = SnapshotDirectory();
        var bootstrap = new FixedStateRestorer(
            new RestrictedStateRestoreResult(
                StateResult.Create(
                    StateAction.Bootstrap,
                    RestrictedStateCodes.Absent),
                null));
        var invalidTransport = new ThrowingTransportFactory();
        var invalidFiles = new ThrowingFileAccessFactory();
        var invalid = await new R3LiveAgentApplication(
            new R3LiveAgentDependencies(
                new CountingSecretSource(new R3LiveAgentSecrets(
                    ProviderSecret,
                    StateSecret)),
                bootstrap,
                invalidTransport,
                invalidFiles,
                new CapturingStateCommitCoordinator(),
                TimeProvider.System))
            .RunAsync(
                Request(
                    Path.Join(snapshot.Path, "missing"),
                    Path.Join(snapshot.Path, "state")),
                CancellationToken.None);
        Assert.Equal(R3LiveAgentCodes.InputInvalid, invalid.Result.Code);
        Assert.Equal(0, invalidTransport.CallCount);
        Assert.Equal(0, invalidFiles.CallCount);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledRestorer = new ThrowingStateRestorer();
        var cancelledTransport = new ThrowingTransportFactory();
        var cancelledFiles = new ThrowingFileAccessFactory();
        var cancellation = await new R3LiveAgentApplication(
            new R3LiveAgentDependencies(
                new CountingSecretSource(new R3LiveAgentSecrets(
                    ProviderSecret,
                    StateSecret)),
                cancelledRestorer,
                cancelledTransport,
                cancelledFiles,
                new CapturingStateCommitCoordinator(),
                TimeProvider.System))
            .RunAsync(
                Request(snapshot.Path, Path.Join(snapshot.Path, "state")),
                cancelled.Token);
        Assert.Equal(AgentFailureCodes.Cancelled, cancellation.Result.Code);
        Assert.Equal(0, cancelledRestorer.CallCount);
        Assert.Equal(0, cancelledTransport.CallCount);
        Assert.Equal(0, cancelledFiles.CallCount);
    }

    [Theory]
    [InlineData(null, StateSecret)]
    [InlineData("", StateSecret)]
    [InlineData(ProviderSecret, null)]
    [InlineData(ProviderSecret, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(ProviderSecret, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task InvalidSecretsAreClearedBeforeStateOrProvider(
        string? provider,
        string? stateKey)
    {
        using var snapshot = SnapshotDirectory();
        var secrets = new CountingSecretSource(
            new R3LiveAgentSecrets(provider, stateKey));
        var restorer = new ThrowingStateRestorer();
        var transport = new ThrowingTransportFactory();
        var files = new ThrowingFileAccessFactory();
        var dependencies = new R3LiveAgentDependencies(
            secrets,
            restorer,
            transport,
            files,
            new CapturingStateCommitCoordinator(),
            TimeProvider.System);

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(
                Request(snapshot.Path, Path.Join(snapshot.Path, "state")),
                CancellationToken.None);

        Assert.Equal(R3LiveAgentCodes.SecretInvalid, execution.Result.Code);
        Assert.Equal(1, secrets.CallCount);
        Assert.Equal(0, restorer.CallCount);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task RestoreUsesExactAdmittedRequestAndZerosRedundantPlaintext()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var bootstrap = Request(snapshot.Path, Path.Join(snapshot.Path, "state"));
        var trusted = bootstrap.StateAdmissionContext.SessionContext.TrustedRequest;
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            new string('c', 64),
            out var materialized));
        var current = bootstrap.StateAdmissionContext.SessionContext
            .CurrentReviewContext;
        var restoredRun = new AgentRunRequest(
            Identity,
            materialized!.StablePlan,
            "session-107",
            [.. materialized.ControlMessages, current]);
        var artifactPlaintext = Enumerable.Range(1, 64)
            .Select(value => (byte)value)
            .ToArray();
        var redundantPlaintext = artifactPlaintext.ToArray();
        var artifact = new AgentSessionArtifact(
            artifactPlaintext,
            new string('c', 64),
            null!);
        var admitted = new RestrictedStateAdmittedSession(
            redundantPlaintext,
            artifact.SessionSha256,
            Identity.BaseSha,
            Identity.HeadSha,
            0,
            null,
            new AgentSessionStateAdmittedValue(restoredRun, artifact));
        var envelopeSha = new string('d', 64);
        var restorer = new FixedStateRestorer(new RestrictedStateRestoreResult(
            StateResult.Create(
                StateAction.Restored,
                RestrictedStateCodes.Restored,
                0,
                artifact.SessionSha256,
                envelopeSha),
            admitted));
        var handler = new GroundedReviewHandler(ProviderSecret);
        var commitCoordinator = new CapturingStateCommitCoordinator();
        var dependencies = Dependencies(
            restorer,
            handler,
            commitCoordinator);
        var restoredContext = bootstrap.StateAdmissionContext with
        {
            Generation = 1,
            PredecessorEnvelopeSha256 = envelopeSha,
        };
        var request = CopyWithState(
            bootstrap,
            RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit,
            restoredContext);

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(request, CancellationToken.None);

        var candidate = Assert.IsType<LiveAgentCandidate>(
            commitCoordinator.Candidate);
        Assert.Same(restoredRun, candidate.Run);
        Assert.Same(artifactPlaintext, candidate.Predecessor!.Plaintext);
        Assert.Equal(artifact.SessionSha256,
            candidate.Predecessor.SessionSha256);
        Assert.Equal(envelopeSha, candidate.Predecessor.EnvelopeSha256);
        Assert.All(redundantPlaintext, value => Assert.Equal(0, value));
        Assert.All(artifactPlaintext, value => Assert.Equal(0, value));
        Assert.Equal(1, restorer.CallCount);
    }

    [Fact]
    public async Task FailedRestoredRunZerosEveryAdmittedPlaintextCopy()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var bootstrap = Request(snapshot.Path, Path.Join(snapshot.Path, "state"));
        var trusted = bootstrap.StateAdmissionContext.SessionContext.TrustedRequest;
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            new string('c', 64),
            out var materialized));
        var restoredRun = new AgentRunRequest(
            Identity,
            materialized!.StablePlan,
            "session-107",
            [.. materialized.ControlMessages,
                bootstrap.StateAdmissionContext.SessionContext
                    .CurrentReviewContext]);
        var artifactPlaintext = Enumerable.Repeat((byte)0x5a, 64).ToArray();
        var redundantPlaintext = artifactPlaintext.ToArray();
        var artifact = new AgentSessionArtifact(
            artifactPlaintext,
            new string('c', 64),
            null!);
        var admitted = new RestrictedStateAdmittedSession(
            redundantPlaintext,
            artifact.SessionSha256,
            Identity.BaseSha,
            Identity.HeadSha,
            0,
            null,
            new AgentSessionStateAdmittedValue(restoredRun, artifact));
        var envelopeSha = new string('d', 64);
        var restorer = new FixedStateRestorer(new RestrictedStateRestoreResult(
            StateResult.Create(
                StateAction.Restored,
                RestrictedStateCodes.Restored,
                0,
                artifact.SessionSha256,
                envelopeSha),
            admitted));
        var handler = new UngroundedReviewHandler();
        var commitCoordinator = new CapturingStateCommitCoordinator();
        var dependencies = Dependencies(
            restorer,
            handler,
            commitCoordinator);
        var request = CopyWithState(
            bootstrap,
            RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit,
            bootstrap.StateAdmissionContext with
            {
                Generation = 1,
                PredecessorEnvelopeSha256 = envelopeSha,
            });

        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(request, CancellationToken.None);

        Assert.Equal(AgentFailureCodes.TerminalInvalid, execution.Result.Code);
        Assert.All(redundantPlaintext, value => Assert.Equal(0, value));
        Assert.All(artifactPlaintext, value => Assert.Equal(0, value));
        Assert.Equal(0, commitCoordinator.CallCount);
    }

    [Fact]
    public async Task RealEncryptedStateRestoreMatchesAuthoritativeRequestBytes()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var stateRoot = Path.Join(snapshot.Path, "state");
        Directory.CreateDirectory(stateRoot);
        var firstRequest = Request(snapshot.Path, stateRoot);
        var firstHandler = new GroundedReviewHandler(ProviderSecret);
        var firstCommit = new CapturingStateCommitCoordinator();
        var first = await new R3LiveAgentApplication(
            Dependencies(
                new R3LiveAgentStateRestorer(),
                firstHandler,
                firstCommit))
            .RunAsync(firstRequest, CancellationToken.None);
        Assert.Equal(R3LiveAgentCodes.Completed, first.Result.Code);
        var firstCandidate = Assert.IsType<LiveAgentCandidate>(
            firstCommit.Candidate);
        var firstAccess = Assert.IsType<AuthorizedStateAccess>(
            firstCommit.Access);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            firstCandidate.Run,
            firstCandidate.Outcome,
            firstCandidate.TrustedRequest,
            firstCandidate.CurrentReviewContextIndex,
            firstCandidate.ContinuationCodec,
            firstCandidate.Predecessor,
            firstCandidate.Transition));
        Assert.True(built.Succeeded, built.FailureCode);
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(
            StateSecret,
            out var stateKeys));
        using (stateKeys)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var service = new RestrictedStateService(
                new LocalRestrictedStateStore(stateRoot),
                stateKeys!,
                new AgentSessionRestrictedStateAdmission(),
                () => now);
            var prepared = service.Prepare(
                firstAccess,
                new RestrictedStatePrepareRequest(
                    Lineage: null,
                    built.Artifact!.Plaintext,
                    firstCandidate.StateAdmissionContext),
                CancellationToken.None);
            Assert.Equal(StateAction.Prepared, prepared.Result.Action);
            var receipt = Assert.IsType<PreparedStateReceipt>(
                prepared.Receipt);
            var accepted = service.Accept(
                firstAccess,
                lineage: null,
                receipt,
                firstCandidate.StateAdmissionContext,
                CancellationToken.None);
            Assert.Equal(StateAction.Accepted, accepted.Action);

            var lineage = new AcceptedLineage(
                firstAccess.Scope,
                receipt.Generation,
                receipt.SessionSha256,
                receipt.EnvelopeSha256,
                ExpectedPredecessorEnvelopeSha256: null,
                now,
                checked(now + RestrictedStateFormat.MaximumRetentionSeconds),
                TransitionAuthorized: true);
            var nextContext = new RestrictedStateSessionAdmissionContext(
                Identity.BaseSha,
                Identity.HeadSha,
                1,
                receipt.EnvelopeSha256,
                new AgentSessionStateAdmissionContext(
                    firstCandidate.TrustedRequest,
                    firstCandidate.Run.SessionId,
                    Identity,
                    new ProjectChatMessage(
                        "user",
                        [new ProjectTextContent(
                            "Review the next selected snapshot.")]),
                    AgentSessionHeadTransition.SameHead,
                    DeepSeekReasoningContinuationCodec.Instance,
                    EnvelopeSha256: null));
            var restoreRequest = new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                nextContext);
            var expected = service.Restore(
                firstAccess,
                restoreRequest,
                CancellationToken.None);
            Assert.Equal(StateAction.Restored, expected.Result.Action);

            try
            {
                var secondHandler = new GroundedReviewHandler(
                    ProviderSecret,
                    "-next");
                var secondRequest = new R3LiveAgentRequest(
                    firstRequest.AuthorizedScope,
                    isTrustedWorkflow: true,
                    isSameRepository: true,
                    isForkOrigin: false,
                    RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit,
                    lineage,
                    nextContext,
                    stateRoot,
                    snapshot.Path,
                    ["a.txt"],
                    [],
                    []);
                var secondCommit = new CapturingStateCommitCoordinator();
                var second = await new R3LiveAgentApplication(
                    Dependencies(
                        new R3LiveAgentStateRestorer(),
                        secondHandler,
                        secondCommit))
                    .RunAsync(secondRequest, CancellationToken.None);
                Assert.True(
                    secondCommit.Candidate is LiveAgentCandidate,
                    secondHandler.Failure?.ToString() ?? second.Result.Code);
                var candidate = (LiveAgentCandidate)secondCommit.Candidate!;
                var expectedRun = expected.Session!.Value.RunRequest;

                Assert.Equal(expectedRun.ReviewedIdentity,
                    candidate.Run.ReviewedIdentity);
                Assert.Equal(expectedRun.StablePlan, candidate.Run.StablePlan);
                Assert.Equal(expectedRun.SessionId, candidate.Run.SessionId);
                Assert.Equal(
                    ProviderNeutralRequestBytes(expectedRun),
                    ProviderNeutralRequestBytes(candidate.Run));
                Assert.Equal(
                    expected.Session.Value.Artifact.SessionSha256,
                    candidate.Predecessor!.SessionSha256);
                Assert.All(
                    candidate.Predecessor.Plaintext,
                    value => Assert.Equal(0, value));
                Assert.Equal(expectedRun.InitialMessages.Length - 1,
                    candidate.CurrentReviewContextIndex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(
                    expected.Session!.Plaintext);
                CryptographicOperations.ZeroMemory(
                    expected.Session.Value.Artifact.Plaintext);
                CryptographicOperations.ZeroMemory(built.Artifact!.Plaintext);
            }
        }
    }

    [Fact]
    public void CandidateHasExactlyEightInternalFieldsAndNoPublicDataSurface()
    {
        var properties = typeof(LiveAgentCandidate).GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        (string Name, Type Type)[] expected =
        [
            ("ContinuationCodec", typeof(IAgentContinuationCodec)),
            ("CurrentReviewContextIndex", typeof(int)),
            ("Outcome", typeof(AgentRunOutcome)),
            ("Predecessor", typeof(AgentSessionPredecessor)),
            ("Run", typeof(AgentRunRequest)),
            ("StateAdmissionContext",
                typeof(RestrictedStateSessionAdmissionContext)),
            ("Transition", typeof(AgentSessionHeadTransition)),
            ("TrustedRequest", typeof(AgentSessionTrustedRequest)),
        ];
        var propertyShape = properties
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var fieldShape = typeof(LiveAgentCandidate).GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Select(field => (field.Name, field.FieldType))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        var expectedFields = expected
            .Select(property =>
                ($"<{property.Name}>k__BackingField", property.Type))
            .ToArray();

        Assert.Equal(expected, propertyShape);
        Assert.Equal(expectedFields, fieldShape);
        Assert.All(properties, property =>
            Assert.False(property.GetMethod!.IsPublic));
    }

    private static R3LiveAgentDependencies Dependencies(
        IR3LiveAgentStateRestorer restorer,
        HttpMessageHandler handler,
        ILiveAgentStateCommitCoordinator? stateCommitCoordinator = null) =>
        new(
            new CountingSecretSource(
                new R3LiveAgentSecrets(ProviderSecret, StateSecret)),
            restorer,
            new TestingTransportFactory(handler),
            new CountingFileAccessFactory(),
            stateCommitCoordinator ?? new CapturingStateCommitCoordinator(),
            TimeProvider.System);

    private static R3LiveAgentRequest Request(
        string snapshotRoot,
        string stateRoot)
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = new RestrictedStateScope(
            trusted.RepositoryId,
            trusted.WorkflowIdentity,
            trusted.ReviewTarget,
            "session-107",
            trusted.ProviderId,
            trusted.ModelId,
            trusted.AdapterId,
            materialized!.StablePlan.PolicySha256,
            materialized.StablePlan.LimitsSha256,
            materialized.StablePlan.ToolsetSha256,
            trusted.BuildId);
        var session = new AgentSessionStateAdmissionContext(
            trusted,
            "session-107",
            Identity,
            new ProjectChatMessage(
                "user",
                [new ProjectTextContent("Review the selected snapshot.")]),
            AgentSessionHeadTransition.SameHead,
            DeepSeekReasoningContinuationCodec.Instance,
            EnvelopeSha256: null);
        var state = new RestrictedStateSessionAdmissionContext(
            Identity.BaseSha,
            Identity.HeadSha,
            0,
            null,
            session);
        return new R3LiveAgentRequest(
            scope,
            isTrustedWorkflow: true,
            isSameRepository: true,
            isForkOrigin: false,
            RestrictedStateLocatorFamily.Absent,
            RestrictedStateRestoreIntent.Automatic,
            acceptedLineage: null,
            state,
            stateRoot,
            snapshotRoot,
            ["a.txt"],
            [],
            []);
    }

    private static R3LiveAgentRequest CopyWithState(
        R3LiveAgentRequest source,
        RestrictedStateLocatorFamily locator,
        RestrictedStateRestoreIntent intent,
        RestrictedStateSessionAdmissionContext context) =>
        new(
            source.AuthorizedScope,
            source.IsTrustedWorkflow,
            source.IsSameRepository,
            source.IsForkOrigin,
            locator,
            intent,
            acceptedLineage: null,
            context,
            source.StateRoot,
            source.SnapshotRoot,
            source.TrackedFiles,
            source.ChangedFiles,
            source.DiffSources);

    private static async Task<R3LiveAgentExecution> DeniedExecution(
        R3LiveAgentRequest source,
        RestrictedStateScope scope,
        bool isTrustedWorkflow,
        bool isSameRepository,
        bool isForkOrigin)
    {
        var secrets = new CountingSecretSource(
            new R3LiveAgentSecrets(ProviderSecret, StateSecret));
        var restorer = new ThrowingStateRestorer();
        var transport = new ThrowingTransportFactory();
        var files = new ThrowingFileAccessFactory();
        var request = new R3LiveAgentRequest(
            scope,
            isTrustedWorkflow,
            isSameRepository,
            isForkOrigin,
            source.StateLocatorFamily,
            source.StateRestoreIntent,
            source.AcceptedLineage,
            source.StateAdmissionContext,
            source.StateRoot,
            source.SnapshotRoot,
            source.TrackedFiles,
            source.ChangedFiles,
            source.DiffSources);
        var execution = await new R3LiveAgentApplication(
            new R3LiveAgentDependencies(
                secrets,
                restorer,
                transport,
                files,
                new CapturingStateCommitCoordinator(),
                new ThrowingTimeProvider()))
            .RunAsync(request, CancellationToken.None);
        Assert.Equal(0, secrets.CallCount);
        Assert.Equal(0, restorer.CallCount);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, files.CallCount);
        return execution;
    }

    private static AgentSessionTrustedRequest Trusted() =>
        new(
            Identity.RepositoryId,
            Identity.ReviewTarget,
            "trusted-r3-workflow",
            Encoding.UTF8.GetBytes(
                "Use snapshot tools for all repository evidence."),
            "build-107",
            DeepSeekAdapterContext.Provider,
            DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter);

    private static byte[] ProviderNeutralRequestBytes(AgentRunRequest run) =>
        AgentRequestWriter.Write(new ProjectChatRequest(
            run.InitialMessages,
            AgentToolRegistry.Definitions.ToArray(),
            run.Continuation,
            ThinkingRequired: true));

    private static TemporaryDirectory SnapshotDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "apr-r3-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    private static byte[] Response(
        string reasoning,
        string name,
        string arguments,
        string callSuffix = "")
    {
        var json = string.Concat(
            "{\"choices\":[{\"index\":0,\"message\":{\"role\":" +
            "\"assistant\",\"content\":\"\",\"reasoning_content\":" ,
            JsonSerializer.Serialize(reasoning),
            ",\"tool_calls\":[{\"id\":\"call_",
            name,
            callSuffix,
            "\",\"type\":\"function\",\"function\":{\"name\":" ,
            JsonSerializer.Serialize(name),
            ",\"arguments\":" ,
            JsonSerializer.Serialize(arguments),
            "}}]},\"finish_reason\":\"tool_calls\"}]," +
            "\"model\":\"deepseek-v4-flash\",\"usage\":{" +
            "\"prompt_tokens\":3,\"completion_tokens\":2," +
            "\"total_tokens\":5,\"prompt_cache_hit_tokens\":1," +
            "\"prompt_cache_miss_tokens\":2}}");
        return Encoding.UTF8.GetBytes(json);
    }

    private sealed class CountingSecretSource(R3LiveAgentSecrets secrets)
        : IR3LiveAgentSecretSource
    {
        internal int CallCount { get; private set; }

        public R3LiveAgentSecrets TakeAndClear()
        {
            CallCount++;
            return secrets;
        }
    }

    private sealed class CapturingStateCommitCoordinator
        : ILiveAgentStateCommitCoordinator
    {
        internal int CallCount { get; private set; }

        internal LiveAgentCandidate? Candidate { get; private set; }

        internal AuthorizedStateAccess? Access { get; private set; }

        public LiveAgentStateCommitResult Commit(
            LiveAgentCandidate candidate,
            AuthorizedStateAccess access,
            AcceptedLineage? priorLineage,
            AgentSessionHeadTransition authorizedTransition,
            string stateRoot,
            IRestrictedStateKeyResolver keyResolver,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Candidate = candidate;
            Access = access;
            return new LiveAgentStateCommitResult(
                R3LiveAgentCodes.Completed,
                0,
                new string('c', 64),
                new string('d', 64),
                handoffReady: true);
        }
    }

    private sealed class TestingTransportFactory(HttpMessageHandler handler)
        : IR3LiveAgentTransportFactory
    {
        public IDeepSeekTransport Create(DeepSeekCredential credential) =>
            DeepSeekTransport.CreateForTesting(
                credential,
                handler,
                TimeSpan.FromSeconds(5));
    }

    private sealed class CountingFileAccessFactory
        : IR3LiveAgentReviewedFileAccessFactory
    {
        internal int CallCount { get; private set; }

        public IReviewedFileAccess Create()
        {
            CallCount++;
            return new VerifiedReviewedFileAccess();
        }
    }

    private sealed class GroundedReviewHandler(
        string expectedCredential,
        string callSuffix = "")
        : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        internal Exception? Failure { get; private set; }

        internal IReadOnlyList<string> FirstRequestToolNames { get; private set; }
            = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                var authorization = request.Headers
                    .GetValues("Authorization")
                    .Single();
                if (!StringComparer.Ordinal.Equals(
                        authorization,
                        "Bearer " + expectedCredential))
                {
                    throw new InvalidOperationException(
                        "The fixed authorization header was not applied.");
                }

                var body = await request.Content!.ReadAsStringAsync(
                    cancellationToken);
                Requests.Add(body);
                byte[] response;
                if (Requests.Count == 1)
                {
                    using var document = JsonDocument.Parse(body);
                    FirstRequestToolNames = document.RootElement
                        .GetProperty("tools")
                        .EnumerateArray()
                        .Select(tool => tool.GetProperty("function")
                            .GetProperty("name").GetString()!)
                        .ToArray();
                    response = Response(
                        "Inspect the selected file.",
                        AgentToolRegistry.ReadFileName,
                        "{\"path\":\"a.txt\"}",
                        callSuffix);
                }
                else
                {
                    using var requestDocument = JsonDocument.Parse(body);
                    var toolMessage = requestDocument.RootElement
                        .GetProperty("messages")
                        .EnumerateArray()
                        .Last(message => StringComparer.Ordinal.Equals(
                            message.GetProperty("role").GetString(),
                            "tool"));
                    using var resultDocument = JsonDocument.Parse(
                        toolMessage.GetProperty("content").GetString()!);
                    var observation = resultDocument.RootElement
                        .GetProperty("observation_id")
                        .GetString();
                    if (observation is not { Length: 64 })
                    {
                        throw new InvalidOperationException(
                            "The admitted observation id was missing.");
                    }

                    var finish = string.Concat(
                        "{\"summary\":\"grounded\",\"findings\":[{" +
                        "\"severity\":\"high\",\"title\":\"bug\"," +
                        "\"message\":\"fix\",\"evidence\":[{" +
                        "\"observation_id\":\"",
                        observation,
                        "\",\"path\":\"a.txt\",\"start_line\":1," +
                        "\"end_line\":1}]}]}");
                    response = Response(
                        "Finish with current evidence.",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        callSuffix);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(response),
                };
            }
            catch (Exception exception)
            {
                Failure = exception;
                return new HttpResponseMessage(
                    HttpStatusCode.InternalServerError);
            }
        }
    }

    private sealed class UngroundedReviewHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var finish =
                "{\"summary\":\"bad\",\"findings\":[{" +
                "\"severity\":\"high\",\"title\":\"bug\"," +
                "\"message\":\"fix\",\"evidence\":[{" +
                "\"observation_id\":\"" + new string('f', 64) +
                "\",\"path\":\"a.txt\",\"start_line\":1," +
                "\"end_line\":1}]}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Response(
                    "Invent evidence.",
                    AgentToolRegistry.FinishReviewName,
                    finish)),
            });
        }
    }

    private sealed class FixedStateRestorer(
        RestrictedStateRestoreResult result) : IR3LiveAgentStateRestorer
    {
        internal int CallCount { get; private set; }

        public RestrictedStateRestoreResult Restore(
            string stateRoot,
            IRestrictedStateKeyResolver keyResolver,
            AuthorizedStateAccess access,
            RestrictedStateRestoreRequest request,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class ThrowingStateRestorer : IR3LiveAgentStateRestorer
    {
        internal int CallCount { get; private set; }

        public RestrictedStateRestoreResult Restore(
            string stateRoot,
            IRestrictedStateKeyResolver keyResolver,
            AuthorizedStateAccess access,
            RestrictedStateRestoreRequest request,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException();
        }
    }

    private sealed class ThrowingTransportFactory : IR3LiveAgentTransportFactory
    {
        internal int CallCount { get; private set; }

        public IDeepSeekTransport Create(DeepSeekCredential credential)
        {
            CallCount++;
            throw new InvalidOperationException();
        }
    }

    private sealed class ThrowingFileAccessFactory
        : IR3LiveAgentReviewedFileAccessFactory
    {
        internal int CallCount { get; private set; }

        public IReviewedFileAccess Create()
        {
            CallCount++;
            throw new InvalidOperationException();
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override long GetTimestamp() =>
            throw new InvalidOperationException();

        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException();
    }

    private sealed class TemporaryDirectory(string path) : IDisposable
    {
        internal string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
