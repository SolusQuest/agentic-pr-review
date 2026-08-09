namespace AgenticPrReview.Runtime.ActionHost.Contracts;

internal enum ActionHostStateMode
{
    Auto = 1,
    Reset = 2,
}

internal enum ActionHostInputError
{
    None = 0,
    CountInvalid,
    AggregateBytesInvalid,
    NameInvalid,
    UnknownName,
    DuplicateName,
    ValueMissing,
    ValueBytesInvalid,
    PullRequestNumberInvalid,
    StateModeInvalid,
}

internal readonly record struct ActionHostRawInput(
    string? Name,
    string? Value);

internal abstract class ActionHostOpaqueSecret
{
    private readonly byte[] utf8Bytes;

    protected ActionHostOpaqueSecret(byte[] utf8Bytes)
    {
        this.utf8Bytes = (byte[])utf8Bytes.Clone();
    }

    internal int ByteCount => utf8Bytes.Length;

    internal bool RequiresPreLaunchMasking => true;

    internal ActionHostPrivacyClass Privacy => ActionHostPrivacyClass.Secret;

    internal string ExportForPrivateLaunch() =>
        ActionHostContractValidation.DecodeOpaqueSecret(utf8Bytes);

    public sealed override string ToString() => "[REDACTED]";
}

internal sealed class ActionHostGitHubToken : ActionHostOpaqueSecret
{
    private ActionHostGitHubToken(byte[] utf8Bytes) : base(utf8Bytes) { }

    internal static bool TryCreate(
        string? value,
        out ActionHostGitHubToken? secret)
    {
        secret = null;
        if (!ActionHostContractValidation.TryEncodeOpaqueSecret(
                value,
                out var bytes))
        {
            return false;
        }

        secret = new ActionHostGitHubToken(bytes!);
        return true;
    }
}

internal sealed class ActionHostProviderApiKey : ActionHostOpaqueSecret
{
    private ActionHostProviderApiKey(byte[] utf8Bytes) : base(utf8Bytes) { }

    internal static bool TryCreate(
        string? value,
        out ActionHostProviderApiKey? secret)
    {
        secret = null;
        if (!ActionHostContractValidation.TryEncodeOpaqueSecret(
                value,
                out var bytes))
        {
            return false;
        }

        secret = new ActionHostProviderApiKey(bytes!);
        return true;
    }
}

internal sealed class ActionHostStateKey : ActionHostOpaqueSecret
{
    private ActionHostStateKey(byte[] utf8Bytes) : base(utf8Bytes) { }

    internal static bool TryCreate(
        string? value,
        out ActionHostStateKey? secret)
    {
        secret = null;
        if (!ActionHostContractValidation.TryEncodeOpaqueSecret(
                value,
                out var bytes))
        {
            return false;
        }

        secret = new ActionHostStateKey(bytes!);
        return true;
    }
}

internal sealed class ActionHostPreviousStateKey : ActionHostOpaqueSecret
{
    private ActionHostPreviousStateKey(byte[] utf8Bytes) : base(utf8Bytes) { }

    internal static bool TryCreate(
        string? value,
        out ActionHostPreviousStateKey? secret)
    {
        secret = null;
        if (!ActionHostContractValidation.TryEncodeOpaqueSecret(
                value,
                out var bytes))
        {
            return false;
        }

        secret = new ActionHostPreviousStateKey(bytes!);
        return true;
    }
}

internal sealed class ActionHostInputs
{
    private ActionHostInputs(
        ActionHostGitHubToken? githubToken,
        ActionHostProviderApiKey? providerApiKey,
        ActionHostStateKey? stateKey,
        ActionHostPreviousStateKey? previousStateKey,
        string? configPath,
        long? pullRequestNumber,
        ActionHostStateMode stateMode)
    {
        GitHubToken = githubToken;
        ProviderApiKey = providerApiKey;
        StateKey = stateKey;
        PreviousStateKey = previousStateKey;
        ConfigPath = configPath;
        PullRequestNumber = pullRequestNumber;
        StateMode = stateMode;
    }

    internal ActionHostGitHubToken? GitHubToken { get; }

    internal ActionHostProviderApiKey? ProviderApiKey { get; }

    internal ActionHostStateKey? StateKey { get; }

    internal ActionHostPreviousStateKey? PreviousStateKey { get; }

    internal string? ConfigPath { get; }

    internal long? PullRequestNumber { get; }

    internal ActionHostStateMode StateMode { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.PrivateLaunch;

    internal static bool TryCreate(
        ActionHostGitHubToken? githubToken,
        ActionHostProviderApiKey? providerApiKey,
        ActionHostStateKey? stateKey,
        ActionHostPreviousStateKey? previousStateKey,
        string? configPath,
        long? pullRequestNumber,
        ActionHostStateMode stateMode,
        out ActionHostInputs? inputs)
    {
        inputs = null;
        if (configPath is not null &&
                !ActionHostContractValidation.IsBoundedPrivateStructure(
                    configPath,
                    ActionHostContractBounds.MaximumConfigPathBytes) ||
            pullRequestNumber is <= 0 ||
            stateMode is not (
                ActionHostStateMode.Auto or ActionHostStateMode.Reset))
        {
            return false;
        }

        inputs = new ActionHostInputs(
            githubToken,
            providerApiKey,
            stateKey,
            previousStateKey,
            configPath,
            pullRequestNumber,
            stateMode);
        return true;
    }
}

internal static class ActionHostInputParser
{
    internal const string GitHubTokenName = "github-token";
    internal const string ProviderApiKeyName = "provider-api-key";
    internal const string StateKeyName = "state-key";
    internal const string PreviousStateKeyName = "previous-state-key";
    internal const string ConfigPathName = "config-path";
    internal const string PullRequestNumberName = "pr-number";
    internal const string StateModeName = "state-mode";

    internal static bool TryParse(
        IReadOnlyList<ActionHostRawInput>? entries,
        out ActionHostInputs? inputs,
        out ActionHostInputError error)
    {
        inputs = null;
        error = ActionHostInputError.None;
        if (entries is null ||
            entries.Count > ActionHostContractBounds.MaximumRawInputs)
        {
            error = ActionHostInputError.CountInvalid;
            return false;
        }

        ActionHostGitHubToken? githubToken = null;
        ActionHostProviderApiKey? providerApiKey = null;
        ActionHostStateKey? stateKey = null;
        ActionHostPreviousStateKey? previousStateKey = null;
        string? configPath = null;
        long? pullRequestNumber = null;
        var stateMode = ActionHostStateMode.Auto;
        var seen = 0;
        var aggregateBytes = 0;

        foreach (var entry in entries)
        {
            if (!ActionHostContractValidation.TryGetUtf8ByteCount(
                    entry.Name,
                    out var nameBytes) ||
                nameBytes is < 1 ||
                nameBytes >
                    ActionHostContractBounds.MaximumRawInputNameBytes)
            {
                error = ActionHostInputError.NameInvalid;
                return false;
            }

            var bit = InputBit(entry.Name!);
            if (bit == 0)
            {
                error = ActionHostInputError.UnknownName;
                return false;
            }

            if ((seen & bit) != 0)
            {
                error = ActionHostInputError.DuplicateName;
                return false;
            }

            seen |= bit;
            if (entry.Value is null || entry.Value.Length == 0)
            {
                error = ActionHostInputError.ValueMissing;
                return false;
            }

            if (!ActionHostContractValidation.TryGetUtf8ByteCount(
                    entry.Value,
                    out var valueBytes))
            {
                error = ActionHostInputError.ValueBytesInvalid;
                return false;
            }

            try
            {
                aggregateBytes = checked(
                    aggregateBytes + nameBytes + valueBytes);
            }
            catch (OverflowException)
            {
                error = ActionHostInputError.AggregateBytesInvalid;
                return false;
            }

            if (aggregateBytes >
                ActionHostContractBounds.MaximumRawInputAggregateBytes)
            {
                error = ActionHostInputError.AggregateBytesInvalid;
                return false;
            }

            switch (entry.Name)
            {
                case GitHubTokenName:
                    if (!ActionHostGitHubToken.TryCreate(
                            entry.Value,
                            out githubToken))
                    {
                        error = ActionHostInputError.ValueBytesInvalid;
                        return false;
                    }

                    break;
                case ProviderApiKeyName:
                    if (!ActionHostProviderApiKey.TryCreate(
                            entry.Value,
                            out providerApiKey))
                    {
                        error = ActionHostInputError.ValueBytesInvalid;
                        return false;
                    }

                    break;
                case StateKeyName:
                    if (!ActionHostStateKey.TryCreate(
                            entry.Value,
                            out stateKey))
                    {
                        error = ActionHostInputError.ValueBytesInvalid;
                        return false;
                    }

                    break;
                case PreviousStateKeyName:
                    if (!ActionHostPreviousStateKey.TryCreate(
                            entry.Value,
                            out previousStateKey))
                    {
                        error = ActionHostInputError.ValueBytesInvalid;
                        return false;
                    }

                    break;
                case ConfigPathName:
                    if (!ActionHostContractValidation.IsBoundedPrivateStructure(
                            entry.Value,
                            ActionHostContractBounds.MaximumConfigPathBytes))
                    {
                        error = ActionHostInputError.ValueBytesInvalid;
                        return false;
                    }

                    configPath = entry.Value;
                    break;
                case PullRequestNumberName:
                    if (valueBytes >
                            ActionHostContractBounds
                                .MaximumPullRequestNumberBytes ||
                        !ActionHostContractValidation.TryParsePositiveInt64(
                            entry.Value,
                            out var parsedPullRequestNumber))
                    {
                        error = ActionHostInputError.PullRequestNumberInvalid;
                        return false;
                    }

                    pullRequestNumber = parsedPullRequestNumber;
                    break;
                case StateModeName:
                    if (valueBytes >
                        ActionHostContractBounds.MaximumStateModeBytes)
                    {
                        error = ActionHostInputError.StateModeInvalid;
                        return false;
                    }

                    if (StringComparer.Ordinal.Equals(entry.Value, "auto"))
                    {
                        stateMode = ActionHostStateMode.Auto;
                    }
                    else if (StringComparer.Ordinal.Equals(
                                 entry.Value,
                                 "reset"))
                    {
                        stateMode = ActionHostStateMode.Reset;
                    }
                    else
                    {
                        error = ActionHostInputError.StateModeInvalid;
                        return false;
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        "Accepted input name did not have a parser branch.");
            }
        }

        if (!ActionHostInputs.TryCreate(
            githubToken,
            providerApiKey,
            stateKey,
            previousStateKey,
            configPath,
            pullRequestNumber,
            stateMode,
            out inputs))
        {
            error = ActionHostInputError.ValueBytesInvalid;
            return false;
        }

        return true;
    }

    internal static bool TryCreateFromWire(
        string? githubToken,
        string? providerApiKey,
        string? stateKey,
        string? previousStateKey,
        string? configPath,
        string? pullRequestNumber,
        string? stateMode,
        out ActionHostInputs? inputs)
    {
        inputs = null;
        if (stateMode is null)
        {
            return false;
        }

        var entries = new List<ActionHostRawInput>(
            ActionHostContractBounds.MaximumRawInputs);
        AddIfPresent(entries, GitHubTokenName, githubToken);
        AddIfPresent(entries, ProviderApiKeyName, providerApiKey);
        AddIfPresent(entries, StateKeyName, stateKey);
        AddIfPresent(entries, PreviousStateKeyName, previousStateKey);
        AddIfPresent(entries, ConfigPathName, configPath);
        AddIfPresent(entries, PullRequestNumberName, pullRequestNumber);
        entries.Add(new ActionHostRawInput(StateModeName, stateMode));
        return TryParse(entries, out inputs, out _);
    }

    private static void AddIfPresent(
        ICollection<ActionHostRawInput> entries,
        string name,
        string? value)
    {
        if (value is not null)
        {
            entries.Add(new ActionHostRawInput(name, value));
        }
    }

    private static int InputBit(string name) => name switch
    {
        GitHubTokenName => 1 << 0,
        ProviderApiKeyName => 1 << 1,
        StateKeyName => 1 << 2,
        PreviousStateKeyName => 1 << 3,
        ConfigPathName => 1 << 4,
        PullRequestNumberName => 1 << 5,
        StateModeName => 1 << 6,
        _ => 0,
    };
}
