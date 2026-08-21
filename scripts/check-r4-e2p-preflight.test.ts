import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  authorizationDigest,
  extractPreflight,
  runExtractedPreflight,
} from './check-r4-e2p-preflight.mjs';

const templatePath = path.join(
  process.cwd(),
  'runtime',
  'tests',
  'fixtures',
  'action-host',
  'trusted-proof-payload',
  'workflow',
  'r4-trusted-proof.yml.template',
);
const source = extractPreflight(fs.readFileSync(templatePath));
const operationId = '1'.repeat(64);
const fixtureHeadSha = 'e'.repeat(40);
const values = {
  repositoryId: '42',
  repository: 'SolusQuest/agentic-pr-review',
  prNumber: '147',
  fixtureHeadSha,
  operationId,
  workflowSha: 'a'.repeat(40),
  actionSourceSha: 'b'.repeat(40),
  payloadSha256: 'f'.repeat(64),
};
const environment = {
  EVENT_NAME: 'workflow_run',
  EVENT_PR_NUMBER: values.prNumber,
  EVENT_HEAD_SHA: values.fixtureHeadSha,
  INPUT_PR_NUMBER: '',
  REPOSITORY: values.repository,
  REPOSITORY_ID: values.repositoryId,
  WORKFLOW_SHA: values.workflowSha,
  ACTION_SOURCE_SHA: values.actionSourceSha,
  PAYLOAD_SHA256: values.payloadSha256,
  R4_TRUSTED_PROOF_AUTHORIZATION: authorizationDigest(values),
};

function pull(overrides: Record<string, unknown> = {}) {
  return {
    number: 147,
    state: 'open',
    draft: false,
    base: { repo: { id: 42, full_name: values.repository } },
    head: {
      repo: { id: 42, full_name: values.repository },
      sha: fixtureHeadSha,
      ref: `r4-trusted-proof/${operationId}`,
    },
    ...overrides,
  };
}

function response(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('R4 E2P exact inline preflight', () => {
  it('performs one exact unauthenticated public PR read and emits one atomic admission', async () => {
    const fetchImpl = vi.fn(async () => response(pull()));

    const result = await runExtractedPreflight({
      source,
      environment,
      fetchImpl,
    });

    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const [url, init] = fetchImpl.mock.calls[0];
    expect(url).toBe('https://api.github.com/repos/SolusQuest/agentic-pr-review/pulls/147');
    expect(init).toMatchObject({
      method: 'GET',
      redirect: 'error',
      headers: {
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
      },
    });
    expect(JSON.stringify(init)).not.toMatch(/authorization|token|secret/iu);
    expect(result.stderr).toBe('');
    expect(result.stdout).toBe(
      `authorized=true\npr-number=147\nfixture-head-sha=${fixtureHeadSha}\noperation-id=${operationId}\nauthorization-manifest-digest=${environment.R4_TRUSTED_PROOF_AUTHORIZATION}\n`,
    );
  });

  it('uses the dispatch input while retaining the same public resolver', async () => {
    const fetchImpl = vi.fn(async () => response(pull()));
    const dispatch = {
      ...environment,
      EVENT_NAME: 'workflow_dispatch',
      EVENT_PR_NUMBER: '',
      EVENT_HEAD_SHA: '',
      INPUT_PR_NUMBER: '147',
    };

    const result = await runExtractedPreflight({
      source,
      environment: dispatch,
      fetchImpl,
    });

    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(result.stdout).toContain('authorized=true\n');
  });

  const rejections: Array<
    [string, (fetchImpl: ReturnType<typeof vi.fn>) => Record<string, string>]
  > = [
    ['authorization mismatch', () => ({ R4_TRUSTED_PROOF_AUTHORIZATION: '0'.repeat(64) })],
    ['workflow head mismatch', () => ({ EVENT_HEAD_SHA: '0'.repeat(40) })],
    ['fork', () => ({})],
    ['draft', () => ({})],
    ['closed', () => ({})],
    ['renamed branch', () => ({})],
    ['missing branch', () => ({})],
    ['rate limit', () => ({})],
    ['not found', () => ({})],
    ['transport failure', () => ({})],
  ];

  it.each(rejections)('fails closed for %s with no partial positive output', async (name) => {
    const fetchImpl = vi.fn(async () => {
      if (name === 'transport failure') throw new Error('network detail');
      if (name === 'rate limit') return response({}, 429);
      if (name === 'not found') return response({}, 404);
      if (name === 'fork') {
        return response(
          pull({ head: { ...pull().head, repo: { id: 43, full_name: 'fork/repo' } } }),
        );
      }
      if (name === 'draft') return response(pull({ draft: true }));
      if (name === 'closed') return response(pull({ state: 'closed' }));
      if (name === 'renamed branch') {
        return response(pull({ head: { ...pull().head, ref: 'renamed' } }));
      }
      if (name === 'missing branch') {
        return response(pull({ head: { ...pull().head, ref: null } }));
      }
      return response(pull());
    });
    const overrides = rejections.find(([caseName]) => caseName === name)![1](fetchImpl);
    const result = await runExtractedPreflight({
      source,
      environment: { ...environment, ...overrides },
      fetchImpl,
    });

    expect(result.stdout).toBe(
      'authorized=false\npr-number=\nfixture-head-sha=\noperation-id=\nauthorization-manifest-digest=\n',
    );
    expect(result.stdout).not.toContain('authorized=true');
    expect(result.stderr).toMatch(/^APR_R4_E2P_PREFLIGHT_REJECTED /u);
  });

  it('rejects malformed and oversized response bodies', async () => {
    for (const body of ['{invalid', 'x'.repeat(65_537)]) {
      const result = await runExtractedPreflight({
        source,
        environment,
        fetchImpl: vi.fn(async () => new Response(body, { status: 200 })),
      });
      expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
    }
  });

  it('rejects duplicate identity keys before JSON parsing can choose a winner', async () => {
    const body = JSON.stringify(pull()).replace(
      `"sha":"${fixtureHeadSha}"`,
      `"sha":"${'0'.repeat(40)}","sha":"${fixtureHeadSha}"`,
    );
    const result = await runExtractedPreflight({
      source,
      environment,
      fetchImpl: vi.fn(async () => new Response(body, { status: 200 })),
    });

    expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
    expect(result.stderr).toContain('github-response-duplicate-key');
  });
});
