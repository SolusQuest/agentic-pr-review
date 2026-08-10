using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Contracts;

internal enum ActionHostPrivacyClass
{
    Secret = 1,
    PrivateLaunch = 2,
    WorkflowPresentation = 3,
}

internal static class ActionHostErrorCode
{
    internal static string Input(ActionHostInputError error) => error switch
    {
        ActionHostInputError.CountInvalid => "count_invalid",
        ActionHostInputError.AggregateBytesInvalid =>
            "aggregate_bytes_invalid",
        ActionHostInputError.NameInvalid => "name_invalid",
        ActionHostInputError.UnknownName => "unknown_name",
        ActionHostInputError.DuplicateName => "duplicate_name",
        ActionHostInputError.ValueMissing => "value_missing",
        ActionHostInputError.ValueBytesInvalid => "value_bytes_invalid",
        ActionHostInputError.PullRequestNumberInvalid =>
            "pr_number_invalid",
        ActionHostInputError.StateModeInvalid => "state_mode_invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    internal static string Contract(ActionHostContractError error) =>
        error switch
        {
            ActionHostContractError.InvalidLaunch => "invalid_launch",
            ActionHostContractError.InvalidCompletion =>
                "invalid_completion",
            ActionHostContractError.InvalidPrivateCommand =>
                "invalid_private_command",
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
}

internal static class ActionHostContractBounds
{
    private const int MaximumJsonEscapedByteExpansion = 6;
    private const int MaximumLaunchFramingBytes = 8 * 1024;
    private const int MaximumLaunchFixedValueBytes =
        64 + 19 + 19 + 10 + 40 + 40 + 64 + 9;

    internal const int MaximumRawInputs = 7;
    internal const int MaximumRawInputNameBytes = 64;
    internal const int MaximumRawInputAggregateBytes = 16 * 1024;
    internal const int MaximumSecretBytes = 4 * 1024;
    internal const int MaximumConfigPathBytes = 1024;
    internal const int MaximumPullRequestNumberBytes = 19;
    internal const int MaximumStateModeBytes = 5;
    internal const int MaximumEventJsonPathBytes = 4 * 1024;
    internal const int MaximumRepositoryNameBytes = 256;
    internal const int MaximumWorkflowPathBytes = 1024;
    internal const int MaximumWorkflowRefBytes = 1024;
    internal const int MaximumBuildDiscriminatorBytes = 128;
    internal const int MaximumBridgeEndpointBytes = 2 * 1024;
    internal const int MaximumLaunchDocumentBytes =
        MaximumJsonEscapedByteExpansion *
        (MaximumRawInputAggregateBytes +
            MaximumEventJsonPathBytes +
            MaximumRepositoryNameBytes +
            MaximumWorkflowPathBytes +
            MaximumWorkflowRefBytes +
            MaximumBuildDiscriminatorBytes +
            MaximumBridgeEndpointBytes +
            MaximumLaunchFixedValueBytes) +
        MaximumLaunchFramingBytes;
    internal const int MaximumCompletionDocumentBytes = 16 * 1024;
    internal const int MaximumPrivateCommandDocumentBytes = 256 * 1024;
    internal const int MaximumPublicationUrlBytes = 2 * 1024;
    internal const int MaximumFindings = 20;
}

internal static class ActionHostContractValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool TryGetUtf8ByteCount(
        string? value,
        out int byteCount)
    {
        byteCount = 0;
        if (value is null)
        {
            return false;
        }

        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    internal static bool TryEncodeOpaqueSecret(
        string? value,
        out byte[]? bytes)
    {
        bytes = null;
        if (!TryGetUtf8ByteCount(value, out var byteCount) ||
            byteCount is < 1 or > ActionHostContractBounds.MaximumSecretBytes)
        {
            return false;
        }

        bytes = StrictUtf8.GetBytes(value!);
        return true;
    }

    internal static string DecodeOpaqueSecret(byte[] bytes) =>
        StrictUtf8.GetString(bytes);

    internal static bool IsBoundedPrivateStructure(
        string? value,
        int maximumBytes)
    {
        if (!TryGetUtf8ByteCount(value, out var byteCount) ||
            byteCount is < 1 ||
            byteCount > maximumBytes)
        {
            return false;
        }

        return !value!.Any(char.IsControl);
    }

    internal static bool IsRepositoryName(string? value)
    {
        if (!IsBoundedPrivateStructure(
                value,
                ActionHostContractBounds.MaximumRepositoryNameBytes))
        {
            return false;
        }

        var separator = value!.IndexOf('/');
        if (separator <= 0 ||
            separator != value.LastIndexOf('/') ||
            separator == value.Length - 1)
        {
            return false;
        }

        return value.All(character => character is
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '.' or
            '_' or
            '-' or
            '/');
    }

    internal static bool IsCommitSha(string? value) =>
        IsLowerHex(value, 40);

    internal static bool IsSha256(string? value) =>
        IsLowerHex(value, 64);

    internal static bool IsBuildDiscriminator(string? value)
    {
        if (value is not { Length: > 0 } ||
            value.Length > ActionHostContractBounds.MaximumBuildDiscriminatorBytes ||
            value[0] is not (
                >= 'a' and <= 'z' or
                >= '0' and <= '9'))
        {
            return false;
        }

        return value.All(character => character is
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '.' or
            '_' or
            '-');
    }

    internal static bool TryParsePositiveInt64(
        string? value,
        out long parsed)
    {
        parsed = 0;
        if (!IsCanonicalDecimal(value, 19) ||
            !long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out parsed) ||
            parsed <= 0)
        {
            parsed = 0;
            return false;
        }

        return true;
    }

    internal static bool TryParsePositiveInt32(
        string? value,
        out int parsed)
    {
        parsed = 0;
        if (!IsCanonicalDecimal(value, 10) ||
            !int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out parsed) ||
            parsed <= 0)
        {
            parsed = 0;
            return false;
        }

        return true;
    }

    internal static string CanonicalDecimal(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    internal static string CanonicalDecimal(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    internal static bool IsPublicationUrl(string? value)
    {
        if (!IsWorkflowPresentationText(
                value,
                ActionHostContractBounds.MaximumPublicationUrlBytes) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, "https") ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "github.com") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            uri.IsDefaultPort is false ||
            uri.AbsolutePath == "/")
        {
            return false;
        }

        return true;
    }

    internal static bool IsWorkflowPresentationText(
        string? value,
        int maximumBytes)
    {
        if (!IsBoundedPrivateStructure(value, maximumBytes))
        {
            return false;
        }

        return !value!.Contains("::", StringComparison.Ordinal) &&
            !value.Contains("%0a", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("%0d", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStrictUtf8Document(
        byte[]? bytes,
        int maximumBytes)
    {
        if (bytes is null ||
            bytes.Length is 0 ||
            bytes.Length > maximumBytes ||
            bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character => character is
            >= '0' and <= '9' or
            >= 'a' and <= 'f');

    private static bool IsCanonicalDecimal(
        string? value,
        int maximumCharacters) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumCharacters &&
        value[0] is >= '1' and <= '9' &&
        value.All(character => character is >= '0' and <= '9');
}
