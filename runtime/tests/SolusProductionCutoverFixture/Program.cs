using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// The fixture owns inputs and observation only. Every review is a fresh child
// process running the supplied production DLL's real argv entry.
internal static class Program
{
    internal const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private static readonly string Base = new('a', 40), First = new('b', 40), Second = new('c', 40);

    internal static async Task<int> Main(string[] args)
    {
        if (args is ["historical-action"]) return await NativeActionProducer.RunAsync();
#if APR_SOLUS_MANAGED
        if (args is ["managed-faults", var faultRoot]) return await ManagedFaultProof.RunAsync(faultRoot);
#endif
        try
        {
            if (args is ["redaction-probe"]) throw new InvalidOperationException("PRIVATE_R6_IDENTIFIER_ERROR_CANARY");
            if (!OperatingSystem.IsLinux() || args.Length != 4) throw new InvalidOperationException("arguments");
            var (dotnet, managed, historical, root) = (args[0], args[1], args[2], args[3]);
            Directory.CreateDirectory(root);
            var fixture = new DirectRoot(Path.Join(root, "solus"));
            var fact = "APR_PRIOR_ONLY_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            fixture.Write(First, First, "first", fact + "\n");
            var bootstrap = await fixture.Run(dotnet, managed, historical: false);
            Require(bootstrap == 0, "bootstrap_exit");
            var first = fixture.Result();
            Require(first is { Code: R3LiveAgentCodes.Completed, Generation: 0,
                ModelCalls: 2, ToolCalls: 1, HandoffReady: true }, "bootstrap_result");
            var sourceLineage = File.ReadAllBytes(fixture.LineagePath);
            var sourceState = fixture.StateFiles();

            // Genuine selected-current SESSION from the explicit historical
            // producer must fail before the new production provider is invoked.
            var legacy = new DirectRoot(Path.Join(root, "legacy"));
            legacy.Write(First, First, "legacy_first", fact + "\n");
            Require(await legacy.Run(dotnet, historical, historical: true) == 0, "legacy_producer");
            var legacyLineage = File.ReadAllBytes(legacy.LineagePath);
            var legacyState = legacy.StateFiles();
            legacy.Write(First, Second, "legacy_continue", "Current safe content.\n", legacyLineage);
            Require(await legacy.Run(dotnet, managed, historical: false) != 0, "legacy_rejected_exit");
            Require(legacy.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false,
                Generation: null }, "legacy_rejected_before_provider");
            Require(legacy.StateFiles().SequenceEqual(legacyState) &&
                File.ReadAllBytes(legacy.LineagePath).AsSpan().SequenceEqual(legacyLineage), "legacy_unchanged");

            // An absent locator cannot authorize a fresh run over actual state.
            File.Delete(fixture.LineagePath);
            fixture.Write(First, First, "false_absence", "Current safe content.\n");
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "false_absence_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "false_absence_no_work");
            Require(fixture.StateFiles().SequenceEqual(sourceState), "false_absence_unchanged");
            File.WriteAllBytes(fixture.LineagePath, sourceLineage);

            // The direct transition contract still requires VerifiedAhead.
            fixture.Write(First, First, "same_head", "Current safe content.\n", sourceLineage);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "same_head_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "same_head_no_work");

            fixture.Write(First, Second, "second", "Current safe content.\n", sourceLineage);
            var continuation = await fixture.Run(dotnet, managed, historical: false);
            Require(continuation == 0, "continue_exit");
            var second = fixture.Result();
            Require(second is { Code: R3LiveAgentCodes.Completed, Generation: 1,
                ModelCalls: 2, ToolCalls: 1, HandoffReady: true } &&
                second.SecondRequestSha256 is not null &&
                second.EnvelopeSha256 != first.EnvelopeSha256 &&
                second.SessionSha256 != first.SessionSha256, "continue_result");
            var accepted = File.ReadAllBytes(fixture.LineagePath);
            var acceptedState = fixture.StateFiles();

            // Repeating the producer invocation is rejected and cannot consume
            // another source grant or advance state.
            fixture.Write(Second, new string('d', 40), "second", "Current safe content.\n", accepted);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "reused_invocation_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "reused_invocation_no_work");
            Require(fixture.StateFiles().SequenceEqual(acceptedState) &&
                File.ReadAllBytes(fixture.LineagePath).AsSpan().SequenceEqual(accepted), "reused_invocation_unchanged");

            // Exact original source restored after successor publication remains
            // spent; the durable claim is not derived from the new invocation.
            File.WriteAllBytes(fixture.LineagePath, sourceLineage);
            fixture.Write(First, Second, "third", "Current safe content.\n", sourceLineage);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "spent_source_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "spent_source_no_work");
            Require(fixture.StateFiles().SequenceEqual(acceptedState), "spent_source_unchanged");
            Console.WriteLine("APR_SOLUS_CUTOVER direct_processes=8 fresh=true restored=true old_session_rejected=true false_absence_rejected=true same_head_rejected=true invocation_reuse_rejected=true spent_source_rejected=true");
            return 0;
        }
        catch (Exception exception)
        {
            var reason = CutoverProofFailure.Describe(exception);
            Console.Error.WriteLine("APR_SOLUS_CUTOVER_FAILED " + reason);
            return 1;
        }
    }

    private static void Require(bool value, string code)
    { if (!value) throw new CutoverProofFailure(code); }

    internal sealed class DirectRoot
    {
        private readonly string root;
        private bool continuation;
        private readonly AgentSessionTrustedRequest trusted = new("owner/repository", 109,
            "trusted-r3-workflow", Encoding.UTF8.GetBytes("Use the immutable reviewed snapshot for all evidence."),
            "build-109", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
        internal DirectRoot(string root)
        {
            this.root = root;
            foreach (var directory in new[] { "input", "host", "state", "private", "output" })
                Directory.CreateDirectory(Path.Join(root, directory));
        }
        internal string LineagePath => Path.Join(root, "host", "accepted-lineage.json");
        private string ResultPath => Path.Join(root, "output", "result.json");
        internal string[] StateFiles() => Directory.GetFiles(Path.Join(root, "state"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();
        internal LiveAgentFreshProcessResultDocument Result() => LiveAgentFreshProcessCodec.ReadResult(File.ReadAllBytes(ResultPath))
            ?? throw new InvalidOperationException("missing_result");
        internal void Write(string from, string head, string invocation, string text, byte[]? lineage = null)
        {
            File.Delete(ResultPath);
            continuation = lineage is not null;
            Require(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized), "materialize");
            var stable = materialized!.StablePlan;
            var scope = new RestrictedStateScope(trusted.RepositoryId, trusted.WorkflowIdentity, trusted.ReviewTarget,
                "session-109", trusted.ProviderId, trusted.ModelId, trusted.AdapterId, stable.PolicySha256,
                stable.LimitsSha256, stable.ToolsetSha256, trusted.BuildId);
            var expected = lineage is null ? null : LiveAgentFreshProcessDomain.RawSha256(lineage);
            var transition = from == head ? "same_head" : "verified_ahead";
            var authorization = new LiveAgentFreshProcessAuthorizationDocument(LiveAgentFreshProcessDomain.AuthorizationKind,
                new(trusted.RepositoryId, trusted.ReviewTarget, trusted.WorkflowIdentity,
                    Encoding.UTF8.GetString(trusted.TrustedPolicyBytes), trusted.BuildId, trusted.ProviderId,
                    trusted.ModelId, trusted.AdapterId, "session-109"), LiveAgentFreshProcessDomain.ScopeDocument(scope),
                true, true, false, LiveAgentFreshProcessDomain.DeterministicProfile,
                lineage is null ? "absent" : "current", lineage is null ? "automatic" : "explicit",
                new(transition, from, Base, head, LiveAgentFreshProcessDomain.TransitionReceiptSha256(expected,
                    transition, from, Base, head, invocation)), invocation, expected);
            var identity = new ReviewedIdentity(trusted.RepositoryId, trusted.ReviewTarget, Base, head);
            var content = Encoding.UTF8.GetBytes(text);
            var reviewed = new LiveAgentFreshProcessReviewedInputDocument(LiveAgentFreshProcessDomain.ReviewedInputKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity), "Review the current immutable snapshot.");
            var manifest = new LiveAgentFreshProcessSnapshotManifestDocument(LiveAgentFreshProcessDomain.SnapshotManifestKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity), ["fact.txt"],
                [new("fact.txt", content.Length, LiveAgentFreshProcessDomain.RawSha256(content), Convert.ToBase64String(content))], [], []);
            File.WriteAllBytes(Path.Join(root, "host", "authorization.json"), LiveAgentFreshProcessCodec.Write(authorization));
            File.WriteAllBytes(Path.Join(root, "input", "reviewed-input.json"), LiveAgentFreshProcessCodec.Write(reviewed));
            File.WriteAllBytes(Path.Join(root, "input", "snapshot-manifest.json"), LiveAgentFreshProcessCodec.Write(manifest));
        }
        internal async Task<int> Run(string dotnet, string dll, bool historical)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false };
            start.Environment.Clear();
            start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet);
            start.Environment["HOME"] = root;
            start.Environment[R3LiveAgentEnvironmentSecretSource.ProviderVariable] = "synthetic-header-only";
            start.Environment[R3LiveAgentEnvironmentSecretSource.StateKeyVariable] = Key;
            start.ArgumentList.Add(dll);
            if (historical) start.ArgumentList.Add("historical-direct");
            foreach (var argument in new[] { "review-live-agent-r3", "--mode", continuation ? "continue" : "bootstrap", "--root", root })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("child_start");
            var output = child.StandardOutput.ReadToEndAsync();
            var diagnostic = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
            Require((await output).Length == 0, "child_stdout");
            _ = await diagnostic; // Never publish private child output.
            return child.ExitCode;
        }
    }
}

internal sealed class CutoverProofFailure(string code) : Exception
{
    internal string Code { get; } = code;
    internal static string Describe(Exception exception) =>
        exception is CutoverProofFailure failure ? failure.Code : exception.GetType().Name;
}
