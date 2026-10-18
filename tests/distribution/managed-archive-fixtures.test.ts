import { Readable } from 'node:stream';
import { gunzipSync } from 'node:zlib';
import { describe, expect, test } from 'vitest';
import {
  createGitHubFixture,
  objectHash,
  workflowSha,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/github-fixture.mjs';
import {
  githubKey,
  providerKey,
  repository,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/official-platform.mjs';
import { createProviderFixture } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/provider-fixture.mjs';

function exchange(host: string, url: string, method = 'GET', payload?: unknown) {
  const incoming = Object.assign(Readable.from([Buffer.from(JSON.stringify(payload ?? {}))]), {
    url,
    method,
    headers: {
      host,
      authorization: 'Bearer ' + (host === 'api.deepseek.com' ? providerKey : githubKey),
    } as Record<string, string>,
  });
  const response = {
    status: 0,
    headers: {} as Record<string, string>,
    bytes: Buffer.alloc(0),
    writeHead(status: number, headers = {}) {
      this.status = status;
      this.headers = headers;
      return this;
    },
    end(bytes?: Buffer | string) {
      this.bytes = Buffer.from(bytes ?? '');
      return this;
    },
  };
  return { incoming, response };
}

describe('D5 bounded external source and provider services', () => {
  test('serves distinct linked source heads and original tar bytes matching Git blob identities', async () => {
    const github = createGitHubFixture('workflow');
    let previous = workflowSha;
    for (const head of github.heads) {
      const commit = exchange('api.github.com', `/repos/${repository}/git/commits/${head}`);
      await github.handle(commit.incoming, commit.response);
      const document = JSON.parse(commit.response.bytes.toString());
      expect(document.parents).toEqual([{ sha: previous }]);
      previous = head;
      const archive = exchange('codeload.github.com', `/${repository}/legacy.tar.gz/${head}`);
      delete archive.incoming.headers.authorization;
      await github.handle(archive.incoming, archive.response);
      const bytes = gunzipSync(archive.response.bytes);
      let offset = 0,
        count = 0;
      while (bytes[offset]) {
        const header = bytes.subarray(offset, offset + 512);
        const size = Number.parseInt(header.subarray(124, 136).toString().replaceAll('\0', ''), 8);
        const type = header.subarray(156, 157).toString();
        const mode = Number.parseInt(header.subarray(100, 108).toString().replaceAll('\0', ''), 8);
        if (type === '5') {
          expect(mode).toBe(0o775);
          expect(size).toBe(0);
          offset += 512;
          continue;
        }
        expect(type).toBe('0');
        expect(mode).toBe(0o664);
        const body = bytes.subarray(offset + 512, offset + 512 + size);
        const sha = objectHash('blob', body);
        const blob = exchange('api.github.com', `/repos/${repository}/git/blobs/${sha}`);
        await github.handle(blob.incoming, blob.response);
        expect(Buffer.from(JSON.parse(blob.response.bytes.toString()).content, 'base64')).toEqual(
          body,
        );
        count++;
        offset += 512 + Math.ceil(size / 512) * 512;
      }
      expect(count).toBeGreaterThanOrEqual(3);
    }
  });

  test('rejects credential-free codeload requests for an unknown source', async () => {
    const github = createGitHubFixture('workflow');
    const request = exchange(
      'codeload.github.com',
      `/${repository}/legacy.tar.gz/${'0'.repeat(40)}`,
    );
    delete request.incoming.headers.authorization;
    await expect(github.handle(request.incoming, request.response)).rejects.toThrow();
  });

  test('rejects a missing restored prefix before issuing a provider response', async () => {
    const github = createGitHubFixture('workflow'),
      provider = createProviderFixture(github);
    provider.configure('success', [{ role: 'system', content: 'preserved context' }]);
    const request = exchange('api.deepseek.com', '/chat/completions', 'POST', {
      model: 'deepseek-flash',
      max_tokens: 1024,
      stream: false,
      thinking: { type: 'enabled' },
      reasoning_effort: 'high',
      tools: ['list_changed_files', 'list_files', 'read_diff', 'read_file', 'search_text'].map(
        (name) => ({ type: 'function', function: { name } }),
      ),
      messages: [
        { role: 'system', content: 'changed context' },
        { role: 'user', content: 'next head' },
      ],
    });
    await expect(provider.handle(request.incoming, request.response)).rejects.toThrow(
      'd5_complete_restored_prefix',
    );
    expect(request.response.status).toBe(0);
  });
});
