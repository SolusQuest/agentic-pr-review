using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostSerializationTests
{
    private const string Build = "r4-h1";

    [Theory]
    [InlineData("launch-js-safe-boundary.json", 9007199254740991L)]
    [InlineData("launch-js-unsafe-odd.json", 9007199254740992L)]
    [InlineData("launch-int64-max.json", long.MaxValue)]
    public void LiteralNodeCompatibleFixturesPreserveInt64Identifiers(
        string fixture,
        long expectedRepositoryId)
    {
        var bytes = File.ReadAllBytes(FixturePath(fixture));

        Assert.True(ActionHostJsonCodec.TryReadLaunch(
            bytes,
            out var launch,
            out var error));
        Assert.Equal(ActionHostContractError.None, error);
        Assert.Equal(expectedRepositoryId, launch!.RepositoryId);
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var first));
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var second));
        Assert.Equal(first, second);
        Assert.True(ActionHostJsonCodec.TryReadLaunch(
            first,
            out var roundTripped,
            out _));
        Assert.Equal(launch.RunId, roundTripped!.RunId);
        Assert.Equal(launch.Inputs.PullRequestNumber,
            roundTripped.Inputs.PullRequestNumber);
    }

    [Fact]
    public void ReorderedEscapedNonAsciiAndControlBearingSecretsAreAccepted()
    {
        var bytes = File.ReadAllBytes(FixturePath("launch-js-unsafe-odd.json"));

        Assert.True(ActionHostJsonCodec.TryReadLaunch(
            bytes,
            out var launch,
            out _));
        Assert.Equal(9007199254740993L, launch!.RunId);
        Assert.Equal(9007199254740993L,
            launch.Inputs.PullRequestNumber);
        Assert.Equal(ActionHostCancellationState.Requested,
            launch.Cancellation);
        Assert.Equal("line1\0line2",
            launch.Inputs.StateKey!.ExportForPrivateLaunch());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    public void LaunchRejectsNonCanonicalIdentifierStrings(string invalid)
    {
        var json = File.ReadAllText(FixturePath(
            "launch-js-safe-boundary.json"));
        json = json.Replace(
            "\"repository_id\": \"9007199254740991\"",
            $"\"repository_id\": \"{invalid}\"",
            StringComparison.Ordinal);

        AssertInvalidLaunch(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void LaunchRejectsClosedShapeUtf8AndOuterBoundViolations()
    {
        var json = File.ReadAllText(FixturePath(
            "launch-js-safe-boundary.json"));
        var cases = new[]
        {
            json[..^2] + ",\"run_id\":\"1\"}\n",
            json.Replace(
                "\"state_mode\": \"auto\"",
                "\"state_mode\": \"auto\", \"state_mode\": \"auto\"",
                StringComparison.Ordinal),
            json[..^2] + ",\"unknown\":null}\n",
            json.Replace(
                "\"state_mode\": \"auto\"",
                "\"state_mode\": \"auto\", \"unknown\": null",
                StringComparison.Ordinal),
            json.Replace(
                "\"run_id\"",
                "\"RunId\"",
                StringComparison.Ordinal),
            json + "x",
        };

        Assert.All(cases, value =>
            AssertInvalidLaunch(Encoding.UTF8.GetBytes(value)));
        AssertInvalidLaunch([0xff]);
        AssertInvalidLaunch([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(json)]);
        var baseBytes = Encoding.UTF8.GetBytes(json);
        var atCap = new byte[ActionHostContractBounds.MaximumLaunchDocumentBytes];
        baseBytes.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', baseBytes.Length,
            atCap.Length - baseBytes.Length);
        Assert.True(ActionHostJsonCodec.TryReadLaunch(atCap, out _, out _));
        AssertInvalidLaunch(new byte[atCap.Length + 1]);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void StructuralTextBoundsAcceptAtCapAndRejectCapPlusOne(
        int maximumBytes)
    {
        Assert.True(ActionHostContractValidation.IsBoundedPrivateStructure(
            new string('x', maximumBytes),
            maximumBytes));
        Assert.False(ActionHostContractValidation.IsBoundedPrivateStructure(
            new string('x', maximumBytes + 1),
            maximumBytes));
        Assert.False(ActionHostContractValidation.IsBoundedPrivateStructure(
            "x\n",
            maximumBytes));
    }

    [Fact]
    public void RepositoryHashAndBuildGrammarsCoverTheirExactBounds()
    {
        Assert.True(ActionHostContractValidation.IsRepositoryName("a/b"));
        Assert.True(ActionHostContractValidation.IsRepositoryName(
            new string('a', 254) + "/b"));
        Assert.False(ActionHostContractValidation.IsRepositoryName(
            new string('a', 255) + "/b"));
        Assert.False(ActionHostContractValidation.IsRepositoryName("a/b/c"));

        Assert.True(ActionHostContractValidation.IsCommitSha(new string('a', 40)));
        Assert.False(ActionHostContractValidation.IsCommitSha(new string('a', 39)));
        Assert.False(ActionHostContractValidation.IsCommitSha(new string('A', 40)));
        Assert.True(ActionHostContractValidation.IsSha256(new string('f', 64)));
        Assert.False(ActionHostContractValidation.IsSha256(new string('f', 65)));

        Assert.True(ActionHostContractValidation.IsBuildDiscriminator("a"));
        Assert.True(ActionHostContractValidation.IsBuildDiscriminator(
            "a" + new string('-', 127)));
        Assert.False(ActionHostContractValidation.IsBuildDiscriminator(
            "a" + new string('-', 128)));
        Assert.False(ActionHostContractValidation.IsBuildDiscriminator("-build"));
    }

    [Fact]
    public void IdentifierParsersPreserveUnsafeJavaScriptIntegers()
    {
        foreach (var value in new[]
        {
            "9007199254740991",
            "9007199254740992",
            "9007199254740993",
            "9223372036854775807",
        })
        {
            Assert.True(ActionHostContractValidation.TryParsePositiveInt64(
                value,
                out var parsed));
            Assert.Equal(value,
                ActionHostContractValidation.CanonicalDecimal(parsed));
        }

        Assert.True(ActionHostContractValidation.TryParsePositiveInt32(
            "2147483647",
            out var runAttempt));
        Assert.Equal(int.MaxValue, runAttempt);
        Assert.False(ActionHostContractValidation.TryParsePositiveInt32(
            "2147483648",
            out _));
        Assert.False(ActionHostContractValidation.TryParsePositiveInt32(
            "0",
            out _));
    }

    [Fact]
    public void LaunchFactoryRejectsInvalidFactsWithoutSideEffects()
    {
        Assert.True(ActionHostInputParser.TryParse([], out var inputs, out _));
        var valid = CreateLaunch(inputs!);
        Assert.NotNull(valid);

        Assert.False(ActionHostLaunchContract.TryCreate(
            inputs,
            "/runner/event.json",
            new string('a', 64),
            "owner/repo",
            0,
            1,
            1,
            ".github/workflows/review.yml",
            "refs/heads/main",
            new string('b', 40),
            new string('c', 40),
            new string('d', 64),
            Build,
            ActionHostCancellationState.Active,
            "/runner/bridge.sock",
            out _));
        Assert.False(ActionHostLaunchContract.TryCreate(
            inputs,
            "/runner/event.json\npoison",
            new string('a', 64),
            "owner/repo",
            1,
            1,
            1,
            ".github/workflows/review.yml",
            "refs/heads/main",
            new string('b', 40),
            new string('c', 40),
            new string('d', 64),
            Build,
            ActionHostCancellationState.Active,
            "/runner/bridge.sock",
            out _));
    }

    [Fact]
    public void NoCommandRootsAreBoundedStrictAndBuildBound()
    {
        Assert.True(ActionHostJsonCodec.TryWriteNoPrivateCommand(
            Build,
            out var request));
        Assert.True(ActionHostJsonCodec.TryReadNoPrivateCommand(request, Build));
        Assert.False(ActionHostJsonCodec.TryReadNoPrivateCommand(
            request,
            "different-build"));
        Assert.False(ActionHostJsonCodec.TryReadNoPrivateCommand(
            Encoding.UTF8.GetBytes(
                "{\"build_discriminator\":\"r4-h1\",\"payload\":{}}"),
            Build));
        Assert.False(ActionHostJsonCodec.TryReadNoPrivateCommand(
            Encoding.UTF8.GetBytes(
                "{\"build_discriminator\":\"r4-h1\",\"payload\":null," +
                "\"state_key\":\"prohibited\"}"),
            Build));
        Assert.False(ActionHostJsonCodec.TryReadNoPrivateCommand(
            Encoding.UTF8.GetBytes(
                "{\"build_discriminator\":\"r4-h1\"," +
                "\"build_discriminator\":\"r4-h1\",\"payload\":null}"),
            Build));

        Assert.True(ActionHostJsonCodec.TryWriteNoPrivateCommandResult(
            Build,
            out var result));
        Assert.True(ActionHostJsonCodec.TryReadNoPrivateCommandResult(
            result,
            Build));
        var atCap = new byte[
            ActionHostContractBounds.MaximumPrivateCommandDocumentBytes];
        request.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', request.Length,
            atCap.Length - request.Length);
        Assert.True(ActionHostJsonCodec.TryReadNoPrivateCommand(atCap, Build));
        Assert.False(ActionHostJsonCodec.TryReadNoPrivateCommandResult(
            new byte[atCap.Length + 1],
            Build));
    }

    [Fact]
    public void SeparateConsumerContextCarriesTypedRequestAndResultPayloads()
    {
        var commandInfo = RequireTypeInfo<
            ActionHostPrivateCommandEnvelope<TestPrivateCommand>>(
            ActionHostPrivateCommandTestJsonContext.Default);
        var resultInfo = RequireTypeInfo<
            ActionHostPrivateCommandResultEnvelope<TestPrivateResult>>(
            ActionHostPrivateCommandTestJsonContext.Default);
        var command = new ActionHostPrivateCommandEnvelope<TestPrivateCommand>(
            Build,
            new TestPrivateCommand("read", "artifact/receipt.json"));
        var result = new ActionHostPrivateCommandResultEnvelope<TestPrivateResult>(
            Build,
            new TestPrivateResult("accepted", new string('a', 64)));

        Assert.True(ActionHostPrivateCommandCodec.TryWriteCommand(
            command,
            Build,
            commandInfo,
            out var commandBytes));
        Assert.True(ActionHostPrivateCommandCodec.TryReadCommand(
            commandBytes,
            Build,
            commandInfo,
            out var parsedCommand));
        Assert.Equal("read", parsedCommand!.Payload!.Operation);

        Assert.True(ActionHostPrivateCommandCodec.TryWriteResult(
            result,
            Build,
            resultInfo,
            out var resultBytes));
        Assert.True(ActionHostPrivateCommandCodec.TryReadResult(
            resultBytes,
            Build,
            resultInfo,
            out var parsedResult));
        Assert.Equal("accepted", parsedResult!.Payload!.Disposition);
        Assert.Equal(ActionHostPrivacyClass.PrivateLaunch,
            ((IActionHostPrivateCommandDocument)command.Payload!).Privacy);
        Assert.Equal(ActionHostPrivacyClass.PrivateLaunch,
            ((IActionHostPrivateCommandResultDocument)result.Payload!).Privacy);
        Assert.Equal(ActionHostPrivacyClass.PrivateLaunch, command.Privacy);
        Assert.Equal(ActionHostPrivacyClass.PrivateLaunch, result.Privacy);

        var tooLarge = new ActionHostPrivateCommandEnvelope<TestPrivateCommand>(
            Build,
            new TestPrivateCommand("read", new string('x',
                ActionHostContractBounds.MaximumPrivateCommandDocumentBytes)));
        Assert.False(ActionHostPrivateCommandCodec.TryWriteCommand(
            tooLarge,
            Build,
            commandInfo,
            out _));

        var poisoned = Encoding.UTF8.GetBytes(
            "{\"build_discriminator\":\"r4-h1\",\"payload\":{" +
            "\"operation\":\"read\",\"path\":\"artifact\"," +
            "\"provider_content\":{}}}");
        Assert.False(ActionHostPrivateCommandCodec.TryReadCommand(
            poisoned,
            Build,
            commandInfo,
            out _));
    }

    private static ActionHostLaunchContract CreateLaunch(ActionHostInputs inputs)
    {
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            "/runner/event.json",
            new string('a', 64),
            "owner/repo",
            1,
            2,
            3,
            ".github/workflows/review.yml",
            "refs/heads/main",
            new string('b', 40),
            new string('c', 40),
            new string('d', 64),
            Build,
            ActionHostCancellationState.Active,
            "/runner/bridge.sock",
            out var launch));
        return launch!;
    }

    private static void AssertInvalidLaunch(byte[] bytes)
    {
        Assert.False(ActionHostJsonCodec.TryReadLaunch(
            bytes,
            out var launch,
            out var error));
        Assert.Null(launch);
        Assert.Equal(ActionHostContractError.InvalidLaunch, error);
        Assert.Equal("invalid_launch", ActionHostErrorCode.Contract(error));
    }

    private static string FixturePath(
        string name,
        [CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", name);

    private static JsonTypeInfo<T> RequireTypeInfo<T>(
        JsonSerializerContext context) =>
        Assert.IsType<JsonTypeInfo<T>>(context.GetTypeInfo(typeof(T)));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestPrivateCommand(string Operation, string Path)
    : IActionHostPrivateCommandDocument;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestPrivateResult(string Disposition, string Sha256)
    : IActionHostPrivateCommandResultDocument;

[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<TestPrivateCommand>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandResultEnvelope<TestPrivateResult>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class ActionHostPrivateCommandTestJsonContext
    : JsonSerializerContext;
