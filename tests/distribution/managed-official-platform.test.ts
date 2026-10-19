import { Readable } from 'node:stream';
import { createHash } from 'node:crypto';
import { describe, expect, test } from 'vitest';
import {
  createOfficialPlatform,
  githubKey,
  repository,
  runtimeToken,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/official-platform.mjs';

const prefix = `/repos/${repository}/actions/`;
function fixture() {
  const platform = createOfficialPlatform(() => 'http://127.0.0.1:8123');
  async function request(route: string, method = 'GET', authorization?: string, payload?: unknown) {
    const bytes = Buffer.isBuffer(payload) ? payload : Buffer.from(JSON.stringify(payload ?? {}));
    const incoming = Object.assign(Readable.from([bytes]), {
      url: route,
      method,
      headers: authorization ? { authorization } : {},
    });
    const response = {
      status: 0,
      headers: {} as Record<string, string | number>,
      bytes: Buffer.alloc(0),
      writeHead(status: number, headers = {}) {
        this.status = status;
        this.headers = headers;
        return this;
      },
      end(value?: Buffer | string) {
        this.bytes = Buffer.from(value ?? '');
        return this;
      },
    };
    expect(await platform.handle(incoming, response)).toBe(true);
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
