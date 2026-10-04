using AgenticPrReview.Runtime.Agent.Session;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed partial class R5LiveHarnessTests
{
    [Theory]
    [InlineData(true, 65536)]
    [InlineData(true, 123)]
    [InlineData(false, 65536)]
    [InlineData(false, 123)]
    [InlineData(true, 0)]
    [InlineData(false, -1)]
    [InlineData(false, 65537)]
    public async Task AdmittedEvaluatorReservationRestrictsActualWireWithoutReleasingReservation(bool generated, int remaining)
    {
        using var file = new PlanFile();
        if (generated) Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus, file.Path).Item1);
        var plan = LivePlanAdmission.Load(file.Path, false, default);
        var cap = generated ? 512 : 4096;
        Assert.Equal(cap, plan.Bounds.PerCall.MaxOutputTokens);
        if (generated) Assert.Equal(104_000, plan.Bounds.SpendCeilingMicroUsd);
        var accounting = new LiveAccounting(plan.Bounds);
        var captured = 0;
        byte[]? sent = null;
        var inner = new FakeTransport((body, _) =>
        {
            sent = body.ToArray();
            using var wire = JsonDocument.Parse(body);
            captured = wire.RootElement.GetProperty("max_tokens").GetInt32();
            return Task.FromResult(DeepSeekTransportResult.TransportFailure());
        });
        using var metered = new LiveMeteredTransport(inner, accounting);
        var backend = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter, "session"), metered);
        var observer = new LiveChatObserver(backend, accounting);
        var run = ReplayAdmission.Load(Corpus).Fixture!.Runs[0];
        var trusted = run.CreateTrustedRequest("budget-wire-test");
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var bootstrap = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable!.StablePlan, "session",
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var boundary = PrefixBoundary.Bootstrap(trusted, bootstrap);
        var baseline = HistoryChatClient.Measure(boundary,
            LiveOutputCapClient.Restrict(HistoryCapture.Request(bootstrap), cap));
        Assert.NotNull(baseline);
        var growth = new GrowthChatMeasurement(observer);
        var history = new HistoryChatClient(boundary, growth, baseline);
        var client = new LiveOutputCapClient(history, cap);
        var request = HistoryCapture.Request(bootstrap) with { MaxOutputTokens = remaining };
        var valid = remaining is >= 1 and <= 65_536;
        if (valid)
            await Assert.ThrowsAsync<DeepSeekChatBackendException>(() => client.GetResponseAsync(request, default));
        else
            await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => client.GetResponseAsync(request, default));
        Assert.Equal(1, growth.Counts.Calls);
        Assert.Equal(AgentRequestWriter.Write(request).Length, growth.Counts.LastProjectRequestBytes);
        var measured = Assert.Single(history.Capture.Calls);
        if (valid)
        {
            Assert.NotNull(measured);
            Assert.NotNull(sent);
            Assert.Equal(sent.Length, measured.Provider.Whole.Bytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes("apr.r6.prefix.provider.whole\0"));
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, sent.Length);
            hash.AppendData(length);
            hash.AppendData(sent);
            Assert.Equal(Convert.ToHexStringLower(hash.GetHashAndReset()), measured.Provider.Whole.Sha256);
            if (remaining >= cap) Assert.Equal(baseline.Provider.Whole, measured.Provider.Whole);
        }
        else Assert.Null(measured);
        Assert.Equal(valid ? 1 : 0, inner.Calls);
        Assert.Equal(valid ? Math.Min(cap, remaining) : 0, captured);
        Assert.Equal(valid ? cap : 0, accounting.ReservedOutputTokens);
        Assert.Equal(valid ? plan.Bounds.PerCall.MaxChargeMicroUsd : 0, accounting.ReservedSpendMicroUsd);
        Assert.Equal(valid ? plan.Bounds.PerCall.MaxInputTokens + cap : 0, accounting.ReservedCombinedTokens);
    }
}
