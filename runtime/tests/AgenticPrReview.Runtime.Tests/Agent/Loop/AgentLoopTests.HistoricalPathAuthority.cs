using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData("read_file", "{\"path\":\"src/a.cs\"}", false)]
    [InlineData("search_text", "{\"query\":\"line\",\"path\":\"src/a.cs\"}", false)]
    [InlineData("read_diff", "{\"path\":\"src/a.cs\"}", true)]
    [InlineData("list_files", "{\"after\":\"src/a.cs\"}", false)]
    [InlineData("list_changed_files", "{\"after\":\"src/a.cs\"}", false)]
    public async Task AuthoredHistoricalPathCannotGrantCurrentSnapshotAuthority(
        string tool, string arguments, bool remainsTracked)
    {
        var trusted = CompletedInputTrusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
        var current = Identity with { HeadSha = new string('2', 40) };
        // Finite typed history exercises current tool authority; this is not an
        // SDK import or a reconstructed durable SESSION.
        var run = new AgentRunRequest(current, materialized!.StablePlan, "session0",
            [.. materialized.ControlMessages,
             new("user", [new ProjectTextContent("previous review")]),
             new("assistant", [new ProjectToolCallContent("read0", "read_file", "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":400}")]),
             new("tool", [new ProjectToolResultContent("read0", "{}")]),
             new("user", [new ProjectTextContent("Review the current snapshot.")])]);
        var snapshot = new ReviewedSnapshot(
            current, Directory.GetCurrentDirectory(),
            remainsTracked ? ["src/a.cs", "current.txt"] : ["current.txt"]);
        var files = new CurrentFileOnlyAccess();
        var turn = 0;
        var outcome = await new AgentLoop(
            new CallbackChatClient((request, ordinal) =>
            {
                ProjectToolCallContent[] calls;
                switch (turn++)
                {
                    case 0:
                        calls = [new("discover-current", "list_files", "{}"), new("reuse-path", tool, arguments)];
                        break;
                    case 1:
                        Assert.Equal(0, files.Reads);
                        Assert.Equal(2, request.Messages.SelectMany(message => message.Contents)
                            .OfType<ProjectToolErrorContent>().Count());
                        calls = [new("lookup-corrected", "list_files", "{}")];
                        break;
                    case 2:
                        calls = [new("read-corrected", "read_file", "{\"path\":\"current.txt\"}")];
                        break;
                    default:
                        var result = request.Messages.SelectMany(message => message.Contents)
                            .OfType<ProjectToolResultContent>().Last();
                        using (var json = JsonDocument.Parse(result.Result))
                        {
                            var observation = json.RootElement.GetProperty("observation_id").GetString();
                            calls = [new("finish-corrected", "finish_review",
                                "{\"summary\":\"found\",\"findings\":[{\"severity\":\"high\",\"title\":\"bug\",\"message\":\"fix\",\"evidence\":[{\"observation_id\":\"" +
                                observation + "\",\"path\":\"current.txt\",\"start_line\":1,\"end_line\":1}]}]}")];
                        }
                        break;
                }
                return Task.FromResult(new ProjectChatResponse(new("assistant", calls), new(1, 1), 1));
            }),
            new SnapshotToolExecutor(snapshot, files))
            .RunAsync(run, CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
        Assert.Equal(1, files.Reads);
        Assert.Equal(2, outcome.Events.OfType<AgentRecoveryToolCallEvent>().Count());
        Assert.Equal(2, outcome.Events.OfType<AgentToolResultEvent>().Count());
    }

    private sealed class CurrentFileOnlyAccess : IReviewedFileAccess
    {
        private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("line\n");
        internal int Reads { get; private set; }
        public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length);
        }
        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length, new(1, 1));
        }
        public ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path,
            ReviewedFileProbe expected, CancellationToken cancellationToken)
        {
            Assert.Equal("current.txt", path);
            Reads++;
            return ValueTask.FromResult(new ReviewedFileRead(ReviewedFileAccessStatus.Success, Bytes));
        }
    }
}
