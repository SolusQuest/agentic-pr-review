import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  authorizationDigest,
  authorizationDigestV2,
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
  EVENT_ACTION: 'completed',
  EVENT_CONCLUSION: 'success',
  EVENT_PR_NUMBER: values.prNumber,
  EVENT_HEAD_SHA: values.fixtureHeadSha,
  EVENT_PULL_REQUESTS_JSON: JSON.stringify([{ number: 147, head: { sha: values.fixtureHeadSha } }]),
  EVENT_WORKFLOW_ID: '294554742',
  EVENT_WORKFLOW_NAME: 'CI',
  EVENT_WORKFLOW_PATH: '.github/workflows/ci.yml',
  EVENT_TRIGGER_EVENT: 'pull_request',
  EVENT_REPOSITORY: values.repository,
  EVENT_REPOSITORY_ID: values.repositoryId,
  EVENT_HEAD_REPOSITORY: values.repository,
  EVENT_HEAD_REPOSITORY_ID: values.repositoryId,
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

  it.each([
    ['unsupported action', { EVENT_ACTION: 'requested' }],
    ['failed conclusion', { EVENT_CONCLUSION: 'failure' }],
    ['cancelled conclusion', { EVENT_CONCLUSION: 'cancelled' }],
    ['neutral conclusion', { EVENT_CONCLUSION: 'neutral' }],
    ['skipped conclusion', { EVENT_CONCLUSION: 'skipped' }],
    ['wrong workflow id', { EVENT_WORKFLOW_ID: '294554743' }],
    ['wrong workflow name', { EVENT_WORKFLOW_NAME: 'Other' }],
    ['wrong workflow path', { EVENT_WORKFLOW_PATH: '.github/workflows/other.yml' }],
    ['wrong trigger event', { EVENT_TRIGGER_EVENT: 'push' }],
    ['wrong event repository', { EVENT_REPOSITORY: 'other/repository' }],
    ['wrong event repository id', { EVENT_REPOSITORY_ID: '43' }],
    ['fork event head repository', { EVENT_HEAD_REPOSITORY: 'fork/repository' }],
    ['fork event head repository id', { EVENT_HEAD_REPOSITORY_ID: '43' }],
    ['zero associated PRs', { EVENT_PULL_REQUESTS_JSON: '[]' }],
    [
      'multiple associated PRs',
      {
        EVENT_PULL_REQUESTS_JSON: JSON.stringify([
          { number: 147, head: { sha: fixtureHeadSha } },
          { number: 148, head: { sha: fixtureHeadSha } },
        ]),
      },
    ],
    [
      'associated PR number mismatch',
      {
        EVENT_PULL_REQUESTS_JSON: JSON.stringify([{ number: 148, head: { sha: fixtureHeadSha } }]),
      },
    ],
    [
      'associated PR head mismatch',
      {
        EVENT_PULL_REQUESTS_JSON: JSON.stringify([{ number: 147, head: { sha: '0'.repeat(40) } }]),
      },
    ],
  ])('rejects workflow_run route fact: %s', async (_name, overrides) => {
    const result = await runExtractedPreflight({
      source,
      environment: { ...environment, ...overrides },
      fetchImpl: vi.fn(async () => response(pull())),
    });

    expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
    expect(result.stderr).toContain('workflow-run-route-facts');
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

describe('R4 E2P v2 exact inline preflight', () => {
  const v2TemplatePath = path.join(
    process.cwd(),
    'runtime',
    'tests',
    'fixtures',
    'action-host',
    'trusted-proof-payload',
    'workflow',
    'r4-trusted-proof-v2.yml.template',
  );
  const v2Source = extractPreflight(fs.readFileSync(v2TemplatePath));
  const v2Values = {
    ...values,
    payloadSourceSha: '1'.repeat(40),
  };
  const v2Environment = {
    ...environment,
    PAYLOAD_SOURCE_SHA: v2Values.payloadSourceSha,
    R4_TRUSTED_PROOF_AUTHORIZATION: authorizationDigestV2(v2Values),
  };
  const v2Pull = (overrides: Record<string, unknown> = {}) => ({
    number: 147,
    state: 'open',
    draft: false,
    merged_at: null,
    base: {
      ref: 'main',
      sha: v2Values.workflowSha,
      repo: { id: 42, full_name: v2Values.repository },
    },
    head: {
      repo: { id: 42, full_name: v2Values.repository },
      sha: fixtureHeadSha,
      ref: `r4-trusted-proof/${operationId}`,
    },
    ...overrides,
  });

  it.each(['workflow_run', 'workflow_dispatch'])(
    'executes the exact %s v2 route',
    async (route) => {
      const fetchImpl = vi.fn(async () => response(v2Pull()));
      const routeEnvironment =
        route === 'workflow_dispatch'
          ? {
              ...v2Environment,
              EVENT_NAME: route,
              EVENT_PR_NUMBER: '',
              EVENT_HEAD_SHA: '',
              INPUT_PR_NUMBER: '147',
            }
          : v2Environment;

      const result = await runExtractedPreflight({
        source: v2Source,
        environment: routeEnvironment,
        fetchImpl,
      });

      expect(result.stderr).toBe('');
      expect(result.stdout).toContain('authorized=true\n');
      expect(fetchImpl).toHaveBeenCalledTimes(1);
      expect(fetchImpl.mock.calls[0][1]).toMatchObject({
        method: 'GET',
        redirect: 'error',
      });
      expect(JSON.stringify(fetchImpl.mock.calls[0][1])).not.toMatch(
        /authorization|token|secret/iu,
      );
    },
  );

  it.each([
    ['merged pull request', {}, { merged_at: '2026-08-24T00:00:00Z' }],
    ['wrong base ref', {}, { base: { ...v2Pull().base, ref: 'release' } }],
    ['missing base ref', {}, { base: { ...v2Pull().base, ref: null } }],
    ['wrong base sha', {}, { base: { ...v2Pull().base, sha: '0'.repeat(40) } }],
    ['missing base sha', {}, { base: { ...v2Pull().base, sha: null } }],
    [
      'wrong base repository',
      {},
      { base: { ...v2Pull().base, repo: { id: 43, full_name: 'fork/repo' } } },
    ],
    [
      'wrong head repository',
      {},
      { head: { ...v2Pull().head, repo: { id: 43, full_name: 'fork/repo' } } },
    ],
    ['wrong head sha', { EVENT_HEAD_SHA: '0'.repeat(40) }, {}],
    ['wrong branch', {}, { head: { ...v2Pull().head, ref: 'renamed' } }],
    ['draft', {}, { draft: true }],
    ['closed', {}, { state: 'closed' }],
    ['wrong payload source', { PAYLOAD_SOURCE_SHA: '2'.repeat(40) }, {}],
    ['missing payload source', { PAYLOAD_SOURCE_SHA: '' }, {}],
    ['wrong workflow sha', { WORKFLOW_SHA: '2'.repeat(40) }, {}],
    ['wrong action source', { ACTION_SOURCE_SHA: '2'.repeat(40) }, {}],
    ['wrong payload digest', { PAYLOAD_SHA256: '2'.repeat(64) }, {}],
    ['wrong repository', { REPOSITORY: 'fork/repo' }, {}],
    ['wrong repository id', { REPOSITORY_ID: '43' }, {}],
  ])('rejects v2 fact drift: %s', async (_name, environmentOverrides, pullOverrides) => {
    const result = await runExtractedPreflight({
      source: v2Source,
      environment: { ...v2Environment, ...environmentOverrides },
      fetchImpl: vi.fn(async () => response(v2Pull(pullOverrides))),
    });

    expect(result.stdout).toBe(
      'authorized=false\npr-number=\nfixture-head-sha=\noperation-id=\nauthorization-manifest-digest=\n',
    );
    expect(result.stdout).not.toContain('authorized=true');
    expect(result.stderr).toMatch(/^APR_R4_E2P_PREFLIGHT_REJECTED /u);
  });

  it.each([
    ['malformed', new Response('{invalid', { status: 200 })],
    ['oversized', new Response('x'.repeat(65_537), { status: 200 })],
    ['redirect', new Response('{}', { status: 302 })],
  ])('rejects %s v2 responses atomically', async (_name, invalidResponse) => {
    const result = await runExtractedPreflight({
      source: v2Source,
      environment: v2Environment,
      fetchImpl: vi.fn(async () => invalidResponse.clone()),
    });
    expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
    expect(result.stdout).not.toContain('authorized=true');
  });

  it('rejects duplicate v2 base identity keys', async () => {
    const body = JSON.stringify(v2Pull()).replace(
      `"sha":"${v2Values.workflowSha}"`,
      `"sha":"${'0'.repeat(40)}","sha":"${v2Values.workflowSha}"`,
    );
    const result = await runExtractedPreflight({
      source: v2Source,
      environment: v2Environment,
      fetchImpl: vi.fn(async () => new Response(body, { status: 200 })),
    });
    expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
    expect(result.stderr).toContain('github-response-duplicate-key');
  });

  it('fails closed on v2 transport or timeout failures', async () => {
    for (const error of [new Error('transport'), new DOMException('timeout', 'AbortError')]) {
      const result = await runExtractedPreflight({
        source: v2Source,
        environment: v2Environment,
        fetchImpl: vi.fn(async () => {
          throw error;
        }),
      });
      expect(result.stdout.startsWith('authorized=false\n')).toBe(true);
      expect(result.stdout).not.toContain('authorized=true');
    }
  });
});
