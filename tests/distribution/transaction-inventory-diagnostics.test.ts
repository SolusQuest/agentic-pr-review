import { execFileSync, spawnSync } from 'node:child_process';
import {
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
  existsSync,
} from 'node:fs';
import { open, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { PassThrough } from 'node:stream';
import assert from 'node:assert/strict';
import { expect, test } from 'vitest';
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
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.SolusProductionCutoverFixture;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
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

test('actual C# reader emits only closed diagnostics and retains real synthetic codec refusal/success', () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'apr-inventory-reader-'));
  const dotnet = process.env.DOTNET_CMD ?? 'dotnet';
  const xml = (value: string) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  try {
    writeFileSync(join(root, 'Program.cs'), probe);
    writeFileSync(
      join(root, 'Probe.csproj'),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost><AssemblyName>AgenticPrReview.Runtime.SolusProductionCutoverFixture</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="${xml(resolve('runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj'))}"/><Compile Include="${xml(resolve('runtime/tests/SolusProductionCutoverFixture/TransactionInventoryProof.cs'))}" Link="TransactionInventoryProof.cs"/></ItemGroup></Project>`,
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
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}, 180000);
