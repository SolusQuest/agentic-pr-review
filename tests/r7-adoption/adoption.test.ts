import { afterAll, beforeAll, describe, expect, test, vi } from 'vitest';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, delimiter } from 'node:path';
import { spawnSync, spawn } from 'node:child_process';
import { finalizationFixture } from '../release/finalization.fixtures.mjs';
import { finalizeCandidate } from '../../scripts/release/finalize-candidate.mjs';
import {
  parsePlan,
  preparationSummary,
  canonicalJson,
  sha256,
} from '../../scripts/r7-adoption/protocol.mjs';
import { projectEvidence, OUTPUTS } from '../../scripts/r7-adoption/evidence.mjs';

let f: any, plan: any, inputs: any;
const now = Date.now();
const bytes = (v: unknown) => Buffer.from(canonicalJson(v));
const parse = (p = plan, data = inputs) => parsePlan(bytes(p), sha256(bytes(p)), data, now);
const canary = 'PRIVATE_SOURCE_TOOL_SESSION_REASONING_RESPONSE_SECRET';
beforeAll(async () => {
  f = finalizationFixture();
  vi.stubGlobal('fetch', f.fetch);
  vi.stubEnv('PATH', f.bin + delimiter + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  const finalized = await finalizeCandidate(f.input, {});
  const pairBytes = readFileSync(join(f.input.output, 'finalized-pair.json'));
  const pair = JSON.parse(pairBytes.toString());
  const q = f.qualificationFiles(pairBytes);
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  const templates = Object.fromEntries(
    ['manual', 'automatic'].map((name) => [
      name,
      readFileSync(`examples/workflows/${name}.yml.template`),
    ]),
  );
  const templateBytes = Buffer.from(
    templates.manual.toString().replaceAll('__ACTION_SOURCE_SHA__', pair.action.commit),
  );
  inputs = {
    pairBytes,
    qualificationBytes: readFileSync(q.archivePath),
    expectedChecksBytes: f.expectedBytes,
    templates,
    templateBytes,
  };
  plan = {
    formatVersion: 1,
    kind: 'r7-adoption-preparation',
    mode: 'synthetic',
    candidate: {
      pairSha256: finalized.pairSha256,
      qualificationSha256: q.archiveSha256,
      receiptSha256: q.receiptSha256,
      qualifierCommit: f.qualifier,
      qualificationArtifactId: '401',
      publicationReleaseId: '501',
      publicationRecordSha256: sha256('synthetic unpublished release placeholder'),
      actionTag: pair.payload.identity.releaseVersion,
      actionCommit: f.action,
    },
    target: {
      repository: 'synthetic/fixture',
      repositoryId: '42',
      pr: '7',
      head: 'a'.repeat(40),
      workflowCommit: 'b'.repeat(40),
      workflowPath: '.github/workflows/agentic-pr-review.yml',
      template: 'manual',
      templateSha256: sha256(templateBytes),
      configSha256: sha256('synthetic config'),
      instructionsSha256: sha256('synthetic instructions'),
    },
    content: {
      approvalRef: 'synthetic-content-approval',
      manifestSha256: sha256('synthetic approved visibility manifest'),
      head: 'a'.repeat(40),
      source: true,
      diff: true,
      instructions: true,
      tools: true,
      continuation: true,
    },
    credentials: {
      owner: 'synthetic-owner',
      environmentOwner: 'synthetic-environment',
      githubTokenRef: 'GITHUB_TOKEN',
      providerKeyRef: 'DEEPSEEK_API_KEY',
      stateKeyRef: 'AGENTIC_PR_REVIEW_STATE_KEY',
      previousStateKeyRef: null,
    },
    invocation: {
      profile: 'cr1-solus-internal4-dev',
      contextFormat: 2,
      compatibility: 1,
      maxModelCalls: 6,
      maxProviderAttempts: 6,
      maxWorkUnits: 6,
      maxToolInvocations: 6,
      maxToolsPerBatch: 3,
      timeoutSeconds: 120,
      maxUncachedInputTokens: 65536,
      maxCachedInputTokens: 65536,
      maxTotalInputTokens: 65536,
      maxOutputTokens: 8192,
    },
    campaign: {
      maxRuns: 6,
      maxModelCalls: 36,
      maxProviderAttempts: 36,
      wallSeconds: 3600,
      currency: 'USD',
      costCeiling: '1.000000',
      unknownSpend: 'stop',
    },
    authority: {
      approvalRef: 'synthetic-execution-approval',
      approvedBy: 'synthetic-operator',
      validUntil: new Date(now + 3600000).toISOString(),
      stopOwner: 'synthetic-stop-owner',
      cleanupOwner: 'synthetic-cleanup-owner',
      stop: 'disable-and-settle',
      cleanup: 'scoped-host-reconciliation',
    },
    runs: ['bootstrap', 'continuation', 'reset', 'stale-head', 'incomplete', 'failure'].map(
      (scenario) => ({
        scenario,
        stateMode: scenario === 'reset' ? 'reset' : 'auto',
        basis: 'synthetic',
      }),
    ),
  };
}, 30000);
afterAll(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  if (f) rmSync(f.root, { recursive: true, force: true });
});
function observation(scenario = 'bootstrap') {
  return {
    scenario,
    workflow: 'success',
    step: 'success',
    outputs: {
      status: 'reviewed',
      'termination-reason': 'review_completed',
      'model-calls': '2',
      'provider-attempts': '2',
      'attempt-accounting-completeness': 'complete',
      'usage-completeness': 'partial',
    },
    state:
      scenario === 'continuation'
        ? 'restored-confirmed'
        : scenario === 'reset'
          ? 'reset-confirmed'
          : 'bootstrap-confirmed',
    acceptance: 'confirmed',
    publication: 'confirmed',
    processClosed: 'confirmed',
    cleanup: 'complete',
    elapsedSeconds: 10,
    billing: 'unknown',
    head: plan.target.head,
    workflowCommit: plan.target.workflowCommit,
    runId: '900',
    runAttempt: '1',
  };
}
const report = (observations: any[], p = parse()) =>
  projectEvidence(bytes({ kind: 'r7-adoption-observations', mode: 'synthetic', observations }), p);

describe('bounded adoption preparation, synthetic identities only', () => {
  test('complete original P3 content interface remains non-operational and does not mutate originals', () => {
    const before = Buffer.from(inputs.pairBytes);
    expect(preparationSummary(parse())).toMatchObject({
      inputContentVerified: true,
      operationallyQualified: false,
      realExecutionAllowed: false,
      releaseAuthenticated: false,
    });
    expect(inputs.pairBytes).toEqual(before);
  });
  test.each([
    'candidate',
    'target',
    'content',
    'credentials',
    'invocation',
    'campaign',
    'authority',
    'runs',
  ])('refuses missing %s', (key) => {
    const p = structuredClone(plan);
    delete p[key];
    expect(() => parse(p)).toThrow();
  });
  test.each([
    'approvalRef',
    'approvedBy',
    'validUntil',
    'stopOwner',
    'cleanupOwner',
    'stop',
    'cleanup',
  ])('requires exact authority %s', (key) => {
    const p = structuredClone(plan);
    delete p.authority[key];
    expect(() => parse(p)).toThrow();
  });
  test.each([
    'maxRuns',
    'maxModelCalls',
    'maxProviderAttempts',
    'wallSeconds',
    'currency',
    'costCeiling',
    'unknownSpend',
  ])('requires explicit finite campaign %s', (key) => {
    const p = structuredClone(plan);
    delete p.campaign[key];
    expect(() => parse(p)).toThrow();
  });
  test.each([
    'pairSha256',
    'qualificationSha256',
    'receiptSha256',
    'qualifierCommit',
    'actionCommit',
    'actionTag',
  ])('refuses changed candidate %s', (key) => {
    const p = structuredClone(plan);
    p.candidate[key] = key.endsWith('Commit') ? 'f'.repeat(40) : 'f'.repeat(64);
    expect(() => parse(p)).toThrow();
  });
  test('independent approved digest, canonical shape, limits and expiry are enforced', () => {
    expect(() => parsePlan(bytes(plan), 'f'.repeat(64), inputs, now)).toThrow();
    expect(() =>
      parsePlan(Buffer.from(JSON.stringify(plan)), sha256(JSON.stringify(plan)), inputs, now),
    ).toThrow();
    const p = structuredClone(plan);
    p.authority.validUntil = new Date(now - 1).toISOString();
    expect(() => parse(p)).toThrow();
    p.authority.validUntil = plan.authority.validUntil;
    p.campaign.maxModelCalls = 1;
    expect(() => parse(p)).toThrow();
    p.campaign.maxModelCalls = 36;
    p.invocation.maxModelCalls = 7;
    expect(() => parse(p)).toThrow();
    p.invocation.maxModelCalls = 1;
    p.invocation.maxProviderAttempts = 1;
    p.invocation.maxWorkUnits = 1;
    expect(parse(p).plan.invocation.maxModelCalls).toBe(1);
  });
  test('exact two H1 templates are recognized; real automatic exposure and automatic reset refuse', () => {
    const p = structuredClone(plan);
    p.target.template = 'automatic';
    p.runs = [p.runs[0]];
    const templateBytes = Buffer.from(
      inputs.templates.automatic
        .toString()
        .replaceAll('__ACTION_SOURCE_SHA__', p.candidate.actionCommit),
    );
    p.target.templateSha256 = sha256(templateBytes);
    expect(parse(p, { ...inputs, templateBytes }).realExecutionAllowed).toBe(false);
    p.runs[0].scenario = 'reset';
    p.runs[0].stateMode = 'reset';
    expect(() => parse(p, { ...inputs, templateBytes })).toThrow();
    p.mode = 'operational-inputs';
    expect(() => parse(p, { ...inputs, templateBytes })).toThrow();
    expect(() =>
      parse(plan, { ...inputs, templateBytes: Buffer.from('changed workflow') }),
    ).toThrow();
  });
  test.each(['source', 'diff', 'instructions', 'tools', 'continuation'])(
    'approval covers %s exposure',
    (key) => {
      const p = structuredClone(plan);
      p.content[key] = false;
      expect(() => parse(p)).toThrow();
    },
  );
  test('wrong head/content/workflow/profile/context and empty budget refuse', () => {
    for (const mutate of [
      (p: any) => (p.content.head = 'f'.repeat(40)),
      (p: any) => (p.target.workflowPath = 'other.yml'),
      (p: any) => (p.invocation.contextFormat = 1),
      (p: any) => (p.invocation.profile = 'other'),
      (p: any) => (p.campaign.costCeiling = '0.000000'),
    ]) {
      const p = structuredClone(plan);
      mutate(p);
      expect(() => parse(p)).toThrow();
    }
  });
  test.each([
    'privateSource',
    'toolResult',
    'SESSION',
    'reasoning',
    'rawResponse',
    'secret',
    'path',
  ])('private %s cannot enter evidence or diagnostics', (field) => {
    const o: any = observation();
    o[field] = canary;
    let error = '';
    try {
      report([o]);
    } catch (e) {
      error = String(e);
    }
    expect(error).toContain('adoption_');
    expect(error).not.toContain(canary);
    o.outputs = { privateSource: canary };
    delete o[field];
    expect(() => report([o])).toThrow();
    const p = structuredClone(plan);
    p[field] = canary;
    expect(() => parse(p)).toThrow();
  });
  test('empty/absent/mapped numerical outputs stay unknown; zero remains measured', () => {
    const o = observation();
    o.outputs = { 'model-calls': '0', 'provider-attempts': '', 'input-tokens': null } as any;
    const result = report([o]);
    expect(result.cases[0].outputs['model-calls']).toBe('0');
    expect(result.cases[0].outputs['provider-attempts']).toBeNull();
    expect(Object.keys(result.cases[0].outputs)).toEqual(OUTPUTS);
    expect(result.cases[0].unknownWork).toBe(true);
    expect(result.result).toBe('inconclusive');
    expect(result.billing).toBe('unknown');
  });
  test('independent partial accounting does not manufacture cache partitions, history or billing', () => {
    const o = observation();
    Object.assign(o.outputs, { 'input-tokens': '10', 'input-cache-hit-tokens': '4' });
    const c = report([o]).cases[0];
    expect(c.outputs['input-cache-miss-tokens']).toBeNull();
    expect(c.usageScope).toBe('current-invocation-only');
    expect(c.outputs['usage-completeness']).toBe('partial');
  });
  test('all finite scenarios classify separately from product success', () => {
    const all = plan.runs.map((r: any) => {
      const o = observation(r.scenario);
      if (['stale-head', 'incomplete', 'failure'].includes(r.scenario)) {
        o.workflow = 'failure';
        o.step = 'failure';
        o.state = 'refused';
        o.acceptance = 'absent';
        o.publication = 'absent';
        o.outputs.status = (
          {
            'stale-head': 'stale_head',
            incomplete: 'snapshot_incomplete',
            failure: 'provider_failed',
          } as any
        )[r.scenario];
        o.outputs['termination-reason'] = 'host_failure';
      }
      return o;
    });
    expect(report(all).result).toBe('success');
    all[0].publication = 'unknown';
    expect(report(all).result).toBe('inconclusive');
    all[0].workflow = 'failure';
    all[0].acceptance = 'absent';
    all[0].publication = 'absent';
    expect(report(all).result).toBe('failed');
  });
  test.each(['processClosed', 'cleanup', 'acceptance', 'publication'])(
    'unknown %s cannot produce success',
    (key) => {
      const o: any = observation();
      o[key] = 'unknown';
      expect(report([o]).cases[0].result).toBe('inconclusive');
    },
  );
  test('observation identity, duplicate scope, overflow, arbitrary tokens and contradictions refuse', () => {
    for (const mutate of [
      (o: any) => (o.head = 'c'.repeat(40)),
      (o: any) => (o.outputs.status = canary),
      (o: any) => (o.outputs['provider-attempts'] = '9223372036854775808'),
      (o: any) => (o.outputs['provider-attempts'] = '7'),
    ]) {
      const o = observation();
      mutate(o);
      expect(() => report([o])).toThrow();
    }
    expect(() => report([observation(), observation()])).toThrow();
  });
  test('actual CLI dry-run and refusal paths have zero network, subprocess or credential access', async () => {
    const files: Record<string, Buffer> = {
      plan: bytes(plan),
      pair: inputs.pairBytes,
      qualification: inputs.qualificationBytes,
      checks: inputs.expectedChecksBytes,
      template: inputs.templateBytes,
    };
    for (const [name, data] of Object.entries(files))
      writeFileSync(join(f.root, 'adoption-' + name), data);
    const guard = join(f.root, 'offline-guard.mjs');
    writeFileSync(
      guard,
      `import net from 'node:net'; import http from 'node:http'; import https from 'node:https'; import cp from 'node:child_process'; import fs from 'node:fs/promises'; import {syncBuiltinESMExports} from 'node:module'; const deny=()=>{throw Error('FORBIDDEN_SIDE_EFFECT')}; globalThis.fetch=deny; net.Socket.prototype.connect=deny; http.request=deny; https.request=deny; for(const key of ['exec','execFile','spawn','fork','execSync','execFileSync','spawnSync'])cp[key]=deny; for(const key of ['readFile','open']) {const read=fs[key];fs[key]=(p,...a)=>{if(String(p).includes('credential-canary'))deny();return read(p,...a)}}; syncBuiltinESMExports();`,
    );
    const args = [
      '--import',
      guard,
      'scripts/r7-adoption/cli.mjs',
      'dry-run',
      ...Object.keys(files).flatMap((name) => ['--' + name, join(f.root, 'adoption-' + name)]),
      '--approved-sha256',
      sha256(files.plan),
    ];
    const env = {
      ...process.env,
      HOME: join(f.root, 'credential-canary'),
      DEEPSEEK_API_KEY: canary,
      GITHUB_TOKEN: canary,
    };
    const ok = spawnSync(process.execPath, args, { encoding: 'utf8', env });
    expect(ok.status, ok.stderr).toBe(0);
    expect(JSON.parse(ok.stdout)).toMatchObject({ realExecutionAllowed: false, mode: 'synthetic' });
    expect(ok.stdout + ok.stderr).not.toContain(canary);
    const sinkExit = await new Promise<number | null>((resolve, reject) => {
      const child = spawn(process.execPath, args, { env, stdio: ['ignore', 'pipe', 'pipe'] });
      child.stdout.destroy();
      child.on('error', reject);
      child.on('close', resolve);
    });
    expect(sinkExit).not.toBe(0);
    const observationsPath = join(f.root, 'adoption-observations');
    writeFileSync(
      observationsPath,
      bytes({ kind: 'r7-adoption-observations', mode: 'synthetic', observations: [observation()] }),
    );
    const observeArgs = [...args];
    observeArgs[3] = 'observe';
    const observed = spawnSync(
      process.execPath,
      [...observeArgs, '--observations', observationsPath],
      { encoding: 'utf8', env },
    );
    expect(observed.status, observed.stderr).toBe(0);
    expect(JSON.parse(observed.stdout).result).toBe('inconclusive');
    const clock = join(f.root, 'observation-clock.mjs');
    for (const mode of ['synthetic', 'operational-inputs']) {
      const historical = structuredClone(plan);
      historical.mode = mode;
      if (mode === 'operational-inputs') {
        // Fabricated original-candidate content exercises the parser, not qualification.
        const q = f.qualificationFiles(inputs.pairBytes, { executionKind: 'original-candidate' });
        historical.candidate.qualificationSha256 = q.archiveSha256;
        historical.candidate.receiptSha256 = q.receiptSha256;
        writeFileSync(join(f.root, 'adoption-qualification'), readFileSync(q.archivePath));
      }
      historical.runs = [historical.runs[0]];
      historical.authority.validUntil = new Date(now + 3600000).toISOString();
      Object.assign(historical.invocation, {
        maxUncachedInputTokens: 8192,
        maxCachedInputTokens: 8192,
        maxTotalInputTokens: 16384,
      });
      const original = bytes(historical);
      writeFileSync(join(f.root, 'adoption-plan'), original);
      const late = observation();
      late.cleanup = 'unknown';
      late.outputs = {
        status: 'reviewed',
        'termination-reason': 'review_completed',
        'input-cache-miss-tokens': '8193',
      } as any;
      writeFileSync(
        observationsPath,
        bytes({ kind: 'r7-adoption-observations', mode, observations: [late] }),
      );
      const invoke = (command: string, time: number) => {
        writeFileSync(clock, `Date.now = () => ${time};`);
        const selected = [...args];
        selected[3] = command;
        selected[selected.length - 1] = sha256(original);
        return spawnSync(
          process.execPath,
          [
            '--import',
            clock,
            ...selected,
            ...(command === 'observe' ? ['--observations', observationsPath] : []),
          ],
          { encoding: 'utf8', env },
        );
      };
      expect(invoke('validate', now).status).toBe(0);
      for (const time of [now, now + 3600000, now + 30 * 86400000]) {
        const retrospective = invoke('observe', time);
        expect(retrospective.status, retrospective.stderr).toBe(0);
        const result = JSON.parse(retrospective.stdout);
        expect(result).toMatchObject({
          result: 'failed',
          stopRequired: true,
          realExecutionAllowed: false,
          releaseAuthenticated: false,
          operationallyQualified: false,
        });
        expect(result.cases[0]).toMatchObject({
          cleanup: 'unknown',
          unknownWork: true,
          outputs: { 'input-cache-miss-tokens': '8193', 'provider-attempts': null },
        });
        expect(retrospective.stdout + retrospective.stderr).not.toContain(canary);
      }
      delete (late.outputs as any)['input-cache-miss-tokens'];
      for (const cleanup of ['unknown', 'complete']) {
        late.cleanup = cleanup;
        writeFileSync(
          observationsPath,
          bytes({ kind: 'r7-adoption-observations', mode, observations: [late] }),
        );
        const reconciled = invoke('observe', now + 30 * 86400000);
        expect(reconciled.status, reconciled.stderr).toBe(0);
        expect(JSON.parse(reconciled.stdout).result).toBe(
          cleanup === 'complete' ? 'success' : 'inconclusive',
        );
      }
      for (const command of ['validate', 'dry-run']) {
        for (const time of [now + 3600000, now + 30 * 86400000, now - 86400000]) {
          const denied = invoke(command, time);
          expect(denied.status).toBe(1);
          expect(denied.stdout).toBe('');
          expect(denied.stderr).toBe('adoption_refused\n');
        }
      }
      expect(readFileSync(join(f.root, 'adoption-plan'))).toEqual(original);
    }
    for (const [name, data] of Object.entries(files))
      writeFileSync(join(f.root, 'adoption-' + name), data);
    const sensitive = observation();
    sensitive.outputs.status = canary;
    writeFileSync(
      observationsPath,
      bytes({ kind: 'r7-adoption-observations', mode: 'synthetic', observations: [sensitive] }),
    );
    const denied = spawnSync(
      process.execPath,
      [...observeArgs, '--observations', observationsPath],
      { encoding: 'utf8', env },
    );
    expect(denied.status).toBe(1);
    expect(denied.stdout).toBe('');
    expect(denied.stderr).toBe('adoption_refused\n');
    const bad = spawnSync(process.execPath, [...args, '--raw', canary], { encoding: 'utf8', env });
    expect(bad.status).toBe(1);
    expect(bad.stdout).toBe('');
    expect(bad.stderr).toBe('adoption_refused\n');
    writeFileSync(join(f.root, 'adoption-plan'), bytes({ ...plan, rawResponse: canary }));
    const rejected = spawnSync(process.execPath, args, { encoding: 'utf8', env });
    expect(rejected.status).toBe(1);
    expect(rejected.stdout + rejected.stderr).not.toContain(canary);
  });
});

describe('review regression: executable ceilings, known facts and actual execution', () => {
  test.each([
    'maxProviderAttempts',
    'maxWorkUnits',
    'maxToolInvocations',
    'maxToolsPerBatch',
    'maxTotalInputTokens',
  ])('B01 refuses independently invented %s', (key) => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    p.invocation[key] = 1;
    expect(() => parse(p)).toThrow();
  });
  test('B01 lower supported fields derive dispatch/work/combined input exactly', () => {
    const p = structuredClone(plan);
    Object.assign(p.invocation, {
      maxModelCalls: 1,
      maxProviderAttempts: 1,
      maxWorkUnits: 1,
      maxUncachedInputTokens: 100,
      maxCachedInputTokens: 200,
      maxTotalInputTokens: 300,
    });
    expect(parse(p).plan.invocation.maxTotalInputTokens).toBe(300);
    p.invocation.maxTotalInputTokens = 299;
    expect(() => parse(p)).toThrow();
  });
  test.each([
    { 'provider-retries': '3' },
    { 'termination-reason': 'not_started' },
    { 'provider-failed-attempts': '2' },
    { 'attempt-accounting-completeness': 'unavailable' },
    { 'provider-retries': '1', 'provider-failed-attempts': '1' },
    { 'provider-failed-attempts': '3' },
    { 'provider-unknown-usage-attempts': '3' },
    { 'provider-unknown-cache-partition-attempts': '3' },
    { 'model-calls': '1', 'provider-attempts': '2' },
    { 'input-tokens': '10', 'input-cache-hit-tokens': '11' },
    { 'input-tokens': '10', 'input-cache-miss-tokens': '11' },
    { 'input-tokens': '10', 'input-cache-hit-tokens': '6', 'input-cache-miss-tokens': '5' },
    { 'usage-completeness': 'complete', 'provider-unknown-usage-attempts': '1' },
    { 'usage-completeness': 'complete', 'provider-unknown-cache-partition-attempts': '1' },
    { 'usage-completeness': 'complete', 'attempt-accounting-completeness': 'partial' },
    {
      'usage-completeness': 'complete',
      'input-tokens': '10',
      'input-cache-hit-tokens': '4',
      'input-cache-miss-tokens': '5',
    },
    { 'provider-attempts': '0', 'input-tokens': '1' },
    { 'usage-completeness': 'unavailable', 'output-tokens': '1' },
  ])('B02 refuses impossible known facts %j', (change) => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    Object.assign(o.outputs, change);
    expect(() => report([o], parse(p))).toThrow();
  });
  test.each([
    'provider-failed-attempts',
    'provider-unknown-usage-attempts',
    'provider-unknown-cache-partition-attempts',
    'input-tokens',
    'input-cache-hit-tokens',
    'input-cache-miss-tokens',
    'output-tokens',
  ])('B06 reported %s overrun remains visible when peer counters are unavailable', (key) => {
    const o = observation();
    o.outputs = {
      status: 'reviewed',
      'termination-reason': 'review_completed',
      [key]: '65537',
    } as any;
    const result = report([o]);
    expect(result.result).toBe('failed');
    expect(result.stopRequired).toBe(true);
    expect(result.cases[0].outputs[key]).toBe('65537');
  });
  test('B02 complete known zero remains zero and partial capture stays unknown', () => {
    const o = observation();
    o.workflow = 'failure';
    o.step = 'failure';
    o.outputs = Object.fromEntries(
      OUTPUTS.map((k) => [
        k,
        [
          'status',
          'termination-reason',
          'attempt-accounting-completeness',
          'usage-completeness',
        ].includes(k)
          ? null
          : '0',
      ]),
    ) as any;
    Object.assign(o.outputs, {
      status: 'authorization_failed',
      'termination-reason': 'not_started',
      'attempt-accounting-completeness': 'complete',
      'usage-completeness': 'complete',
    });
    const c = report([o]).cases[0];
    expect(c.outputs['provider-attempts']).toBe('0');
    expect(c.unknownWork).toBe(false);
    delete (o.outputs as any)['provider-attempts'];
    expect(report([o]).cases[0].outputs['provider-attempts']).toBeNull();
  });
  test('B02 optional unobserved accounting does not prevent independently proven review success', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    o.outputs = { status: 'reviewed', 'termination-reason': 'review_completed' } as any;
    const result = report([o], parse(p));
    expect(result.result).toBe('success');
    expect(result.cases[0].outputs['provider-attempts']).toBeNull();
    expect(result.billing).toBe('unknown');
  });
  test.each(['manual', 'automatic'])(
    'B03 admits only H1-authorized LF/CRLF equivalence: %s',
    (variant) => {
      const p = structuredClone(plan);
      p.target.template = variant;
      p.runs = [p.runs[0]];
      const lf = inputs.templates[variant]
        .toString()
        .replaceAll('__ACTION_SOURCE_SHA__', p.candidate.actionCommit);
      for (const text of [lf, lf.replaceAll('\n', '\r\n')]) {
        const templateBytes = Buffer.from(text);
        p.target.templateSha256 = sha256(templateBytes);
        expect(parse(p, { ...inputs, templateBytes }).contentVerified).toBe(true);
      }
      for (const text of [lf.replace('jobs:', 'jobs: '), lf.replace('\n', '\r')]) {
        const templateBytes = Buffer.from(text);
        p.target.templateSha256 = sha256(templateBytes);
        expect(() => parse(p, { ...inputs, templateBytes })).toThrow();
      }
    },
  );
  test.each(['stale-head', 'incomplete', 'failure'])(
    'B04 non-started %s is never an exercised refusal',
    (scenario) => {
      const p = structuredClone(plan);
      p.runs = [p.runs.find((r: any) => r.scenario === scenario)];
      const o = observation(scenario);
      o.workflow = 'failure';
      o.step = 'not-started';
      o.state = 'refused';
      o.acceptance = 'absent';
      o.publication = 'absent';
      o.outputs.status = (
        {
          'stale-head': 'stale_head',
          incomplete: 'snapshot_incomplete',
          failure: 'provider_failed',
        } as any
      )[scenario];
      o.outputs['termination-reason'] = 'host_failure';
      expect(report([o], parse(p)).result).toBe('inconclusive');
      o.step = 'failure';
      expect(report([o], parse(p)).result).toBe('success');
    },
  );
});

describe('round2 reported measurements and finite outcome regressions', () => {
  test('B05 unavailable dispatch accounting retains product-valid partial measured usage', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    Object.assign(o, {
      workflow: 'failure',
      step: 'failure',
      state: 'refused',
      acceptance: 'absent',
      publication: 'absent',
    });
    o.outputs = {
      status: 'provider_failed',
      'termination-reason': 'provider_failure',
      'model-calls': '1',
      'provider-attempts': '0',
      'provider-retries': '0',
      'provider-failed-attempts': '0',
      'provider-unknown-usage-attempts': '0',
      'provider-unknown-cache-partition-attempts': '0',
      'attempt-accounting-completeness': 'unavailable',
      'usage-completeness': 'partial',
      'input-tokens': '10',
      'input-cache-hit-tokens': '4',
      'input-cache-miss-tokens': '6',
      'output-tokens': '3',
    } as any;
    const result = report([o], parse(p));
    expect(result.cases[0].outputs['input-tokens']).toBe('10');
    expect(result.cases[0].unknownWork).toBe(true);
    expect(result.result).toBe('failed');
  });
  test('B02 complete zero observed sends cannot support review_completed with failed count missing', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    o.outputs = {
      status: 'reviewed',
      'termination-reason': 'review_completed',
      'model-calls': '1',
      'provider-attempts': '0',
      'attempt-accounting-completeness': 'complete',
    } as any;
    expect(() => report([o], parse(p))).toThrow();
  });
  test('B02 complete zero logical population cannot complete a review with physical count unobserved', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    o.outputs = {
      status: 'reviewed',
      'termination-reason': 'review_completed',
      'model-calls': '0',
      'attempt-accounting-completeness': 'complete',
    } as any;
    expect(() => report([o], parse(p))).toThrow();
  });
  test('B06 actual token overrun stays visible as failed stop evidence', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    Object.assign(p.invocation, {
      maxUncachedInputTokens: 8192,
      maxCachedInputTokens: 8192,
      maxTotalInputTokens: 16384,
    });
    const o = observation();
    Object.assign(o, {
      workflow: 'failure',
      step: 'failure',
      state: 'refused',
      acceptance: 'absent',
      publication: 'absent',
    });
    Object.assign(o.outputs, {
      status: 'agent_result_invalid',
      'termination-reason': 'token_limit',
      'model-calls': '1',
      'provider-attempts': '1',
      'provider-retries': '0',
      'provider-failed-attempts': '0',
      'provider-unknown-usage-attempts': '0',
      'provider-unknown-cache-partition-attempts': '0',
      'attempt-accounting-completeness': 'complete',
      'usage-completeness': 'complete',
      'input-tokens': '8193',
      'input-cache-hit-tokens': '0',
      'input-cache-miss-tokens': '8193',
      'output-tokens': '3',
    });
    const result = report([o], parse(p));
    expect(result.result).toBe('failed');
    expect(result.stopRequired).toBe(true);
    expect(result.cases[0].outputs['input-cache-miss-tokens']).toBe('8193');
    expect(result.cases[0].authorityExceeded).toContain('input-cache-miss-tokens');
  });
  test('B06 campaign call/time breaches preserve retrospective observations', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    Object.assign(p.invocation, { maxModelCalls: 1, maxProviderAttempts: 1, maxWorkUnits: 1 });
    Object.assign(p.campaign, {
      maxRuns: 1,
      maxModelCalls: 1,
      maxProviderAttempts: 1,
      wallSeconds: 120,
    });
    const o = observation();
    o.elapsedSeconds = 121;
    const result = report([o], parse(p));
    expect(result.result).toBe('failed');
    expect(result.stopRequired).toBe(true);
    expect(result.cases[0].outputs['provider-attempts']).toBe('2');
    expect(result.authorityExceeded).toEqual(['model-calls', 'provider-attempts', 'wall-seconds']);
  });
  test.each(['stale-head', 'incomplete', 'failure'])(
    'B07 contradictory successful outcome cannot certify %s',
    (scenario) => {
      const p = structuredClone(plan);
      p.runs = [p.runs.find((r: any) => r.scenario === scenario)];
      const o = observation(scenario);
      o.state = 'refused';
      o.acceptance = 'absent';
      o.publication = 'absent';
      o.outputs.status = (
        {
          'stale-head': 'stale_head',
          incomplete: 'snapshot_incomplete',
          failure: 'provider_failed',
        } as any
      )[scenario];
      o.outputs['termination-reason'] = 'host_failure';
      for (const [workflow, step] of [
        ['success', 'success'],
        ['failure', 'success'],
        ['success', 'failure'],
      ]) {
        o.workflow = workflow;
        o.step = step;
        expect(report([o], parse(p)).result).toBe('inconclusive');
      }
      o.workflow = 'failure';
      o.step = 'failure';
      expect(report([o], parse(p)).result).toBe('success');
    },
  );
  test('B07 observed completed cancellation is scoped; a lost lifetime remains inconclusive', () => {
    const p = structuredClone(plan);
    p.runs = [p.runs.find((r: any) => r.scenario === 'failure')];
    const o = observation('failure');
    Object.assign(o, {
      workflow: 'cancelled',
      step: 'cancelled',
      state: 'refused',
      acceptance: 'absent',
      publication: 'absent',
    });
    Object.assign(o.outputs, { status: 'cancelled', 'termination-reason': 'cancelled' });
    expect(report([o], parse(p)).result).toBe('success');
    o.processClosed = 'unknown';
    expect(report([o], parse(p)).result).toBe('inconclusive');
  });
});

describe('additional authorized B02 known-operand closure', () => {
  function sparse(outputs: Record<string, string>) {
    const p = structuredClone(plan);
    p.runs = [p.runs[0]];
    const o = observation();
    o.outputs = { status: 'reviewed', 'termination-reason': 'review_completed', ...outputs } as any;
    return () => report([o], parse(p));
  }
  test.each([undefined, 'partial', 'unavailable', 'complete'])(
    'known zero calls cannot complete a new review with completeness %s',
    (completeness) => {
      const outputs: Record<string, string> = { 'model-calls': '0' };
      if (completeness !== undefined) outputs['attempt-accounting-completeness'] = completeness;
      expect(sparse(outputs)).toThrow();
    },
  );
  test.each([
    ['attempt-accounting-completeness', 'partial'],
    ['attempt-accounting-completeness', 'unavailable'],
    ['usage-completeness', 'partial'],
    ['usage-completeness', 'unavailable'],
  ])('known zero calls contradicts captured %s=%s even outside review_completed', (key, value) => {
    expect(
      sparse({
        status: 'provider_failed',
        'termination-reason': 'provider_failure',
        'model-calls': '0',
        [key]: value,
      }),
    ).toThrow();
  });
  test.each([
    { 'provider-attempts': '1', 'provider-unknown-usage-attempts': '0' },
    { 'provider-attempts': '2', 'provider-unknown-usage-attempts': '1' },
    { 'provider-unknown-cache-partition-attempts': '1' },
  ])('unavailable usage rejects independently contradictory known operands %j', (outputs) => {
    expect(
      sparse({ 'model-calls': '2', 'usage-completeness': 'unavailable', ...outputs }),
    ).toThrow();
  });
  test.each([
    {},
    { 'provider-attempts': '1' },
    { 'provider-unknown-usage-attempts': '1' },
    { 'provider-attempts': '1', 'provider-unknown-usage-attempts': '1' },
    {
      'provider-attempts': '1',
      'provider-unknown-usage-attempts': '1',
      'provider-unknown-cache-partition-attempts': '0',
      'attempt-accounting-completeness': 'complete',
    },
  ])(
    'unavailable usage preserves consistent known facts and independent missing peers %j',
    (outputs) => {
      const c = sparse({ 'model-calls': '1', 'usage-completeness': 'unavailable', ...outputs })()
        .cases[0];
      expect(c.result).toBe('success');
      expect(c.outputs['input-tokens']).toBeNull();
      expect(c.outputs['provider-attempts']).toBe(outputs['provider-attempts'] ?? null);
      expect(c.outputs['provider-unknown-usage-attempts']).toBe(
        outputs['provider-unknown-usage-attempts'] ?? null,
      );
      expect(c.billing).toBe('unknown');
    },
  );
  test.each([
    {},
    { 'attempt-accounting-completeness': 'complete', 'usage-completeness': 'complete' },
  ])('zero-call recovery keeps missing observations unknown %j', (outputs) => {
    const c = sparse({ 'termination-reason': 'not_started', 'model-calls': '0', ...outputs })()
      .cases[0];
    expect(c.outputs['model-calls']).toBe('0');
    expect(c.outputs['provider-attempts']).toBeNull();
    expect(c.outputs['input-tokens']).toBeNull();
  });
});

describe('B08 historical observation preserves original input validation', () => {
  test.each([
    (p: any) => (p.authority.validUntil = 'invalid'),
    (p: any) => delete p.authority.validUntil,
    (p: any) => (p.authority.cleanup = 'delete-everything'),
    (p: any) => (p.campaign.maxModelCalls = 0),
    (p: any) => (p.candidate.pairSha256 = 'f'.repeat(64)),
    (p: any) => (p.content.rawResponse = canary),
  ])('expired observation refuses invalid original content %#', (mutate) => {
    const p = structuredClone(plan);
    mutate(p);
    expect(() =>
      parsePlan(bytes(p), sha256(bytes(p)), inputs, now + 30 * 86400000, 'observation'),
    ).toThrow();
  });
  test('historical reporting still binds the independent original digest', () => {
    expect(() =>
      parsePlan(bytes(plan), 'f'.repeat(64), inputs, now + 30 * 86400000, 'observation'),
    ).toThrow();
    expect(() => parsePlan(bytes(plan), sha256(bytes(plan)), inputs, now, 'execute')).toThrow();
  });
});
