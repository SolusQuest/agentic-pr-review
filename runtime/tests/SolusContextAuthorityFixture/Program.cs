using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Solus;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Tools.Api;

try
{
    if (!OperatingSystem.IsLinux()) throw new ProofFailure("platform");
    Proof.Require(!Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key =>
        key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("NUGET_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NuGetPackageSourceCredentials_", StringComparison.OrdinalIgnoreCase)), "credentials");
    if (args is ["child", var childRoot, var id, var executionText, var roundText])
    {
        File.WriteAllText(Path.Join(childRoot, "ready-" + id), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(Path.Join(childRoot, "go"))) await Task.Delay(20, timeout.Token);
        await using var fixture = await Fixture.OpenAsync(childRoot, timeout.Token);
        var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
            fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, timeout.Token);
        using var source = selected.SolusSource;
        Proof.Require(source is not null, "source_selection");
        using var consumed = await source!.PrepareAsync(Guid.Parse(executionText), Guid.Parse(roundText), timeout.Token);
        if (consumed is null)
        {
            Proof.Require(fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0, "deny_effects");
            Console.WriteLine("DENIED import=0 exchange=0 tools=0 acceptance=0");
            return 10;
        }
        Proof.Require(fixture.Runtime.TryArm(consumed, out var context, out var grant), "arm");
        var host = new CandidateHost();
        _ = await ((IContextCandidateAgent)fixture.Runtime.Agent).ExecuteCandidatesWithContextAsync(
            new(Proof.Request(grant!.ExecutionId), ContextExecutionIntent.NewRunFromContext, context, grant),
            new(1, 0, 0), host);
        Proof.Require(consumed.WasClaimed && fixture.Provider.Imports > 0 && fixture.Provider.Calls == 0 &&
            fixture.Tool.Calls == 0 && host.Calls == 0, "burn_after_import");
        Console.WriteLine("WINNER claim=1 import=1 exchange=0 tools=0 acceptance=0");
        return 0;
    }
    Proof.Require(args.Length == 0, "arguments");
    var parent = Path.Join(Path.GetTempPath(), "apr-solus-context-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        foreach (var sameGrant in new[] { true, false })
        {
            var root = Path.Join(parent, sameGrant ? "same-grant" : "different-grants");
            Directory.CreateDirectory(root);
            await using (var seed = await Fixture.OpenAsync(root, CancellationToken.None))
                await seed.SeedAsync();
            var execution = Guid.NewGuid(); var round = Guid.NewGuid();
            using var first = Proof.Start(root, "first", execution, round);
            using var second = Proof.Start(root, "second", sameGrant ? execution : Guid.NewGuid(), sameGrant ? round : Guid.NewGuid());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(Path.Join(root, "ready-first")) || !File.Exists(Path.Join(root, "ready-second")))
                await Task.Delay(20, deadline.Token);
            File.WriteAllText(Path.Join(root, "go"), "go");
            await Task.WhenAll(first.WaitForExitAsync(deadline.Token), second.WaitForExitAsync(deadline.Token));
            var outputs = new[] { await first.StandardOutput.ReadToEndAsync(), await second.StandardOutput.ReadToEndAsync() };
            if (first.ExitCode is not (0 or 10) || second.ExitCode is not (0 or 10)) Console.Error.WriteLine($"race exits={first.ExitCode},{second.ExitCode} first={await first.StandardError.ReadToEndAsync()} second={await second.StandardError.ReadToEndAsync()}");
            Proof.Require(new[] { first.ExitCode, second.ExitCode }.Order().SequenceEqual(new[] { 0, 10 }), "exactly_one");
            Proof.Require(outputs.Count(text => text.StartsWith("WINNER", StringComparison.Ordinal)) == 1 &&
                outputs.Count(text => text.StartsWith("DENIED", StringComparison.Ordinal)) == 1, "race_counters");
            using var later = Proof.Start(root, "later", execution, round);
            await later.WaitForExitAsync(deadline.Token);
            Proof.Require(later.ExitCode == 10 && (await later.StandardOutput.ReadToEndAsync()).StartsWith("DENIED", StringComparison.Ordinal), "third_process_spent");
        }
        Proof.Stage = "shared-lease";
        var sharedRoot = Path.Join(parent, "shared-lease");
        Directory.CreateDirectory(sharedRoot);
        await using (var seed = await Fixture.OpenAsync(sharedRoot, default)) await seed.SeedAsync();
        await using (var fixture = await Fixture.OpenAsync(sharedRoot, default))
        {
            var controlled = new PausedUploadStore(fixture.Store);
            fixture.Dependencies.StoreOverride = controlled;
            using var targets = SolusFreshHostContext.TryCreate(fixture.Request, fixture.Snapshot, fixture.Exclusion);
            Proof.Require(targets is not null, "shared_targets");
            ControlledProvider? provider = null; SnapshotTool? tool = null;
            using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets!, context =>
            {
                provider = new(context.Policy.ProviderId, context.Policy.ModelId);
                tool = new();
                return new(provider, [new RuntimeToolRegistration(tool, new SnapshotCapability(context))], new Hooks());
            });
            var firstSelected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
            var secondSelected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = runtime.SourceSelection }, default);
            using var firstSource = firstSelected.SolusSource;
            using var secondSource = secondSelected.SolusSource;
            Proof.Require(firstSource is not null && secondSource is not null, "shared_sources");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            controlled.Pause = true;
            var first = firstSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), deadline.Token);
            await controlled.Entered.Task.WaitAsync(deadline.Token);
            var reads = controlled.Reads;
            var cancelledSelected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = runtime.SourceSelection }, default);
            var disposedSelected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = runtime.SourceSelection }, default);
            using var cancelledSource = cancelledSelected.SolusSource;
            using var disposedSource = disposedSelected.SolusSource;
            Proof.Require(cancelledSource is not null && disposedSource is not null, "shared_waiter_sources");
            reads = controlled.Reads;
            using var cancelled = new CancellationTokenSource();
            var cancelledPreparation = cancelledSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), cancelled.Token);
            cancelled.Cancel();
            using var cancelledConsumption = await cancelledPreparation;
            Proof.Require(cancelledConsumption is null && controlled.Reads == reads, "shared_waiter_cancelled");
            var disposedPreparation = disposedSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), deadline.Token);
            disposedSource.Dispose();
            // Prepare begins synchronously: without a lease-owned gate its first
            // inventory ListExact enters before this call returns. The first
            // upload remains suspended until the second preparation has begun.
            var second = secondSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), deadline.Token);
            var serialized = controlled.Reads == reads && !second.IsCompleted;
            controlled.Release.TrySetResult();
            using var firstConsumed = await first;
            using var secondConsumed = await second;
            using var disposedConsumption = await disposedPreparation;
            Proof.Require(disposedConsumption is null, "shared_waiter_disposed");
            Proof.Require(serialized, "shared_lease_serialization");
            Proof.Require(firstConsumed is not null && secondConsumed is null, "shared_exactly_one");
            Proof.Require(fixture.Runtime.TryArm(firstConsumed!, out var context, out var grant), "shared_arm");
            var host = new CandidateHost();
            _ = await ((IContextCandidateAgent)fixture.Runtime.Agent).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(grant!.ExecutionId), ContextExecutionIntent.NewRunFromContext, context, grant),
                new(1, 0, 0), host);
            Proof.Require(firstConsumed!.WasClaimed && fixture.Provider.Imports == 1 && fixture.Provider.Calls == 0 &&
                fixture.Tool.Calls == 0 && provider!.Imports == 0 && provider.Calls == 0 && tool!.Calls == 0 && host.Calls == 0,
                "shared_effects");
        }
        File.WriteAllText(Path.Join(sharedRoot, "go"), "go");
        using (var later = Proof.Start(sharedRoot, "later", Guid.NewGuid(), Guid.NewGuid()))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await later.WaitForExitAsync(deadline.Token);
            Proof.Require(later.ExitCode == 10 && (await later.StandardOutput.ReadToEndAsync()).StartsWith("DENIED", StringComparison.Ordinal),
                "shared_third_process_spent");
        }
        var pendingDenied = true;
        foreach (var variant in new[] { "pending-candidate", "pending-reset", "pending-expiry" })
        {
            Proof.Stage = variant;
            var root = Path.Join(parent, variant); Directory.CreateDirectory(root);
            SolusRestrictedStateRecord record;
            await using (var seed = await Fixture.OpenAsync(root, default)) record = await seed.SeedAsync();
            await using var fixture = await Fixture.OpenAsync(root, default);
            var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
            using var source = selected.SolusSource;
            Proof.Require(source is not null, "pending_initial_source");
            await fixture.InjectInventoryAsync(source!, variant, record);
            var writes = Directory.GetFiles(Path.Join(root, "state"), "*", SearchOption.AllDirectories).Length;
            var reselected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
            using var rejectedSource = reselected.SolusSource;
            using var consumed = await source!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
            pendingDenied &= rejectedSource is null && consumed is null &&
                writes == Directory.GetFiles(Path.Join(root, "state"), "*", SearchOption.AllDirectories).Length;
            Proof.Require(fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0,
                "pending_no_effects");
        }
        Proof.Require(pendingDenied, "pending_work_refused");
        var negatives = 0;
        foreach (var variant in new[] { "valid", "near-expiry", "payload", "grant", "checkpoint", "execution", "round", "disposed", "expired", "snapshot-replaced", "exclusion-disposed", "source-work", "source-round", "source-execution" })
        {
            Proof.Stage = variant;
            var root = Path.Join(parent, variant);
            Directory.CreateDirectory(root);
            await using (var seed = await Fixture.OpenAsync(root, default)) await seed.SeedAsync();
            await using var fixture = await Fixture.OpenAsync(root, default);
            var selection = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
            using var source = selection.SolusSource;
            Proof.Require(source is not null, "sequential_source");
            if (variant == "near-expiry") fixture.Clock.Now = source!.Selection.Current.Generation.PreparedExpiresAtUnixSeconds - 1;
            using var consumed = await source!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
            Proof.Require(consumed is not null && fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0, "consume_without_effects");
            Proof.Require(fixture.Runtime.TryArm(consumed!, out var context, out var grant), "sequential_arm");
            if (variant == "payload")
            {
                var bytes = context!.CopyRestrictedPayload(); bytes[^1] ^= 1;
                context = new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion, bytes);
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (variant == "grant") grant = new(Guid.NewGuid(), grant!.Source, grant.ExecutionId, grant.RoundId);
            if (variant == "checkpoint") grant = new(grant!.GrantId, grant.Source with { CheckpointId = Guid.NewGuid() }, grant.ExecutionId, grant.RoundId);
            if (variant == "source-work") grant = new(grant!.GrantId, grant.Source with { LogicalWorkId = Guid.NewGuid() }, grant.ExecutionId, grant.RoundId);
            if (variant == "source-round") grant = new(grant!.GrantId, grant.Source with { RoundId = Guid.NewGuid() }, grant.ExecutionId, grant.RoundId);
            if (variant == "source-execution") grant = new(grant!.GrantId, grant.Source with { ExecutionId = Guid.NewGuid() }, grant.ExecutionId, grant.RoundId);
            if (variant == "execution") grant = new(grant!.GrantId, grant.Source, Guid.NewGuid(), grant.RoundId);
            if (variant == "round") grant = new(grant!.GrantId, grant.Source, grant.ExecutionId, Guid.NewGuid());
            if (variant == "disposed") fixture.Targets.Dispose();
            if (variant == "expired") fixture.Clock.Now = consumed!.Claim.SourceExpiresAtUnixSeconds;
            if (variant == "snapshot-replaced")
            {
                fixture.ReplaceSnapshotRootForProof();
            }
            if (variant == "exclusion-disposed") fixture.Exclusion.Dispose();
            fixture.Provider.FailImport = false;
            var host = new CandidateHost();
            var result = await ((IContextCandidateAgent)fixture.Runtime.Agent).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(grant!.ExecutionId), ContextExecutionIntent.NewRunFromContext, context, grant), new(1, 0, 0), host);
            if (variant is "valid" or "near-expiry")
                Proof.Require(consumed!.WasClaimed && fixture.Provider.Imports > 0 && fixture.Provider.Calls == 2 && fixture.Tool.Calls == 1 && host.Calls == 1 && result.Context.Outcome?.IsCompleted == true, "legitimate_fresh_targets");
            else
            {
                Proof.Require(!consumed!.WasClaimed && fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0 && host.Calls == 0, "tamper_denies_before_import");
                negatives++;
            }
        }
        foreach (var variant in new[] { "precommit", "committed-error", "cancelled", "disposed-source", "expiry-before" })
        {
            Proof.Stage = variant;
            var root = Path.Join(parent, variant); Directory.CreateDirectory(root);
            await using (var seed = await Fixture.OpenAsync(root, default)) await seed.SeedAsync();
            var faults = new FixtureFaults();
            var execution = Guid.NewGuid(); var round = Guid.NewGuid();
            await using (var fixture = await Fixture.OpenAsync(root, default, faults))
            {
                var selection = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                    fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
                using var source = selection.SolusSource;
                Proof.Require(source is not null, "failure_source");
                faults.BeforeWrite = variant == "precommit";
                faults.DirectorySync = variant == "committed-error";
                using var cancellation = new CancellationTokenSource();
                if (variant == "cancelled") cancellation.Cancel();
                if (variant == "disposed-source") source!.Dispose();
                if (variant == "expiry-before") fixture.Clock.Now = source!.Selection.Current.Generation.PreparedExpiresAtUnixSeconds;
                using var consumed = await source!.PrepareAsync(execution, round, cancellation.Token);
                if (variant != "committed-error") Proof.Require(consumed is null, "failed_prepare_denied");
                Proof.Require(fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0, "failed_prepare_no_effects");
            }
            if (variant == "committed-error")
            {
                File.WriteAllText(Path.Join(root, "go"), "go");
                using var later = Proof.Start(root, "later", execution, round);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await later.WaitForExitAsync(timeout.Token);
                Proof.Require(later.ExitCode == 10, "committed_error_stays_spent");
            }
            negatives++;
        }
        foreach (var variant in new[] { "capacity", "under-retained", "stale-receipt" })
        {
            Proof.Stage = variant;
            var root = Path.Join(parent, variant); Directory.CreateDirectory(root);
            await using (var seed = await Fixture.OpenAsync(root, default)) await seed.SeedAsync();
            await using var fixture = await Fixture.OpenAsync(root, default);
            var selection = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
            using var source = selection.SolusSource;
            Proof.Require(source is not null, "inventory_source");
            await fixture.InjectInventoryAsync(source!, variant);
            using var consumed = await source!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
            Proof.Require(consumed is null && fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0, "inventory_denied");
            negatives++;
        }
        foreach (var hasPrevious in new[] { true, false })
        {
            var root = Path.Join(parent, hasPrevious ? "previous-key" : "missing-key"); Directory.CreateDirectory(root);
            await using (var seed = await Fixture.OpenAsync(root, default)) await seed.SeedAsync();
            await using (var original = await Fixture.OpenAsync(root, default))
            {
                var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                    original.Request with { SolusSourceSelection = original.Runtime.SourceSelection }, default);
                using var source = selected.SolusSource;
                Proof.Require(source is not null, "key_original_source");
                using var consumed = await source!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
                Proof.Require(consumed is not null, "key_original_consumed");
            }
            var newKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x43, 32).ToArray());
            await using var rotated = await Fixture.OpenAsync(root, default, currentKey: newKey,
                previousKey: hasPrevious ? Fixture.StateKey : null);
            var next = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                rotated.Request with { SolusSourceSelection = rotated.Runtime.SourceSelection }, default);
            using var selectedSource = next.SolusSource;
            if (hasPrevious)
            {
                Proof.Require(selectedSource is not null, "previous_key_readable");
                using var denied = await selectedSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
                Proof.Require(denied is null, "previous_key_preserves_claim");
            }
            else Proof.Require(selectedSource is null, "missing_key_denied");
            Proof.Require(rotated.Provider.Imports == 0 && rotated.Provider.Calls == 0 && rotated.Tool.Calls == 0, "key_denial_no_effects");
            negatives++;
        }
        {
            var root = Path.Join(parent, "early-compatibility"); Directory.CreateDirectory(root);
            await using var fixture = await Fixture.OpenAsync(root, default);
            var invalid = fixture.Runtime.SourceSelection with
            {
                Compatibility = fixture.Runtime.SourceSelection.Compatibility with { PackageVersion = "unsupported" },
            };
            var denied = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                fixture.Request with { SolusSourceSelection = invalid }, default);
            using var source = denied.SolusSource;
            Proof.Require(source is null && fixture.Dependencies.StoreCreates == 0 &&
                fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0, "early_compatibility_no_store");
            var entered = false;
            try
            {
                using var duplicate = SolusRuntimeConstruction.CreateForAuthorizedReview(fixture.Targets, _ =>
                { entered = true; throw new InvalidOperationException(); });
                throw new ProofFailure("duplicate_construction_accepted");
            }
            catch (ArgumentException) { Proof.Require(!entered, "duplicate_construction_no_callback"); }
            negatives++;
        }
        Console.WriteLine($"APR_SOLUS_CONTEXT_PROOF negative_checks={negatives} races=3 winners=3 shared_lease=true pending_checks=3 import_burn=true third_process_spent=true credential_free=true http_calls=0");
        return 0;
    }
    finally { Directory.Delete(parent, recursive: true); }
}
catch (Exception error)
{
    Console.Error.WriteLine("APR_SOLUS_CONTEXT_PHASE " + Proof.Stage);
    // No restricted record or provider content in diagnostics.
    Console.Error.WriteLine("APR_SOLUS_CONTEXT_FAILED " + error.GetType().Name +
        (error is ProofFailure failure ? " " + failure.Stage : string.Empty));
    return 1;
}

sealed class ProofFailure(string stage) : Exception { internal string Stage { get; } = stage; }
static class Proof
{
    internal static string Stage = "startup";
    internal static void Require(bool condition, string stage) { if (!condition) throw new ProofFailure(stage); }
    internal static AgentRequest Request(Guid execution) => new(execution, "Review the exact snapshot.", [],
        new(3, TimeSpan.FromSeconds(10)), AgentCapability.None);
    internal static Process Start(string root, string id, Guid execution, Guid round)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(Proof).Assembly.Location, "child", root, id, execution.ToString(), round.ToString() })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
}

sealed class Fixture : IAsyncDisposable
{
    private string? displacedSnapshot;

    internal void ReplaceSnapshotRootForProof()
    {
        var original = Targets.Snapshot.AbsoluteRoot;
        var displaced = original + "-displaced";
        Directory.Move(original, displaced);
        displacedSnapshot = displaced;
        Directory.CreateDirectory(original);
    }

    internal required ArtifactStateRestoreRequest Request { get; init; }
    internal required FixtureClock Clock { get; init; }
    internal required FixtureDependencies Dependencies { get; init; }
    internal required LocalRestrictedStateStore Store { get; init; }
    internal required SolusContextExclusion Exclusion { get; init; }
    internal required SolusFreshHostContext Targets { get; init; }
    internal required SolusAuthorizedRuntime Runtime { get; init; }
    internal required ControlledProvider Provider { get; init; }
    internal required SnapshotTool Tool { get; init; }
    internal required BoundedReviewedSnapshotLease Snapshot { get; init; }
    internal required ReviewedTreeSnapshot Tree { get; init; }
    internal required string Staging { get; init; }
    internal static readonly string StateKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray());

    internal static async Task<Fixture> OpenAsync(string root, CancellationToken token, FixtureFaults? faults = null, string? currentKey = null, string? previousKey = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowRun);
        var old = scenario.Launch;
        Proof.Require(ActionHostStateKey.TryCreate(currentKey ?? StateKey, out var key), "state_key");
        ActionHostPreviousStateKey? previous = null;
        if (previousKey is not null) Proof.Require(ActionHostPreviousStateKey.TryCreate(previousKey, out previous), "previous_key");
        Proof.Require(ActionHostInputs.TryCreate(old.Inputs.GitHubToken, old.Inputs.ProviderApiKey, key, previous,
            old.Inputs.ConfigPath, null, ActionHostStateMode.Auto, out var inputs), "inputs");
        Proof.Require(ActionHostLaunchContract.TryCreate(inputs!, old.EventJsonPath, old.EventJsonSha256,
            old.RepositoryName, old.RepositoryId, old.RunId, old.RunAttempt, old.WorkflowPath, old.WorkflowRef,
            old.WorkflowSha, old.ActionSourceSha, old.PayloadSha256, old.BuildDiscriminator, old.Cancellation,
            old.ArtifactBridgeEndpoint, out var launch), "launch");
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(launch!, token);
        Proof.Require(authorized.Invocation is not null, "invocation");
        var invocation = authorized.Invocation!;
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, withInlineFile: true);
        Proof.Require(ActionHostTrustedPolicyRequest.TryBind(launch!, invocation, out var policyRequest, out _), "policy_bind");
        using var policyTransport = github.CreateExactObjectTransport(launch!.Inputs.GitHubToken!);
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(policyRequest!, policyTransport, token);
        Proof.Require(materialized.Policy is not null, "policy");
        var staging = Path.Join(root, "snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var treeResult = await new ReviewedTreeReader(new ReviewedGitObjectTransportFactory(github), TimeProvider.System)
            .MaterializeAsync(invocation, launch.Inputs.GitHubToken!, staging, token);
        Proof.Require(treeResult.Snapshot is not null, "tree");
        var built = await new BoundedReviewedSnapshotBuilder(github).BuildAsync(invocation,
            launch.Inputs.GitHubToken!, treeResult.Snapshot!, staging, token);
        Proof.Require(built.Lease is not null, "snapshot");
        Proof.Require(ActionHostReviewContextFactory.TryCreate(invocation, built.Lease!.Identities, out var review), "review");
        var storePath = Path.Join(root, "state"); Directory.CreateDirectory(storePath);
        var store = new LocalRestrictedStateStore(storePath,
            beforeWriteTestHook: () => { if (faults?.BeforeWrite == true) throw new IOException(); },
            syncDirectoryTestHook: faults is null ? null : _ => !faults.DirectorySync,
            producingRun: new(launch.RunId.ToString(CultureInfo.InvariantCulture), launch.RunAttempt),
            objectIdFactory: () => new(((BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & (ulong)LineageFormat.MaximumJavaScriptInteger) | 1UL).ToString(CultureInfo.InvariantCulture)));
        var clock = new FixtureClock();
        var dependencies = new FixtureDependencies(store, github);
        var request = new ArtifactStateRestoreRequest(launch, invocation, materialized.Policy!, review!,
            DeepSeekReasoningContinuationCodec.Instance, dependencies, clock);
        var exclusion = await SolusContextExclusion.AcquireInvocationAsync(request, token);
        Proof.Require(exclusion is not null, "gate");
        var targets = SolusFreshHostContext.TryCreate(request, built.Lease, exclusion!);
        Proof.Require(targets is not null, "targets");
        ControlledProvider? provider = null; SnapshotTool? tool = null;
        var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets!, context =>
        {
            provider = new(context.Policy.ProviderId, context.Policy.ModelId);
            tool = new();
            return new(provider, [new RuntimeToolRegistration(tool, new SnapshotCapability(context))], new Hooks());
        });
        return new() { Dependencies = dependencies, Clock = clock, Request = request, Store = store, Exclusion = exclusion!, Targets = targets!, Runtime = runtime,
            Provider = provider!, Tool = tool!, Snapshot = built.Lease, Tree = treeResult.Snapshot!, Staging = staging };
    }

    internal async Task<SolusRestrictedStateRecord> SeedAsync()
    {
        var bootstrap = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(Request, default);
        using var bootstrapContext = bootstrap.Context;
        Proof.Require(bootstrap.IsBootstrap && bootstrapContext is not null &&
            bootstrapContext.TryGetLineageSnapshot(out _), "bootstrap");
        bootstrapContext!.TryGetLineageSnapshot(out var selected);
        Provider.FailImport = false;
        var execution = Guid.NewGuid();
        using var capture = new SolusRestrictedStateCapture(execution, Runtime.SourceSelection.Compatibility);
        var fresh = await ((IContextCandidateAgent)Runtime.Agent).ExecuteCandidatesWithContextAsync(
            new(Proof.Request(execution), ContextExecutionIntent.Fresh), new(1, 0, 0), new CandidateHost(), capture);
        Proof.Require(capture.TryComplete(fresh, out var record) && Provider.Calls == 2 && Tool.Calls == 1, "fresh_capture");
        Proof.Require(AcceptedStateProductionAuthorization.TryAuthorize(Request, out var authorization), "seed_authorization");
        var repository = Request.Launch.RepositoryId.ToString(CultureInfo.InvariantCulture);
        using var access = AuthorizedLocatorAccess.Issue(authorization!, repository);
        Proof.Require(LocatorStateKeyRing.TryCreate(access!, repository, StateKey, null, out var keys, out _), "seed_keys");
        using (keys)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = now + StateRetentionRequirements.LogicalWindowSeconds;
            var platform = now + StateRetentionRequirements.ScopedPlatformRequestSeconds;
            var resolved = await new LocatorRootService(Store, keys!).ResolveAsync(access!,
                platform + StateRetentionRequirements.LogicalWindowSeconds, default);
            Proof.Require(resolved.Context is not null, "seed_locator");
            using var locator = resolved.Context!;
            var policy = Request.TrustedPolicy; var invocation = Request.Invocation;
            var baseScope = AuthorizedAcceptedStateComposer.BaseScope(authorization!);
            var stateScope = new RestrictedStateScope(repository, baseScope.TrustedWorkflowIdentity,
                invocation.PullRequest.Number, selected!.SessionId, policy.ProviderId, policy.ModelId,
                policy.AdapterId, policy.InstructionsSha256, policy.LimitsSha256, policy.ToolsetSha256, policy.BuildDiscriminator);
            AuthorizedStateAccess.Authorize(new(stateScope, stateScope, true, true, false), out var stateAccess);
            var binding = new RestrictedStateBinding(stateScope, invocation.PullRequest.BaseSha,
                invocation.PullRequest.HeadSha, 0, null, now, expires);
            Proof.Require(SolusRestrictedStateSnapshotCodec.TrySeal(stateAccess, binding, record,
                new SeedKeys(locator, access!), out var envelope, out var recordHash, out _), "seed_seal");
            var scope = new R4PublicationScopeV1((ulong)Request.Launch.RepositoryId, (ulong)Request.Launch.RepositoryId,
                invocation.WorkflowPath, Request.Launch.WorkflowRef, (ulong)invocation.PullRequest.Number,
                policy.PolicySha256, AuthorizedAcceptedStateComposer.PayloadBuildIdentity(policy));
            var identity = Snapshot.Snapshot.Identity;
            var outcome = AgentRunOutcome.Success(new AgentTerminalReview("Review complete", [], new string('d', 64), [1, 2, 3]),
                identity, ImmutableArray<AgentLogicalEvent>.Empty, continuation: null);
            Proof.Require(R4ValidatedPublicationReview.TryCreate(outcome, scope, out var validated), "publication_review");
            var rendered = R4StickyRenderer.Render(validated!);
            Proof.Require(ValidatedPublicationPayloadV1.TryCreate(rendered.Comment, Request.Launch.RepositoryId,
                Request.Launch.RepositoryName, invocation.PullRequest.Number, policy.PolicySha256,
                policy.PayloadSha256, policy.BuildDiscriminator, AcceptedStateFormat.RenderingVersion, out var publication), "publication");
            Proof.Require(AcceptedStatePublicationPayloadCodec.TryEncode(publication, out var publicationBytes), "publication_bytes");
            var generation = new StateGenerationRecordV1(ImmutableArray.CreateRange(envelope!),
                RestrictedStateEnvelope.EnvelopeSha256(envelope!), recordHash!, invocation.PullRequest.BaseSha,
                invocation.PullRequest.HeadSha, 0, null, null, now, expires, ImmutableArray.CreateRange(publicationBytes),
                AcceptedStateRecordValidation.Sha256(publicationBytes), policy.PolicySha256, policy.ConfigSha256,
                policy.InstructionsSha256, policy.PayloadSha256, policy.BuildDiscriminator);
            Proof.Require(AcceptedStateGenerationRecordCodec.TryEncode(generation, out var generationBytes), "generation");
            Proof.Require(AcceptedStateIdentity.TryComputeLogicalGeneration(generationBytes, selected.BaseScopeDigest,
                selected.Epoch, selected.SessionId, null, out var logical), "logical_identity");
            Proof.Require(LineageBaseScopeCodec.TryEncode(baseScope, out var canonical), "scope");
            async Task<StateControlHeaderV1> Upload(StateObjectClass kind, byte[] payload)
            {
                Proof.Require(locator.TryDeriveOpaqueName(access!, StateObjectClasses.ToWireName(kind), canonical, out var name), "name");
                var draft = new StateControlHeaderDraft(selected.BaseScopeDigest, selected.Epoch, selected.SessionId,
                    kind, null, null, Request.Launch.RunId.ToString(CultureInfo.InvariantCulture), Request.Launch.RunAttempt,
                    now, expires, platform);
                Proof.Require(StateControlEnvelopeV1Codec.TryEncrypt(locator, access!, name!, draft, payload,
                    out var encrypted, out var header, out _), "encrypt");
                var uploaded = await Store.UploadImmutableAsync(new(name!, new(Guid.NewGuid().ToString("N")), encrypted,
                    new(OpaqueStoreHash.Sha256(encrypted)), platform), default);
                Proof.Require(uploaded.Succeeded, "upload");
                return header!;
            }
            var candidate = await Upload(StateObjectClass.Candidate, generationBytes);
            var receipt = new AcceptanceReceiptV1(logical, candidate.ObjectIdentity, null, null,
                invocation.PullRequest.HeadSha, StickyPublicationOperation.Observed, Request.Launch.RepositoryId,
                invocation.PullRequest.Number, 99, $"https://github.com/{Request.Launch.RepositoryName}/pull/{invocation.PullRequest.Number}#issuecomment-99",
                publication!.ScopeSha256, publication.BodySha256, generation.PublicationPayloadSha256,
                Request.Launch.RunId.ToString(CultureInfo.InvariantCulture), Request.Launch.RunAttempt, now, expires);
            Proof.Require(AcceptedStateAcceptanceReceiptCodec.TryEncode(receipt, out var receiptBytes), "receipt");
            _ = await Upload(StateObjectClass.Acceptance, receiptBytes);
            CryptographicOperations.ZeroMemory(envelope!);
        }
        return record!;
    }

    internal async Task InjectInventoryAsync(SolusContextSource source, string variant, SolusRestrictedStateRecord? sourceRecord = null)
    {
        Proof.Require(AcceptedStateProductionAuthorization.TryAuthorize(Request, out var authorization), "inventory_authorization");
        var repository = Request.Launch.RepositoryId.ToString(CultureInfo.InvariantCulture);
        using var access = AuthorizedLocatorAccess.Issue(authorization!, repository);
        Proof.Require(LocatorStateKeyRing.TryCreate(access!, repository, StateKey, null, out var keys, out _), "inventory_keys");
        using (keys)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var platform = now + StateRetentionRequirements.ScopedPlatformRequestSeconds;
            var resolved = await new LocatorRootService(Store, keys!).ResolveAsync(access!, platform, default);
            Proof.Require(resolved.Context is not null, "inventory_locator");
            using var locator = resolved.Context!;
            var selection = source.Selection; var head = selection.LineageHead.Header;
            var generation = selection.Current.Generation;
            var baseScope = AuthorizedAcceptedStateComposer.BaseScope(authorization!);
            Proof.Require(LineageBaseScopeCodec.TryEncode(baseScope, out var canonical), "inventory_scope");
            var count = variant == "capacity" ? LineageFormat.MaximumPhysicalPerClass : 1;
            for (var i = 0; i < count; i++)
            {
                byte[] payload; var kind = StateObjectClass.ContextClaim;
                string? predecessor = selection.Current.ReceiptPhysical.Header.ObjectIdentity;
                string? successor = selection.Current.LogicalGenerationIdentity;
                if (variant == "stale-receipt")
                {
                    kind = StateObjectClass.Acceptance;
                    predecessor = selection.Current.Receipt.PreviousAcceptanceReceiptIdentity; successor = null;
                    Proof.Require(AcceptedStateAcceptanceReceiptCodec.TryEncode(selection.Current.Receipt, out payload), "inventory_receipt");
                }
                else if (variant == "pending-candidate")
                {
                    kind = StateObjectClass.Candidate;
                    successor = null;
                    var policy = Request.TrustedPolicy;
                    var scope = new RestrictedStateScope(repository, baseScope.TrustedWorkflowIdentity,
                        Request.Invocation.PullRequest.Number, head.SessionId, policy.ProviderId, policy.ModelId,
                        policy.AdapterId, policy.InstructionsSha256, policy.LimitsSha256, policy.ToolsetSha256, policy.BuildDiscriminator);
                    AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var stateAccess);
                    var binding = new RestrictedStateBinding(scope, generation.ProducerBaseSha, generation.ProducerHeadSha,
                        generation.Generation + 1, generation.StateEnvelopeSha256, now, generation.PreparedExpiresAtUnixSeconds);
                    Proof.Require(SolusRestrictedStateSnapshotCodec.TrySeal(stateAccess, binding, sourceRecord!,
                        new SeedKeys(locator, access!), out var envelope, out var digest, out _), "pending_seal");
                    var pending = generation with
                    {
                        EncryptedStateEnvelope = ImmutableArray.CreateRange(envelope!),
                        StateEnvelopeSha256 = RestrictedStateEnvelope.EnvelopeSha256(envelope!),
                        SessionSha256 = digest!, Generation = generation.Generation + 1,
                        PredecessorEnvelopeSha256 = generation.StateEnvelopeSha256,
                        PreviousLogicalGenerationIdentity = selection.Current.LogicalGenerationIdentity,
                        PreparedAtUnixSeconds = now,
                    };
                    Proof.Require(AcceptedStateGenerationRecordCodec.TryEncode(pending, out payload), "pending_candidate");
                    CryptographicOperations.ZeroMemory(envelope!);
                }
                else if (variant is "pending-reset" or "pending-expiry")
                {
                    var reset = variant == "pending-reset";
                    kind = reset ? StateObjectClass.Reset : StateObjectClass.ExpiryTransition;
                    predecessor = head.ObjectIdentity; successor = null;
                    var targets = ImmutableArray.Create(LineageHeadCodec.Evidence(selection.Current.Physical.Metadata),
                        LineageHeadCodec.Evidence(selection.Current.ReceiptPhysical.Metadata));
                    var intent = new LineageTransitionIntentV1(reset ? LineageTransitionIntentKind.Reset : LineageTransitionIntentKind.Expiry,
                        head.ObjectIdentity, head.Epoch, selection.Current.ReceiptPhysical.Header.ObjectIdentity,
                        reset ? null : generation.PreparedExpiresAtUnixSeconds,
                        new(generation.ProducerBaseSha, generation.ProducerHeadSha), LineageCryptography.InventoryDigest(targets), targets,
                        reset ? Request.Launch.RunId.ToString(CultureInfo.InvariantCulture) : null,
                        reset ? Request.Launch.RunAttempt : null);
                    Proof.Require(LineageTransitionIntentCodec.TryEncode(intent, out payload), "pending_transition");
                }
                else
                {
                    var checkpoint = new SolusRestrictedStateCheckpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
                    var execution = Guid.NewGuid(); var round = Guid.NewGuid();
                    var grant = SolusContextClaimRecordCodec.DeriveGrantId(head.BaseScopeDigest, head.Epoch, head.SessionId,
                        selection.Current.LogicalGenerationIdentity, checkpoint.LogicalWorkId, checkpoint.RoundId, execution, round);
                    var claim = new SolusContextClaimRecord(head.BaseScopeDigest, head.Epoch, head.SessionId,
                        head.ObjectIdentity, selection.Current.ReceiptPhysical.Header.ObjectIdentity,
                        selection.Current.LogicalGenerationIdentity, generation.StateEnvelopeSha256, generation.SessionSha256,
                        checkpoint, grant, execution, round, generation.PreparedExpiresAtUnixSeconds);
                    Proof.Require(SolusContextClaimRecordCodec.TryEncode(claim, out payload), "inventory_claim");
                }
                Proof.Require(locator.TryDeriveOpaqueName(access!, StateObjectClasses.ToWireName(kind), canonical, out var name), "inventory_name");
                var created = variant == "stale-receipt" ? selection.Current.Receipt.AcceptedAtUnixSeconds : now;
                var expiry = generation.PreparedExpiresAtUnixSeconds;
                var draft = new StateControlHeaderDraft(head.BaseScopeDigest, head.Epoch, head.SessionId, kind,
                    predecessor, successor, Request.Launch.RunId.ToString(CultureInfo.InvariantCulture), Request.Launch.RunAttempt,
                    created, expiry, platform);
                Proof.Require(StateControlEnvelopeV1Codec.TryEncrypt(locator, access!, name!, draft, payload,
                    out var encrypted, out _, out _), "inventory_encrypt");
                var uploaded = await Store.UploadImmutableAsync(new(name!, new(Guid.NewGuid().ToString("N")), encrypted,
                    new(OpaqueStoreHash.Sha256(encrypted)), variant == "under-retained" ? expiry : platform), default);
                Proof.Require(uploaded.Succeeded, "inventory_upload");
                CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Runtime.Dispose(); Targets.Dispose(); Exclusion.Dispose();
        if (displacedSnapshot is not null)
        {
            // Restore the fixture-owned inode so the real lease can clean its
            // read-only tree. Never recursively delete a replacement root.
            var original = Targets.Snapshot.AbsoluteRoot;
            if (Directory.Exists(original)) Directory.Delete(original, recursive: false);
            Directory.Move(displacedSnapshot, original);
            displacedSnapshot = null;
        }
        await Snapshot.DisposeAsync(); await Tree.DisposeAsync();
        Directory.Delete(Staging, recursive: true);
    }

    internal sealed class FixtureDependencies(IRestrictedStateStore store, FullPathGitHubFactory github) : IAcceptedStateProductionDependencies
    {
        internal int StoreCreates;
        internal IRestrictedStateStore? StoreOverride;
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) { StoreCreates++; return StoreOverride ?? store; }
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => github.CreateExactObjectTransport(token);
    }
    private sealed class SeedKeys(LocatorContext locator, AuthorizedLocatorAccess authority) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? key)
        {
            key = null; Span<byte> material = stackalloc byte[32];
            try
            {
                if (!locator.TryCopyCurrentStateKey(authority, material, out var id)) return false;
                key = new(id, material); return true;
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string id, long expires, out RestrictedStateKey? key)
        { key = null; return false; }
    }
}

sealed class CandidateHost : ICandidateHost
{
    internal int Calls;
    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
    }
}
sealed class Hooks : IRuntimeExposureHooks
{
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
}
sealed class ControlledProvider(string provider, string model) : ModelProvider(new(provider, model),
    ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolCalls), IProviderContextPersistence
{
    internal int Calls, Imports;
    internal bool FailImport = true;
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        Calls++; observation.ObserveDispatch(DispatchExposure.Dispatched);
        observation.CaptureUsage(new(1, 1), new(UsageSettlement.Settled));
        return ValueTask.FromResult(new ProviderResponse(Scope, request.Attempt,
            Calls == 1 ? ProviderFinish.ToolCalls : ProviderFinish.Final, Calls == 1 ? null : "S2_FINAL",
            Calls == 1 ? [new ToolCall("probe-" + request.Attempt.ExecutionId.ToString("N"), "snapshot_probe", "{}")] : [],
            new(Scope, request.Attempt, "S2_CONTINUATION"u8.ToArray())));
    }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1, [1, 2, 3]);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return !FailImport; }
}
sealed class SnapshotCapability(SolusFreshHostContext context) : IToolCapability
{
    public string CapabilityId => "snapshot_probe";
    internal SolusFreshHostContext Context => context;
}
sealed class SnapshotTool() : FunctionTool<SnapshotCapability>(new("snapshot_probe", "Read the authorized snapshot probe",
    ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
    ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}"),
    "snapshot_probe", ToolEffect.ReadOnly, 2048, 4096))
{
    internal int Calls;
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, SnapshotCapability capability, CancellationToken cancellationToken)
    {
        Calls++;
        Proof.Require(capability.Context.IsLive, "tool_authority");
        var path = capability.Context.Snapshot.OrderedTrackedFiles.Single();
        Proof.Require(File.ReadAllText(Path.Join(capability.Context.Snapshot.AbsoluteRoot, path)).Length > 0, "actual_snapshot_target");
        return ValueTask.FromResult(ToolOutput.Success(call, "{\"ok\":true}"));
    }
}

sealed class FixtureClock : TimeProvider
{
    internal long? Now;
    public override DateTimeOffset GetUtcNow() => Now is { } now ? DateTimeOffset.FromUnixTimeSeconds(now) : DateTimeOffset.UtcNow;
}

sealed class FixtureFaults
{
    internal bool BeforeWrite;
    internal bool DirectorySync;
}

// Real local storage with a deterministic pause immediately before the first
// claim write. No inventory bytes or authentication outcomes are substituted.
sealed class PausedUploadStore(IRestrictedStateStore inner) : IRestrictedStateStore
{
    internal bool Pause;
    internal int Reads;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token)
    { Interlocked.Increment(ref Reads); return inner.ListExactAsync(request, token); }
    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) => inner.ReadMetadataAsync(request, token);
    public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token) => inner.DownloadAsync(request, token);
    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => inner.ReadBackExactAsync(request, token);
    public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => inner.DeleteExactAsync(request, token);
    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token)
    {
        if (Pause && Entered.TrySetResult()) await Release.Task.WaitAsync(token);
        return await inner.UploadImmutableAsync(request, token);
    }
}
