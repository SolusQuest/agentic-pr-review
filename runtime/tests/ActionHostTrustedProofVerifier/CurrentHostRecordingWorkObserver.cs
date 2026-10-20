using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofVerifier;

// Independent observer of authored APR work; it cannot choose or execute steps.
internal sealed class CurrentHostRecordingWorkObserver(string root, bool successor)
{
    internal const string Domain = "apr-s5-current-host-typed-work-v1";
    private readonly object gate = new();
    internal void ObserveWork(ProjectChatRequest request, int ordinal, string phase)
    {
        var shape = Observe(request, ordinal);
        if (phase == "before") return;
        lock (gate)
        {
            File.WriteAllText(Path.Join(root, "synthetic-chat-call-count"), ordinal.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Join(root, "synthetic-chat-sequence"), ordinal.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Join(root, "typed-chat-observation-domain"), Domain);
            File.AppendAllText(Path.Join(root, "verifier-typed-chat-trace.tsv"),
                string.Join('\t', Domain, ordinal.ToString(CultureInfo.InvariantCulture), phase, shape) + "\n");
        }
    }

    private string Observe(ProjectChatRequest request, int ordinal)
    {
        // This schedule is verifier-owned, independently frozen from the script.
        string[] ids = successor
            ? ["e2p-successor-read-file", "e2p-successor-search"]
            : ["e2p-list-changed", "e2p-list-files", "e2p-read-diff", "e2p-read-file", "e2p-search"];
        string[] hashes = successor
            ? ["45700d2afb155e53426f33f2ccfd133af8897af6d399cae313e31a87aebc79c3", "94695db0825eb7a2d1079381248b0d7e5a45670c00daf20350715164ac9b9a18"]
            : ["f755f60a030e4ab480e2f28ad4fd6e31a98c7825bc5cdd6b7b3062a71b12765c", "6ca41046f0f7ddb647c450814ad86a8725f38a2611d04b46b46abf757e117bab",
                "4439e2c51fcc62776f64f57771209602c4323f8eb88145baadde7566d2533c00", "45700d2afb155e53426f33f2ccfd133af8897af6d399cae313e31a87aebc79c3",
                "94695db0825eb7a2d1079381248b0d7e5a45670c00daf20350715164ac9b9a18"];
        if (ordinal < 1 || ordinal > ids.Length + 1 || request.Messages.Length != 2 + (ordinal - 1) * 2 ||
            request.Messages[0].Role != "system" || request.Messages[1].Role != "user")
            throw new ProjectChatNormalizationException();
        var observedHashes = new List<string>();
        for (var index = 0; index < ordinal - 1; index++)
        {
            if (request.Messages[2 + index * 2] is not { Role: "assistant" } assistant ||
                assistant.Contents.OfType<ProjectToolCallContent>().ToArray() is not [var call] || call.CallId != ids[index] ||
                request.Messages[3 + index * 2] is not { Role: "tool", Contents: [ProjectToolResultContent result] } ||
                result.CallId != call.CallId || string.IsNullOrWhiteSpace(result.Result)) throw new ProjectChatNormalizationException();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson)));
            if (hash != hashes[index]) throw new ProjectChatNormalizationException();
            observedHashes.Add(hash);
        }
        var trace = AgentRequestWriter.Write(request);
        string requestHash;
        try
        {
            foreach (var secret in new[] { FrameworkCanaries.ProviderKey, FrameworkCanaries.GitHubToken,
                FrameworkCanaries.StateKey, FrameworkCanaries.PreviousStateKey, FrameworkCanaries.PublicResult })
                if (trace.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0) throw new ProjectChatNormalizationException();
            requestHash = Convert.ToHexStringLower(SHA256.HashData(trace));
        }
        finally { CryptographicOperations.ZeroMemory(trace); }
        return string.Join('\t', successor ? "successor" : "fresh", "0", request.Messages.Length,
            ordinal - 1, observedHashes.Count == 0 ? "-" : string.Join(',', observedHashes), requestHash);
    }
}
