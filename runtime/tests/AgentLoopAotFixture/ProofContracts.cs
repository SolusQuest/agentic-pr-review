using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProofCodes
{
    internal const string ArgumentsInvalid = "APR_AGENT_ARGUMENTS_INVALID";
    internal const string EnvironmentInvalid = "APR_AGENT_ENVIRONMENT_INVALID";
    internal const string EnvironmentCanary = "APR_AGENT_ENVIRONMENT_CANARY";
    internal const string Unexpected = "APR_AGENT_UNEXPECTED";
    internal const string BootstrapOk = "APR_AGENT_BOOTSTRAP_OK";
    internal const string ContinueOk = "APR_AGENT_CONTINUE_OK";
    internal const string NegativeOk = "APR_AGENT_NEGATIVE_OK";
    internal const string ProofFailed = "APR_AGENT_PROOF_FAILED";
}

internal sealed record ProofCommand(
    string Verb,
    string Mode,
    string Root,
    string Output,
    string EnvironmentManifest,
    string? Case);

internal static class ProofArguments
{
    internal static bool TryParse(
        IReadOnlyList<string> args,
        out ProofCommand? command)
    {
        command = null;
        if (args.Count < 1)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count ||
                !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        var verb = args[0];
        if (verb is not ("bootstrap" or "continue" or "negative") ||
            !values.TryGetValue("--mode", out var mode) ||
            mode is not ("framework" or "aot") ||
            !values.TryGetValue("--root", out var root) ||
            !Path.IsPathFullyQualified(root) ||
            !values.TryGetValue("--output", out var output) ||
            !Path.IsPathFullyQualified(output) ||
            !values.TryGetValue(
                "--environment-manifest",
                out var environmentManifest) ||
            !Path.IsPathFullyQualified(environmentManifest))
        {
            return false;
        }

        values.TryGetValue("--case", out var @case);
        if ((verb == "negative") != (@case is not null) ||
            values.Keys.Any(key => key is not (
                "--mode" or
                "--root" or
                "--output" or
                "--environment-manifest" or
                "--case")))
        {
            return false;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        output = Path.GetFullPath(output);
        environmentManifest = Path.GetFullPath(environmentManifest);
        if (!IsDescendant(root, output) ||
            !File.Exists(environmentManifest))
        {
            return false;
        }

        command = new ProofCommand(
            verb,
            mode,
            root,
            output,
            environmentManifest,
            @case);
        return true;
    }

    private static bool IsDescendant(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." &&
            relative != ".." &&
            !relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProofOutput(
    string Kind,
    string Phase,
    string Status,
    int ModelCalls,
    int ToolCalls,
    bool ThinkingRequired,
    long Generation,
    string Transition,
    string SessionIdentitySha256,
    string PredecessorEnvelope,
    string StablePlanSha256,
    string LimitsSha256,
    string ToolsetSha256,
    ImmutableArray<string> RequestSha256,
    ImmutableArray<string> Evidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HostLineageFile(
    string Kind,
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    string? PredecessorEnvelopeSha256,
    long AcceptedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    string ScopeSha256,
    string IntegritySha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ContinuationPayload(
    string Readable,
    string Opaque,
    string Framing);

internal static class ProofPaths
{
    internal static string StateRoot(ProofCommand command) =>
        Path.Join(command.Root, "restricted-state");

    internal static string RepositoryRoot(ProofCommand command) =>
        Path.Join(command.Root, "repository");

    internal static string Lineage(ProofCommand command) =>
        Path.Join(command.Root, "host-lineage.json");

    internal static string StartupNonce(ProofCommand command) =>
        Path.Join(command.Root, string.Concat(command.Verb, ".startup"));
}

internal static class ProofFiles
{
    internal static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static void ReplaceAtomically(
        string path,
        ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = string.Concat(path, ".tmp");
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }

        WriteNew(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    internal static byte[] StrictUtf8(string value) =>
        new UTF8Encoding(false, true).GetBytes(value);
}
