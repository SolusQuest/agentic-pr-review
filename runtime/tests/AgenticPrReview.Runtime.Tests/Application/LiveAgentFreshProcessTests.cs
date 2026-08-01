using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
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

        Assert.True(
            first.ExitCode == 0,
            $"{first.StandardError}{first.StandardOutput}");
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

        Assert.True(
            second.ExitCode == 0,
            $"{second.StandardError}{second.StandardOutput}");
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

    [Theory]
    [InlineData("absent")]
    [InlineData("non_current")]
    public async Task AutomaticAbsentOrNonCurrentContinueBootstraps(
        string locator)
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
            "automatic_bootstrap",
            locator,
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);

        var run = await fixture.RunAsync("continue");

        Assert.Equal(0, run.ExitCode);
        var result = fixture.ReadResult();
        Assert.Equal(R3LiveAgentCodes.Completed, result.Code);
        Assert.Equal(0, result.Generation);
        Assert.True(result.HandoffReady);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("non_current")]
    public async Task ExplicitAbsentOrNonCurrentRetainsExactFailure(
        string locator)
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
            "explicit_missing",
            locator,
            "explicit",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);

        var run = await fixture.RunAsync("continue");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            RestrictedStateCodes.ExplicitMissing,
            run.StandardError.Trim());
        var result = fixture.ReadResult();
        Assert.Equal(RestrictedStateCodes.ExplicitMissing, result.Code);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
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

    [Theory]
    [InlineData("unknown")]
    [InlineData("diverged")]
    [InlineData("unrelated")]
    public async Task NonVerifiedTransitionFailsBeforeProviderConstruction(
        string transition)
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
            "APR_PRIOR_ONLY_" + new string('c', 64) + "\n",
            "transition_bootstrap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: firstIdentity.HeadSha);
        Assert.Equal(0, (await fixture.RunAsync("bootstrap")).ExitCode);
        var first = fixture.ReadResult();
        var lineageBefore = File.ReadAllBytes(fixture.LineagePath);
        File.Delete(fixture.ResultPath);
        var secondIdentity = new ReviewedIdentity(
            firstIdentity.RepositoryId,
            firstIdentity.ReviewTarget,
            firstIdentity.HeadSha,
            new string('d', 40));
        fixture.WritePhase(
            secondIdentity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "transition_rejected",
            "current",
            "explicit",
            transition,
            first.LineageSha256,
            firstIdentity.HeadSha);

        var rejected = await fixture.RunAsync("continue");

        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.TransitionRejected,
            rejected.StandardError.Trim());
        var result = fixture.ReadResult();
        Assert.Equal(LiveAgentFreshProcessCodes.TransitionRejected, result.Code);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(lineageBefore, File.ReadAllBytes(fixture.LineagePath));
    }

    [Fact]
    public async Task CurrentAutomaticIsRejectedBeforeProviderConstruction()
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
            "APR_PRIOR_ONLY_" + new string('c', 64) + "\n",
            "current_automatic_bootstrap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: firstIdentity.HeadSha);
        Assert.Equal(0, (await fixture.RunAsync("bootstrap")).ExitCode);
        var first = fixture.ReadResult();
        var lineageBefore = File.ReadAllBytes(fixture.LineagePath);
        File.Delete(fixture.ResultPath);
        var secondIdentity = new ReviewedIdentity(
            firstIdentity.RepositoryId,
            firstIdentity.ReviewTarget,
            firstIdentity.HeadSha,
            new string('d', 40));
        fixture.WritePhase(
            secondIdentity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "current_automatic_continue",
            "current",
            "automatic",
            "verified_ahead",
            first.LineageSha256,
            firstIdentity.HeadSha);

        var rejected = await fixture.RunAsync("continue");

        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.TransitionRejected,
            rejected.StandardError.Trim());
        var result = fixture.ReadResult();
        Assert.Equal(
            LiveAgentFreshProcessCodes.TransitionRejected,
            result.Code);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(lineageBefore, File.ReadAllBytes(fixture.LineagePath));
    }

    [Fact]
    public async Task SelectedCurrentMissingRetainsExactFailure()
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
            "APR_PRIOR_ONLY_" + new string('c', 64) + "\n",
            "missing_current_bootstrap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: firstIdentity.HeadSha);
        Assert.Equal(0, (await fixture.RunAsync("bootstrap")).ExitCode);
        var first = fixture.ReadResult();
        var lineageBefore = File.ReadAllBytes(fixture.LineagePath);
        File.Delete(fixture.ResultPath);
        Directory.Delete(fixture.StateRoot, recursive: true);
        Directory.CreateDirectory(fixture.StateRoot);
        var secondIdentity = new ReviewedIdentity(
            firstIdentity.RepositoryId,
            firstIdentity.ReviewTarget,
            firstIdentity.HeadSha,
            new string('d', 40));
        fixture.WritePhase(
            secondIdentity,
            "Review the next immutable snapshot.",
            "APR_CURRENT_ONLY\n",
            "missing_current_continue",
            "current",
            "explicit",
            "verified_ahead",
            first.LineageSha256,
            firstIdentity.HeadSha);

        var rejected = await fixture.RunAsync("continue");

        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal(
            RestrictedStateCodes.CurrentMissing,
            rejected.StandardError.Trim());
        var result = fixture.ReadResult();
        Assert.Equal(RestrictedStateCodes.CurrentMissing, result.Code);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(lineageBefore, File.ReadAllBytes(fixture.LineagePath));
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

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("adapter")]
    [InlineData("policy")]
    [InlineData("limits")]
    [InlineData("toolset")]
    [InlineData("build")]
    [InlineData("session")]
    [InlineData("scope")]
    public async Task StableOrScopeMismatchFailsBeforeAuthorizedLayout(
        string mismatch)
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var document = Authorization(
            trusted,
            scope,
            "session-109",
            "mismatch_invocation",
            "absent",
            "automatic",
            "same_head",
            new string('b', 40),
            new string('a', 40),
            new string('b', 40),
            expectedLineageSha256: null);
        document = mismatch switch
        {
            "provider" => document with
            {
                Stable = document.Stable with { ProviderId = "other" },
            },
            "model" => document with
            {
                Stable = document.Stable with { ModelId = "other" },
            },
            "adapter" => document with
            {
                Stable = document.Stable with { AdapterId = "other" },
            },
            "policy" => document with
            {
                Stable = document.Stable with
                {
                    TrustedPolicy = document.Stable.TrustedPolicy + " changed",
                },
            },
            "limits" => document with
            {
                AuthorizedScope = document.AuthorizedScope with
                {
                    LimitsSha256 = new string('0', 64),
                },
            },
            "toolset" => document with
            {
                AuthorizedScope = document.AuthorizedScope with
                {
                    ToolsetSha256 = new string('0', 64),
                },
            },
            "build" => document with
            {
                AuthorizedScope = document.AuthorizedScope with
                {
                    BuildId = "other-build",
                },
            },
            "session" => document with
            {
                Stable = document.Stable with { SessionId = "other-session" },
            },
            "scope" => document with
            {
                AuthorizedScope = document.AuthorizedScope with
                {
                    RepositoryId = "other/repository",
                },
            },
            _ => throw new InvalidOperationException("Unknown mismatch."),
        };
        var files = new DenialProbeFileSystem(
            LiveAgentFreshProcessCodec.Write(document));

        var result = await LiveAgentFreshProcessCommand.RunAsync(
            "bootstrap",
            files,
            CancellationToken.None);

        Assert.Equal(1, files.AuthorizationReads);
        Assert.Equal(0, files.AuthorizedLayoutCalls);
        if (mismatch is "provider" or "model" or "adapter")
        {
            Assert.Equal(10, result.ExitCode);
            Assert.Equal(
                LiveAgentFreshProcessCodes.AuthorizationInvalid,
                result.DiagnosticCode);
        }
        else
        {
            Assert.Equal(13, result.ExitCode);
            Assert.Equal(
                RestrictedStateCodes.AccessDenied,
                result.DiagnosticCode);
        }
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
    [InlineData("kind")]
    [InlineData("stable")]
    [InlineData("scope")]
    [InlineData("transition")]
    public async Task AuthorizationExplicitNullIsClosedInputFailure(
        string member)
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var valid = Encoding.UTF8.GetString(
            LiveAgentFreshProcessCodec.Write(Authorization(
                trusted,
                scope,
                "session-109",
                "null_invocation",
                "absent",
                "automatic",
                "same_head",
                new string('b', 40),
                new string('a', 40),
                new string('b', 40),
                expectedLineageSha256: null)));
        var invalid = member switch
        {
            "kind" => valid.Replace(
                $"\"kind\":\"{LiveAgentFreshProcessDomain.AuthorizationKind}\"",
                "\"kind\":null",
                StringComparison.Ordinal),
            "stable" => valid.Replace(
                "\"trusted_policy\":\"Use the immutable reviewed " +
                    "snapshot for all evidence.\"",
                "\"trusted_policy\":null",
                StringComparison.Ordinal),
            "scope" => valid.Replace(
                "\"policy_sha256\":\"" + scope.PolicySha256 + "\"",
                "\"policy_sha256\":null",
                StringComparison.Ordinal),
            "transition" => valid.Replace(
                "\"classification\":\"same_head\"",
                "\"classification\":null",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown member."),
        };
        Assert.NotEqual(valid, invalid);
        var files = new DenialProbeFileSystem(
            Encoding.UTF8.GetBytes(invalid));

        var result = await LiveAgentFreshProcessCommand.RunAsync(
            "bootstrap",
            files,
            CancellationToken.None);

        Assert.Equal(10, result.ExitCode);
        Assert.Equal(
            LiveAgentFreshProcessCodes.AuthorizationInvalid,
            result.DiagnosticCode);
        Assert.Equal(1, files.AuthorizationReads);
        Assert.Equal(0, files.AuthorizedLayoutCalls);
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

    [Fact]
    public void WrongLineageDigestIsRejectedBeforeDecode()
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var bytes = Encoding.UTF8.GetBytes("{}");
        var read = new LiveAgentFreshProcessRead(
            bytes,
            new LiveAgentFreshProcessFileVersion(1, 1, bytes.Length));
        var decodeCalls = 0;

        var admitted = LiveAgentFreshProcessLineageAdmission.TryAdmit(
            read,
            new string('0', 64),
            scope,
            value =>
            {
                decodeCalls++;
                return LiveAgentFreshProcessCodec.ReadLineage(value);
            },
            out _);

        Assert.False(admitted);
        Assert.Equal(0, decodeCalls);
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

    [Fact]
    public void AuthorizedDirectorySwapCannotRedirectCapability()
    {
        using var fixture = new FreshProcessFixture();
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var authorization = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                scope,
                scope,
                IsTrustedWorkflow: true,
                IsSameRepository: true,
                IsForkOrigin: false),
            out var access);
        Assert.Equal(StateAction.Authorized, authorization.Action);
        Assert.NotNull(access);
        var identity = new ReviewedIdentity(
            "owner/repository",
            109,
            new string('a', 40),
            new string('b', 40));
        fixture.WritePhase(
            identity,
            "Review the immutable snapshot.",
            "APR_PRIOR_ONLY_" + new string('b', 64) + "\n",
            "directory_swap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);
        Assert.True(LiveAgentFreshProcessFileSystem.TryCreate(
            fixture.Root,
            out var files));
        var authorizationRead = files!.ReadAuthorization();
        Assert.NotNull(authorizationRead);
        Assert.True(files!.TryAuthorizeLayout(
            authorizationRead!,
            access!,
            lineageExpected: false,
            out var authorizedRoot));

        var input = Path.Join(fixture.Root, "input");
        var displaced = fixture.Root + "-input-displaced";
        var expected = File.ReadAllBytes(
            Path.Join(input, "reviewed-input.json"));
        var moved = false;
        try
        {
            try
            {
                Directory.Move(input, displaced);
                moved = true;
            }
            catch (IOException)
            {
                Assert.True(OperatingSystem.IsWindows());
                Assert.NotNull(files.ReadReviewedInput(authorizedRoot!));
                return;
            }

            Directory.CreateDirectory(input);
            foreach (var name in new[]
            {
                "reviewed-input.json",
                "snapshot-manifest.json",
            })
            {
                File.Copy(Path.Join(displaced, name), Path.Join(input, name));
            }
            File.WriteAllText(
                Path.Join(input, "reviewed-input.json"),
                "{}");

            var read = files.ReadReviewedInput(authorizedRoot!);
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(read);
            }
            else
            {
                Assert.Equal(expected, Assert.IsType<
                    LiveAgentFreshProcessRead>(read).Bytes);
            }
        }
        finally
        {
            authorizedRoot?.Dispose();
            if (moved)
            {
                Directory.Delete(input, recursive: true);
                Directory.Move(displaced, input);
            }
        }
    }

    [Fact]
    public void RootReplacementAfterAuthorizationReadIsRejected()
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
            "APR_PRIOR_ONLY_" + new string('b', 64) + "\n",
            "root_swap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);
        Assert.True(LiveAgentFreshProcessFileSystem.TryCreate(
            fixture.Root,
            out var files));
        var authorizationRead = files!.ReadAuthorization();
        Assert.NotNull(authorizationRead);
        var displaced = fixture.Root + "-root-displaced";
        Directory.Move(fixture.Root, displaced);
        try
        {
            foreach (var name in new[]
            {
                "host",
                "input",
                "output",
                "private",
                "state",
            })
            {
                Directory.CreateDirectory(Path.Join(fixture.Root, name));
            }

            File.Copy(
                Path.Join(displaced, "host", "authorization.json"),
                Path.Join(fixture.Root, "host", "authorization.json"));
            foreach (var name in new[]
            {
                "reviewed-input.json",
                "snapshot-manifest.json",
            })
            {
                File.Copy(
                    Path.Join(displaced, "input", name),
                    Path.Join(fixture.Root, "input", name));
            }

            Assert.False(files.TryAuthorizeLayout(
                authorizationRead!,
                AuthorizedAccess(),
                lineageExpected: false,
                out _));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
            Directory.Move(displaced, fixture.Root);
        }
    }

    [Theory]
    [InlineData("host")]
    [InlineData("output")]
    public void AuthorizedPublicationCannotBeRedirected(string directory)
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
            "APR_PRIOR_ONLY_" + new string('b', 64) + "\n",
            "publication_swap",
            "absent",
            "automatic",
            "same_head",
            expectedLineageSha256: null,
            fromHeadSha: identity.HeadSha);
        Assert.True(LiveAgentFreshProcessFileSystem.TryCreate(
            fixture.Root,
            out var files));
        var authorizationRead = files!.ReadAuthorization();
        Assert.NotNull(authorizationRead);
        Assert.True(files.TryAuthorizeLayout(
            authorizationRead!,
            AuthorizedAccess(),
            lineageExpected: false,
            out var authorizedRoot));
        var original = Path.Join(fixture.Root, directory);
        var displaced = fixture.Root + "-" + directory + "-displaced";
        var moved = false;
        try
        {
            try
            {
                Directory.Move(original, displaced);
                moved = true;
            }
            catch (IOException)
            {
                Assert.True(OperatingSystem.IsWindows());
            }

            if (moved)
            {
                Directory.CreateDirectory(original);
            }

            var receipt = directory == "host"
                ? files.PublishLineage(
                    authorizedRoot!,
                    [1],
                    expectedPrior: null)
                : files.PublishResult(authorizedRoot!, [1]);
            if (!moved || !OperatingSystem.IsWindows())
            {
                Assert.NotNull(receipt);
                var target = directory == "host"
                    ? "accepted-lineage.json"
                    : "result.json";
                Assert.True(File.Exists(Path.Join(
                    moved ? displaced : original,
                    target)));
                if (moved)
                {
                    Assert.False(File.Exists(Path.Join(original, target)));
                }
            }
            else
            {
                Assert.Null(receipt);
            }
        }
        finally
        {
            authorizedRoot?.Dispose();
            if (moved)
            {
                Directory.Delete(original, recursive: true);
                Directory.Move(displaced, original);
            }
        }
    }

    [Fact]
    public void ExactRestoredReplayShapeIsAccepted()
    {
        var fact = "APR_PRIOR_ONLY_" + new string('a', 64);
        var messages = RestoredMessages(fact);
        using var document = JsonDocument.Parse(messages.ToJsonString());

        var accepted = LiveAgentFreshProcessDeterministicTransport
            .TryValidateRestoredMessages(
                document.RootElement,
                [Encoding.UTF8.GetBytes("fresh public input")],
                out var restored);

        Assert.True(accepted);
        Assert.Equal(fact, restored);
    }

    [Theory]
    [InlineData("first_reasoning")]
    [InlineData("first_call_id")]
    [InlineData("first_arguments")]
    [InlineData("first_result_id")]
    [InlineData("finish_reasoning")]
    [InlineData("finish_call_id")]
    [InlineData("finish_arguments")]
    [InlineData("finish_result_id")]
    [InlineData("historical_user_fact")]
    [InlineData("fresh_user_fact")]
    [InlineData("fresh_public_fact")]
    [InlineData("unapproved_reasoning")]
    public void RestoredReplayMutationIsRejected(string mutation)
    {
        var fact = "APR_PRIOR_ONLY_" + new string('a', 64);
        var messages = RestoredMessages(fact);
        var currentInputs = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("fresh public input"),
        };
        switch (mutation)
        {
            case "first_reasoning":
                Message(messages, 2)["reasoning_content"] = "changed";
                break;
            case "first_call_id":
                ToolCall(Message(messages, 2))["id"] = "changed";
                break;
            case "first_arguments":
                ToolFunction(Message(messages, 2))["arguments"] =
                    "{\"path\":\"fact.txt\"}";
                break;
            case "first_result_id":
                Message(messages, 3)["tool_call_id"] = "changed";
                break;
            case "finish_reasoning":
                Message(messages, 4)["reasoning_content"] = "changed";
                break;
            case "finish_call_id":
                ToolCall(Message(messages, 4))["id"] = "changed";
                break;
            case "finish_arguments":
                ToolFunction(Message(messages, 4))["arguments"] = "{}";
                break;
            case "finish_result_id":
                Message(messages, 5)["tool_call_id"] = "changed";
                break;
            case "historical_user_fact":
                Message(messages, 1)["content"] = fact;
                break;
            case "fresh_user_fact":
                Message(messages, 6)["content"] = fact;
                break;
            case "fresh_public_fact":
                currentInputs.Add(Encoding.UTF8.GetBytes(fact));
                break;
            case "unapproved_reasoning":
                Message(messages, 3)["reasoning_content"] = "hidden";
                break;
            default:
                throw new InvalidOperationException("Unknown mutation.");
        }

        using var document = JsonDocument.Parse(messages.ToJsonString());
        Assert.False(LiveAgentFreshProcessDeterministicTransport
            .TryValidateRestoredMessages(
                document.RootElement,
                currentInputs,
                out _));
    }

    [Fact]
    public void TransportProofRequiresExactValidatedTerminalDigest()
    {
        var expected = new string('a', 64);
        var proof = new LiveAgentFreshProcessTransportProof(
            true,
            2,
            new string('b', 64),
            new string('c', 64),
            expected);

        Assert.True(proof.IsSatisfiedBy(expected));
        Assert.False(proof.IsSatisfiedBy(new string('0', 64)));
        Assert.False((proof with { Succeeded = false })
            .IsSatisfiedBy(expected));
    }

    private static JsonArray RestoredMessages(string fact)
    {
        var observation = new string('e', 64);
        return
        [
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "stable control",
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "historical review context",
            },
            AssistantMessage(
                "Inspect the manifest-backed reviewed file.",
                "bootstrap_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"fact.txt\",\"start_line\":1," +
                    "\"line_count\":400}"),
            new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "bootstrap_read",
                ["content"] = "{\"observation_id\":\"" + observation +
                    "\",\"content\":\"" + fact + "\"}",
            },
            AssistantMessage(
                "Remember " + fact,
                "bootstrap_finish",
                AgentToolRegistry.FinishReviewName,
                RestoredFinishArguments(fact, observation)),
            new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "bootstrap_finish",
                ["content"] = "{}",
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "fresh review context",
            },
        ];
    }

    private static JsonObject AssistantMessage(
        string reasoning,
        string callId,
        string name,
        string arguments) => new()
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["reasoning_content"] = reasoning,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = callId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = arguments,
                    },
                },
            },
        };

    private static JsonObject Message(JsonArray messages, int index) =>
        Assert.IsType<JsonObject>(messages[index]);

    private static JsonObject ToolCall(JsonObject message) =>
        Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(message["tool_calls"])[0]);

    private static JsonObject ToolFunction(JsonObject message) =>
        Assert.IsType<JsonObject>(ToolCall(message)["function"]);

    private static string RestoredFinishArguments(
        string fact,
        string observation) => string.Concat(
            "{\"summary\":\"grounded ",
            fact,
            "\",\"findings\":[{\"severity\":\"high\"," +
                "\"title\":\"grounded continuation\"," +
                "\"message\":\"The reviewed fact was grounded.\"," +
                "\"evidence\":[{\"observation_id\":\"",
            observation,
            "\",\"path\":\"fact.txt\",\"start_line\":1," +
                "\"end_line\":1}]}]}");

    private static AuthorizedStateAccess AuthorizedAccess()
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var scope = Scope(trusted, materialized!, "session-109");
        var authorization = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                scope,
                scope,
                IsTrustedWorkflow: true,
                IsSameRepository: true,
                IsForkOrigin: false),
            out var access);
        Assert.Equal(StateAction.Authorized, authorization.Action);
        return Assert.IsType<AuthorizedStateAccess>(access);
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
            start.ArgumentList.Add(Path.Join(
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

        public LiveAgentFreshProcessAuthorizationRead? ReadAuthorization()
        {
            AuthorizationReads++;
            return LiveAgentFreshProcessAuthorizationRead.Create(
                this,
                new LiveAgentFreshProcessRead(
                    authorization,
                    new LiveAgentFreshProcessFileVersion(
                        1,
                        1,
                        authorization.Length)),
                []);
        }

        public bool TryAuthorizeLayout(
            LiveAgentFreshProcessAuthorizationRead authorizationRead,
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
