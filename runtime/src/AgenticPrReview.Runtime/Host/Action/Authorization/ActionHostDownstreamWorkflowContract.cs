using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal static class ActionHostDownstreamWorkflowContract
{
    internal const string WorkflowPath = ".github/workflows/agentic-pr-review.yml";
    private const string ResourcePrefix = "AgenticPrReview.Runtime.ActionHost.Downstream";
    private static readonly Lazy<string> Automatic = new(() => Load("Automatic"));
    private static readonly Lazy<string> Manual = new(() => Load("Manual"));

    internal static string Render(string actionSourceSha, bool automatic)
    {
        if (actionSourceSha.Length != 40 || !actionSourceSha.All(static c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("Workflow Action identity is invalid.");
        }

        return (automatic ? Automatic : Manual).Value.Replace(
            ActionHostTrustedWorkflowContract.ActionSourcePlaceholder,
            actionSourceSha, StringComparison.Ordinal);
    }

    private static string Load(string variant)
    {
        using var stream = typeof(ActionHostDownstreamWorkflowContract).Assembly
            .GetManifestResourceStream(ResourcePrefix + variant + "Workflow") ??
            throw new InvalidOperationException("The downstream workflow resource is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        var value = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var placeholder = ActionHostTrustedWorkflowContract.ActionSourcePlaceholder;
        var index = value.IndexOf(placeholder, StringComparison.Ordinal);
        if (value.Contains('\r') || !value.EndsWith('\n') || index < 0 ||
            value.IndexOf(placeholder, index + placeholder.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("The downstream workflow resource is invalid.");
        }

        return value;
    }
}
