import { execFileSync, spawnSync, spawn } from 'node:child_process';
import {
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
  existsSync,
  symlinkSync,
  readdirSync,
} from 'node:fs';
import { open, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { PassThrough } from 'node:stream';
import assert from 'node:assert/strict';
import { expect, test } from 'vitest';
import { awaitFixtureGroupClose } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs';
import {
  projectTransactionInventoryFailure,
  transactionInventoryStages,
  transactionInventoryErrors,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/transaction-observation.mjs';

const archive = readFileSync(
  'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/archive-child.mjs',
  'utf8',
);
const actualInspect = archive.slice(
  archive.indexOf('  async function inspectTransaction('),
  archive.indexOf('  async function run('),
);
const goodFrame = Buffer.from(
  'APR_TRANSACTION_INVENTORY_FAILURE stage=control_identity error=invalid_operation\n',
);

test('actual inspectTransaction preserves refusal, supervision, byte bounds and owned-input cleanup', async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'apr-inventory-parent-'));
  const file = join(root, 'transaction-1.json');
  try {
    for (const scenario of [
      'success',
      'nonzero',
      'signal',
      'watchdog',
      'overflow',
      'private',
      'mixed',
      'badjson',
      'stderr-success',
      'spawn',
      'child-error',
      'write',
      'collision',
    ]) {
      const diagnostic: any = {};
      const stdout = new PassThrough(),
        stderr = new PassThrough();
      const chunks: Buffer[] = [];
      let killed = false,
        closed = false,
        timeout = 0;
      const child = {
        stdout,
        stderr,
        kill(signal: string) {
          expect(signal).toBe('SIGKILL');
          killed = true;
        },
      };
      if (scenario === 'collision') writeFileSync(file, 'OTHER_OWNER');
      const ownedOpen = async (name: string, flags: string, mode: number) => {
        expect(flags).toBe('wx');
        expect(mode).toBe(0o600);
        const handle = await open(name, flags, mode);
        return {
          async writeFile(text: string) {
            await handle.writeFile(text);
            if (scenario === 'write') throw new Error('PRIVATE_WRITE /private/input');
          },
          async close() {
            closed = true;
            await handle.close();
          },
        };
      };
      const wait = async () => {
        const out =
          scenario === 'success'
            ? Buffer.from('{"records":[],"private":"OWNED_TEST_DATA"}')
            : ['mixed', 'badjson', 'stderr-success'].includes(scenario)
              ? Buffer.from('PRIVATE_CONTEXT')
              : Buffer.alloc(0);
        const err = ['nonzero', 'signal', 'watchdog', 'mixed', 'overflow'].includes(scenario)
          ? Buffer.from(goodFrame)
          : ['private', 'stderr-success'].includes(scenario)
            ? Buffer.from('PRIVATE_KEY /private/source\n')
            : Buffer.alloc(0);
        chunks.push(out, err);
        stdout.write(out);
        stderr.write(err);
        if (scenario === 'overflow') {
          const huge = Buffer.alloc(512 * 1024 + 1, 0x78);
          chunks.push(huge);
          stderr.write(huge);
        }
        if (scenario === 'child-error') throw new Error('PRIVATE_CHILD_ERROR');
        return {
          code: ['success', 'badjson', 'stderr-success', 'overflow'].includes(scenario)
            ? 0
            : scenario === 'signal'
              ? null
              : 1,
          signal: scenario === 'signal' ? 'SIGTERM' : null,
          watchdogFired: scenario === 'watchdog',
        };
      };
      const invoke = new Function(
        'join',
        'open',
        'spawn',
        'awaitFixtureGroupClose',
        'roleDeadline',
        'assert',
        'rm',
        'projectTransactionInventoryFailure',
        'root',
        'diagnostic',
        `let transactionSerial=0; const transactionIdentity=undefined; const transaction={entries:()=>[]};\n${actualInspect}\nreturn inspectTransaction;`,
      )(
        join,
        ownedOpen,
        () => {
          if (scenario === 'spawn') throw new Error('PRIVATE_SPAWN');
          return child;
        },
        wait,
        {
          supportingMilliseconds(value: number) {
            timeout = value;
            return value;
          },
        },
        assert,
        rm,
        projectTransactionInventoryFailure,
        root,
        diagnostic,
      );
      if (scenario === 'success') {
        expect(await invoke('decode')).toEqual({ records: [], private: 'OWNED_TEST_DATA' });
        expect(diagnostic).toEqual({});
      } else {
        await expect(invoke('decode')).rejects.toThrow();
        expect(diagnostic.transaction_inventory.stage).toBe(
          scenario === 'nonzero' ? 'control_identity' : 'unknown',
        );
        expect(JSON.stringify(diagnostic)).not.toMatch(/PRIVATE|private\/source|OWNED_TEST/u);
      }
      if (!['write', 'spawn', 'collision'].includes(scenario)) expect(timeout).toBe(45000);
      expect(killed).toBe(scenario === 'overflow');
      if (scenario === 'collision') {
        expect(readFileSync(file, 'utf8')).toBe('OTHER_OWNER');
        rmSync(file);
      } else {
        expect(existsSync(file)).toBe(false);
        expect(closed).toBe(true);
      }
      for (const bytes of chunks) expect(bytes.every((byte) => byte === 0)).toBe(true);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// Link the actual public supporting reader; this probe never executes a review,
// an original candidate, the archive consumer, or a managed/private package build.
const probe = String.raw`
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.SolusProductionCutoverFixture;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

if (args[0] == "scope-fixture") return await EmitScopeFixture(args);
if (args[0] == "transaction-inventory") {
 using var facts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(Environment.CurrentDirectory, "probe-facts.json")));
 var fixture = FixtureHost(Environment.CurrentDirectory, facts.RootElement.GetProperty("workflow").GetString()!,
     facts.RootElement.GetProperty("permission").GetString()!);
 using var fixtureInputs = fixture.Launch.Inputs;
 var code = await TransactionInventoryProof.RunAsync(args[1], fixture.Host);
 fixture.NoEffects();
 return code;
}
if (args[0] == "classifications") {
 var type = typeof(TransactionInventoryProof);
 var stageType = type.GetNestedType("InventoryStage", BindingFlags.NonPublic)!;
 var describe = type.GetMethod("DescribeFailure", BindingFlags.NonPublic | BindingFlags.Static)!;
 var stages = Enum.GetValues(stageType).Cast<object>().Append(Enum.ToObject(stageType, 999));
 Exception[] failures = [new OperationCanceledException("PRIVATE_CANCEL"), new UnauthorizedAccessException("PRIVATE_ACCESS"),
  new JsonException("PRIVATE_JSON"), new CryptographicException("PRIVATE_CRYPTO"), new PoisonFormat(), new PoisonIo(),
  new ArgumentException("PRIVATE_ARG"), new InvalidOperationException("PRIVATE_GUARD"), new PoisonUnknown()];
 var rows = new List<string>();
 foreach (var stage in stages) foreach (var failure in failures) rows.Add((string)describe.Invoke(null, [stage, failure])! + "\n");
 Console.Write(JsonSerializer.Serialize(rows)); return 0;
}
var mode = args[0]; var path = Path.Join(args[1], "PRIVATE-input.json");
if (mode == "missing") return await TransactionInventoryProof.RunAsync(path);
if (mode == "json") { await File.WriteAllTextAsync(path, "{PRIVATE_KEY\n/private/source"); return await TransactionInventoryProof.RunAsync(path); }
using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle("42");
if (!LocatorStateKeyRing.TryCreate(access, "42", Convert.ToBase64String(Enumerable.Repeat((byte)0x74, 32).ToArray()), null, out var keys, out _)) throw new Exception("probe_keys");
using (keys) {
 var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
 var sentinel = new LocatorRootSentinel(Enumerable.Repeat((byte)0x65, 32).ToArray(), 0, keys!.CurrentKeyId, now - 1, now + 86400, [], []);
 if (!LocatorRootSentinelCodec.TryEncrypt(access, keys, sentinel, out var root, out _)) throw new Exception("probe_root");
 if (!LocatorContext.TryCreate(access, keys, sentinel.Root, true, sentinel.RequiredExpiresAtUnixSeconds, TimeProvider.System, out var context)) throw new Exception("probe_context");
 using (context) {
  var scope = new string('a', 64);
  var names = StateObjectClasses.All.ToDictionary(StateObjectClasses.ToWireName, value => "apr-state-test-" + StateObjectClasses.ToWireName(value).Replace('_','-'));
  names["locator_root"] = LocatorRootFormat.StoreName;
  var entries = new List<TransactionInventoryProof.Entry>();
  var rootBytes = mode == "root" ? new byte[]{1,2,3} : root!;
  entries.Add(Carrier("1", mode == "no-root" ? "other-root" : LocatorRootFormat.StoreName, rootBytes));
  if (mode == "base64") entries[0] = entries[0] with { Archive = "%PRIVATE_BASE64" };
  if (mode == "transport") entries[0] = entries[0] with { Archive = Convert.ToBase64String(new byte[]{1,2,3}) };
  if (mode == "transport-identity") entries[0] = entries[0] with { Run = "43" };
  if (mode == "scope") names.Clear();
  if (mode == "name") names["locator_root"] = "other-root";
  if (new[]{"envelope","identity","candidate","acceptance","claim"}.Contains(mode)) {
   var kind = mode == "candidate" ? StateObjectClass.Candidate : mode == "acceptance" ? StateObjectClass.Acceptance : mode == "claim" ? StateObjectClass.ContextClaim : StateObjectClass.Cleanup;
   var name = names[StateObjectClasses.ToWireName(kind)];
   var draft = new StateControlHeaderDraft(mode == "identity" ? new string('b',64) : scope, new string('c',64), new string('d',64), kind, null, null, "42", 1, now, now+3600, now+86400);
   if (!StateControlEnvelopeV1Codec.TryEncrypt(context!, access, new(name), draft, new byte[]{1,2,3}, out var envelope, out _, out _)) throw new Exception("probe_envelope");
   if (mode == "envelope") envelope[0] ^= 1;
   entries.Add(Carrier("2", name, envelope));
  }
  var input = new { mode = mode == "derive" ? "derive" : mode == "shape" ? "PRIVATE_MODE" : "decode", launch = (object?)null, artifacts = entries, names, scopeDigest = scope };
  await File.WriteAllTextAsync(path, JsonSerializer.Serialize(input));
  return await TransactionInventoryProof.RunAsync(path);
 }
}
static TransactionInventoryProof.Entry Carrier(string id, string name, byte[] bytes) {
 var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
 var json = ArtifactTransportFormat.EncodeCarrier("42", 1, bytes, hash);
 using var stream = new MemoryStream();
 using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
  using var entry = zip.CreateEntry(ArtifactTransportFormat.Entry, CompressionLevel.NoCompression).Open(); entry.Write(json);
 }
 return new(id, ArtifactTransportFormat.PhysicalName(name, hash), Convert.ToBase64String(stream.ToArray()), "42", 1);
}

static ProbeHost FixtureHost(string root, string workflow, string permission) {
 var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
 scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with { HeadSha = workflow };
 scenario.Transport.Permission = new(permission);
 if (!ActionHostProviderApiKey.TryCreate("provider-key-canary", out var provider) ||
     !ActionHostStateKey.TryCreate(Convert.ToBase64String(Enumerable.Repeat((byte)0x74, 32).ToArray()), out var stateKey) ||
     !ActionHostInputs.TryCreate(scenario.Launch.Inputs.GitHubToken, provider, stateKey, null,
         scenario.Launch.Inputs.ConfigPath, scenario.Launch.Inputs.PullRequestNumber, ActionHostStateMode.Auto, out var inputs))
     throw new Exception("probe_inputs");
 var first = scenario.Launch;
 if (!ActionHostLaunchContract.TryCreate(inputs, first.EventJsonPath, first.EventJsonSha256,
     first.RepositoryName, first.RepositoryId, first.RunId, first.RunAttempt, first.WorkflowPath,
     first.WorkflowRef, workflow, first.ActionSourceSha, first.PayloadSha256, first.BuildDiscriminator,
     first.Cancellation, first.ArtifactBridgeEndpoint, out var launch)) throw new Exception("probe_launch");
 var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, workflowSha: workflow);
 var state = new DeniedState(); var providerCounter = new DeniedProvider(); var publisher = new DeniedPublisher();
 var host = new ActionHostComposition(new ActionHostCompositionDependencies(scenario.EventReader,
     scenario.Factory, github, github, state, publisher, providerCounter, TimeProvider.System,
     () => Path.Join(root, "preparation-" + Guid.NewGuid().ToString("N"))));
 return new(host, launch!, scenario, github, state, providerCounter, publisher);
}
static async Task<int> EmitScopeFixture(string[] args) {
 var fixture = FixtureHost(args[1], args[3], "write");
 using var inputs = fixture.Launch.Inputs;
 var prepared = await fixture.Host.RunPreparedFreshAsync(fixture.Launch, current => {
     if (!AcceptedStateProductionAuthorization.TryAuthorize(current.RestoreRequest, out var authority))
         throw new Exception("probe_scope_authorization");
     return Task.FromResult(new ProbeScope(AuthorizedAcceptedStateComposer.BaseScope(authority!)));
 }, default);
 if (prepared.Failure is not null || prepared.Value is null || fixture.Scenario.Factory.Calls == 0 ||
     fixture.GitHub.GitBlobCalls == 0) throw new Exception("probe_scope_preparation");
 fixture.NoEffects();
 if (!LineageBaseScopeCodec.TryEncode(prepared.Value.Scope, out var canonical) ||
     !ActionHostJsonCodec.TryWriteLaunch(fixture.Launch, out var launchBytes)) throw new Exception("probe_scope_encoding");
 var scope = Convert.ToHexStringLower(SHA256.HashData(canonical));
 using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle("42");
 if (!LocatorStateKeyRing.TryCreate(access, "42", Convert.ToBase64String(Enumerable.Repeat((byte)0x74, 32).ToArray()), null, out var keys, out _)) throw new Exception("probe_scope_keys");
 using (keys) {
   var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   var required = now + 10 * 86400;
   var sentinel = new LocatorRootSentinel(Enumerable.Repeat(byte.Parse(args[2]), 32).ToArray(), 0, keys!.CurrentKeyId, now-1, required, [], []);
   if (!LocatorRootSentinelCodec.TryEncrypt(access, keys, sentinel, out var root, out _) ||
       !LocatorContext.TryCreate(access, keys, sentinel.Root, true, required, TimeProvider.System, out var locator)) throw new Exception("probe_scope_root");
   using (locator) {
     var names = new Dictionary<string,string> { ["locator_root"] = LocatorRootFormat.StoreName };
     foreach (var kind in StateObjectClasses.Scoped) {
       var wire = StateObjectClasses.ToWireName(kind);
       if (!locator!.TryDeriveOpaqueName(access, wire, canonical, out var name)) throw new Exception("probe_scope_name");
       names.Add(wire, name!.Value);
     }
     var variant = args[4]; var objectClass = variant == "acceptance" ? StateObjectClass.Acceptance : StateObjectClass.Cleanup;
     var objectName = names[StateObjectClasses.ToWireName(objectClass)];
     byte[] payload = [1,2,3];
     if (variant == "acceptance") {
       var accepted = new AcceptanceReceiptV1(new string('1',64), new string('2',64), null, null,
         new string('e',40), StickyPublicationOperation.Observed, 42, 147, 99,
         "https://github.com/SolusQuest/agentic-pr-review/pull/147#issuecomment-99",
         new string('3',64), new string('4',64), new string('5',64), "42", 1, now,
         now + AcceptedStateFormat.LogicalWindowSeconds);
       if (!AcceptedStateAcceptanceReceiptCodec.TryEncode(accepted, out payload)) throw new Exception("probe_acceptance");
     }
     var draft = new StateControlHeaderDraft(variant == "wrong-scope" ? new string('f',64) : scope,
       new string('c',64), new string('d',64), objectClass, null, null, "42", 1, now,
       now + AcceptedStateFormat.LogicalWindowSeconds, now + 8 * 86400);
     if (!StateControlEnvelopeV1Codec.TryEncrypt(locator!, access, new(objectName), draft, payload, out var envelope, out _, out _)) throw new Exception("probe_scope_envelope");
     if (variant == "envelope") envelope[0] ^= 1;
     var physicalName = variant == "name" ? objectName[..^1] + (objectName[^1] == '0' ? '1' : '0') : objectName;
     var entry = Carrier("2", physicalName, envelope);
     if (variant == "identity") entry = entry with { Run = "43" };
     // The transport identity must agree; a separate valid carrier tests the header guard.
     if (variant == "identity") {
       var carrierJson = ArtifactTransportFormat.EncodeCarrier("43", 1, envelope, Convert.ToHexStringLower(SHA256.HashData(envelope)));
       using var stream = new MemoryStream();
       using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { using var dest = zip.CreateEntry(ArtifactTransportFormat.Entry, CompressionLevel.NoCompression).Open(); dest.Write(carrierJson); }
       entry = entry with { Archive = Convert.ToBase64String(stream.ToArray()) };
     }
     var entries = new[] { Carrier("1", LocatorRootFormat.StoreName, root!), entry };
     Console.Write(JsonSerializer.Serialize(new { launch = JsonSerializer.Deserialize<JsonElement>(launchBytes), artifacts = entries, names, scopeDigest = scope }));
   }
 }
 return 0;
}

sealed record ProbeScope(LineageBaseScope Scope);
sealed record ProbeHost(ActionHostComposition Host, ActionHostLaunchContract Launch,
 ActionHostAuthorizationScenario Scenario, FullPathGitHubFactory GitHub, DeniedState State,
 DeniedProvider Provider, DeniedPublisher Publisher) {
 internal void NoEffects() { if (State.Calls != 0 || Provider.Calls != 0 || Publisher.Calls != 0) throw new Exception("probe_effect"); }
}
sealed class DeniedState : IAcceptedStateProductionDependencies {
 internal int Calls;
 public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) { Calls++; throw new Exception("probe_state"); }
 public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) { Calls++; throw new Exception("probe_state"); }
}
sealed class DeniedProvider : IActionHostProviderRunnerFactory {
 internal int Calls;
 public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key, ReviewedSnapshot snapshot, TimeProvider time) { Calls++; throw new Exception("probe_provider"); }
}
sealed class DeniedPublisher : IStickyGitHubPublisherTransportFactory {
 internal int Calls;
 public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token, AuthorizedStickyPublicationRequest request, CancellationToken cancellation = default) { Calls++; throw new Exception("probe_publication"); }
 public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token, AuthorizedStickyReadbackRequest request, CancellationToken cancellation = default) { Calls++; throw new Exception("probe_publication"); }
}
sealed class PoisonIo : IOException {
 public override string Message => throw new Exception("message_accessed");
 public override string? StackTrace => throw new Exception("stack_accessed");
 public override string ToString() => throw new Exception("string_accessed");
}
sealed class PoisonFormat : FormatException {
 public override string Message => throw new Exception("PRIVATE_FORMAT");
 public override string? StackTrace => throw new Exception("PRIVATE_STACK");
}
sealed class PoisonUnknown : Exception {
 public override string Message => throw new Exception("PRIVATE_UNKNOWN");
 public override string? StackTrace => throw new Exception("PRIVATE_STACK");
}
`;

// Exercise the actual capacity postcondition and child process with synthetic
// completion facts. Only the archive consumer is replaced; authorization,
// canonical scope, physical names, envelopes and the supporting reader are real.
async function verifyScopeLifetimes(root: string, dotnet: string, dll: string) {
  const workflowA = 'a'.repeat(40),
    workflowB = 'b'.repeat(40);
  const executable = realpathSync(
    dotnet.includes('/') ? dotnet : execFileSync('which', [dotnet], { encoding: 'utf8' }).trim(),
  );
  symlinkSync(dirname(executable), join(root, 'runtime'));
  symlinkSync(join(root, 'out'), join(root, 'cutover-inputs'));
  const fixture = (seed: number, workflow: string, variant = 'cleanup') =>
    JSON.parse(
      execFileSync(dotnet, [dll, 'scope-fixture', root, String(seed), workflow, variant], {
        encoding: 'utf8',
        timeout: 20000,
      }),
    );
  const a = fixture(101, workflowA),
    b = fixture(101, workflowB);
  expect(b.scopeDigest).not.toBe(a.scopeDigest);
  const rootB = fixture(102, workflowA),
    both = fixture(102, workflowB);
  let current = a;
  const diagnostic: any = {};
  const facts = (workflow: string, permission = 'write') =>
    writeFileSync(join(root, 'probe-facts.json'), JSON.stringify({ workflow, permission }));
  const inspect = new Function(
    'join',
    'open',
    'spawn',
    'awaitFixtureGroupClose',
    'roleDeadline',
    'assert',
    'rm',
    'projectTransactionInventoryFailure',
    'root',
    'diagnostic',
    'entries',
    'identity',
    `let transactionSerial=0; const transactionIdentity=identity; const transaction={entries};\n${actualInspect}\nreturn inspectTransaction;`,
  )(
    join,
    open,
    spawn,
    awaitFixtureGroupClose,
    { supportingMilliseconds: (value: number) => value },
    assert,
    rm,
    projectTransactionInventoryFailure,
    root,
    diagnostic,
    () => current.artifacts,
    a,
  );
  facts(workflowA);
  const initial = await inspect('derive', a.launch);
  expect(initial.scopeDigest).toBe(a.scopeDigest);
  expect((await inspect('decode')).records.map((record: any) => record.kind)).toEqual(['cleanup']);
  // Historical names must fail when either canonical scope or root changes.
  for (const [value, workflow] of [
    [b, workflowB],
    [rootB, workflowA],
    [both, workflowB],
  ] as const) {
    current = value;
    facts(workflow);
    await expect(inspect('decode')).rejects.toThrow('v1_inventory_refused');
    expect(diagnostic.transaction_inventory.stage).toBe('object_name');
    expect((await inspect('derive', current.launch)).scopeDigest).toBe(current.scopeDigest);
  }
  const capacity = archive.slice(
    archive.indexOf('    for (const [name, mode, sends, termination'),
    archive.indexOf('    for (const [name, review, expectedSends] of ['),
  );
  const accounting = archive.slice(
    archive.indexOf('  function assertAccounting('),
    archive.indexOf('  async function diagnosePreparation('),
  );
  expect(capacity).toContain('managed-batch-capacity');
  const seen: string[] = [];
  const provider = { count: 0 },
    github = { writes: 0, restore: () => {} };
  let records = () => [{ kind: 'launch', document: current.launch }];
  const run = async (name: string, _ordinal: number, options: { mode: string }) => {
    seen.push(name);
    const sends = (
      { 'tool-capacity': 3, 'call-capacity': 6, 'batch-capacity': 1 } as Record<string, number>
    )[options.mode];
    provider.count = sends;
    return {
      status: 'failed',
      records: records(),
      reported: {
        'termination-reason': 'resource_limit',
        'model-calls': String(sends),
        'provider-attempts': String(sends),
        'provider-retries': '0',
        'provider-failed-attempts': String(options.mode === 'batch-capacity' ? 1 : 0),
        'input-tokens': String(sends * 10),
        'input-cache-hit-tokens': String(sends * 4),
        'input-cache-miss-tokens': String(sends * 6),
        'output-tokens': String(sends * 5),
      },
    };
  };
  const exercise = new Function(
    'assert',
    'official',
    'github',
    'provider',
    'run',
    'inspectTransaction',
    `return async () => {${accounting}\n${capacity}\n};`,
  )(assert, { restore: () => {} }, github, provider, run, inspect);
  current = b;
  facts(workflowB);
  // RED on the old pure-decode implementation; GREEN derives each closed case.
  await exercise();
  expect(seen).toEqual([
    'managed-tool-capacity',
    'managed-call-capacity',
    'managed-batch-capacity',
  ]);
  for (const value of [rootB, both]) {
    current = value;
    facts(value === rootB ? workflowA : workflowB);
    await exercise();
  }
  for (const [variant, stage] of [
    ['name', 'object_name'],
    ['wrong-scope', 'control_identity'],
    ['envelope', 'control_envelope'],
    ['identity', 'control_identity'],
  ]) {
    current = fixture(101, workflowB, variant);
    facts(workflowB);
    await expect(exercise()).rejects.toThrow('v1_inventory_refused');
    expect(diagnostic.transaction_inventory.stage, variant).toBe(stage);
  }
  current = fixture(101, workflowB, 'acceptance');
  facts(workflowB);
  expect(
    (await inspect('derive', current.launch)).records.some(
      (record: any) => record.kind === 'acceptance',
    ),
  ).toBe(true);
  await expect(exercise()).rejects.toThrow();
  current = b;
  facts(workflowB, 'read');
  await expect(exercise()).rejects.toThrow('v1_inventory_refused');
  expect(diagnostic.transaction_inventory.stage).toBe('scope_derive');
  facts(workflowA);
  await expect(exercise()).rejects.toThrow('v1_inventory_refused');
  expect(diagnostic.transaction_inventory.stage).toBe('scope_derive');
  facts(workflowB);
  for (const launch of [null, {}, { ...b.launch, cancellation: 'requested' }]) {
    current = { ...b, launch };
    await expect(exercise()).rejects.toThrow('v1_inventory_refused');
    expect(diagnostic.transaction_inventory.stage).toBe('scope_derive');
  }
  current = { ...b, artifacts: [] };
  await expect(exercise()).rejects.toThrow('v1_inventory_refused');
  expect(diagnostic.transaction_inventory.stage).toBe('input_shape');
  current = b;
  for (const count of [0, 2]) {
    records = () =>
      Array.from({ length: count }, () => ({ kind: 'launch', document: current.launch }));
    await expect(exercise()).rejects.toThrow('v1_current_launch_required');
  }
  expect(readdirSync(root).filter((name) => /^transaction-\d+\.json$/u.test(name))).toEqual([]);
}

test('actual C# reader emits only closed diagnostics and retains real synthetic codec refusal/success', async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'apr-inventory-reader-'));
  const dotnet = process.env.DOTNET_CMD ?? 'dotnet';
  const xml = (value: string) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  try {
    writeFileSync(join(root, 'Program.cs'), probe);
    writeFileSync(
      join(root, 'Probe.csproj'),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost><AssemblyName>AgenticPrReview.Runtime.SolusProductionCutoverFixture</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="${xml(resolve('runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj'))}"/><Compile Include="${xml(resolve('runtime/tests/SolusProductionCutoverFixture/TransactionInventoryProof.cs'))}" Link="TransactionInventoryProof.cs"/><Compile Include="${xml(resolve('runtime/tests/SharedHostPorts/ActionHostAuthorizationTestData.cs'))}" Link="ActionHostAuthorizationTestData.cs"/><Compile Include="${xml(resolve('runtime/tests/SharedHostPorts/FullPathGitHubFactory.cs'))}" Link="FullPathGitHubFactory.cs"/></ItemGroup></Project>`,
    );
    execFileSync(
      dotnet,
      [
        'build',
        join(root, 'Probe.csproj'),
        '--artifacts-path',
        join(root, 'artifacts'),
        '-o',
        join(root, 'out'),
        '-m:1',
        '-p:APRManagedPackage=false',
        '-p:UseSharedCompilation=false',
        '-p:NuGetAudit=false',
        '-p:RestoreSources=https://api.nuget.org/v3/index.json',
        '--nologo',
        '-v:q',
      ],
      { timeout: 120000, maxBuffer: 4 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] },
    );
    const dll = join(root, 'out/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll');
    const frames: string[] = JSON.parse(
      execFileSync(dotnet, [dll, 'classifications'], { encoding: 'utf8', timeout: 20000 }),
    );
    const seenStages = new Set<string>(),
      seenErrors = new Set<string>();
    expect(frames).toHaveLength(
      transactionInventoryStages.length * transactionInventoryErrors.length,
    );
    for (const frame of frames) {
      const projected = projectTransactionInventoryFailure({
        code: 1,
        signal: null,
        watchdog: false,
        overflow: false,
        stdout: Buffer.alloc(0),
        stderr: Buffer.from(frame),
      });
      expect(projected.error).not.toBe('unavailable');
      expect(frame).not.toMatch(/PRIVATE|Exception|accessed/u);
      seenStages.add(projected.stage);
      seenErrors.add(projected.error);
    }
    expect([...seenStages].sort()).toEqual([...transactionInventoryStages].sort());
    expect([...seenErrors].sort()).toEqual([...transactionInventoryErrors].sort());
    const expected: Record<string, string> = {
      missing: 'input_read',
      json: 'input_json',
      shape: 'input_shape',
      base64: 'transport',
      transport: 'transport',
      'transport-identity': 'transport_identity',
      'no-root': 'locator_root',
      root: 'locator_root',
      scope: 'scope_identity',
      name: 'object_name',
      derive: 'scope_derive',
      envelope: 'control_envelope',
      identity: 'control_identity',
      candidate: 'candidate_copy',
      acceptance: 'acceptance_record',
      claim: 'context_claim',
    };
    for (const [mode, stage] of Object.entries(expected)) {
      const input = join(root, 'PRIVATE-input.json');
      rmSync(input, { force: true });
      const child = spawnSync(dotnet, [dll, mode, root], { timeout: 20000 });
      expect(child.error).toBeUndefined();
      expect(child.signal).toBeNull();
      expect(child.status).toBe(1);
      expect(child.stdout.length).toBe(0);
      const projected = projectTransactionInventoryFailure({
        code: child.status,
        signal: child.signal,
        watchdog: false,
        overflow: false,
        stdout: child.stdout,
        stderr: child.stderr,
      });
      expect(projected.stage, mode).toBe(stage);
      expect(projected.error).not.toBe('unavailable');
      expect(child.stderr.toString()).not.toMatch(/PRIVATE|private\/source|Exception/u);
    }
    const success = spawnSync(dotnet, [dll, 'success', root], { timeout: 20000 });
    expect(success.error).toBeUndefined();
    expect(success.status).toBe(0);
    expect(success.stderr.length).toBe(0);
    const originalResult = JSON.parse(success.stdout.toString());
    expect(originalResult.records).toEqual([]);
    expect(originalResult.contextFormat).toBe(2);
    expect(originalResult.contextCompatibility).toBe(1);
    expect(originalResult.policy).toBeNull();
    expect(originalResult.scopeDigest).toBe('a'.repeat(64));
    await verifyScopeLifetimes(root, dotnet, dll);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}, 180000);
