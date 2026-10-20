#if APR_SOLUS_MANAGED
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Exposure;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Supporting negative proof only. Genuine on-disk issuers and the actual
// package run remain in use; capture faults enter through compiled test seams.
internal static class ManagedFaultProof
{
    private static readonly string Head = new('b', 40), Next = new('c', 40);
    private static readonly string Fact = "APR_PRIOR_ONLY_" + new string('e', 64);
    internal static async Task<int> RunAsync(string root)
    {
        try
        {
            var revokedPath = Path.Join(root, "revoked");
            new Program.DirectRoot(revokedPath).Write(Head, Head, "before", Fact + "\n");
            using (var revoked = await Authority.Open(revokedPath))
            {
                Check(LiveAgentFreshProcessFileSystem.TryCreate(revokedPath, out var other), "other_owner");
                Check(SolusDirectReviewAuthorization.TryCreate(other!, revoked.Root) is null, "owner_reference");
                new Program.DirectRoot(revokedPath).Write(Head, Head, "after", Fact + "\n");
                Check(!revoked.Authorization.IsLive && SolusFreshHostContext.TryCreateDirect(revoked.Authorization) is null,
                    "changed_authorization_revokes");
            }
            await InvalidSecrets(root);
            await ActualDirectOwnedBuffersDisposeAcrossCallerFailure(root);
            foreach (var mode in new[] { "missing", "duplicate", "wrong-format", "throw", "cancel" })
            {
                var path = Path.Join(root, mode);
                var input = new Program.DirectRoot(path);
                input.Write(Head, Head, "capture_first", Fact + "\n");
                using var authority = await Authority.Open(path);
                using var cancellation = new CancellationTokenSource();
                SetSecrets();
                var execution = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                    null, 0, null, authority.Sink, cancellation.Token,
                    trustedCaptureDecorator: sink => new CaptureFault(sink, mode, cancellation));
                Check(execution.Execution.Result is { ModelCalls: 2, ToolCalls: 1, AcceptedGeneration: null,
                    HandoffReady: false } && !File.Exists(input.LineagePath), "capture_incomplete");
                Check(await authority.EmptyAccepted(), "capture_no_acceptance");
            }

            // A structurally valid encrypted record can still contain an opaque
            // runtime payload that fails import. The trusted fixture seeds it
            // through the real typed transaction, not a copied context codec.
            var importPath = Path.Join(root, "import");
            var importInput = new Program.DirectRoot(importPath);
            importInput.Write(Head, Head, "source", Fact + "\n");
            using (var source = await Authority.Open(importPath)) await source.Seed(false);
            var lineage = File.ReadAllBytes(importInput.LineagePath);
            importInput.Write(Head, Next, "import_first", "Current input only.\n", lineage);
            using (var first = await Authority.Open(importPath))
            {
                SetSecrets();
                var rejected = await first.Run();
                Check(rejected.Execution.Result is { ModelCalls: 0, ToolCalls: 0, AcceptedGeneration: null,
                    HandoffReady: false }, "import_no_work");
            }
            var spent = importInput.StateFiles();
            Check(File.ReadAllBytes(importInput.LineagePath).AsSpan().SequenceEqual(lineage), "import_preserves_source");
            importInput.Write(Head, Next, "import_second", "Current input only.\n", lineage);
            using (var second = await Authority.Open(importPath))
            {
                SetSecrets();
                var rejected = await second.Run();
                Check(rejected.Execution.Result is { Code: RestrictedStateCodes.Conflict, ModelCalls: 0,
                    ToolCalls: 0, AcceptedGeneration: null }, "import_claim_spent");
            }
            Check(importInput.StateFiles().SequenceEqual(spent), "import_no_second_claim");

            var incompatiblePath = Path.Join(root, "incompatible");
            var incompatibleInput = new Program.DirectRoot(incompatiblePath);
            incompatibleInput.Write(Head, Head, "source", Fact + "\n");
            using (var source = await Authority.Open(incompatiblePath)) await source.Seed(true);
            var incompatibleLineage = File.ReadAllBytes(incompatibleInput.LineagePath);
            var incompatibleState = incompatibleInput.StateFiles();
            incompatibleInput.Write(Head, Next, "incompatible_first", "Current input only.\n", incompatibleLineage);
            using (var current = await Authority.Open(incompatiblePath))
            {
                SetSecrets();
                var rejected = await current.Run();
                Check(rejected.Execution.Result is { ModelCalls: 0, ToolCalls: 0, AcceptedGeneration: null,
                    HandoffReady: false }, "incompatible_no_work");
            }
            Check(incompatibleInput.StateFiles().SequenceEqual(incompatibleState) &&
                File.ReadAllBytes(incompatibleInput.LineagePath).AsSpan().SequenceEqual(incompatibleLineage),
                "incompatible_no_mutation");
            Console.WriteLine("APR_SOLUS_DIRECT_FAULTS capture_cases=5 import_burn=true repeated_claim_refused=true incompatible_no_work=true");
            return 0;
        }
        catch (Exception exception)
        {
            var code = CutoverProofFailure.Describe(exception);
            Console.Error.WriteLine("APR_SOLUS_DIRECT_FAULTS_FAILED " + code);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, null);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, null);
        }
    }

    // Component ownership only: the real typed seed intentionally contains an
    // invalid SDK payload. This does not establish SDK import/history or expose
    // RunAsync locals, immutable records/strings or universal managed zeroing.
    private static async Task ActualDirectOwnedBuffersDisposeAcrossCallerFailure(string root)
    {
        var path = Path.Join(root, "owned-buffers");
        var input = new Program.DirectRoot(path);
        input.Write(Head, Head, "owned_source", Fact + "\n");
        using (var seed = await Authority.Open(path)) await seed.Seed(false);
        var lineage = File.ReadAllBytes(input.LineagePath);
        input.Write(Head, Next, "owned_failure", "Current input only.\n", lineage);
        using var authority = await Authority.Open(path);
        using var targets = SolusFreshHostContext.TryCreateDirect(authority.Authorization);
        Check(targets is not null && authority.Prior is not null, "owned_current_authority");
        var tools = new SolusSnapshotTools(Guid.NewGuid(), authority.Authorization.Snapshot,
            new SnapshotToolExecutor(authority.Authorization.Snapshot, authority.Authorization.CreateFileAccess()));
        var compatibility = SolusRuntimeConstruction.DescribeFreshConfiguration(targets!, tools.Registrations, new Hooks());
        Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "owned_key");
        using var ownedKeys = keys!;
        var state = new SolusDirectStateComposer(authority.Files, authority.Authorization, targets!, compatibility,
            keys!, TimeProvider.System);
        var context = new RestrictedStateSessionAdmissionContext(authority.Authorization.Snapshot.Identity.BaseSha,
            authority.Authorization.Snapshot.Identity.HeadSha, authority.Prior!.Lineage.Generation + 1,
            authority.Prior.Lineage.EnvelopeSha256, null!);
        byte[]? selectedBytes = null, preparedBytes = null;
        SolusDirectSelectedState? selected = null;
        SolusDirectPreparedContext? prepared = null;
        var injected = false;
        try
        {
            var selection = await state.SelectAsync(authority.Prior, context, CancellationToken.None);
            Check(selection.Code == RestrictedStateCodes.Restored && selection.Selection?.Session?.SolusRecord is not null,
                "owned_real_selection");
            using var ownedSelection = selected = selection.Selection!;
            selectedBytes = selected.Session!.Plaintext;
            using var ownedPreparation = prepared = await state.PrepareAsync(selected, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None);
            Check(prepared is not null, "owned_real_preparation");
            var field = typeof(SolusDirectPreparedContext).GetField("payload",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly);
            Check(field is { IsStatic: false } && field.DeclaringType == typeof(SolusDirectPreparedContext) &&
                field.FieldType == typeof(byte[]), "owned_exact_payload_field");
            // Observe the real underlying reference, never a copied surrogate.
            preparedBytes = field!.GetValue(prepared) as byte[];
            Check(selectedBytes.Length > 0 && selectedBytes.Any(value => value != 0) && preparedBytes is { Length: > 0 } &&
                preparedBytes.Any(value => value != 0) && !ReferenceEquals(selectedBytes, preparedBytes), "owned_distinct_live_buffers");
            throw new OwnedBufferProbeFailure();
        }
        catch (OwnedBufferProbeFailure) { injected = true; }
        Check(injected && selectedBytes is not null && preparedBytes is not null &&
            selectedBytes.All(value => value == 0) && preparedBytes.All(value => value == 0), "owned_disposed_zero");
        var afterClaim = StateSnapshot(authority.Root.StateRootPath);
        prepared!.Dispose();
        selected!.Dispose();
        Check(!selected.IsLive && selectedBytes!.All(value => value == 0) && preparedBytes!.All(value => value == 0),
            "owned_dispose_idempotent");
        Check(!prepared.TryTransfer(out var refusedTargets, out var refusedClaim, out var refusedCompatibility,
            out var refusedBytes, out var refusedClock) && refusedTargets is null && refusedClaim is null &&
            refusedCompatibility is null && refusedBytes is null && refusedClock is null, "owned_disposed_transfer_refused");
        var retry = await state.SelectAsync(authority.Prior, context, CancellationToken.None);
        Check(retry.Code == RestrictedStateCodes.Restored && retry.Selection is not null, "owned_source_still_accepted");
        using var retrySelection = retry.Selection!;
        using var retryPreparation = await state.PrepareAsync(retrySelection, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Check(retryPreparation is null && StateSnapshot(authority.Root.StateRootPath).SequenceEqual(afterClaim) &&
            File.ReadAllBytes(input.LineagePath).AsSpan().SequenceEqual(lineage), "owned_claim_remains_spent");
    }

    private sealed class OwnedBufferProbeFailure : Exception { }

    private static async Task InvalidSecrets(string root)
    {
        // A genuine current successful run establishes the same fixture's
        // authority, manifest, runtime construction and provider prerequisites.
        var positivePath = Path.Join(root, "secret-positive");
        var positive = new Program.DirectRoot(positivePath);
        positive.Write(Head, Head, "secret_control", Fact + "\n");
        using (var authority = await Authority.Open(positivePath))
        {
            SetSecrets();
            var completed = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                null, 0, null, authority.Sink, CancellationToken.None);
            Check(completed.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2,
                ToolCalls: 1, AcceptedGeneration: 0, HandoffReady: true } &&
                File.Exists(positive.LineagePath), "secret_positive_control");
        }
        const string originalSyntheticProvider = "provider-secret-must-not-enter-body";
        (string? Provider, string? Key)[] invalid = [
            (null, Program.Key), ("", Program.Key), (originalSyntheticProvider, null),
            (originalSyntheticProvider, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="),
            (originalSyntheticProvider, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")];
        for (var index = 0; index < invalid.Length; index++)
        {
            var path = Path.Join(root, "secret-negative-" + index);
            var input = new Program.DirectRoot(path);
            input.Write(Head, Head, "secret_negative", Fact + "\n");
            using var authority = await Authority.Open(path);
            Check(authority.Authorization.IsLive, "secret_live_authority");
            Check(await authority.EmptyAccepted(), "secret_initial_absent");
            var before = StateSnapshot(authority.Root.StateRootPath);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, invalid[index].Provider);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, invalid[index].Key);
            var rejected = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                null, 0, null, authority.Sink, CancellationToken.None);
            Check(rejected.Execution.Result is { Code: R3LiveAgentCodes.SecretInvalid, ModelCalls: 0,
                ToolCalls: 0, AcceptedGeneration: null, AcceptedSessionSha256: null,
                AcceptedEnvelopeSha256: null, HandoffReady: false }, "secret_no_work");
            Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
                Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
                "secret_environment_cleared");
            Check(!File.Exists(input.LineagePath) && await authority.EmptyAccepted(), "secret_no_acceptance");
            Check(before.SequenceEqual(StateSnapshot(authority.Root.StateRootPath)), "secret_state_unchanged");
        }
    }

    private static string[] StateSnapshot(string root)
    {
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root)
            .Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
            var identity = Path.GetRelativePath(root, path) + ":" + (int)info.Attributes + ":" + mode + ":" +
                info.CreationTimeUtc.Ticks + ":" + info.LastWriteTimeUtc.Ticks;
            if (!directory)
            {
                var bytes = File.ReadAllBytes(path);
                try { identity += ":" + bytes.Length + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            entries.Add(identity);
        }
        return entries.ToArray();
    }

    private static void Check(bool valid, string code)
    { if (!valid) throw new CutoverProofFailure(code); }
    private static void SetSecrets()
    {
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, "synthetic-header-only");
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, Program.Key);
    }

    private sealed class Authority : IDisposable
    {
        internal required LiveAgentFreshProcessFileSystem Files;
        internal required LiveAgentFreshProcessAuthorizedRoot Root;
        internal required SolusDirectReviewAuthorization Authorization;
        internal required LiveAgentFreshProcessLineageSink Sink;
        internal LiveAgentFreshProcessAdmittedLineage? Prior;
        internal static async Task<Authority> Open(string path)
        {
            Check(LiveAgentFreshProcessFileSystem.TryCreate(path, out var files), "filesystem");
            var read = files!.ReadAuthorization();
            Check(read is not null, "authorization");
            Check(LiveAgentFreshProcessCommand.TryAdmitAuthorization(
                LiveAgentFreshProcessCodec.ReadAuthorization(read!.Bytes)!, out var input, out _, out var admitted), "scope");
            var hasPrior = input!.LocatorFamily == RestrictedStateLocatorFamily.Current;
            Check(files.TryAuthorizeLayout(read, admitted!, hasPrior, out var root), "layout");
            try
            {
                Check(await files.AcquireContextExclusionAsync(root!, CancellationToken.None), "exclusion");
                var authority = SolusDirectReviewAuthorization.TryCreate(files, root!);
                Check(authority is not null, "issuer");
                LiveAgentFreshProcessAdmittedLineage? prior = null;
                if (hasPrior) Check(LiveAgentFreshProcessLineageAdmission.TryAdmit(files.ReadLineage(root!)!,
                    input.Document.ExpectedLineageSha256!, root!.Access.Scope, out prior), "lineage");
                return new() { Files = files, Root = root!, Authorization = authority!, Prior = prior,
                    Sink = new(files, root!, authority!.Snapshot.Identity, input.InvocationIdentitySha256, prior) };
            }
            catch { root?.Dispose(); throw; }
        }
        internal Task<SolusDirectExecution> Run() => SolusDirectReviewComposition.RunAsync(Files, Authorization,
            Prior, Prior!.Lineage.Generation + 1, Prior.Lineage.EnvelopeSha256, Sink, CancellationToken.None);
        internal async Task<bool> EmptyAccepted()
        {
            Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "key");
            using var owned = keys!;
            var state = await new RestrictedStateOpaqueSnapshotStore(Files.CreateStateStore(Root, Root.StateRootPath), keys!)
                .ReadAsync(Root.Access, CancellationToken.None);
            return state.Snapshot is { Accepted.IsEmpty: true, Staging: null };
        }
        internal async Task Seed(bool incompatible)
        {
            using var targets = SolusFreshHostContext.TryCreateDirect(Authorization);
            Check(targets is not null, "targets");
            var tools = new SolusSnapshotTools(Guid.NewGuid(), Authorization.Snapshot,
                new SnapshotToolExecutor(Authorization.Snapshot, Authorization.CreateFileAccess()));
            var expected = SolusRuntimeConstruction.DescribeFreshConfiguration(targets!, tools.Registrations, new Hooks());
            if (incompatible) expected = expected with { RuntimeConfigurationSha256 = new string('e', 64) };
            Check(SolusRestrictedStateRecord.TryCreate(expected,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), "invalid opaque context"u8,
                out var record), "record");
            Check(SolusRestrictedStateSnapshotCodec.TryWrite(record, out var bytes), "encode");
            Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "key");
            using var owned = keys!;
            try
            {
                var state = new SolusDirectStateComposer(Files, Authorization, targets!, expected, keys!, TimeProvider.System);
                var result = await state.CreateCommitCoordinator(Sink).CommitPreparedAsync(
                    new(bytes, SolusRestrictedStateSnapshotCodec.RecordSha256(bytes), 0), Root.Access, null,
                    ReviewedHeadTransition.SameHead, new(Authorization.Snapshot.Identity.BaseSha,
                        Authorization.Snapshot.Identity.HeadSha, 0, null, null!), Root.StateRootPath, keys!, CancellationToken.None);
                Check(result.Code == R3LiveAgentCodes.Completed && result.AcceptedGeneration == 0, "seed_acceptance");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { Authorization.Dispose(); Root.Dispose(); }
    }

    private sealed class CaptureFault(IRestrictedContextSink inner, string mode, CancellationTokenSource cancellation)
        : IRestrictedContextSink
    {
        public void Capture(AgentContextEnvelope context)
        {
            if (mode == "missing") return;
            if (mode == "wrong-format")
            {
                var bytes = context.CopyRestrictedPayload();
                try { inner.Capture(new(context.ImplementationId, context.FormatVersion + 1, context.CompatibilityVersion, bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                return;
            }
            inner.Capture(context);
            if (mode == "duplicate") inner.Capture(context);
            if (mode == "throw") throw new InvalidOperationException("private_capture_failure");
            if (mode == "cancel") cancellation.Cancel();
        }
    }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token) =>
            ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
                ExposureDecision.Permit, exposure.RequiredAcknowledgement));
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
            ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged,
                RuntimeContinuation.Continue, settlement.Accounting));
    }
}
#endif
