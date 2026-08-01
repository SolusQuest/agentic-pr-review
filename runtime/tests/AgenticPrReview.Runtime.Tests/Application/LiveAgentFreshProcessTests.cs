using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed class LiveAgentFreshProcessTests
{
    private const string ProviderSecret =
        "r3-provider-secret-must-remain-header-only";
    private const string StateKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task BootstrapAndContinueRunInTwoFreshProcesses()
    {
        using var fixture = new FreshProcessFixture();
        var priorFact = "APR_PRIOR_ONLY_" + Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var firstIdentity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        var firstInvocation = "invocation_bootstrap";
        fixture.WritePhase(
            firstIdentity,
            "Review the first immutable snapshot.",
            priorFact + "\n",
            firstInvocation,
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: firstIdentity.HeadSha);

        var first = await fixture.RunAsync("bootstrap");

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.StandardOutput);
        Assert.Empty(first.StandardError);
        var firstResult = fixture.ReadResult();
        Assert.Equal(R3LiveAgentCodes.Completed, firstResult.Code);
        Assert.Equal(0, firstResult.Generation);
        Assert.True(firstResult.HandoffReady);
        Assert.NotNull(firstResult.LineageSha256);
        Assert.Null(firstResult.SecondRequestSha256);
        var firstLineageBytes = File.ReadAllBytes(fixture.LineagePath);
        Assert.Equal(
            firstResult.LineageSha256,
            LiveAgentFreshProcessDomain.RawSha256(firstLineageBytes));
        var firstLineage = Assert.IsType<LiveAgentFreshProcessLineageDocument>(
            LiveAgentFreshProcessCodec.ReadLineage(firstLineageBytes));
        Assert.Equal(firstIdentity.HeadSha, firstLineage.ProducerHeadSha);
        Assert.Equal(
            firstResult.InvocationIdentitySha256,
            firstLineage.InvocationIdentitySha256);

        File.Delete(fixture.ResultPath);
        var secondIdentity = new ReviewedIdentity(
            firstIdentity.RepositoryId,
            firstIdentity.ReviewTarget,
            firstIdentity.HeadSha,
            new string('c', 40));
        fixture.WritePhase(
            secondIdentity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY_4a3b2c1d\n",
            "invocation_continue",
            "current",
            "explicit",
            "verified_ahead",
            firstResult.LineageSha256,
            firstIdentity.HeadSha);

        var second = await fixture.RunAsync("continue");

        Assert.Equal(0, second.ExitCode);
        Assert.Empty(second.StandardOutput);
        Assert.Empty(second.StandardError);
        var secondResult = fixture.ReadResult();
        Assert.Equal(R3LiveAgentCodes.Completed, secondResult.Code);
        Assert.Equal(1, secondResult.Generation);
        Assert.Equal("verified_ahead", secondResult.TransitionClass);
        Assert.True(secondResult.HandoffReady);
        Assert.NotNull(secondResult.SecondRequestSha256);
        Assert.NotEqual(
            firstResult.InvocationIdentitySha256,
            secondResult.InvocationIdentitySha256);
        Assert.False(File.Exists(fixture.CapturePath));

        var publicBytes = Directory.EnumerateFiles(
                fixture.Root,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(
                fixture.StateRoot,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        Assert.DoesNotContain(
            priorFact,
            Encoding.UTF8.GetString(publicBytes),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ProviderSecret,
            Encoding.UTF8.GetString(publicBytes),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            StateKey,
            Encoding.UTF8.GetString(publicBytes),
            StringComparison.Ordinal);
        var stateBytes = Directory.EnumerateFiles(
                fixture.StateRoot,
                "*",
                SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        Assert.DoesNotContain(
            priorFact,
            Encoding.UTF8.GetString(stateBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReusedInvocationIdentityFailsBeforeStateOrProviderAdvance()
    {
        using var fixture = new FreshProcessFixture();
        var firstIdentity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        fixture.WritePhase(
            firstIdentity,
            "Review the first immutable snapshot.",
            "APR_PRIOR_ONLY_" + new string('d', 64) + "\n",
            "same_invocation",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: firstIdentity.HeadSha);
        Assert.Equal(0, (await fixture.RunAsync("bootstrap")).ExitCode);
        var first = fixture.ReadResult();
        var lineageBefore = File.ReadAllBytes(fixture.LineagePath);
        var stateBefore = Directory.EnumerateFiles(
                fixture.StateRoot,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileName(path)!,
                File.ReadAllBytes,
                StringComparer.Ordinal);
        File.Delete(fixture.ResultPath);
        var secondIdentity = new ReviewedIdentity(
            firstIdentity.RepositoryId,
            firstIdentity.ReviewTarget,
            firstIdentity.HeadSha,
            new string('c', 40));
        fixture.WritePhase(
            secondIdentity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "same_invocation",
            "current",
            "explicit",
            "verified_ahead",
            first.LineageSha256,
            firstIdentity.HeadSha);

        var rejected = await fixture.RunAsync("continue");

        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.ProcessIdentityReused,
            rejected.StandardError.Trim());
        Assert.Equal(
            LiveAgentFreshProcessCodes.ProcessIdentityReused,
            fixture.ReadResult().Code);
        Assert.Equal(lineageBefore, File.ReadAllBytes(fixture.LineagePath));
        var stateAfter = Directory.EnumerateFiles(
                    fixture.StateRoot,
                    "*",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetFileName(path)!,
                    File.ReadAllBytes,
                    StringComparer.Ordinal);
        Assert.Equal(
            stateBefore.Keys.Order(StringComparer.Ordinal),
            stateAfter.Keys.Order(StringComparer.Ordinal));
        foreach (var path in stateBefore.Keys)
        {
            Assert.Equal(stateBefore[path], stateAfter[path]);
        }
    }

    [Fact]
    public async Task AuthorizationDenialDoesNotRequestAuthorizedLayout()
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var requested = Scope(trusted, materialized!, "session-109");
        var denied = requested with { BuildId = "other-build" };
        var authorization = Authorization(
            trusted,
            denied,
            "session-109",
            "denied_invocation",
            "absent",
            "automatic",
            "same_head",
            new string('b', 40),
            new string('a', 40),
            new string('b', 40),
            expectedLineageSha256: null);
        var files = new DenialProbeFileSystem(
            LiveAgentFreshProcessCodec.Write(authorization));

        var result = await LiveAgentFreshProcessCommand.RunAsync(
            "bootstrap",
            files,
            CancellationToken.None);

        Assert.Equal(13, result.ExitCode);
        Assert.Equal(RestrictedStateCodes.AccessDenied, result.DiagnosticCode);
        Assert.Equal(1, files.AuthorizationReads);
        Assert.Equal(0, files.AuthorizedLayoutCalls);
    }

    [Fact]
    public void AuthorizationCodecRejectsUnknownDuplicateAndWhitespace()
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var valid = LiveAgentFreshProcessCodec.Write(Authorization(
            trusted,
            scope,
            "session-109",
            "codec_invocation",
            "absent",
            "automatic",
            "same_head",
            new string('b', 40),
            new string('a', 40),
            new string('b', 40),
            expectedLineageSha256: null));

        Assert.NotNull(LiveAgentFreshProcessCodec.ReadAuthorization(valid));
        Assert.Null(LiveAgentFreshProcessCodec.ReadAuthorization(
            [.. valid, (byte)' ']));
        var text = Encoding.UTF8.GetString(valid);
        Assert.Null(LiveAgentFreshProcessCodec.ReadAuthorization(
            Encoding.UTF8.GetBytes(text.Replace(
                "{\"kind\":",
                "{\"unknown\":0,\"kind\":",
                StringComparison.Ordinal))));
        Assert.Null(LiveAgentFreshProcessCodec.ReadAuthorization(
            Encoding.UTF8.GetBytes(text.Replace(
                "{\"kind\":",
                "{\"kind\":\"duplicate\",\"kind\":",
                StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("input", "unexpected.json")]
    [InlineData("private", "second-request.capture")]
    [InlineData("output", "result.json")]
    public async Task FixedLayoutRejectsUnexpectedOrPreexistingEntries(
        string directory,
        string fileName)
    {
        using var fixture = new FreshProcessFixture();
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        fixture.WritePhase(
            identity,
            "Review the immutable snapshot.",
            "APR_PRIOR_ONLY_" + new string('e', 64) + "\n",
            "closed_layout",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);
        File.WriteAllText(Path.Join(fixture.Root, directory, fileName), "x");

        var result = await fixture.RunAsync("bootstrap");

        Assert.Equal(10, result.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.RootInvalid,
            result.StandardError.Trim());
        Assert.False(File.Exists(fixture.LineagePath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.StateRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SelectedCurrentWithoutLineageNeverBootstraps()
    {
        using var fixture = new FreshProcessFixture();
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('b', 40),
            new string('c', 40));
        fixture.WritePhase(
            identity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "missing_lineage",
            "current",
            "explicit",
            "verified_ahead",
            new string('d', 64),
            fromHeadSha: identity.BaseSha);

        var result = await fixture.RunAsync("continue");

        Assert.Equal(10, result.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.RootInvalid,
            result.StandardError.Trim());
        Assert.False(File.Exists(fixture.ResultPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.StateRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MalformedSelectedLineageFailsBeforeRestore()
    {
        using var fixture = new FreshProcessFixture();
        var malformed = Encoding.UTF8.GetBytes("{}");
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('b', 40),
            new string('c', 40));
        fixture.WritePhase(
            identity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "malformed_lineage",
            "current",
            "explicit",
            "verified_ahead",
            LiveAgentFreshProcessDomain.RawSha256(malformed),
            fromHeadSha: identity.BaseSha);
        File.WriteAllBytes(fixture.LineagePath, malformed);

        var result = await fixture.RunAsync("continue");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.LineageInvalid,
            result.StandardError.Trim());
        Assert.Equal(
            LiveAgentFreshProcessCodes.LineageInvalid,
            fixture.ReadResult().Code);
        Assert.Empty(Directory.EnumerateFiles(
            fixture.StateRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, StateKey)]
    [InlineData(ProviderSecret, "not-canonical-base64")]
    public async Task MissingOrMalformedSecretsFailBeforeStateCommit(
        string? providerSecret,
        string? stateKey)
    {
        using var fixture = new FreshProcessFixture();
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        fixture.WritePhase(
            identity,
            "Review the immutable snapshot.",
            "APR_PRIOR_ONLY_" + new string('f', 64) + "\n",
            "invalid_secrets",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);

        var result = await fixture.RunAsync(
            "bootstrap",
            providerSecret,
            stateKey);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(R3LiveAgentCodes.SecretInvalid, result.StandardError.Trim());
        Assert.Equal(R3LiveAgentCodes.SecretInvalid, fixture.ReadResult().Code);
        Assert.False(File.Exists(fixture.LineagePath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.StateRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CommandGrammarRejectsRelativeRootAndSecretArguments()
    {
        var relative = await LiveAgentFreshProcessCommand.RunAsync(
            [
                "review-live-agent-r3",
                "--mode",
                "bootstrap",
                "--root",
                "relative-root",
            ],
            CancellationToken.None);
        var secret = await LiveAgentFreshProcessCommand.RunAsync(
            [
                "review-live-agent-r3",
                "--mode",
                "bootstrap",
                "--root",
                Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                "--api-key",
                "must-not-be-an-argument",
            ],
            CancellationToken.None);

        Assert.Equal(2, relative.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.UsageInvalid,
            relative.DiagnosticCode);
        Assert.Equal(2, secret.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.UsageInvalid,
            secret.DiagnosticCode);
    }

    [Fact]
    public async Task ReviewedInputReparseLinkFailsClosed()
    {
        using var fixture = new FreshProcessFixture();
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        fixture.WritePhase(
            identity,
            "Review the immutable snapshot.",
            "APR_PRIOR_ONLY_" + new string('a', 64) + "\n",
            "reparse_input",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);
        var reviewedPath = Path.Join(
            fixture.Root,
            "input",
            "reviewed-input.json");
        var outside = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(outside, File.ReadAllBytes(reviewedPath));
            File.Delete(reviewedPath);
            try
            {
                File.CreateSymbolicLink(reviewedPath, outside);
            }
            catch (Exception exception) when (exception is
                UnauthorizedAccessException or
                PlatformNotSupportedException or
                IOException)
            {
                return;
            }

            var result = await fixture.RunAsync("bootstrap");

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(
                LiveAgentFreshProcessCodes.InputInvalid,
                result.StandardError.Trim());
            Assert.Equal(
                LiveAgentFreshProcessCodes.InputInvalid,
                fixture.ReadResult().Code);
            Assert.False(File.Exists(fixture.LineagePath));
            Assert.Empty(Directory.EnumerateFiles(
                fixture.StateRoot,
                "*",
                SearchOption.AllDirectories));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private static AgentSessionTrustedRequest Trusted() => new(
        "owner/repository",
        109,
        "trusted-r3-workflow",
        Encoding.UTF8.GetBytes(
            "Use the immutable reviewed snapshot for all evidence."),
        "build-109",
        DeepSeekAdapterContext.Provider,
        DeepSeekAdapterContext.Model,
        DeepSeekAdapterContext.Adapter);

    private static RestrictedStateScope Scope(
        AgentSessionTrustedRequest trusted,
        AgentSessionMaterializedStableRequest materialized,
        string sessionId) => new(
            trusted.RepositoryId,
            trusted.WorkflowIdentity,
            trusted.ReviewTarget,
            sessionId,
            trusted.ProviderId,
            trusted.ModelId,
            trusted.AdapterId,
            materialized.StablePlan.PolicySha256,
            materialized.StablePlan.LimitsSha256,
            materialized.StablePlan.ToolsetSha256,
            trusted.BuildId);

    private static LiveAgentFreshProcessAuthorizationDocument Authorization(
        AgentSessionTrustedRequest trusted,
        RestrictedStateScope scope,
        string sessionId,
        string invocation,
        string locator,
        string intent,
        string transition,
        string fromHead,
        string toBase,
        string toHead,
        string? expectedLineageSha256)
    {
        var receipt = LiveAgentFreshProcessDomain.TransitionReceiptSha256(
            expectedLineageSha256,
            transition,
            fromHead,
            toBase,
            toHead,
            invocation);
        return new LiveAgentFreshProcessAuthorizationDocument(
            LiveAgentFreshProcessDomain.AuthorizationKind,
            new LiveAgentFreshProcessStableAuthority(
                trusted.RepositoryId,
                trusted.ReviewTarget,
                trusted.WorkflowIdentity,
                Encoding.UTF8.GetString(trusted.TrustedPolicyBytes),
                trusted.BuildId,
                trusted.ProviderId,
                trusted.ModelId,
                trusted.AdapterId,
                sessionId),
            LiveAgentFreshProcessDomain.ScopeDocument(scope),
            IsTrustedWorkflow: true,
            IsSameRepository: true,
            IsForkOrigin: false,
            LiveAgentFreshProcessDomain.DeterministicProfile,
            locator,
            intent,
            new LiveAgentFreshProcessTransitionDocument(
                transition,
                fromHead,
                toBase,
                toHead,
                receipt),
            invocation,
            expectedLineageSha256);
    }

    private sealed class FreshProcessFixture : IDisposable
    {
        private readonly AgentSessionTrustedRequest trusted = Trusted();

        internal FreshProcessFixture()
        {
            Root = Path.Join(
                Path.GetTempPath(),
                "apr-r3-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Join(Root, "input"));
            Directory.CreateDirectory(Path.Join(Root, "host"));
            Directory.CreateDirectory(StateRoot);
            Directory.CreateDirectory(Path.Join(Root, "private"));
            Directory.CreateDirectory(Path.Join(Root, "output"));
        }

        internal string Root { get; }

        internal string StateRoot => Path.Join(Root, "state");

        internal string ResultPath => Path.Join(Root, "output", "result.json");

        internal string LineagePath =>
            Path.Join(Root, "host", "accepted-lineage.json");

        internal string CapturePath =>
            Path.Join(Root, "private", "second-request.capture");

        internal void WritePhase(
            ReviewedIdentity identity,
            string reviewContext,
            string fileContent,
            string invocation,
            string locator,
            string intent,
            string transition,
            string? expectedLineageSha256,
            string fromHeadSha)
        {
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized));
            var scope = Scope(trusted, materialized!, "session-109");
            var authorization = Authorization(
                trusted,
                scope,
                "session-109",
                invocation,
                locator,
                intent,
                transition,
                fromHeadSha,
                identity.BaseSha,
                identity.HeadSha,
                expectedLineageSha256);
            var reviewed = new LiveAgentFreshProcessReviewedInputDocument(
                LiveAgentFreshProcessDomain.ReviewedInputKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity),
                reviewContext);
            var bytes = Encoding.UTF8.GetBytes(fileContent);
            var manifest = new LiveAgentFreshProcessSnapshotManifestDocument(
                LiveAgentFreshProcessDomain.SnapshotManifestKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity),
                ["fact.txt"],
                [
                    new LiveAgentFreshProcessFileDocument(
                        "fact.txt",
                        bytes.Length,
                        LiveAgentFreshProcessDomain.RawSha256(bytes),
                        Convert.ToBase64String(bytes)),
                ],
                [],
                []);
            File.WriteAllBytes(
                Path.Join(Root, "host", "authorization.json"),
                LiveAgentFreshProcessCodec.Write(authorization));
            File.WriteAllBytes(
                Path.Join(Root, "input", "reviewed-input.json"),
                LiveAgentFreshProcessCodec.Write(reviewed));
            File.WriteAllBytes(
                Path.Join(Root, "input", "snapshot-manifest.json"),
                LiveAgentFreshProcessCodec.Write(manifest));
        }

        internal LiveAgentFreshProcessResultDocument ReadResult() =>
            Assert.IsType<LiveAgentFreshProcessResultDocument>(
                LiveAgentFreshProcessCodec.ReadResult(
                    File.ReadAllBytes(ResultPath)));

        internal async Task<RunResult> RunAsync(
            string phase,
            string? providerSecret = ProviderSecret,
            string? stateKey = StateKey)
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable(
                    "DOTNET_HOST_PATH") ?? "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(
                AppContext.BaseDirectory,
                "AgenticPrReview.Runtime.Tests.runtimeconfig.json"));
            start.ArgumentList.Add(typeof(RuntimeApplication).Assembly.Location);
            start.ArgumentList.Add("review-live-agent-r3");
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add(phase);
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(Root);
            SetSecret(
                start,
                R3LiveAgentEnvironmentSecretSource.ProviderVariable,
                providerSecret);
            SetSecret(
                start,
                R3LiveAgentEnvironmentSecretSource.StateKeyVariable,
                stateKey);
            using var process = Process.Start(start) ??
                throw new InvalidOperationException(
                    "Could not start the runtime process.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new RunResult(
                process.ExitCode,
                await stdout,
                await stderr);
        }

        private static void SetSecret(
            ProcessStartInfo start,
            string name,
            string? value)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
                return;
            }

            start.Environment[name] = value;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class DenialProbeFileSystem(byte[] authorization)
        : ILiveAgentFreshProcessFileSystem
    {
        internal int AuthorizationReads { get; private set; }

        internal int AuthorizedLayoutCalls { get; private set; }

        public LiveAgentFreshProcessRead? ReadAuthorization()
        {
            AuthorizationReads++;
            return new LiveAgentFreshProcessRead(
                authorization,
                new LiveAgentFreshProcessFileVersion(1, 1, authorization.Length));
        }

        public bool TryAuthorizeLayout(
            AuthorizedStateAccess access,
            bool lineageExpected,
            out LiveAgentFreshProcessAuthorizedRoot? authorizedRoot)
        {
            AuthorizedLayoutCalls++;
            authorizedRoot = null;
            throw new InvalidOperationException(
                "Authorization denial must not request the root layout.");
        }

        public LiveAgentFreshProcessRead? ReadReviewedInput(
            LiveAgentFreshProcessAuthorizedRoot root) => throw Unreachable();

        public LiveAgentFreshProcessRead? ReadSnapshotManifest(
            LiveAgentFreshProcessAuthorizedRoot root) => throw Unreachable();

        public LiveAgentFreshProcessRead? ReadLineage(
            LiveAgentFreshProcessAuthorizedRoot root) => throw Unreachable();

        public LiveAgentFreshProcessAtomicWriteReceipt? PublishLineage(
            LiveAgentFreshProcessAuthorizedRoot root,
            byte[] bytes,
            LiveAgentFreshProcessFileVersion? expectedPrior) =>
            throw Unreachable();

        public LiveAgentFreshProcessAtomicWriteReceipt? PublishResult(
            LiveAgentFreshProcessAuthorizedRoot root,
            byte[] bytes) => throw Unreachable();

        private static Exception Unreachable() =>
            new InvalidOperationException(
                "Authorization denial reached a protected capability.");
    }

    private sealed record RunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
