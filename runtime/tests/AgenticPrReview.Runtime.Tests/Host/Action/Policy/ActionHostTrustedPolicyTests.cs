using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Policy;

public sealed class ActionHostTrustedPolicyTests
{
    [Fact]
    public async Task DefaultsMaterializeExactBytesAndProductOwnedIdentity()
    {
        var (request, _) = await Request();
        var config = Config("sticky", inlineSeverity: null);
        var instructions = Encoding.UTF8.GetBytes("Review carefully.\r\n");
        var transport = ScriptedObjectTransport.Valid(config, instructions);

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var policy = result.Policy!;
        Assert.Equal(ActionHostTrustedPolicyRequest.DefaultConfigPath,
            policy.ConfigPath);
        Assert.Equal(".github/agentic-pr-review/instructions.md",
            policy.InstructionsPath);
        Assert.Equal(config, policy.ConfigBytes.ToArray());
        Assert.Equal(instructions, policy.InstructionBytes.ToArray());
        Assert.Equal(AgentCanonical.HashRaw(config), policy.ConfigSha256);
        Assert.Equal(AgentCanonical.HashRaw(instructions),
            policy.InstructionsSha256);
        Assert.Equal(ActionHostPublicationMode.Sticky,
            policy.PublicationMode);
        Assert.Equal(ActionHostInlineSeverity.High,
            policy.InlineMinSeverity);
        Assert.Equal(DeepSeekAdapterContext.Provider, policy.ProviderId);
        Assert.Equal(DeepSeekAdapterContext.Model, policy.ModelId);
        Assert.Equal(DeepSeekAdapterContext.Adapter, policy.AdapterId);
        Assert.Equal(DeepSeekTransportPolicy.Endpoint, policy.Endpoint);
        Assert.True(policy.ThinkingEnabled);
        Assert.True(policy.PromptCachingEnabled);
        Assert.Equal(AgentCanonical.ToolsetSha256(
            AgentToolRegistry.Definitions), policy.ToolsetSha256);
        Assert.Equal(AgentCanonical.LimitsSha256(), policy.LimitsSha256);
        Assert.Equal(RestrictedStateFormat.MaximumRetentionSeconds,
            policy.StateRetentionSeconds);
        Assert.Equal(7 * 24 * 60 * 60, policy.StateRetentionSeconds);
        Assert.Equal(5, policy.MaximumInlineComments);
        Assert.Equal(ActionHostTrustedPolicy.SecurityPolicy,
            policy.SecurityPolicyId);
        Assert.Equal(ActionHostPayloadContinuityMode.ExactExecutable,
            policy.PayloadContinuityMode);
        Assert.Equal(policy.PayloadSha256,
            policy.PayloadContinuitySha256);
        Assert.Equal(
            "0b8da22450401fb26cc219dc8c6c5af771dbd8f6f7c02c27409d313468c792f5",
            policy.PolicySha256);
        Assert.All(transport.Calls, call => Assert.DoesNotContain(
            ActionHostAuthorizationScenario.HeadSha,
            call,
            StringComparison.Ordinal));
        Assert.Contains(transport.Calls, call =>
            call == "commit:" + request.WorkflowCommitSha);
    }

    [Fact]
    public async Task ExplicitConfigPathResolvesInsideTheAuthorizedCommit()
    {
        var (defaultRequest, scenario) = await Request();
        var customLaunch = CloneLaunch(
            scenario.Launch,
            configPath: ".github/custom-policy.json");
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            customLaunch,
            CancellationToken.None);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            customLaunch,
            authorization.Invocation,
            out var request,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);

        var transport = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        transport.Trees[ScriptedObjectTransport.GitHubTree] = new(
            ScriptedObjectTransport.GitHubTree,
        [
            new("custom-policy.json", "100644", "blob",
                ScriptedObjectTransport.ConfigBlob),
            new("agentic-pr-review", "040000", "tree",
                ScriptedObjectTransport.InstructionsTree),
        ]);

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request!,
            transport,
            CancellationToken.None);
        var defaultResult = await ActionHostTrustedPolicy.MaterializeAsync(
            defaultRequest,
            ScriptedObjectTransport.Valid(
                Config("sticky", null),
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(".github/custom-policy.json",
            result.Policy!.ConfigPath);
        Assert.Equal("commit:" + request!.WorkflowCommitSha,
            transport.Calls[0]);
        Assert.DoesNotContain(transport.Calls, call =>
            call.Contains(ActionHostAuthorizationScenario.HeadSha,
                StringComparison.Ordinal));
        Assert.NotEqual(defaultResult.Policy!.PolicySha256,
            result.Policy.PolicySha256);
    }

    [Fact]
    public async Task DeepContractValidPathsMaterializeBeyondPriorRequestLimit()
    {
        var configPath = DeepPath("policy", "config.json", 20);
        var instructionsPath = DeepPath(
            ".github/agentic-pr-review",
            "instructions.md",
            20);
        var (_, scenario) = await Request();
        var customLaunch = CloneLaunch(
            scenario.Launch,
            configPath: configPath);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            customLaunch,
            CancellationToken.None);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            customLaunch,
            authorization.Invocation,
            out var request,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);
        var config = Config("sticky", null, instructionsPath);
        var instructions = Encoding.UTF8.GetBytes("instructions");
        var transport = ScriptedObjectTransport.ValidAtPaths(
            configPath,
            config,
            instructionsPath,
            instructions);

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request!,
            transport,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(configPath, result.Policy!.ConfigPath);
        Assert.Equal(instructionsPath, result.Policy.InstructionsPath);
        Assert.True(transport.Calls.Count > 32);
    }

    [Theory]
    [InlineData("sticky", "high", "Sticky", "High")]
    [InlineData("sticky", "critical", "Sticky", "Critical")]
    [InlineData("sticky_and_inline", "high",
        "StickyAndInline", "High")]
    [InlineData("sticky_and_inline", "critical",
        "StickyAndInline", "Critical")]
    public async Task AcceptedPublicationMatrixIsClosed(
        string mode,
        string severity,
        string expectedMode,
        string expectedSeverity)
    {
        var (request, _) = await Request();
        var transport = ScriptedObjectTransport.Valid(
            Config(mode, severity),
            Encoding.UTF8.GetBytes("instructions"));

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);

        Assert.Equal(expectedMode, result.Policy!.PublicationMode.ToString());
        Assert.Equal(expectedSeverity,
            result.Policy.InlineMinSeverity.ToString());
    }

    public static TheoryData<string> InvalidConfigs => new()
    {
        "{}",
        "{\"schema\":null,\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"provider\":\"other\"}",
        "{\"schema\":\"agentic-pr-review.config.v2\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"Sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\",\"inlineMinSeverity\":null}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\",\"model\":\"x\"}}",
        "{\"Schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":1,\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":1,\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":\"sticky\"}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":1}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\",\"inlineMinSeverity\":1}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"model\":\"deepseek-v4-flash\"}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"endpoint\":\"https://example.test\"}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"tools\":[]}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"budget\":1}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"retention\":1}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"secretName\":\"KEY\"}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"rawResponse\":true}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"stateMigration\":true}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"},\"payload\":\"${ENV}\"}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}{}",
        "{/*comment*/\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\"}}",
        "{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"sticky\",}}",
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public async Task ClosedJsonRejectsMalformedDuplicateUnknownAndForbidden(
        string json)
    {
        var (request, _) = await Request();
        var transport = ScriptedObjectTransport.Valid(
            Encoding.UTF8.GetBytes(json),
            Encoding.UTF8.GetBytes("instructions"));

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure, new[]
        {
            ActionHostTrustedPolicyFailure.MalformedConfig,
            ActionHostTrustedPolicyFailure.InvalidInstructionsPath,
        });
        Assert.DoesNotContain(transport.Calls,
            call => call == "blob:" + ScriptedObjectTransport.InstructionsBlob);
    }

    public static TheoryData<string> InvalidInstructionPaths => new()
    {
        "/.github/agentic-pr-review/instructions.md",
        "C:/instructions.md",
        "https://example.test/instructions.md",
        ".github\\agentic-pr-review\\instructions.md",
        ".github//agentic-pr-review/instructions.md",
        ".github/agentic-pr-review/../instructions.md",
        ".github/agentic-pr-review-other/instructions.md",
        ".github/agentic-pr-review/",
    };

    [Theory]
    [MemberData(nameof(InvalidInstructionPaths))]
    public async Task InstructionPathMustBeCanonicalTrustedSubtree(string path)
    {
        var (request, _) = await Request();
        var transport = ScriptedObjectTransport.Valid(
            Config("sticky", null, path),
            Encoding.UTF8.GetBytes("instructions"));

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);

        Assert.Equal(ActionHostTrustedPolicyFailure.InvalidInstructionsPath,
            result.Failure);
    }

    [Theory]
    [InlineData("120000", "blob", false)]
    [InlineData("160000", "commit", false)]
    [InlineData("040000", "tree", false)]
    [InlineData("120000", "blob", true)]
    public async Task SymlinkSubmoduleDirectoryAndIntermediateSymlinkFail(
        string mode,
        string type,
        bool intermediate)
    {
        var (request, _) = await Request();
        var transport = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        if (intermediate)
        {
            transport.Trees[ScriptedObjectTransport.RootTree] = new(
                ScriptedObjectTransport.RootTree,
                [new(".github", mode, type,
                    ScriptedObjectTransport.GitHubTree)]);
        }
        else
        {
            var tree = transport.Trees[ScriptedObjectTransport.InstructionsTree];
            transport.Trees[ScriptedObjectTransport.InstructionsTree] = new(
                tree.Sha,
                [new("instructions.md", mode, type,
                    ScriptedObjectTransport.InstructionsBlob)]);
        }

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);

        Assert.Equal(ActionHostTrustedPolicyFailure.SourceNonRegular,
            result.Failure);
    }

    [Fact]
    public async Task ExactContentCapsAreInclusiveAndPlusOneFails()
    {
        var (request, _) = await Request();
        var baseConfig = Config("sticky", null);
        byte[] paddedConfig = [.. baseConfig,
            .. Enumerable.Repeat((byte)' ', 16 * 1024 - baseConfig.Length)];
        var exactInstructions = Enumerable.Repeat((byte)'x', 64 * 1024)
            .ToArray();
        var acceptedTransport = ScriptedObjectTransport.Valid(
            paddedConfig,
            exactInstructions);

        var accepted = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            acceptedTransport,
            CancellationToken.None);

        Assert.True(accepted.Succeeded);
        Assert.Equal(16 * 1024, accepted.Policy!.ConfigBytes.Length);
        Assert.Equal(64 * 1024, accepted.Policy.InstructionBytes.Length);

        byte[] oversizedConfig = [.. paddedConfig, (byte)' '];
        var rejectedTransport = ScriptedObjectTransport.Valid(
            oversizedConfig,
            exactInstructions);
        var rejected = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            rejectedTransport,
            CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.SourceIncomplete,
            rejected.Failure);

        var oversizedInstructions = new byte[exactInstructions.Length + 1];
        Array.Fill<byte>(oversizedInstructions, (byte)'y');
        var rejectedInstructions =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                ScriptedObjectTransport.Valid(
                    baseConfig,
                    oversizedInstructions),
                CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.SourceIncomplete,
            rejectedInstructions.Failure);
    }

    [Fact]
    public async Task ConfigAlsoRejectsBomInvalidUtf8AndEmptyBytes()
    {
        var (request, _) = await Request();
        var cases = new[]
        {
            new byte[] { 0xef, 0xbb, 0xbf, (byte)'{' , (byte)'}' },
            new byte[] { 0xc3, 0x28 },
            Array.Empty<byte>(),
        };

        foreach (var config in cases)
        {
            var result = await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                ScriptedObjectTransport.Valid(
                    config,
                    Encoding.UTF8.GetBytes("instructions")),
                CancellationToken.None);
            Assert.Equal(ActionHostTrustedPolicyFailure.MalformedConfig,
                result.Failure);
        }
    }

    [Fact]
    public async Task Utf8BomInvalidBytesAndEmptyInstructionsFailClosed()
    {
        var (request, _) = await Request();
        var cases = new[]
        {
            new byte[] { 0xef, 0xbb, 0xbf, (byte)'x' },
            new byte[] { 0xc3, 0x28 },
            Array.Empty<byte>(),
        };

        foreach (var instructions in cases)
        {
            var result = await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                ScriptedObjectTransport.Valid(
                    Config("sticky", null),
                    instructions),
                CancellationToken.None);
            Assert.Equal(
                ActionHostTrustedPolicyFailure.MalformedInstructions,
                result.Failure);
        }
    }

    [Fact]
    public async Task PolicyIdentityIsDeterministicAndDistinctFromInstructions()
    {
        var (request, _) = await Request();
        var instructions = Encoding.UTF8.GetBytes("same instructions");
        var firstConfig = Config("sticky", null);
        byte[] secondConfig = [.. firstConfig, (byte)' '];

        var first = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            ScriptedObjectTransport.Valid(firstConfig, instructions),
            CancellationToken.None);
        var repeated = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            ScriptedObjectTransport.Valid(firstConfig, instructions),
            CancellationToken.None);
        var changed = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            ScriptedObjectTransport.Valid(secondConfig, instructions),
            CancellationToken.None);
        var changedInstructions =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                ScriptedObjectTransport.Valid(
                    firstConfig,
                    Encoding.UTF8.GetBytes("same instructiont")),
                CancellationToken.None);
        var changedPublication =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                ScriptedObjectTransport.Valid(
                    Config("sticky_and_inline", "critical"),
                    instructions),
                CancellationToken.None);

        Assert.Equal(first.Policy!.PolicySha256,
            repeated.Policy!.PolicySha256);
        Assert.NotEqual(first.Policy.PolicySha256,
            changed.Policy!.PolicySha256);
        Assert.Equal(first.Policy.InstructionBytes.ToArray(),
            changed.Policy.InstructionBytes.ToArray());
        Assert.Equal(first.Policy.InstructionsSha256,
            changed.Policy.InstructionsSha256);
        Assert.NotEqual(first.Policy.PolicySha256,
            first.Policy.InstructionsSha256);
        Assert.NotEqual(first.Policy.PolicySha256,
            changedInstructions.Policy!.PolicySha256);
        Assert.NotEqual(first.Policy.PolicySha256,
            changedPublication.Policy!.PolicySha256);
    }

    [Fact]
    public async Task BuildAndPayloadInputsParticipateInPolicyIdentity()
    {
        var (original, scenario) = await Request();
        var changedLaunch = CloneLaunch(
            scenario.Launch,
            payloadSha256: new string('e', 64),
            buildDiscriminator: "build-identity-variant");
        var changedWorkflow = Encoding.UTF8.GetBytes(
            ActionHostTrustedWorkflowContract.Render(
                ActionHostAuthorizationScenario.ActionSha,
                new string('e', 64)));
        scenario.Transport.Source = new ActionHostGitHubWorkflowSourceFact(
            ActionHostAuthorizationPolicy.PrivilegedWorkflowPath,
            "r4-trusted-proof.yml",
            GitBlobSha(changedWorkflow),
            changedWorkflow);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            changedLaunch,
            CancellationToken.None);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            changedLaunch,
            authorization.Invocation,
            out var changed,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);

        var originalResult = await ActionHostTrustedPolicy.MaterializeAsync(
            original,
            ScriptedObjectTransport.Valid(
                Config("sticky", null),
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);
        var changedResult = await ActionHostTrustedPolicy.MaterializeAsync(
            changed!,
            ScriptedObjectTransport.Valid(
                Config("sticky", null),
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);

        Assert.NotEqual(originalResult.Policy!.PolicySha256,
            changedResult.Policy!.PolicySha256);
    }

    [Fact]
    public async Task TrustedV2UsesExactSourceForCrossBuildContinuity()
    {
        var firstRequest = await TrustedV2Request(new string('a', 64));
        var secondRequest = await TrustedV2Request(new string('b', 64));
        var first = await Materialize(firstRequest);
        var second = await Materialize(secondRequest);

        Assert.Equal(ActionHostPayloadContinuityMode.ExactSource,
            first.PayloadContinuityMode);
        Assert.Equal(new string('a', 64), first.PayloadSha256);
        Assert.Equal(new string('b', 64), second.PayloadSha256);
        Assert.NotEqual(first.PayloadSha256, second.PayloadSha256);
        Assert.Equal(first.PayloadContinuitySha256,
            second.PayloadContinuitySha256);
        Assert.Equal(first.PolicySha256, second.PolicySha256);
        Assert.Equal(
            AuthorizedAcceptedStateComposer.PayloadBuildIdentity(first),
            AuthorizedAcceptedStateComposer.PayloadBuildIdentity(second));
    }

    [Theory]
    [InlineData("action-source")]
    [InlineData("build-discriminator")]
    [InlineData("payload-source-commit")]
    [InlineData("payload-source-tree")]
    [InlineData("workflow-blob")]
    [InlineData("workflow-commit")]
    [InlineData("workflow-path")]
    public void ExactSourceContinuityBindsEveryStableProjectionField(
        string mutation)
    {
        var original = new ExactSourceProjection(
            new string('a', 40),
            "runtime-payload-v1",
            new string('b', 40),
            new string('c', 40),
            new string('d', 40),
            new string('b', 40),
            ".github/workflows/r4-trusted-proof.yml");
        var changed = mutation switch
        {
            "action-source" => original with
            {
                ActionSourceSha = new string('e', 40),
            },
            "build-discriminator" => original with
            {
                BuildDiscriminator = "runtime-payload-v2",
            },
            "payload-source-commit" => original with
            {
                PayloadSourceCommit = new string('e', 40),
            },
            "payload-source-tree" => original with
            {
                PayloadSourceTree = new string('e', 40),
            },
            "workflow-blob" => original with
            {
                WorkflowBlobSha = new string('e', 40),
            },
            "workflow-commit" => original with
            {
                WorkflowCommitSha = new string('e', 40),
            },
            "workflow-path" => original with
            {
                WorkflowPath = ".github/workflows/other.yml",
            },
            _ => throw new InvalidOperationException("Unknown mutation."),
        };

        Assert.NotEqual(Continuity(original), Continuity(changed));
    }

    [Fact]
    public async Task MissingAndInconsistentTrustedObjectsNeverFallback()
    {
        var (request, _) = await Request();
        var missing = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        missing.Trees[ScriptedObjectTransport.GitHubTree] = new(
            ScriptedObjectTransport.GitHubTree,
        [
            new("agentic-pr-review", "040000", "tree",
                ScriptedObjectTransport.InstructionsTree),
        ]);

        var missingResult = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            missing,
            CancellationToken.None);

        Assert.Equal(ActionHostTrustedPolicyFailure.SourceMissing,
            missingResult.Failure);
        Assert.DoesNotContain(missing.Calls,
            call => call.StartsWith("blob:", StringComparison.Ordinal));

        var inconsistent = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        inconsistent.Blobs[ScriptedObjectTransport.ConfigBlob] = new(
            new string('9', 40),
            Config("sticky", null));
        var inconsistentResult =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                inconsistent,
                CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.SourceIdentityMismatch,
            inconsistentResult.Failure);
        Assert.DoesNotContain(inconsistent.Calls, call =>
            call.Contains(ActionHostAuthorizationScenario.HeadSha,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaterializedBytesAreSnapshotsAndDiagnosticsAreValueFree()
    {
        var (request, _) = await Request();
        var config = Config("sticky", null);
        var instructions = Encoding.UTF8.GetBytes(
            "sensitive-instruction-canary");
        var expectedConfig = (byte[])config.Clone();
        var expectedInstructions = (byte[])instructions.Clone();
        var transport = ScriptedObjectTransport.Valid(config, instructions);

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);
        transport.Blobs[ScriptedObjectTransport.ConfigBlob].Bytes[0] ^= 1;
        transport.Blobs[ScriptedObjectTransport.InstructionsBlob].Bytes[0]
            ^= 1;

        Assert.Equal(expectedConfig, result.Policy!.ConfigBytes.ToArray());
        Assert.Equal(expectedInstructions,
            result.Policy.InstructionBytes.ToArray());
        Assert.DoesNotContain("sensitive-instruction-canary",
            result.Policy.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(request.RepositoryName,
            request.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-instruction-canary",
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateAndUnexpectedTransportFailuresFailClosed()
    {
        var (request, _) = await Request();
        var aggregate = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        aggregate.CapturedResponseBytes = 8 * 1024 * 1024 + 1;

        var aggregateResult =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                aggregate,
                CancellationToken.None);

        Assert.Equal(ActionHostTrustedPolicyFailure.AggregateLimit,
            aggregateResult.Failure);

        var unexpected = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        unexpected.Throw = new InvalidOperationException("canary");
        var unexpectedResult =
            await ActionHostTrustedPolicy.MaterializeAsync(
                request,
                unexpected,
                CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.InternalInvariant,
            unexpectedResult.Failure);
    }

    [Fact]
    public async Task CrossPairedLaunchFailsBindingBeforeAnyRead()
    {
        var (request, scenario) = await Request();
        Assert.NotNull(request);
        var mismatched = CloneLaunch(
            scenario.Launch,
            workflowSha: new string('9', 40));
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        var bound = ActionHostTrustedPolicyRequest.TryBind(
            mismatched,
            authorization.Invocation,
            out var rejected,
            out var failure);

        Assert.False(bound);
        Assert.Null(rejected);
        Assert.Equal(ActionHostTrustedPolicyFailure.AuthorityMismatch,
            failure);
    }

    [Fact]
    public async Task SourceIdentityCredentialTransportAndCancellationFailuresClose()
    {
        var (request, _) = await Request();
        var mismatch = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        mismatch.Commit = new(new string('9', 40),
            ScriptedObjectTransport.RootTree);
        var mismatched = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            mismatch,
            CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.SourceIdentityMismatch,
            mismatched.Failure);

        var denied = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        denied.Failure = ActionHostGitObjectFailure.Forbidden;
        var deniedResult = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            denied,
            CancellationToken.None);
        Assert.Equal(ActionHostTrustedPolicyFailure.CredentialDenied,
            deniedResult.Failure);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            ScriptedObjectTransport.Valid(
                Config("sticky", null),
                Encoding.UTF8.GetBytes("instructions")),
            cancelled.Token);
        Assert.Equal(ActionHostTrustedPolicyFailure.Cancelled,
            cancelledResult.Failure);
    }

    private static async Task<(ActionHostTrustedPolicyRequest Request,
        ActionHostAuthorizationScenario Scenario)> Request()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        Assert.NotNull(authorization.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            scenario.Launch,
            authorization.Invocation,
            out var request,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);
        return (request!, scenario);
    }

    private static async Task<ActionHostTrustedPolicyRequest> TrustedV2Request(
        string payloadSha256,
        string? buildDiscriminator = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var source = TrustedProofPayloadBuildIdentity.SourceCommit;
        var workflowBytes = Encoding.UTF8.GetBytes(
            TrustedProofV2WorkflowAdmission.Render(source));
        scenario.Transport.Source = scenario.Transport.Source with
        {
            BlobSha = GitBlobSha(workflowBytes),
            Bytes = workflowBytes,
        };
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            HeadSha = source,
        };
        var launch = CloneLaunch(
            scenario.Launch,
            workflowSha: source,
            actionSourceSha: source,
            payloadSha256: payloadSha256,
            buildDiscriminator: buildDiscriminator);
        var authorizer = new ActionHostAuthorizer(
            scenario.EventReader,
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof,
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance);
        var authorization = await authorizer.AuthorizeAsync(
            launch,
            CancellationToken.None);
        Assert.NotNull(authorization.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            authorization.Invocation,
            out var request,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);
        return request!;
    }

    private static async Task<ActionHostTrustedPolicy> Materialize(
        ActionHostTrustedPolicyRequest request)
    {
        var transport = ScriptedObjectTransport.Valid(
            Config("sticky", null),
            Encoding.UTF8.GetBytes("instructions"));
        transport.Commit = transport.Commit with
        {
            Sha = request.WorkflowCommitSha,
        };
        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request,
            transport,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Failure.ToString());
        return result.Policy!;
    }

    private static string Continuity(ExactSourceProjection value) =>
        ActionHostTrustedPolicyRequest.ComputeExactSourceContinuitySha256(
            value.ActionSourceSha,
            value.BuildDiscriminator,
            value.PayloadSourceCommit,
            value.PayloadSourceTree,
            value.WorkflowBlobSha,
            value.WorkflowCommitSha,
            value.WorkflowPath);

    internal static byte[] Config(
        string mode,
        string? inlineSeverity,
        string instructionsPath =
            ".github/agentic-pr-review/instructions.md")
    {
        var inline = inlineSeverity is null
            ? string.Empty
            : ",\"inlineMinSeverity\":\"" + inlineSeverity + "\"";
        return Encoding.UTF8.GetBytes(
            "{\"schema\":\"agentic-pr-review.config.v1\"," +
            "\"instructionsPath\":\"" + instructionsPath
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"," +
            "\"publication\":{\"mode\":\"" + mode + "\"" +
            inline + "}}");
    }

    private static string DeepPath(
        string prefix,
        string fileName,
        int segmentCount)
    {
        var prefixSegments = prefix.Split('/');
        Assert.True(segmentCount > prefixSegments.Length);
        return string.Join('/',
        [
            .. prefixSegments,
            .. Enumerable.Repeat(
                "d",
                segmentCount - prefixSegments.Length - 1),
            fileName,
        ]);
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static ActionHostLaunchContract CloneLaunch(
        ActionHostLaunchContract launch,
        string? workflowSha = null,
        string? actionSourceSha = null,
        string? configPath = null,
        string? payloadSha256 = null,
        string? buildDiscriminator = null)
    {
        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            launch.Inputs.ProviderApiKey,
            launch.Inputs.StateKey,
            launch.Inputs.PreviousStateKey,
            configPath ?? launch.Inputs.ConfigPath,
            launch.Inputs.PullRequestNumber,
            launch.Inputs.StateMode,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            launch.RunId,
            launch.RunAttempt,
            launch.WorkflowPath,
            launch.WorkflowRef,
            workflowSha ?? launch.WorkflowSha,
            actionSourceSha ?? launch.ActionSourceSha,
            payloadSha256 ?? launch.PayloadSha256,
            buildDiscriminator ?? launch.BuildDiscriminator,
            launch.Cancellation,
            launch.ArtifactBridgeEndpoint,
            out var clone));
        return clone!;
    }

    private sealed record ExactSourceProjection(
        string ActionSourceSha,
        string BuildDiscriminator,
        string PayloadSourceCommit,
        string PayloadSourceTree,
        string WorkflowBlobSha,
        string WorkflowCommitSha,
        string WorkflowPath);

    internal sealed class ScriptedObjectTransport :
        IActionHostGitObjectTransport
    {
        internal static readonly string RootTree = new('1', 40);
        internal static readonly string GitHubTree = new('2', 40);
        internal static readonly string InstructionsTree = new('3', 40);
        internal static readonly string ConfigBlob = new('4', 40);
        internal static readonly string InstructionsBlob = new('5', 40);

        internal required ActionHostGitCommitObject Commit { get; set; }
        internal Dictionary<string, ActionHostGitTreeObject> Trees { get; } =
            new(StringComparer.Ordinal);
        internal Dictionary<string, ActionHostGitBlobObject> Blobs { get; } =
            new(StringComparer.Ordinal);
        internal ActionHostGitObjectFailure Failure { get; set; }
        internal int CapturedResponseBytes { get; set; } = 100;
        internal Exception? Throw { get; set; }
        internal List<string> Calls { get; } = [];

        internal static ScriptedObjectTransport Valid(
            byte[] config,
            byte[] instructions)
        {
            var transport = new ScriptedObjectTransport
            {
                Commit = new(
                    ActionHostAuthorizationScenario.WorkflowSha,
                    RootTree),
            };
            transport.Trees[RootTree] = new(RootTree,
                [new(".github", "040000", "tree", GitHubTree)]);
            transport.Trees[GitHubTree] = new(GitHubTree,
            [
                new("agentic-pr-review.json", "100644", "blob", ConfigBlob),
                new("agentic-pr-review", "040000", "tree",
                    InstructionsTree),
            ]);
            transport.Trees[InstructionsTree] = new(InstructionsTree,
                [new("instructions.md", "100644", "blob",
                    InstructionsBlob)]);
            transport.Blobs[ConfigBlob] = new(ConfigBlob,
                (byte[])config.Clone());
            transport.Blobs[InstructionsBlob] = new(InstructionsBlob,
                (byte[])instructions.Clone());
            return transport;
        }

        internal static ScriptedObjectTransport ValidAtPaths(
            string configPath,
            byte[] config,
            string instructionsPath,
            byte[] instructions)
        {
            var transport = new ScriptedObjectTransport
            {
                Commit = new(
                    ActionHostAuthorizationScenario.WorkflowSha,
                    RootTree),
            };
            transport.Trees[RootTree] = new(RootTree, []);
            transport.AddRegularBlobPath(
                configPath,
                ConfigBlob,
                config);
            transport.AddRegularBlobPath(
                instructionsPath,
                InstructionsBlob,
                instructions);
            return transport;
        }

        private void AddRegularBlobPath(
            string path,
            string blobSha,
            byte[] bytes)
        {
            var segments = path.Split('/');
            var treeSha = RootTree;
            var prefix = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                prefix = prefix.Length == 0
                    ? segments[index]
                    : prefix + "/" + segments[index];
                var childSha = ObjectSha("tree:" + prefix);
                var tree = Trees[treeSha];
                var existing = tree.Entries.SingleOrDefault(entry =>
                    StringComparer.Ordinal.Equals(
                        entry.Path,
                        segments[index]));
                if (existing is null)
                {
                    Trees[treeSha] = new(
                        tree.Sha,
                        [
                            .. tree.Entries,
                            new(
                                segments[index],
                                "040000",
                                "tree",
                                childSha),
                        ]);
                    Trees[childSha] = new(childSha, []);
                }
                else
                {
                    Assert.Equal("040000", existing.Mode);
                    Assert.Equal("tree", existing.Type);
                    childSha = existing.Sha;
                }

                treeSha = childSha;
            }

            var parent = Trees[treeSha];
            Trees[treeSha] = new(
                parent.Sha,
                [
                    .. parent.Entries,
                    new(
                        segments[^1],
                        "100644",
                        "blob",
                        blobSha),
                ]);
            Blobs[blobSha] = new(blobSha, (byte[])bytes.Clone());
        }

        private static string ObjectSha(string value) =>
            Convert.ToHexStringLower(
                SHA1.HashData(Encoding.UTF8.GetBytes(value)));

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add("commit:" + commitSha);
            return Task.FromResult(Result(Commit));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add("tree:" + treeSha);
            return Task.FromResult(Failure == ActionHostGitObjectFailure.None &&
                Trees.TryGetValue(treeSha, out var value)
                ? ActionHostGitObjectResult<ActionHostGitTreeObject>.Success(
                    value,
                    CapturedResponseBytes)
                : ActionHostGitObjectResult<ActionHostGitTreeObject>.Failed(
                    Failure == ActionHostGitObjectFailure.None
                        ? ActionHostGitObjectFailure.NotFound
                        : Failure));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add("blob:" + blobSha);
            return Task.FromResult(Failure == ActionHostGitObjectFailure.None &&
                Blobs.TryGetValue(blobSha, out var value)
                ? ActionHostGitObjectResult<ActionHostGitBlobObject>.Success(
                    value,
                    CapturedResponseBytes)
                : ActionHostGitObjectResult<ActionHostGitBlobObject>.Failed(
                    Failure == ActionHostGitObjectFailure.None
                        ? ActionHostGitObjectFailure.NotFound
                        : Failure));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<ActionHostGitArchiveReader>
                .Failed(ActionHostGitObjectFailure.InvalidRequest));

        public void Dispose() { }

        private ActionHostGitObjectResult<ActionHostGitCommitObject> Result(
            ActionHostGitCommitObject value) =>
            Failure == ActionHostGitObjectFailure.None
                ? ActionHostGitObjectResult<ActionHostGitCommitObject>
                    .Success(value, CapturedResponseBytes)
                : ActionHostGitObjectResult<ActionHostGitCommitObject>
                    .Failed(Failure);
    }
}
