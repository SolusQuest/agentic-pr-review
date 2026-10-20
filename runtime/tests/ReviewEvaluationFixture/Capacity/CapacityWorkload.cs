using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

internal sealed record CapacitySnapshot(ReviewedSnapshot Snapshot, IReviewedFileAccess Files, long Bytes)
{
    internal static CapacitySnapshot Create(string root, ReviewedIdentity identity, bool fresh)
    {
        var tool = fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool;
        var paths = Enumerable.Range(0, 320).Select(index => $"file-{index:D4}.txt").ToArray();
        // The file and its diff describe the same bytes. A short first line bounds read_file
        // observations; the remaining 63 lines still push aggregate canonical diffs above 8 MiB.
        var text = Enumerable.Range(1, 64).Select(line => line == 1 ? tool : tool.PadRight(512, 'x')).ToArray();
        var diffs = paths.Select(path => new ReviewedDiffSource(identity, path, null, "added", false,
            [new ReviewedDiffHunk(0, 0, 1, 64, Enumerable.Range(1, 64).Select(line =>
                new ReviewedDiffLine("addition", null, line, text[line - 1])))])).ToArray();
        var bytes = diffs.Sum(diff => (long)diff.CanonicalBytes.Length);
        CapacitySpec.Require(bytes > 8 * 1024 * 1024 && bytes < AgentLimits.DiffSnapshotBytes, "snapshot_growth");
        var snapshot = new ReviewedSnapshot(identity, root, paths,
            diffs.Select(diff => new ReviewedChangedFile(diff.Path, null, "added", 64, 0, 64,
                "available", diff.PatchSha256, false)), diffs);
        var contents = Encoding.UTF8.GetBytes(string.Join('\n', text) + "\n").ToImmutableArray();
        var files = paths.ToImmutableDictionary(path => path, _ => contents, StringComparer.Ordinal);
        return new(snapshot, new ReplayMemoryFiles(snapshot, files), bytes);
    }
}

internal sealed record CapacityCall(string Id, string Name, string Arguments);

// Independent positive transcript inventory. Expectations come from authored run metadata
// (local) or each previously accepted Host context, never restored logical users.
internal static class CapacityUserOracle
{
    internal static string LocalText(string runId, bool fresh) =>
        (fresh ? CapacitySpec.FreshUser : CapacitySpec.OldUser) + "_" + runId + " Review this synthetic snapshot.";

    internal static void Verify(JsonElement[] messages, CapacityHistory[] history, CapacityCase selected,
        int sentTurns, bool fresh, string[]? hostUsers = null, string[]? forbiddenUsers = null)
    {
        var users = hostUsers ?? history.Select(run => LocalText(run.Id, fresh)).Append(LocalText(selected.Id, fresh)).ToArray();
        CapacitySpec.Require(users.Length == history.Length + 1 && users.All(text => !string.IsNullOrEmpty(text)), "logical_user_inventory");
        var expected = new List<(string Role, string? User)>();
        for (var index = 0; index < history.Length; index++)
        {
            expected.Add(("user", users[index]));
            AddTurns(history[index].Calls, history[index].ToolsPerTurn, history[index].Calls);
        }
        expected.Add(("user", users[^1]));
        AddTurns(selected.Calls, selected.ToolsPerTurn, sentTurns);
        var controlCount = messages.Length - expected.Count;
        CapacitySpec.Require(controlCount >= 1, "logical_user_message_count");
        foreach (var control in messages.Take(controlCount))
            CapacitySpec.Require(control.GetProperty("role").GetString() is "system" or "developer", "logical_user_control_prefix");
        // An intact user suffix does not prove that its untrusted contents were not also
        // copied into control/assistant/tool messages. Decode every string, including nested
        // values, so escaped newlines or a wrapping field cannot hide that promotion.
        var forbidden = (forbiddenUsers ?? []).Concat(fresh ? [CapacitySpec.OldUser] : []).ToArray();
        CapacitySpec.Require(forbidden.All(text => !string.IsNullOrEmpty(text)), "logical_user_forbidden_inventory");
        var forbiddenTokens = forbidden.SelectMany(Tokens).Distinct(StringComparer.Ordinal).ToArray();
        var userTokens = users.SelectMany(Tokens).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var message in messages)
        {
            CapacitySpec.Require(!forbiddenTokens.Any(text => Contains(message, text)), "logical_user_forbidden");
            if (message.GetProperty("role").GetString() != "user")
                CapacitySpec.Require(!userTokens.Any(text => Contains(message, text)), "logical_user_promotion");
        }
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = messages[controlCount + index]; var wanted = expected[index];
            CapacitySpec.Require(actual.GetProperty("role").GetString() == wanted.Role, "logical_user_placement");
            if (wanted.User is not null)
                CapacitySpec.Require(actual.GetProperty("contents").GetArrayLength() == 1 &&
                    actual.GetProperty("contents")[0].GetProperty("kind").GetString() == "text" &&
                    actual.GetProperty("contents")[0].GetProperty("text").GetString() == wanted.User, "logical_user_content");
        }
        void AddTurns(int total, int tools, int completed)
        {
            CapacitySpec.Require(completed >= 0 && completed <= total, "logical_user_turns");
            for (var turn = 0; turn < completed; turn++)
            {
                expected.Add(("assistant", null));
                for (var slot = 0; slot < (turn == total - 1 ? 1 : tools); slot++) expected.Add(("tool", null));
            }
        }
    }

    private static IEnumerable<string> Tokens(string context)
    {
        yield return context;
        // Real Host contexts have this independently authored identity line. Keep the
        // anchor observable even if a leaked context is wrapped as a serialized JSON string.
        foreach (var line in context.Split('\n'))
            if (line.StartsWith("head-sha=", StringComparison.Ordinal)) yield return line;
    }

    private static bool Contains(JsonElement value, string text) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(text, StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(item => Contains(item, text)),
        JsonValueKind.Object => value.EnumerateObject().Any(item =>
            item.Name.Contains(text, StringComparison.Ordinal) || Contains(item.Value, text)),
        _ => false,
    };
}
