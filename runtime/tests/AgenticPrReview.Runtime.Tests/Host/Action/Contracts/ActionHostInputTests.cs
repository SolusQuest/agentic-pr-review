using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostInputTests
{
    [Fact]
    public void AllInputsPreserveTheirTypedMeaning()
    {
        var entries = new[]
        {
            new ActionHostRawInput("github-token", "github-secret"),
            new ActionHostRawInput("provider-api-key", "provider-secret"),
            new ActionHostRawInput("state-key", "state-secret"),
            new ActionHostRawInput("previous-state-key", "previous-secret"),
            new ActionHostRawInput("config-path", ".github/reviewer.yml"),
            new ActionHostRawInput("pr-number", "9223372036854775807"),
            new ActionHostRawInput("state-mode", "reset"),
        };

        Assert.True(ActionHostInputParser.TryParse(
            entries,
            out var inputs,
            out var error));
        Assert.Equal(ActionHostInputError.None, error);
        Assert.IsType<ActionHostGitHubToken>(inputs!.GitHubToken);
        Assert.IsType<ActionHostProviderApiKey>(inputs.ProviderApiKey);
        Assert.IsType<ActionHostStateKey>(inputs.StateKey);
        Assert.IsType<ActionHostPreviousStateKey>(inputs.PreviousStateKey);
        Assert.Equal(".github/reviewer.yml", inputs.ConfigPath);
        Assert.Equal(long.MaxValue, inputs.PullRequestNumber);
        Assert.Equal(ActionHostStateMode.Reset, inputs.StateMode);
        Assert.Equal(ActionHostPrivacyClass.PrivateLaunch, inputs.Privacy);
    }

    [Fact]
    public void MissingOptionalInputsDefaultOnlyStateMode()
    {
        Assert.True(ActionHostInputParser.TryParse(
            [],
            out var inputs,
            out var error));

        Assert.Equal(ActionHostInputError.None, error);
        Assert.Null(inputs!.GitHubToken);
        Assert.Null(inputs.ProviderApiKey);
        Assert.Null(inputs.StateKey);
        Assert.Null(inputs.PreviousStateKey);
        Assert.Null(inputs.ConfigPath);
        Assert.Null(inputs.PullRequestNumber);
        Assert.Equal(ActionHostStateMode.Auto, inputs.StateMode);
    }

    [Theory]
    [InlineData(null, "value", (int)ActionHostInputError.NameInvalid)]
    [InlineData("", "value", (int)ActionHostInputError.NameInvalid)]
    [InlineData("token", "value", (int)ActionHostInputError.UnknownName)]
    [InlineData("GitHub-token", "value", (int)ActionHostInputError.UnknownName)]
    [InlineData("github_token", "value", (int)ActionHostInputError.UnknownName)]
    [InlineData("github-token", null, (int)ActionHostInputError.ValueMissing)]
    [InlineData("github-token", "", (int)ActionHostInputError.ValueMissing)]
    [InlineData("state-mode", "AUTO", (int)ActionHostInputError.StateModeInvalid)]
    [InlineData("state-mode", "other", (int)ActionHostInputError.StateModeInvalid)]
    public void InvalidEntriesReturnOnlyFixedErrors(
        string? name,
        string? value,
        int expected)
    {
        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput(name, value)],
            out var inputs,
            out var error));

        Assert.Null(inputs);
        Assert.Equal((ActionHostInputError)expected, error);
    }

    [Fact]
    public void DuplicateAndCountEvidenceCannotBeMaterializedAway()
    {
        Assert.False(ActionHostInputParser.TryParse(
            [
                new ActionHostRawInput("github-token", "one"),
                new ActionHostRawInput("github-token", "two"),
            ],
            out _,
            out var duplicateError));
        Assert.Equal(ActionHostInputError.DuplicateName, duplicateError);

        var tooMany = Enumerable.Range(0, 8)
            .Select(_ => new ActionHostRawInput("github-token", "value"))
            .ToArray();
        Assert.False(ActionHostInputParser.TryParse(
            tooMany,
            out _,
            out var countError));
        Assert.Equal(ActionHostInputError.CountInvalid, countError);
    }

    [Fact]
    public void IndividualAndAggregateUtf8BoundsAreEnforced()
    {
        Assert.True(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("github-token", new string('x', 4096))],
            out _,
            out _));
        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("github-token", new string('x', 4097))],
            out _,
            out var individualError));
        Assert.Equal(ActionHostInputError.ValueBytesInvalid, individualError);

        var aggregate = new[]
        {
            new ActionHostRawInput("github-token", new string('a', 4096)),
            new ActionHostRawInput("provider-api-key", new string('b', 4096)),
            new ActionHostRawInput("state-key", new string('c', 4096)),
            new ActionHostRawInput("previous-state-key", new string('d', 4096)),
        };
        Assert.False(ActionHostInputParser.TryParse(
            aggregate,
            out _,
            out var aggregateError));
        Assert.Equal(
            ActionHostInputError.AggregateBytesInvalid,
            aggregateError);

        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("github-token", "\ud800")],
            out _,
            out var unicodeError));
        Assert.Equal(ActionHostInputError.ValueBytesInvalid, unicodeError);
    }

    [Theory]
    [InlineData("github-token")]
    [InlineData("provider-api-key")]
    [InlineData("state-key")]
    [InlineData("previous-state-key")]
    public void EverySecretChannelUsesTheSameOpaqueByteBounds(string name)
    {
        Assert.True(ActionHostInputParser.TryParse(
            [new ActionHostRawInput(name, new string('x', 4096))],
            out _,
            out _));
        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput(name, new string('x', 4097))],
            out _,
            out var error));
        Assert.Equal(ActionHostInputError.ValueBytesInvalid, error);
    }

    [Fact]
    public void ConfigPathAndRawNameBoundsAreExplicit()
    {
        Assert.True(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("config-path", new string('p', 1024))],
            out _,
            out _));
        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("config-path", new string('p', 1025))],
            out _,
            out var pathError));
        Assert.Equal(ActionHostInputError.ValueBytesInvalid, pathError);

        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput(new string('n', 65), "value")],
            out _,
            out var nameError));
        Assert.Equal(ActionHostInputError.NameInvalid, nameError);
    }

    [Fact]
    public void FixedInputFailureCodesContainNoRejectedEvidence()
    {
        var expected = new[]
        {
            "count_invalid",
            "aggregate_bytes_invalid",
            "name_invalid",
            "unknown_name",
            "duplicate_name",
            "value_missing",
            "value_bytes_invalid",
            "pr_number_invalid",
            "state_mode_invalid",
        };
        var errors = Enum.GetValues<ActionHostInputError>()
            .Where(error => error != ActionHostInputError.None)
            .ToArray();

        Assert.Equal(expected, errors.Select(ActionHostErrorCode.Input));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e1")]
    [InlineData("9223372036854775808")]
    public void PullRequestNumberRequiresCanonicalPositiveInt64(string value)
    {
        Assert.False(ActionHostInputParser.TryParse(
            [new ActionHostRawInput("pr-number", value)],
            out _,
            out var error));
        Assert.Equal(ActionHostInputError.PullRequestNumberInvalid, error);
    }

    [Fact]
    public void SecretContentIsOpaqueRedactedAndPreLaunchMasked()
    {
        var canary = "line1\r\n\0\u0085::error::%0A{\"exception\":\"C:\\\\secret\"}";
        Assert.True(ActionHostInputParser.TryParse(
            [
                new ActionHostRawInput("github-token", canary),
                new ActionHostRawInput("provider-api-key", canary),
                new ActionHostRawInput("state-key", canary),
                new ActionHostRawInput("previous-state-key", canary),
            ],
            out var inputs,
            out _));

        var secrets = new ActionHostOpaqueSecret?[]
        {
            inputs!.GitHubToken,
            inputs.ProviderApiKey,
            inputs.StateKey,
            inputs.PreviousStateKey,
        };
        Assert.All(secrets, secret =>
        {
            Assert.NotNull(secret);
            Assert.True(secret!.RequiresPreLaunchMasking);
            Assert.Equal(ActionHostPrivacyClass.Secret, secret.Privacy);
            Assert.Equal("[REDACTED]", secret.ToString());
            var preserved = StringComparer.Ordinal.Equals(
                canary,
                secret.ExportForPrivateLaunch());
            Assert.True(preserved);
        });

        var leaked = secrets.Any(secret =>
            secret!.ToString().Contains(canary, StringComparison.Ordinal));
        Assert.False(leaked);
    }
}
