using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using System.Text;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using Xunit;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofDeterministicProviderTests
{
    private static DeepSeekAdapterContext Context => new(DeepSeekAdapterContext.Provider,
        DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "trusted-proof-test");

    [Fact]
    public async Task BootstrapUsesAuthoredTypedMessagesAndWaitsAtValueFreeSignal()
    {
        var signal = new TrustedProofStaleSignal();
        var script = new TrustedProofDeterministicDeepSeekHandler(Context, signal);
        var messages = new List<ProjectChatMessage>();
        var first = await Send(script, messages);
        Assert.Equal("list_changed_files", Call(first).Name);
        AppendExchange(messages, first);
        var secondTask = Send(script, messages);
        await signal.Ready;
        Assert.False(secondTask.IsCompleted);
        signal.Release();
        var second = await secondTask;
        Assert.Equal("list_files", Call(second).Name);
        AppendExchange(messages, second);
        var names = new List<string> { "list_changed_files", "list_files" };
        for (var ordinal = 3; ordinal <= 6; ordinal++)
        {
            var response = await Send(script, messages);
            names.Add(Call(response).Name);
            Assert.Equal(0, response.CapturedResponseBodyBytes); // authored admission sentinel, no wire measurement
            AppendExchange(messages, response);
        }
        Assert.Equal(["list_changed_files", "list_files", "read_diff", "read_file", "search_text", "finish_review"], names);
    }

    [Fact]
    public async Task RestoredContinuationRequiresExactPriorExchangesAndIsDistinct()
    {
        var messages = await BootstrapHistory();
        var response = await Send(new TrustedProofDeterministicDeepSeekHandler(Context), messages);
        Assert.Equal("read_file", Call(response).Name);
        Assert.DoesNotContain(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker,
            Assert.IsType<ProjectReasoningContent>(response.Message.Contents[0]).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list_changed_files", "{}")]
    [InlineData("list_changed_files", "{\"after\":\"forged\"}")]
    [InlineData("list_files", "{}")]
    [InlineData("list_files", "{\"prefix\":null}")]
    public async Task TypedHistoryRejectsRawOrMalformedCanonicalArguments(string toolName, string canonicalArguments)
    {
        var script = new TrustedProofDeterministicDeepSeekHandler(Context);
        var messages = new List<ProjectChatMessage>();
        AppendExchange(messages, await Send(script, messages));
        if (toolName == "list_files") AppendExchange(messages, await Send(script, messages));
        var index = messages.FindIndex(message => message.Contents.OfType<ProjectToolCallContent>().Any(call => call.Name == toolName));
        var call = Assert.Single(messages[index].Contents.OfType<ProjectToolCallContent>());
        messages[index] = messages[index] with { Contents = [messages[index].Contents[0], call with { ArgumentsJson = canonicalArguments }] };
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Send(script, messages));
    }

    [Fact]
    public async Task ContinuationRejectsCurrentContentInjectionAndMissingHistory()
    {
        var injected = await BootstrapHistory();
        injected.Add(new("user", [new ProjectTextContent(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker)]));
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Send(new TrustedProofDeterministicDeepSeekHandler(Context), injected));
        var missing = await BootstrapHistory();
        missing.RemoveAt(1);
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Send(new TrustedProofDeterministicDeepSeekHandler(Context), missing));
    }

    [Theory]
    [InlineData("swapped-exchanges")]
    [InlineData("late-result")]
    [InlineData("extra-exchange")]
    [InlineData("duplicate-call")]
    [InlineData("duplicate-result")]
    [InlineData("missing-marker")]
    [InlineData("duplicate-marker")]
    [InlineData("wrong-carrier")]
    public async Task ContinuationRejectsNonExactGlobalExchangeOrder(string mutation)
    {
        var messages = await BootstrapHistory();
        switch (mutation)
        {
            case "swapped-exchanges": (messages[0], messages[2]) = (messages[2], messages[0]);
                (messages[1], messages[3]) = (messages[3], messages[1]); break;
            case "late-result": var result = messages[1]; messages.RemoveAt(1); messages.Insert(3, result); break;
            case "extra-exchange": messages.Add(new("assistant", [new ProjectToolCallContent("unexpected", "read_file", "{}")]));
                messages.Add(new("tool", [new ProjectToolResultContent("unexpected", "{}")])); break;
            case "duplicate-call": messages.Insert(1, messages[0]); break;
            case "duplicate-result": messages.Insert(2, messages[1]); break;
            case "missing-marker": messages[4] = messages[4] with { Contents = [messages[4].Contents[1]] }; break;
            case "duplicate-marker": messages.Add(new("user", [new ProjectTextContent(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker)])); break;
            case "wrong-carrier": messages[4] = messages[4] with { Contents = [new ProjectTextContent(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker), messages[4].Contents[1]] }; break;
            default: throw new InvalidOperationException();
        }
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Send(new TrustedProofDeterministicDeepSeekHandler(Context), messages));
    }

    [Fact]
    public async Task ContinuationCarriesPriorHistoryAcrossItsOwnToolExchanges()
    {
        var messages = await BootstrapHistory();
        var script = new TrustedProofDeterministicDeepSeekHandler(Context);
        var first = await Send(script, messages);
        Assert.Equal("read_file", Call(first).Name);
        AppendExchange(messages, first);
        var second = await Send(script, messages);
        Assert.Equal("search_text", Call(second).Name);
        AppendExchange(messages, second);
        var terminal = await Send(script, messages);
        Assert.Equal("finish_review", Call(terminal).Name);
        Assert.Equal("e2p-continuation-finish", Call(terminal).CallId);
        Assert.DoesNotContain(messages, message => message.Contents.OfType<ProjectToolCallContent>().Any(call => call.CallId == Call(terminal).CallId));
        Assert.Contains("Trusted continuation complete.", Call(terminal).ArgumentsJson);
        Assert.DoesNotContain(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker, Call(terminal).ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredScriptHasNoCredentialOrHttpConstructorCapability()
    {
        var constructor = Assert.Single(typeof(TrustedProofDeterministicDeepSeekHandler).GetConstructors());
        Assert.Equal([typeof(DeepSeekAdapterContext), typeof(ITrustedProofStaleSignal)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.False(typeof(HttpMessageHandler).IsAssignableFrom(typeof(TrustedProofDeterministicDeepSeekHandler)));
    }

    [Fact]
    public async Task VerifierProbeBuildsCanonicalHistoryBeforeRejectingReorderedExchanges()
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-verifier-order-probe-").FullName;
        try
        {
            var assembly = Observer(root, new PermissiveChat()).GetType().Assembly;
            var type = assembly.GetType("AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.TrustedProofVerifierHost", true)!;
            var method = type.GetMethod("RejectsReorderedContinuationAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(await Assert.IsType<Task<bool>>(method.Invoke(null, null)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActualLoopCanonicalHistoryPassesIndependentObserverForEveryScriptTool()
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-actual-loop-observer-").FullName;
        try
        {
            var trusted = new AgentTrustedRequest("repo", 404, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", Context.ProviderId, Context.ModelId, Context.AdapterId);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(new("repo", 404, new('a', 40), new('b', 40)), materialized!.StablePlan,
                Context.SessionId, [.. materialized.ControlMessages, new("user", [new ProjectTextContent("Review the fixed fixture.")])]);
            const string path = "proof/apr178-path-canary.txt";
            Directory.CreateDirectory(Path.Join(root, "proof"));
            File.WriteAllText(Path.Join(root, path), "trusted-proof\n");
            var diff = new ReviewedDiffSource(run.ReviewedIdentity, path, null, "modified", false,
                [new ReviewedDiffHunk(0, 0, 1, 1, [new ReviewedDiffLine("addition", null, 1, "trusted-proof")])]);
            var snapshot = new ReviewedSnapshot(run.ReviewedIdentity, root, [path],
                [new ReviewedChangedFile(path, null, "modified", 1, 0, 1, "available", diff.PatchSha256, false)], [diff]);
            var tools = new AcceptedTools(new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()));
            var outcome = await new AgentLoop(Observer(root, new TrustedProofDeterministicDeepSeekHandler(Context)), tools).RunAsync(run, default);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
            Assert.Equal(["list_changed_files", "list_files", "read_diff", "read_file", "search_text"], tools.Names);
            Assert.True(FrameworkSupervisor.TypedTraceIsValid(root, 6, false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CapacityObserverReadsRealLoopCarrierAndRejectsMetadataMutations()
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-capacity-observer-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "mode"), "r7-capacity-0");
            var trusted = new AgentTrustedRequest("repo", 404, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", Context.ProviderId, Context.ModelId, Context.AdapterId);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(new("repo", 404, new('a', 40), new('b', 40)), materialized!.StablePlan,
                Context.SessionId, [.. materialized.ControlMessages, new("user", [new ProjectTextContent("Review the fixed capacity fixture.")])]);
            const string path = FrameworkCanaries.ReviewedPath;
            Directory.CreateDirectory(Path.Join(root, "proof"));
            File.WriteAllText(Path.Join(root, path), FrameworkCanaries.ToolData + "\n");
            var diff = new ReviewedDiffSource(run.ReviewedIdentity, path, null, "modified", false,
                [new ReviewedDiffHunk(0, 0, 1, 1, [new ReviewedDiffLine("addition", null, 1, FrameworkCanaries.ToolData)])]);
            var snapshot = new ReviewedSnapshot(run.ReviewedIdentity, root, [path],
                [new ReviewedChangedFile(path, null, "modified", 1, 0, 1, "available", diff.PatchSha256, false)], [diff]);
            var capture = new CapacityCapture(new FrameworkProviderHandler(root, Context));
            var outcome = await new AgentLoop(capture, new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess())).RunAsync(run, default);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code + ":" + capture.Failure);
            Assert.Equal(6, capture.Requests.Count);
            var request = capture.Requests[2];
            var items = request.Continuation!.Items;
            Assert.Equal(2, items.Length);
            Assert.DoesNotContain(request.Messages.SelectMany(message => message.Contents), content => content is ProjectReasoningContent);
            void Verify(ProjectChatRequest value) => FrameworkSessionCapacity.Observe(root, value, AgentRequestWriter.Write(value), 3, "r7-capacity-0");
            Verify(request);
            var first = items[0];
            foreach (var replacement in new[]
            {
                first with { Readable = "wrong" }, first with { Opaque = "wrong" },
                first with { Framing = "wrong" }, first with { AssociatedCallId = "wrong" },
                first with { MessagePosition = first.MessagePosition + 1 }, first with { ContentPosition = 1 },
            })
                Assert.Throws<InvalidOperationException>(() => Verify(request with { Continuation = request.Continuation with { Items = [replacement, items[1]] } }));
            foreach (var changed in new[] { items.Reverse().ToArray(), [first, first], [first], [.. items, first] })
                Assert.Throws<InvalidOperationException>(() => Verify(request with { Continuation = request.Continuation with { Items = changed } }));
            Assert.Throws<InvalidOperationException>(() => Verify(request with { Continuation = null }));
            var messages = request.Messages.ToArray();
            var inline = new ProjectReasoningContent(first.Readable, first.Opaque, first.Framing, first.AssociatedCallId, first.MessagePosition, first.ContentPosition);
            messages[first.MessagePosition] = messages[first.MessagePosition] with { Contents = [inline, .. messages[first.MessagePosition].Contents] };
            Verify(request with { Messages = messages });
            messages[first.MessagePosition] = messages[first.MessagePosition] with { Contents = [inline with { Text = "conflict" }, .. request.Messages[first.MessagePosition].Contents] };
            Assert.Throws<InvalidOperationException>(() => Verify(request with { Messages = messages }));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CapacityCapture(IProjectChatClient inner) : IProjectChatClient
    {
        internal List<ProjectChatRequest> Requests { get; } = [];
        internal Exception? Failure { get; private set; }
        public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        { Requests.Add(request); try { return await inner.GetResponseAsync(request, token); } catch (Exception error) { Failure = error; throw; } }
    }

    private sealed class AcceptedTools(IAgentToolExecutor inner) : IAgentToolExecutor
    {
        internal List<string> Names { get; } = [];
        public string? Preflight(PreparedAgentToolCall call) => inner.Preflight(call);
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Names.Add(call.Name);
            return inner.ExecuteAsync(call, token);
        }
    }

    [Fact]
    public async Task IndependentObserverReadsContinuationWithoutDuplicateInlineReasoning()
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-observer-").FullName;
        try
        {
            var script = new TrustedProofDeterministicDeepSeekHandler(Context);
            var observer = Observer(root, script);
            var messages = new List<ProjectChatMessage>();
            for (var ordinal = 0; ordinal < 6; ordinal++)
            {
                var request = ObservedRequest(messages);
                var response = await observer.GetResponseAsync(request, default);
                AppendExchange(messages, response);
            }
            Assert.True(FrameworkSupervisor.TypedTraceIsValid(root, 6, false));
            var trace = Path.Join(root, "verifier-typed-chat-trace.tsv");
            var original = File.ReadAllLines(trace);
            foreach (var mutation in new[] { "missing", "reordered", "duplicate", "marker", "outcome", "hash", "argument-hash", "domain", "counter", "sequence", "http" })
            {
                var lines = original.ToArray();
                File.WriteAllText(Path.Join(root, "synthetic-chat-call-count"), "6");
                File.WriteAllText(Path.Join(root, "synthetic-chat-sequence"), "6");
                File.WriteAllText(Path.Join(root, "provider-request-count"), "0");
                switch (mutation)
                {
                    case "missing": lines = lines[..^1]; break;
                    case "reordered": (lines[0], lines[1]) = (lines[1], lines[0]); break;
                    case "duplicate": lines[1] = lines[0]; break;
                    case "marker": { var fields = lines[3].Split('\t'); fields[3] = "0"; lines[3] = string.Join('\t', fields); break; }
                    case "outcome": lines[2] = lines[2].Replace("succeeded", "cancelled", StringComparison.Ordinal); break;
                    case "hash": { var fields = lines[2].Split('\t'); fields[8] = "bad"; lines[2] = string.Join('\t', fields); break; }
                    case "argument-hash": { var fields = lines[3].Split('\t'); var values = fields[7].Split(','); values[0] = new string('0', 64); fields[7] = string.Join(',', values); lines[3] = string.Join('\t', fields); break; }
                    case "domain": lines[0] = lines[0].Replace("apr-historical-authored-typed-chat-v1", "wire-valid", StringComparison.Ordinal); break;
                    case "counter": File.WriteAllText(Path.Join(root, "synthetic-chat-call-count"), "5"); break;
                    case "sequence": File.WriteAllText(Path.Join(root, "synthetic-chat-sequence"), "5"); break;
                    case "http": File.WriteAllText(Path.Join(root, "provider-request-count"), "1"); break;
                }
                File.WriteAllLines(trace, lines);
                Assert.False(FrameworkSupervisor.TypedTraceIsValid(root, 6, false), mutation);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("swapped")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("marker")]
    [InlineData("carrier")]
    [InlineData("position")]
    [InlineData("association")]
    public async Task IndependentObserverRejectsHistoryBeforePermissiveInnerScript(string mutation)
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-observer-negative-").FullName;
        try
        {
            var messages = await BootstrapHistory();
            var request = ObservedRequest(messages);
            var items = request.Continuation!.Items.ToArray();
            switch (mutation)
            {
                case "swapped": (request.Messages[0], request.Messages[2]) = (request.Messages[2], request.Messages[0]);
                    (request.Messages[1], request.Messages[3]) = (request.Messages[3], request.Messages[1]); break;
                case "missing": request = request with { Messages = request.Messages[..^2] }; break;
                case "duplicate": request = request with { Messages = [request.Messages[0], .. request.Messages] }; break;
                case "marker": request = request with { Continuation = request.Continuation with { Items = items.Where(item => !item.Readable.Contains(TrustedProofDeterministicDeepSeekHandler.ContinuationMarker, StringComparison.Ordinal)).ToArray() } }; break;
                case "carrier": request.Messages[4] = request.Messages[4] with { Contents = [new ProjectToolCallContent("wrong", "read_diff", "{}")] }; break;
                case "position": items[2] = items[2] with { ContentPosition = 1 }; request = request with { Continuation = request.Continuation with { Items = items } }; break;
                case "association": items[2] = items[2] with { AssociatedCallId = "wrong" }; request = request with { Continuation = request.Continuation with { Items = items } }; break;
            }
            var inner = new PermissiveChat();
            await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Observer(root, inner).GetResponseAsync(request, default));
            Assert.Equal(0, inner.Calls);
            Assert.Contains("\tfailed\t", File.ReadAllText(Path.Join(root, "verifier-typed-chat-trace.tsv")), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task IndependentObserverRejectsArgumentsForEveryHistoricalTool(int exchange)
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-observer-arguments-").FullName;
        try
        {
            var request = ObservedRequest(await BootstrapHistory());
            var position = exchange * 2;
            var call = Assert.IsType<ProjectToolCallContent>(Assert.Single(request.Messages[position].Contents));
            request.Messages[position] = request.Messages[position] with { Contents = [call with { ArgumentsJson = "{}" }] };
            var inner = new PermissiveChat();
            await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Observer(root, inner).GetResponseAsync(request, default));
            Assert.Equal(0, inner.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IndependentObserverRecordsActualFailureAndCancellation(bool cancelled)
    {
        var root = Directory.CreateTempSubdirectory("apr-r8-observer-outcome-").FullName;
        try
        {
            var observer = Observer(root, new ThrowingChat(cancelled));
            if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => observer.GetResponseAsync(new([], [], null), default));
            else await Assert.ThrowsAsync<IOException>(() => observer.GetResponseAsync(new([], [], null), default));
            Assert.Equal("1", File.ReadAllText(Path.Join(root, "synthetic-chat-call-count")));
            Assert.Contains(cancelled ? "\tcancelled\t" : "\tfailed\t", File.ReadAllText(Path.Join(root, "verifier-typed-chat-trace.tsv")), StringComparison.Ordinal);
            Assert.False(FrameworkSupervisor.TypedTraceIsValid(root, 6, false));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProjectChatRequest ObservedRequest(List<ProjectChatMessage> messages) => new(
        messages.Select(message => message with { Contents = message.Contents.Where(part => part is not ProjectReasoningContent).ToArray() }).ToArray(),
        [], Continuation(messages));

    private static IProjectChatClient Observer(string root, IProjectChatClient inner)
    {
        // The normal unit project builds the separate verifier without linking its observer into product code.
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = Assert.Single(typeof(TrustedProofDeterministicProviderTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>()).Configuration;
        Assert.Contains(configuration, new[] { "Debug", "Release" });
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Join(repository.FullName, "runtime", "tests",
            "ActionHostTrustedProofVerifier", "bin", configuration, "net10.0", "AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll"));
        var type = assembly.GetType("AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.VerifierRecordingChatClient", true)!;
        return Assert.IsAssignableFrom<IProjectChatClient>(Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [root, inner], null));
    }
    private sealed class PermissiveChat : IProjectChatClient
    {
        internal int Calls { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(new ProjectChatResponse(new("assistant", []), CapturedResponseBodyBytes: 0)); }
    }
    private sealed class ThrowingChat(bool cancelled) : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token) =>
            throw (cancelled ? new OperationCanceledException() : new IOException());
    }

    private static Task<ProjectChatResponse> Send(IProjectChatClient script, List<ProjectChatMessage> messages) =>
        script.GetResponseAsync(new(messages.ToArray(), [], Continuation(messages)), CancellationToken.None);

    private static ProjectContinuation? Continuation(List<ProjectChatMessage> messages)
    {
        var items = messages.SelectMany(message => message.Contents.OfType<ProjectReasoningContent>())
            .Select(reasoning => new ProjectContinuationItem(reasoning.Text, reasoning.Opaque, reasoning.Framing,
                reasoning.AssociatedCallId, reasoning.MessagePosition, reasoning.Position)).ToArray();
        return items.Length == 0 ? null : new(Context.ProviderId, Context.ModelId, Context.AdapterId, Context.SessionId, items);
    }

    private static async Task<List<ProjectChatMessage>> BootstrapHistory()
    {
        var script = new TrustedProofDeterministicDeepSeekHandler(Context);
        var messages = new List<ProjectChatMessage>();
        for (var ordinal = 0; ordinal < 6; ordinal++) AppendExchange(messages, await Send(script, messages));
        return messages;
    }
    private static ProjectToolCallContent Call(ProjectChatResponse response) => Assert.Single(response.Message.Contents.OfType<ProjectToolCallContent>());
    private static void AppendExchange(List<ProjectChatMessage> messages, ProjectChatResponse response)
    {
        var call = Call(response);
        var admitted = call with { ArgumentsJson = call.Name switch
        {
            "list_changed_files" => "{\"after\":null}",
            "list_files" => "{\"prefix\":null,\"after\":null}",
            _ => call.ArgumentsJson,
        } };
        messages.Add(response.Message with { Contents = [response.Message.Contents[0], admitted] });
        messages.Add(new("tool", [new ProjectToolResultContent(call.CallId, "{\"result\":\"accepted\"}")]));
    }
}
