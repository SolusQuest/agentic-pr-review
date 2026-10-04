using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R7CapacityUserOracleTests
{
    private static readonly CapacityHistory[] History = [new("older", 1, 0), new("recent", 1, 0)];
    private static readonly CapacityCase Current = new("current", "success", 2, 1, 64);
    // Authored wire, independent of the oracle's transcript construction. All user contexts
    // share the old marker but have distinct run identity, including the current context.
    private static JsonArray Wire() => JsonNode.Parse("""
        [
          {"role":"system","content":"trusted control"},
          {"role":"user","content":"APR_R7_PRIVATE_USER_9d421_older Review this synthetic snapshot."},
          {"role":"assistant","content":""},
          {"role":"tool","content":"{}","tool_call_id":"older_0_0"},
          {"role":"user","content":"APR_R7_PRIVATE_USER_9d421_recent Review this synthetic snapshot."},
          {"role":"assistant","content":""},
          {"role":"tool","content":"{}","tool_call_id":"recent_0_0"},
          {"role":"user","content":"APR_R7_PRIVATE_USER_9d421_current Review this synthetic snapshot."}
        ]
        """)!.AsArray();

    private static JsonElement[] Messages(JsonArray wire)
    {
        using var document = JsonDocument.Parse(wire.ToJsonString());
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
    private static void Verify(JsonArray wire, int turns = 0) =>
        CapacityUserOracle.Verify(Messages(wire), History, Current, turns, fresh: false);
    private static void Rejected(JsonArray wire) => Assert.Throws<InvalidOperationException>(() => Verify(wire));

    [Fact]
    public void CompleteAuthoredHistoryAndWithinRunPlacementAreRequired()
    {
        Verify(Wire());
        var wire = Wire();
        wire.Add(JsonNode.Parse("""{"role":"assistant","content":""}"""));
        wire.Add(JsonNode.Parse("""{"role":"tool","content":"{}","tool_call_id":"current_0_0"}"""));
        Verify(wire, turns: 1);
        var current = wire[7]!.DeepClone(); wire.RemoveAt(7); wire.Add(current);
        Assert.Throws<InvalidOperationException>(() => Verify(wire, turns: 1));
    }

    [Fact]
    public void EveryRestoredAndCurrentUserMustBePresentExactlyOnceWithItsOwnContent()
    {
        foreach (var index in new[] { 1, 4, 7 })
        {
            var deleted = Wire(); deleted.RemoveAt(index); Rejected(deleted);
            var replaced = Wire(); replaced[index]!["content"] = Wire()[index == 7 ? 1 : 7]!["content"]!.DeepClone(); Rejected(replaced);
            var duplicated = Wire(); duplicated.Insert(index, duplicated[index]!.DeepClone()); Rejected(duplicated);
        }
        // The current user retains the very same marker that previously made omission vacuous.
        var absentHistory = Wire(); absentHistory.RemoveAt(4); absentHistory.RemoveAt(1); Rejected(absentHistory);
    }

    [Fact]
    public void UserRoleOrderAndPositionCannotBeReplacedByCorrectCanaryPresence()
    {
        foreach (var index in new[] { 1, 4, 7 })
            foreach (var role in new[] { "system", "developer", "assistant", "tool" })
            {
                var changed = Wire(); changed[index]!["role"] = role; Rejected(changed);
            }
        var swapped = Wire(); var older = swapped[1]!.DeepClone(); var recent = swapped[4]!.DeepClone();
        swapped[1] = recent; swapped[4] = older; Rejected(swapped);
        var relocated = Wire(); var user = relocated[1]!.DeepClone(); relocated.RemoveAt(1); relocated.Insert(3, user); Rejected(relocated);
    }

    [Fact]
    public void ResetRequiresBothFreshContextsAndRejectsOldHistory()
    {
        static JsonArray Fresh() => JsonNode.Parse("""
            [
              {"role":"system","content":"trusted control"},
              {"role":"user","content":"APR_R7_PRIVATE_FRESH_USER_54ea7_reset Review this synthetic snapshot."},
              {"role":"assistant","content":""}, {"role":"tool","content":"{}"},
              {"role":"assistant","content":""}, {"role":"tool","content":"{}"},
              {"role":"user","content":"APR_R7_PRIVATE_FRESH_USER_54ea7_reset_restore Review this synthetic snapshot."}
            ]
            """)!.AsArray();
        static void Check(JsonArray wire) => CapacityUserOracle.Verify(Messages(wire), [new("reset", 2, 1)],
            new("reset_restore", "success", 1, 0, 64), 0, fresh: true);
        Check(Fresh());
        foreach (var index in new[] { 1, 6 })
        {
            var missing = Fresh(); missing.RemoveAt(index); Assert.Throws<InvalidOperationException>(() => Check(missing));
            var old = Fresh(); old[index]!["content"] = Wire()[1]!["content"]!.DeepClone();
            Assert.Throws<InvalidOperationException>(() => Check(old));
        }
        var inserted = Fresh(); inserted.Insert(1, Wire()[1]!.DeepClone()); Assert.Throws<InvalidOperationException>(() => Check(inserted));
    }

    [Fact]
    public void HostHistoryUsesPreviouslyAcceptedContextsRatherThanReturnedWireUsers()
    {
        var expected = new[] { "authored head-sha=100", "authored head-sha=101", "authored head-sha=102" };
        var wire = Wire();
        for (var index = 0; index < 3; index++) wire[new[] { 1, 4, 7 }[index]]!["content"] = expected[index];
        void Check(JsonArray value) => CapacityUserOracle.Verify(Messages(value), History, Current, 0, false, expected);
        Check(wire);
        var omitted = wire.DeepClone().AsArray(); omitted.RemoveAt(1); Assert.Throws<InvalidOperationException>(() => Check(omitted));
        var replaced = wire.DeepClone().AsArray(); replaced[1]!["content"] = expected[2];
        Assert.Throws<InvalidOperationException>(() => Check(replaced));
        var swapped = wire.DeepClone().AsArray(); swapped[1]!["content"] = expected[1]; swapped[4]!["content"] = expected[0];
        Assert.Throws<InvalidOperationException>(() => Check(swapped));
    }
}
