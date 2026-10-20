using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

// Independent observer for the full Framework Host lane's current work. The
// historical authored transcript remains a separate regression consumer.
internal sealed class CurrentFrameworkChatObserver(string root, IAuthoredResponseSource? inner = null) : IAuthoredResponseSource
{
    internal const string Domain = "apr-s5-current-framework-host-work-v1";
    private int calls;

    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var ordinal = Interlocked.Increment(ref calls);
        var outcome = "rejected";
        var shape = "-\t-\t-\t-\t-\t-";
        try
        {
            shape = Observe(request, ordinal);
            var response = await inner!.GetResponseAsync(request, token).ConfigureAwait(false);
            outcome = "succeeded";
            return response;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "failed"; throw; }
        finally
        {
            File.WriteAllText(Path.Join(root, "typed-chat-observation-domain"), Domain);
            File.AppendAllText(Path.Join(root, "current-framework-chat-trace.tsv"),
                string.Join('\t', Domain, ordinal.ToString(CultureInfo.InvariantCulture), outcome, shape) + "\n");
        }
    }

    internal void ObserveWork(ProjectChatRequest request, int ordinal, string phase)
    {
        var shape = Observe(request, ordinal);
        if (phase == "before") return;
        File.WriteAllText(Path.Join(root, "typed-chat-observation-domain"), Domain);
        File.AppendAllText(Path.Join(root, "current-framework-chat-trace.tsv"),
            string.Join('\t', Domain, ordinal.ToString(CultureInfo.InvariantCulture), phase, shape) + "\n");
    }

    private string Observe(ProjectChatRequest request, int ordinal)
    {
        var mode = File.ReadAllText(Path.Join(root, "mode")).Trim();
        var successor = mode == "continuation";
        const string prefix = "framework-";
        string[] ids = successor
            ? ["continuation-read-file", "continuation-search-text"]
            : [prefix + "list-changed", prefix + "list-files", prefix + "read-diff", prefix + "read-file", prefix + "search-text"];
        var expectedArguments = ExpectedArguments(successor);
        if (ordinal < 1 || ordinal > ids.Length + 1 || request.Messages.Length != 2 + (ordinal - 1) * 2 ||
            request.Messages[0].Role != "system" || request.Messages[1].Role != "user" ||
            request.Continuation?.Items.Any(item => item.MessagePosition < 2) == true)
            throw new ProjectChatNormalizationException();
        var hashes = new List<string>();
        for (var index = 0; index < ordinal - 1; index++)
        {
            var assistant = request.Messages[2 + index * 2];
            var tool = request.Messages[3 + index * 2];
            if (assistant.Role != "assistant" || tool.Role != "tool" ||
                assistant.Contents.OfType<ProjectToolCallContent>().ToArray() is not [var call] ||
                tool.Contents.OfType<ProjectToolResultContent>().ToArray() is not [var result] ||
                call.CallId != ids[index] || result.CallId != ids[index] || string.IsNullOrWhiteSpace(result.Result))
                throw new ProjectChatNormalizationException();
            var hash = Hash(Encoding.UTF8.GetBytes(call.ArgumentsJson));
            if (hash != expectedArguments[index]) throw new ProjectChatNormalizationException();
            hashes.Add(hash);
        }
        var bytes = AgentRequestWriter.Write(request);
        try
        {
            foreach (var marker in new[] { FrameworkCanaries.ProviderKey, FrameworkCanaries.GitHubToken,
                FrameworkCanaries.StateKey, FrameworkCanaries.PreviousStateKey,
                FrameworkCanaries.ContinuationMarker, FrameworkCanaries.PublicResult })
                if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0)
                    throw new ProjectChatNormalizationException();
            return string.Join('\t', successor ? "successor" : "fresh", "0", request.Messages.Length,
                ordinal - 1, hashes.Count == 0 ? "-" : string.Join(',', hashes), Hash(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Verifier-owned canonical argument identities, independent of the script.
    internal static string[] ExpectedArguments(bool successor)
    {
        var read = Hash(Encoding.UTF8.GetBytes("{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}"));
        var search = Hash(Encoding.UTF8.GetBytes("{\"query\":\"" + FrameworkCanaries.ToolData + "\",\"path\":\"proof/apr178-path-canary.txt\"}"));
        return successor ? [read, search] :
            [Hash("{\"after\":null}"u8.ToArray()), Hash("{\"prefix\":null,\"after\":null}"u8.ToArray()),
                Hash("{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1,\"hunk_count\":20}"u8.ToArray()), read, search];
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
