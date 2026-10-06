import { createServer } from 'node:http';
import { afterEach, describe, expect, test } from 'vitest';
import { CAPS, decimal, githubReader, verifyStorage } from '../../scripts/release/candidate.mjs';
import { NOW, artifactFixture, producerFixture, runFixture, storageFixture } from './fixtures.mjs';

const closers: Array<() => Promise<void>> = [];
afterEach(async () => {
  for (const close of closers.splice(0)) await close();
});
async function api({
  run = runFixture(),
  artifact = artifactFixture(),
  artifacts = [artifact],
  total = artifacts.length,
  change = (_path: string, value: unknown) => value,
  status = 200,
}: any = {}) {
  const requests: Array<{
    path: string;
    method?: string;
    authorization?: string;
    userAgent?: string;
  }> = [];
  const server = createServer((request, response) => {
    const path = request.url || '';
    requests.push({
      path,
      method: request.method,
      authorization: request.headers.authorization,
      userAgent: request.headers['user-agent'],
    });
    const page = Number(new URL('http://fixture' + path).searchParams.get('page') || 1);
    const value = path.includes('/attempts/')
      ? run
      : path.includes('/runs/')
        ? {
            total_count: total,
            artifacts: artifacts.slice((page - 1) * CAPS.page, page * CAPS.page),
          }
        : artifact;
    response.writeHead(status, { 'content-type': 'application/json' });
    response.end(JSON.stringify(change(path, structuredClone(value))));
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = (server.address() as any).port;
  closers.push(
    () =>
      new Promise<void>((resolve) => {
        server.close(() => resolve());
        server.closeAllConnections();
      }),
  );
  const get = githubReader('SYNTHETIC_READ_TOKEN', (url: any, options: any) => {
    const parsed = new URL(url);
    expect(parsed.origin).toBe('https://api.github.com');
    return fetch(`http://127.0.0.1:${port}${parsed.pathname}${parsed.search}`, options);
  });
  return { get, requests };
}
const verify = (
  get: any,
  producer = producerFixture(),
  storage = storageFixture(),
  extra: any = {},
) => verifyStorage(producer, storage, get, { now: NOW, ...extra });

describe('exact candidate storage through a credential-free local fake API', () => {
  test('reads exact historical attempt and artifact; only GETs and no latest lookup', async () => {
    const { get, requests } = await api();
    expect(await verify(get)).toEqual(storageFixture());
    expect(requests.map((x) => x.method)).toEqual(['GET', 'GET', 'GET']);
    expect(requests[0].path).toContain('/runs/101/attempts/2');
    expect(requests[1].path).toContain('/artifacts/201');
    expect(requests.every((x) => x.authorization === 'Bearer SYNTHETIC_READ_TOKEN')).toBe(true);
    expect(requests.every((x) => x.userAgent === 'agentic-pr-review-candidate/1')).toBe(true);
  });
  test('retains JS-unsafe decimal string identities without rounding', async () => {
    const p = { ...producerFixture(), runId: '9007199254740993' };
    const s = { ...storageFixture(p), artifactId: '9007199254740995' };
    const { get } = await api({ run: runFixture(p), artifact: artifactFixture(s, p) });
    expect(await verify(get, p, s)).toEqual(s);
    expect(() => decimal(9007199254740993)).toThrow('unsafe_remote_id');
  });
  test.each(['0', '01', '-1', '1e3', '18446744073709551616', '1\n'])(
    'rejects noncanonical ID %j',
    (id) => expect(() => decimal(id)).toThrow(),
  );
  test.each(['head_sha', 'head_branch', 'path', 'event', 'run_attempt', 'id'])(
    'stops for wrong producer %s',
    async (field) => {
      const run: any = runFixture();
      run[field] = field === 'run_attempt' || field === 'id' ? '9' : 'wrong';
      const { get } = await api({ run });
      await expect(verify(get)).rejects.toThrow('candidate_run_identity_drift');
    },
  );
  test.each(['repository', 'head_repository'])('refuses wrong %s identity', async (field) => {
    const run: any = runFixture();
    run[field].id = '9';
    const { get } = await api({ run });
    await expect(verify(get)).rejects.toThrow('candidate_run_identity_drift');
  });
  test.each(['failure', 'cancelled', 'skipped', null])(
    'requires terminal successful run, conclusion %j',
    async (conclusion) => {
      const { get } = await api({ run: { ...runFixture(), conclusion } });
      await expect(verify(get)).rejects.toThrow('candidate_run_incomplete_stop');
    },
  );
  test('in-progress exception is only for the exact preparing producer, never handoff', async () => {
    const { get } = await api({
      run: { ...runFixture(), status: 'in_progress', conclusion: null },
    });
    await expect(verify(get)).rejects.toThrow('candidate_run_incomplete_stop');
    expect(await verify(get, undefined, undefined, { currentProducer: producerFixture() })).toEqual(
      storageFixture(),
    );
    await expect(
      verify(get, undefined, undefined, {
        currentProducer: { ...producerFixture(), runAttempt: '3' },
      }),
    ).rejects.toThrow('candidate_run_incomplete_stop');
  });
  test.each([404, 410])('missing/gone storage %s requires reprepare', async (status) => {
    const { get } = await api({ status });
    await expect(verify(get)).rejects.toThrow('candidate_storage_missing_reprepare');
  });
  test.each([true, null])('expired or unknown expiry flag %j refuses handoff', async (expired) => {
    const { get } = await api({ artifact: { ...artifactFixture(), expired } });
    await expect(verify(get)).rejects.toThrow('candidate_storage_expired_reprepare');
  });
  test.each(['2026-10-05T10:00:00Z', '2026-10-05T09:59:59Z', '2026-02-30T00:00:00Z', null])(
    'expired/malformed expiry %j is never replaced',
    async (expires_at) => {
      const { get } = await api({ artifact: { ...artifactFixture(), expires_at } });
      await expect(verify(get)).rejects.toThrow();
    },
  );
  test.each(['d'.repeat(64), 'sha512:' + 'd'.repeat(64), 'sha256:' + 'e'.repeat(64), null])(
    'distinguishes storage digest representation/drift %j',
    async (digest) => {
      const { get } = await api({ artifact: { ...artifactFixture(), digest } });
      await expect(verify(get)).rejects.toThrow('candidate_storage_identity_drift');
    },
  );
  test.each(['id', 'repository_id', 'head_repository_id', 'head_sha', 'head_branch'])(
    'rejects artifact workflow %s substitution',
    async (field) => {
      const a: any = artifactFixture();
      a.workflow_run[field] = '9';
      const { get } = await api({ artifact: a });
      await expect(verify(get)).rejects.toThrow('candidate_storage_identity_drift');
    },
  );
  test('rejects a prior-attempt artifact even when run/W agree', async () => {
    const oldStorage = { ...storageFixture(), createdAt: '2026-10-05T09:49:59Z' };
    const { get } = await api({
      artifact: artifactFixture(oldStorage),
    });
    await expect(verify(get, producerFixture(), oldStorage)).rejects.toThrow(
      'candidate_storage_attempt_drift',
    );
  });
  test('does not choose an identical-name alternate artifact', async () => {
    const original = artifactFixture();
    const { get } = await api({ artifacts: [original, { ...original, id: '203' }] });
    await expect(verify(get)).rejects.toThrow('candidate_storage_listing_ambiguous_stop');
  });
  test('detects listing readback drift from the exact artifact GET', async () => {
    const { get } = await api({
      artifacts: [{ ...artifactFixture(), digest: 'sha256:' + 'e'.repeat(64) }],
    });
    await expect(verify(get)).rejects.toThrow('candidate_storage_listing_ambiguous_stop');
  });
  test('fully paginates unrelated artifacts before accepting unique exact identity', async () => {
    const artifacts = Array.from({ length: 100 }, (_, i) => ({
      id: String(1000 + i),
      name: 'unrelated',
    }));
    artifacts.push(artifactFixture() as any);
    const { get, requests } = await api({ artifacts });
    expect(await verify(get)).toEqual(storageFixture());
    expect(requests.at(-1)?.path).toContain('page=2');
  });
  test.each([501, 2])('explicitly refuses listing truncation/over-cap total %s', async (total) => {
    const { get } = await api({ total });
    await expect(verify(get)).rejects.toThrow('candidate_storage_listing_incomplete_stop');
  });
  test('refuses duplicate IDs across pagination rather than hiding repeats', async () => {
    const artifacts = Array.from({ length: 100 }, (_, i) => ({
      id: String(1000 + i),
      name: 'unrelated',
    }));
    artifacts.push(artifacts[0]);
    const { get } = await api({ artifacts });
    await expect(verify(get)).rejects.toThrow('candidate_storage_listing_ambiguous_stop');
  });
  test('unsafe numeric REST IDs fail closed instead of matching rounded inputs', async () => {
    const { get } = await api({ run: { ...runFixture(), id: 9007199254740993 } });
    await expect(verify(get)).rejects.toThrow('unsafe_remote_id');
  });
  test('fixed production origin rejects redirects and does not print response secrets', async () => {
    const get = githubReader('TOKEN_CANARY', async (_url, options: any) => {
      expect(options.redirect).toBe('error');
      expect(options.method).toBe('GET');
      return new Response('RESPONSE_CANARY', {
        status: 302,
        headers: { location: 'https://evil.invalid' },
      });
    });
    await expect(verify(get)).rejects.toThrow('candidate_storage_unknown_stop');
  });
  test('response bytes are capped even without Content-Length', async () => {
    for (const headers of [{ 'content-length': String(CAPS.api + 1) }, {}]) {
      const get = githubReader(
        null,
        async () => new Response('x'.repeat(CAPS.api + 1), { headers }),
      );
      await expect(verify(get)).rejects.toThrow('api_response_too_large');
    }
  });
  test('metadata artifact gets its own exact expiry/digest/name checks', async () => {
    const p = producerFixture(),
      s = storageFixture(p, 'metadata');
    const { get } = await api({ artifact: artifactFixture(s, p) });
    expect(await verify(get, p, s, { role: 'metadata' })).toEqual(s);
  });
});
