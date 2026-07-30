using System.Collections;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal sealed record ProofEnvironmentResult(
    bool Succeeded,
    string Code,
    int ExitCode);

internal static class ProofEnvironment
{
    internal static ProofEnvironmentResult Validate(ProofCommand command)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!StringComparer.FromComparison(comparison).Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Environment.CurrentDirectory)),
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(command.Root))))
        {
            return Failure(ProofCodes.EnvironmentInvalid);
        }

        string[] expected;
        try
        {
            expected = File.ReadAllLines(command.EnvironmentManifest)
                .Where(line => line.Length > 0)
                .ToArray();
        }
        catch
        {
            return Failure(ProofCodes.EnvironmentInvalid);
        }

        if (expected.Length == 0 ||
            expected.Any(name => name.Trim() != name || name.Contains('=')) ||
            expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
        {
            return Failure(ProofCodes.EnvironmentInvalid);
        }

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                entry.Value is not string value ||
                !actual.TryAdd(key, value))
            {
                return Failure(ProofCodes.EnvironmentInvalid);
            }
        }

        if (actual.Any(pair =>
            CanarySet.ContainsCanary(pair.Key) ||
            CanarySet.ContainsCanary(pair.Value)))
        {
            return Failure(ProofCodes.EnvironmentCanary);
        }

        var expectedOrdered = expected.Order(StringComparer.Ordinal).ToArray();
        var actualOrdered = actual.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!expectedOrdered.SequenceEqual(actualOrdered, StringComparer.Ordinal))
        {
            return Failure(ProofCodes.EnvironmentInvalid);
        }

        return new ProofEnvironmentResult(true, "APR_AGENT_ENVIRONMENT_OK", 0);
    }

    private static ProofEnvironmentResult Failure(string code) =>
        new(false, code, 3);
}

internal static class CanarySet
{
    internal static readonly string[] Classes =
    [
        "github",
        "actions",
        "provider",
        "state-encryption",
        "unrelated-workflow",
        "runner",
        "package-registry",
        "cloud",
        "signing",
    ];

    internal static bool ContainsCanary(string value) =>
        value.Contains("apr-canary-", StringComparison.Ordinal);

    internal static string Provider(string scenario) =>
        Create("provider", scenario);

    internal static byte[] StateKey(string scenario)
    {
        var canary = Create("state-encryption", scenario);
        return System.Security.Cryptography.SHA256.HashData(
            ProofFiles.StrictUtf8(canary));
    }

    internal static string Create(string @class, string scenario)
    {
        if (!Classes.Contains(@class, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown canary class.", nameof(@class));
        }

        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                ProofFiles.StrictUtf8(scenario))).ToLowerInvariant();
        return string.Concat("apr-canary-", @class, "-", digest[..16]);
    }
}
