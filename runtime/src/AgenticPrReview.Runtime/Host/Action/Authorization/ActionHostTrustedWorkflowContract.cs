using System.Reflection;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal static class ActionHostTrustedWorkflowContract
{
    internal const string ActionSourcePlaceholder = "__ACTION_SOURCE_SHA__";
    internal const string PayloadShaPlaceholder = "__PAYLOAD_SHA256__";
    internal const string ResourceName =
        "AgenticPrReview.Runtime.ActionHost.TrustedProofWorkflowTemplate";
    internal const string Runner = "ubuntu-24.04";
    internal const string ProtectedEnvironment = "r4-trusted-proof";
    internal const string TrustedConfigPath =
        ".github/agentic-pr-review/trusted-proof.json";
    internal const string CheckoutAction =
        "actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803";
    internal const string SetupNodeAction =
        "actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38";
    internal const string SetupDotnetAction =
        "actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1";

    private static readonly Lazy<string> Template = new(LoadTemplate);

    internal static string Render(string actionSourceSha, string payloadSha256)
    {
        if (!IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(payloadSha256, 64))
        {
            throw new ArgumentException("Workflow identities are invalid.");
        }

        return Template.Value
            .Replace(
                ActionSourcePlaceholder,
                actionSourceSha,
                StringComparison.Ordinal)
            .Replace(
                PayloadShaPlaceholder,
                payloadSha256,
                StringComparison.Ordinal);
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(ActionHostTrustedWorkflowContract).Assembly
            .GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException(
                "The trusted workflow template resource is missing.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        var value = reader.ReadToEnd().Replace(
            "\r\n",
            "\n",
            StringComparison.Ordinal);
        if (value.Contains('\r') ||
            value.Count(ActionSourcePlaceholder, StringComparison.Ordinal) != 7 ||
            value.Count(PayloadShaPlaceholder, StringComparison.Ordinal) != 3)
        {
            throw new InvalidOperationException(
                "The trusted workflow template is invalid.");
        }

        return value;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int Count(
        this string value,
        string match,
        StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(match, index, comparison)) >= 0)
        {
            count++;
            index += match.Length;
        }

        return count;
    }
}
