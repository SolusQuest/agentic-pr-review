import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

import {
  H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES,
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
      expect(typeof parsed.repository_id).toBe('string');
      expect(typeof parsed.run_id).toBe('string');
      expect(typeof parsed.run_attempt).toBe('string');
      expect(typeof parsed.inputs.pr_number === 'string' || parsed.inputs.pr_number === null).toBe(
        true,
      );
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
    expect(parseLaunchDocument(exact).repository_id).toBe('9223372036854775807');
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

  it('serializes compact deterministic JSON with explicit optional nulls', async () => {
    const document = parseLaunchDocument(
      await readFile(path.join(fixtures, 'launch-js-safe-boundary.json')),
    );
    const serialized = serializeLaunchDocument(document);
    expect(serialized.includes(Buffer.from('\n'))).toBe(false);
    expect(JSON.parse(serialized.toString('utf8')).inputs).toMatchObject({
      github_token: null,
      previous_state_key: null,
      config_path: null,
    });
  });
});
