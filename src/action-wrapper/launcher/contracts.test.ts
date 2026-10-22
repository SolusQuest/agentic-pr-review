import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

import {
  H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES,
  buildLaunchDocument,
  parseLaunchDocument,
  parseProductionWorkflowRef,
  serializeLaunchDocument,
  type ActionHostLaunchDocument,
} from './contracts.js';

const fixtures = path.resolve(
  'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures',
);

describe('W1 H1 launch contract', () => {
  it.each(['launch-js-safe-boundary.json', 'launch-js-unsafe-odd.json', 'launch-int64-max.json'])(
    'consumes the checked-in H1 fixture %s without numeric coercion',
    async (name) => {
      const bytes = await readFile(path.join(fixtures, name));
      const parsed = parseLaunchDocument(bytes);
      expect(typeof parsed.facts.repository_id).toBe('string');
      expect(typeof parsed.facts.run_id).toBe('string');
      expect(typeof parsed.facts.run_attempt).toBe('string');
      expect(
        typeof parsed.inputs.find((entry) => entry.name === 'pr-number')?.value === 'string',
      ).toBe(true);
    },
  );

  it('accepts legal JSON whitespace through the exact H1 launch cap', async () => {
    const fixture = JSON.parse(
      await readFile(path.join(fixtures, 'launch-int64-max.json'), 'utf8'),
    ) as ActionHostLaunchDocument;
    const compact = Buffer.from(JSON.stringify(fixture));
    const exact = Buffer.concat([
      compact,
      Buffer.alloc(H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES - compact.length, 0x20),
    ]);
    expect(parseLaunchDocument(exact).facts.repository_id).toBe('9223372036854775807');
    expect(() => parseLaunchDocument(Buffer.concat([exact, Buffer.of(0x20)]))).toThrow(
      'wrapper_document_invalid',
    );
  });

  it('rejects escaped duplicate keys, unknown keys, wrong casing, and trailing JSON values', async () => {
    const source = JSON.stringify(
      JSON.parse(await readFile(path.join(fixtures, 'launch-js-safe-boundary.json'), 'utf8')),
    );
    expect(() =>
      parseLaunchDocument(Buffer.from(source.replace('{', '{"\\u0069nputs":null,'))),
    ).toThrow('wrapper_document_invalid');
    expect(() =>
      parseLaunchDocument(Buffer.from(source.replace(/}$/, ',"private":true}'))),
    ).toThrow('wrapper_launch_invalid');
    expect(() => parseLaunchDocument(Buffer.from(source.replace('"run_id"', '"RunId"')))).toThrow(
      'wrapper_launch_invalid',
    );
    expect(() => parseLaunchDocument(Buffer.from(`${source} {}`))).toThrow(
      'wrapper_document_invalid',
    );
  });

  it('forwards bounded product-invalid strings, names and counts through builder and codec', async () => {
    const base = parseLaunchDocument(
      await readFile(path.join(fixtures, 'launch-js-safe-boundary.json')),
    );
    const cases = [
      [{ name: 'pr-number', value: '01' }],
      [{ name: 'pr-number', value: '9223372036854775808' }],
      [{ name: 'state-mode', value: 'other' }],
      [{ name: 'config-path', value: 'bad\u0000path' }],
      [{ name: 'state-key', value: 'x'.repeat(4097) }],
      ['github-token', 'provider-api-key', 'state-key', 'previous-state-key'].map((name) => ({
        name,
        value: 'x'.repeat(4096),
      })),
      [{ name: 'unknown', value: '' }],
      [
        { name: 'state-mode', value: '' },
        { name: 'state-mode', value: 'auto' },
      ],
      Array.from({ length: 8 }, () => ({ name: 'state-mode', value: '' })),
    ];
    for (const inputs of cases) {
      const f = base.facts;
      const launch = buildLaunchDocument({
        inputs,
        runtimeFacts: {
          eventJsonPath: f.event_json_path,
          repositoryName: f.repository_name,
          repositoryId: f.repository_id,
          runId: f.run_id,
          runAttempt: f.run_attempt,
          workflowPath: f.workflow_path,
          workflowRef: f.workflow_ref,
          workflowSha: f.workflow_sha,
        },
        eventJsonSha256: f.event_json_sha256,
        prepared: {
          actionSourceSha: f.action_source_sha,
          payloadSha256: f.payload_sha256,
          buildDiscriminator: base.build_discriminator,
        },
        artifactBridgeEndpoint: base.upload_endpoint,
        cancellation: base.cancellation,
      });
      expect(parseLaunchDocument(serializeLaunchDocument(launch)).inputs).toEqual(inputs);
    }
  });

  it('retains raw wire shape, Unicode and whole-frame allocation guards', async () => {
    const base = parseLaunchDocument(
      await readFile(path.join(fixtures, 'launch-js-safe-boundary.json')),
    );
    for (const inputs of [
      null,
      {},
      [null],
      [{ name: 'state-key', value: null }],
      [{ name: 'state-key', value: 1 }],
      [{ name: 'state-key' }],
      [{ name: 'state-key', value: '', extra: true }],
    ]) {
      expect(() => parseLaunchDocument(Buffer.from(JSON.stringify({ ...base, inputs })))).toThrow(
        'wrapper_launch_invalid',
      );
    }
    expect(() =>
      serializeLaunchDocument({ ...base, inputs: [{ name: 'state-key', value: '\ud800' }] }),
    ).toThrow('wrapper_string_invalid');
    expect(() =>
      serializeLaunchDocument({
        ...base,
        inputs: [{ name: 'state-key', value: 'x'.repeat(H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES) }],
      }),
    ).toThrow('wrapper_launch_invalid');
    expect(() =>
      parseLaunchDocument(Buffer.from(JSON.stringify({ ...base, protocol: 'other' }))),
    ).toThrow('wrapper_launch_invalid');
  });

  it('extracts only workflow_path while preserving the complete H2 workflow_ref', () => {
    const full =
      'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main';
    expect(parseProductionWorkflowRef('SolusQuest/agentic-pr-review', full)).toEqual({
      workflowPath: '.github/workflows/r4-trusted-proof.yml',
      workflowRef: full,
    });
    expect(() => parseProductionWorkflowRef('other/repo', full)).toThrow(
      'wrapper_runtime_facts_invalid',
    );
    expect(() =>
      parseProductionWorkflowRef('SolusQuest/agentic-pr-review', `${full}@heads/other`),
    ).toThrow('wrapper_runtime_facts_invalid');
  });

  it('serializes compact deterministic JSON with exact raw strings', async () => {
    const document = parseLaunchDocument(
      await readFile(path.join(fixtures, 'launch-js-safe-boundary.json')),
    );
    const serialized = serializeLaunchDocument(document);
    expect(serialized.includes(Buffer.from('\n'))).toBe(false);
    expect(JSON.parse(serialized.toString('utf8')).inputs).toContainEqual({
      name: 'github-token',
      value: '',
    });
  });
});
