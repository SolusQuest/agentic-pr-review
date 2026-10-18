using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.SolusFreshHostProof;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["self-test"])
            {
                await SelfTests.Run();
                Console.WriteLine("APR_SOLUS_ECONOMICS_SELF_TEST suites=" + string.Join(',', SelfTests.Passed) + " checks=" + Proof.Checks); return 0;
            }
            if (args is ["validate", var candidate])
            {
                if (Evidence.Read(ReadBounded(candidate)) is null) return 1;
                Console.WriteLine("APR_SOLUS_ECONOMICS_REPORT_VALID"); return 0;
            }
            if (args is ["rebind", var original])
            {
                var value = JsonSerializer.Deserialize(ReadBounded(original), EconomicsJson.Default.EconomicsDocument)!;
                var reboundBytes = Evidence.Write(Evidence.Seal(value)); Privacy.Protect(reboundBytes);
                Console.WriteLine(Encoding.UTF8.GetString(reboundBytes)); return 0;
            }
            if (args is ["compare", var leftPath, var rightPath])
            {
                var left = Evidence.Read(ReadBounded(leftPath)); var right = Evidence.Read(ReadBounded(rightPath));
                var result = left is null || right is null
                    ? new ComparisonFact("invalid_evidence", null, "invalid_evidence", null, "unknown", "not_evidenced", "inconclusive")
                    : Comparison.Create(left, right);
                Console.WriteLine(JsonSerializer.Serialize(result, EconomicsJson.Default.ComparisonFact)); return 0;
            }
            if (args is ["network-probe"])
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try { await socket.ConnectAsync("127.0.0.1", 9, deadline.Token); } catch (SocketException) { } catch (OperationCanceledException) { }
                Console.WriteLine("APR_SOLUS_ECONOMICS_NETWORK_PROBE"); return 0;
            }
            if (args is ["readback", var readbackRoot, var readbackTime, var readbackCorpus, var expectedReport])
            {
                await Readback(readbackRoot, long.Parse(readbackTime, System.Globalization.CultureInfo.InvariantCulture),
                    readbackCorpus, Evidence.Read(ReadBounded(expectedReport))!);
                Console.WriteLine("APR_SOLUS_ECONOMICS_READBACK"); return 0;
            }
            Proof.Require(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64, "v2_linux_x64");
            Proof.Require(args.Length == 4 && Evidence.Scenarios.Contains(args[1]), "v2_arguments");
            foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NUGET_AUTH_TOKEN", "NuGetPackageSourceCredentials_solus", "DEEPSEEK_API_KEY" })
                Proof.Require(Environment.GetEnvironmentVariable(key) is null, "v2_credential_free");
            var root = Path.GetFullPath(args[0]); var mode = args[1]; Proof.Stage = mode;
            var now = long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            using var corpus = new Corpus(args[3]);
            var seeding = mode == "seed"; var recovery = mode == "recovery";
            var seed = seeding ? null : Evidence.Read(ReadBounded(Path.Join(root, "seed-report.json")));
            Proof.Require(seeding || seed is { DurableAccepted: true }, "v2_accepted_seed_witness");
            using var fixture = new HostFixture(corpus, root, seeding ? "seed" : recovery ? "retry" : "review",
                seeding || !recovery && mode != "missing-key", now);
            await fixture.BindNamesAsync();
            var wire = new WireObservation();
            using var handler = new ControlledProvider(corpus, mode, wire, seed);
            using var cancellation = new CancellationTokenSource();
            if (mode == "stale-publication") handler.BeforeFinal = () => fixture.GitHub.CurrentOverride =
                fixture.GitHub.PullRequest with { HeadSha = new string('f', 40) };
            ObservedProvider? observer = null; DomainFact? domain = null;
            void Captured(SolusRestrictedStateRecord record)
            {
                fixture.Fault.CurrentReviewCaptured = true;
                var value = record.Compatibility;
                domain = new(value.PackageVersion, value.Profile, value.PackageClosureSha256, value.ImplementationId,
                    value.FormatVersion, value.CompatibilityVersion, value.RuntimeConfigurationSha256, value.ProviderConfigurationSha256);
            }
            SolusFreshReviewResult? work; ActionHostCompletion? completion; bool durable, recovered;
            if (seeding)
            {
                var result = await new SolusFreshStateCommitComposition(fixture.Host, () => handler,
                    trustedProviderDecorator: provider => observer = new(provider, wire), trustedCapturedRecord: Captured)
                    .RunAsync(fixture.Launch, cancellation.Token,
                        [new(AgentInputSource.Repository, Encoding.UTF8.GetString(corpus.Member("cases/cs-defect/context.txt")))]);
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                durable = result.Value?.DurableStateAccepted is true; recovered = result.Value?.RecoveredWithoutWork is true;
            }
            else
            {
                var result = await new SolusRestoredReviewComposition(fixture.Host, () => handler,
                    trustedProviderDecorator: provider => observer = new(mode == "reject-import" ? new RejectedImport(provider) : provider,
                        wire, mode == "cancel-after-usage" ? cancellation.Cancel : null),
                    trustedCaptureDecorator: mode == "capture-failure" ? sink => new RejectCapture() : null,
                    trustedCapturedRecord: Captured)
                    .RunAsync(fixture.Launch, cancellation.Token, [new(AgentInputSource.Repository, ControlledProvider.CurrentOnly)]);
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                durable = result.Value?.DurableStateAccepted is true; recovered = result.Value?.RecoveredWithoutWork is true;
            }
            Proof.Require(!handler.InvariantFailure, "v2_wire_oracle_passed");
            var exchanges = observer?.Freeze() ?? [];
            var wireSnapshot = wire.Freeze();
            var current = work?.Work?.CurrentUsage is { } usage ? RunFact.From(usage) : null;
            var history = work?.Context?.History.Select(r => new RoundFact(r.LogicalWorkId, r.RoundId, RunFact.From(r.Usage))).ToArray() ?? [];
            if (handler.Sends > 0 && !seeding)
                Proof.Require(work?.Context?.Admission == ContextAdmission.Supplied && history.Length == 1 &&
                    history[0].RoundId == seed!.RoundId && history[0].LogicalWorkId == seed.LogicalWorkId &&
                    EqualRun(history[0].Usage, seed.Current!) && current?.ExecutionId != seed.Current!.ExecutionId,
                    "v2_exact_current_historical_attribution");
            var source = new SourceFact(FixtureSource.Commit, FixtureSource.Tree, FixtureSource.Clean,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))), corpus.ManifestSha256);
            var zero = completion?.Accounting.IsCompleteZero is true;
            var report = Evidence.Seal(new(Evidence.Format, "controlled", mode, source, domain, Environment.ProcessId,
                Evidence.Hash("startup", Guid.NewGuid().ToByteArray()), work?.Context?.Admission, work?.Context?.CaptureStatus,
                work?.Context?.Checkpoint?.LogicalWorkId, work?.Context?.Checkpoint?.RoundId,
                work?.Context?.Admission is ContextAdmission.Fresh or ContextAdmission.Supplied,
                current, history, exchanges, wireSnapshot.Requests, wireSnapshot.Final,
                work?.ProductReviewAccepted is true, durable, recovered, zero,
                work?.Observations.Length ?? 0, fixture.NativeCreates, SyntheticPrice.Create(current, zero), ""));
            var bytes = Evidence.Write(report);
            Proof.Require(Evidence.Read(bytes) is not null, "v2_report_consumer_admits_original");
            Privacy.Protect(bytes);
            if (File.Exists(Path.Join(root, "sticky.txt"))) Privacy.Protect(File.ReadAllBytes(Path.Join(root, "sticky.txt")), publication: true);
            Console.WriteLine(Encoding.UTF8.GetString(bytes)); return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("APR_SOLUS_ECONOMICS_FAILED " + (error is ProofFailure failure ? failure.Code : "unexpected"));
            return 1;
        }
    }
    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path); Proof.Require(stream.Length is > 0 and <= Evidence.MaximumReportBytes, "v2_report_bound");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    internal static bool EqualRun(RunFact left, RunFact right) => JsonSerializer.Serialize(left, EconomicsJson.Default.RunFact) ==
        JsonSerializer.Serialize(right, EconomicsJson.Default.RunFact);
    private static async Task Readback(string root, long now, string corpusPath, EconomicsDocument expected)
    {
        Proof.Require(OperatingSystem.IsLinux() && expected.DurableAccepted && expected.Current is not null && expected.Domain is not null,
            "v2_readback_selection");
        using var corpus = new Corpus(corpusPath);
        using var fixture = new HostFixture(corpus, root, "readback", false, now);
        var result = await fixture.Host.RunPreparedFreshAsync(fixture.Launch, async prepared => {
            var execution = new SolusFreshReviewComposition(fixture.Host).PrepareStateExecution(prepared);
            var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId,
                    SolusTransactionMode.CompletedContextReview) }, default);
            using var state = restored.Context;
            Proof.Require(restored.Succeeded && state is not null &&
                RestrictedStateService.TryReadAcceptedSolusRecord(state, out var record) && record is not null &&
                record.Checkpoint.ExecutionId == expected.Current!.ExecutionId && record.Checkpoint.RoundId == expected.RoundId &&
                record.Checkpoint.LogicalWorkId == expected.LogicalWorkId &&
                record.Compatibility.RuntimeConfigurationSha256 == expected.Domain!.RuntimeConfigurationSha256 &&
                record.Compatibility.ProviderConfigurationSha256 == expected.Domain.ProviderConfigurationSha256,
                "v2_authenticated_exact_readback");
            return new ReadbackResult(true);
        }, default);
        Proof.Require(result.Value?.Verified is true && fixture.NativeCreates == 0, "v2_readback_no_provider");
    }
    private sealed record ReadbackResult(bool Verified);
    private sealed class RejectCapture : IRestrictedContextSink
    { public void Capture(AgentContextEnvelope context) => throw new InvalidOperationException("V2_PRIVATE_CAPTURE_CANARY"); }
}

internal static class Privacy
{
    internal static void Protect(ReadOnlySpan<byte> bytes, bool publication = false)
    {
        Proof.Require(bytes.Length <= Evidence.MaximumReportBytes, "v2_privacy_bound");
        var text = Encoding.UTF8.GetString(bytes);
        var decoded = new List<string> { text };
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 });
            void Strings(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.String) decoded.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Strings(item);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject())
                    { decoded.Add(property.Name); Strings(property.Value); }
            }
            Strings(document.RootElement);
        }
        catch (JsonException) { }
        var canaries = new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
            ControlledProvider.CurrentOnly, ControlledProvider.Reasoning, ControlledProvider.ProviderRequestId,
            "V2_PRIVATE_CAPTURE_CANARY", "V2_PRIVATE_EXCEPTION_CANARY", "APR242_POLICY_CANARY", HistoryOracle.Fact };
        foreach (var value in canaries)
        {
            // Review summaries can describe the public source; the exact private
            // fixture line still must never be copied as a diagnostic or dump.
            Proof.Require(decoded.All(part => !part.Contains(value, StringComparison.Ordinal)) &&
                !text.Contains(JsonSerializer.Serialize(value)[1..^1], StringComparison.Ordinal) &&
                !text.Contains(System.Net.WebUtility.HtmlEncode(value), StringComparison.Ordinal) &&
                !text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), StringComparison.Ordinal),
                publication ? "v2_safe_publication" : "v2_safe_report");
        }
    }
}
