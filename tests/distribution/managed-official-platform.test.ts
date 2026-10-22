import { EventEmitter } from 'node:events';
import { createOperationTrace } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/operation-trace.mjs';
import { Readable } from 'node:stream';
import { createHash } from 'node:crypto';
import { describe, expect, test } from 'vitest';
import {
  createOfficialPlatform,
  archiveDownloadHost,
  githubKey,
  repository,
  runtimeToken,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/official-platform.mjs';

const prefix = `/repos/${repository}/actions/`;
function fixture(trace?: ReturnType<typeof createOperationTrace>) {
  const platform = createOfficialPlatform(() => 'http://127.0.0.1:8123', trace);
  async function request(
    route: string,
    method = 'GET',
    authorization?: string,
    payload?: unknown,
    httpsHost?: string,
  ) {
    const bytes = Buffer.isBuffer(payload) ? payload : Buffer.from(JSON.stringify(payload ?? {}));
    const incoming = Object.assign(Readable.from([bytes]), {
      url: route,
      method,
      headers: {
        ...(authorization ? { authorization } : {}),
        ...(httpsHost ? { host: httpsHost } : {}),
      },
    });
    const response = Object.assign(new EventEmitter(), {
      status: 0,
      statusCode: 0,
      headers: {} as Record<string, string | number>,
      bytes: Buffer.alloc(0),
      writeHead(status: number, headers = {}) {
        this.status = status;
        this.statusCode = status;
        this.headers = headers;
        return this;
      },
      end(value?: Buffer | string) {
        this.bytes = Buffer.from(value ?? '');
        this.emit('finish');
        return this;
      },
    });
    expect(
      await (httpsHost
        ? platform.handleHttps(incoming, response)
        : platform.handle(incoming, response)),
    ).toBe(true);
    return response;
  }
  async function upload(name: string, bytes: Buffer, run: number, attempt: number) {
    const token = platform.configure(run, attempt);
    const context = {
      workflow_run_backend_id: `d5-run-${run}`,
      workflow_job_run_backend_id: `d5-job-${run}-${attempt}`,
    };
    const created = await request(
      '/twirp/ArtifactService/CreateArtifact',
      'POST',
      'Bearer ' + token,
      {
        ...context,
        name,
        expires_at: '2030-01-01T00:00:00Z',
      },
    );
    const url = new URL(JSON.parse(created.bytes.toString()).signedUploadUrl);
    const route = url.pathname + url.search;
    const id = 'YmxvY2stMQ==';
    await request(route + '&comp=block&blockid=' + encodeURIComponent(id), 'PUT', undefined, bytes);
    await request(
      route + '&comp=blocklist',
      'PUT',
      undefined,
      Buffer.from(`<BlockList><Latest>${id}</Latest></BlockList>`),
    );
    const finalized = await request(
      '/twirp/ArtifactService/FinalizeArtifact',
      'POST',
      'Bearer ' + token,
      { ...context, name },
    );
    return Number(JSON.parse(finalized.bytes.toString()).artifactId);
  }
  return { platform, request, upload };
}

describe('D5 external official artifact service fixture', () => {
  test('preserves distinct opaque bytes and original producing runs across independent invocations', async () => {
    const { platform, request, upload } = fixture();
    const first = Buffer.from('opaque original SDK archive A');
    const second = Buffer.from('opaque original SDK archive B');
    const a = await upload('a', first, 1101, 1);
    const b = await upload('b', second, 1102, 2);
    expect(a).not.toBe(b);
    const list = JSON.parse(
      (
        await request(prefix + 'artifacts?per_page=100&page=1', 'GET', 'token ' + githubKey)
      ).bytes.toString(),
    );
    expect(list.total_count).toBe(2);
    expect(list.artifacts.map((item: any) => item.workflow_run.id)).toEqual([1101, 1102]);
    expect(list.artifacts[0].digest).toBe(
      'sha256:' + createHash('sha256').update(first).digest('hex'),
    );
    const oldRun = JSON.parse(
      (
        await request(prefix + 'runs/1101/attempts/1', 'GET', 'token ' + githubKey)
      ).bytes.toString(),
    );
    expect(oldRun).toEqual({ id: 1101, run_attempt: 1 });
    expect(
      (await request(prefix + 'runs/1101/attempts/2', 'GET', 'token ' + githubKey)).status,
    ).toBe(404);
    const redirect = await request(prefix + `artifacts/${a}/zip`, 'GET', 'token ' + githubKey);
    expect(redirect.status).toBe(302);
    expect((await request(new URL(String(redirect.headers.location)).pathname)).bytes).toEqual(
      first,
    );
    await request(prefix + `artifacts/${a}`, 'DELETE', 'token ' + githubKey);
    expect(platform.size).toBe(1);
    expect(
      JSON.parse(
        (await request(prefix + `artifacts/${b}`, 'GET', 'token ' + githubKey)).bytes.toString(),
      ).name,
    ).toBe('b');
  });

  test('serves current Bearer HTTPS REST and anonymous allowed-host downloads from original SDK bytes', async () => {
    const trace = createOperationTrace();
    trace.beginCase();
    const { platform, request, upload } = fixture(trace);
    const original = Buffer.from('original SDK ZIP bytes over current HTTPS fixture');
    const id = await upload('current', original, 1401, 1);
    const api = (route: string, method = 'GET', authorization = 'Bearer ' + githubKey) =>
      request(prefix + route, method, authorization, undefined, 'api.github.com');
    const listed = JSON.parse((await api('artifacts?per_page=100&page=1')).bytes.toString());
    expect(listed.artifacts).toHaveLength(1);
    expect(listed.artifacts[0].workflow_run.id).toBe(1401);
    expect(JSON.parse((await api(`artifacts/${id}`)).bytes.toString()).digest).toBe(
      'sha256:' + createHash('sha256').update(original).digest('hex'),
    );
    const redirect = await api(`artifacts/${id}/zip`);
    expect(redirect.status).toBe(302);
    const location = new URL(String(redirect.headers.location));
    expect(location.protocol).toBe('https:');
    expect(location.hostname).toBe(archiveDownloadHost);
    expect(
      (await request(location.pathname, 'GET', undefined, undefined, archiveDownloadHost)).bytes,
    ).toEqual(original);
    await expect(api('artifacts', 'GET', 'token ' + githubKey)).rejects.toThrow();
    await expect(
      request(location.pathname, 'GET', 'Bearer ' + githubKey, undefined, archiveDownloadHost),
    ).rejects.toThrow();
    await expect(
      request(location.pathname, 'POST', undefined, undefined, archiveDownloadHost),
    ).rejects.toThrow();
    await expect(
      request(prefix + 'artifacts', 'GET', 'Bearer ' + githubKey, undefined, 'foreign.example'),
    ).rejects.toThrow();
    await expect(
      request(
        '/twirp/ArtifactService/CreateArtifact',
        'POST',
        'Bearer ' + runtimeToken(1401, 1),
        {},
        'api.github.com',
      ),
    ).rejects.toThrow();
    await api(`artifacts/${id}`, 'DELETE');
    expect(platform.size).toBe(0);
    expect((await api(`artifacts/${id}`)).status).toBe(404);
    const observed = trace.snapshot();
    expect(observed.counts.sdk_create).toEqual([1, 1, 0]);
    expect(observed.counts.sdk_finalize).toEqual([1, 1, 0]);
    expect(observed.counts.zip_body).toEqual([1, 1, 0]);
    expect(observed.last_mutation.slice(2, 5)).toEqual(['delete', 'o1', 204]);
    expect(observed.in_flight_total).toBe(0);
  });

  test('snapshot forks do not mutate the accepted original transport bytes', async () => {
    const { platform, request, upload } = fixture();
    const original = Buffer.from('accepted immutable opaque archive');
    const id = await upload('accepted', original, 1201, 1);
    const source = platform.snapshot();
    const corrupt = platform.snapshot();
    corrupt[0][1].bytes[0] ^= 0x80;
    expect((await request(`/d5-download/${id}`)).bytes).toEqual(original);
    platform.restore(corrupt);
    expect((await request(`/d5-download/${id}`)).bytes).not.toEqual(original);
    platform.restore(source);
    expect((await request(`/d5-download/${id}`)).bytes).toEqual(original);
  });

  test('refuses foreign authority and an upload finalized before its ZIP is complete', async () => {
    const { platform, request } = fixture();
    platform.configure(1301, 1);
    await expect(request(prefix + 'artifacts', 'GET', 'token foreign')).rejects.toThrow();
    await expect(
      request('/twirp/ArtifactService/CreateArtifact', 'POST', 'Bearer ' + runtimeToken(1302, 1), {
        name: 'foreign',
      }),
    ).rejects.toThrow();
    const context = {
      workflow_run_backend_id: 'd5-run-1301',
      workflow_job_run_backend_id: 'd5-job-1301-1',
      name: 'incomplete',
    };
    await request(
      '/twirp/ArtifactService/CreateArtifact',
      'POST',
      'Bearer ' + runtimeToken(1301, 1),
      { ...context, expires_at: '2030-01-01T00:00:00Z' },
    );
    await expect(
      request(
        '/twirp/ArtifactService/FinalizeArtifact',
        'POST',
        'Bearer ' + runtimeToken(1301, 1),
        context,
      ),
    ).rejects.toThrow();
    expect(platform.size).toBe(0);
    expect(() => platform.restore([])).toThrow();
  });
});
