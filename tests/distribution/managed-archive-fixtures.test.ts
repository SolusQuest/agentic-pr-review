import { Readable } from 'node:stream';
import { gunzipSync } from 'node:zlib';
import { describe, expect, test } from 'vitest';
import {
  createGitHubFixture,
  objectHash,
  workflowSha,
  json,
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
  test('serves downstream automatic authority and retains its producing run across dispatch', async () => {
    const github = createGitHubFixture('historical');
    github.selectDownstreamWorkflow('downstream');
    github.configure(0, 21001, true);
    const trigger = github.triggerRun();
    expect(trigger).toMatchObject({
      name: 'CI',
      path: '.github/workflows/ci.yml',
      event: 'pull_request',
      conclusion: 'success',
      head_sha: github.heads[0],
    });
    const source = exchange(
      'api.github.com',
      `/repos/${repository}/contents/${github.workflowPath}?ref=${workflowSha}`,
    );
    await github.handle(source.incoming, source.response);
    expect(JSON.parse(source.response.bytes.toString())).toMatchObject({
      path: '.github/workflows/agentic-pr-review.yml',
      sha: objectHash('blob', Buffer.from('downstream')),
      content: Buffer.from('downstream').toString('base64'),
    });
    const association = exchange(
      'api.github.com',
      `/repos/${repository}/commits/${trigger.head_sha}/pulls`,
    );
    await github.handle(association.incoming, association.response);
    expect(JSON.parse(association.response.bytes.toString())).toMatchObject([
      { id: 1000, number: 147, head: { sha: trigger.head_sha } },
    ]);
    const ci = exchange('api.github.com', `/repos/${repository}/actions/runs/901/attempts/1`);
    await github.handle(ci.incoming, ci.response);
    expect(JSON.parse(ci.response.bytes.toString())).toEqual(trigger);
    github.configure(1, 21002);
    for (const [id, event] of [
      [21001, 'workflow_run'],
      [21002, 'workflow_dispatch'],
    ]) {
      const call = exchange('api.github.com', `/repos/${repository}/actions/runs/${id}/attempts/1`);
      await github.handle(call.incoming, call.response);
      expect(JSON.parse(call.response.bytes.toString())).toMatchObject({
        id,
        event,
        path: '.github/workflows/agentic-pr-review.yml',
        head_sha: workflowSha,
      });
    }
    expect(github.configPath).toBe('.github/agentic-pr-review.json');
  });

  test('revalidates complete provenance bytes while retaining rate headers and changed-body responses', async () => {
    const github = createGitHubFixture('workflow');
    github.configure(0, 1101);
    const route = `/repos/${repository}/actions/runs/1101/attempts/1`;
    const initial = exchange('api.github.com', route);
    await github.handle(initial.incoming, initial.response);
    const tag = initial.response.headers.ETag;
    expect(tag).toMatch(/^"[a-f0-9]{64}"$/);
    const same = exchange('api.github.com', route);
    same.incoming.headers['if-none-match'] = tag;
    await github.handle(same.incoming, same.response);
    expect(same.response.status).toBe(304);
    expect(same.response.bytes.length).toBe(0);
    expect(same.response.headers['Content-Length']).toBeUndefined();
    for (const response of [initial.response, same.response]) {
      expect(response.headers['x-ratelimit-limit']).toBe('1000');
      expect(response.headers['x-ratelimit-remaining']).toBe('950');
      expect(response.headers['x-ratelimit-reset']).toBe('4102444800');
    }
    // Exercise the same response writer when this resource's provenance changes.
    const changed = exchange('api.github.com', route);
    changed.incoming.headers['if-none-match'] = tag;
    const document = { ...JSON.parse(initial.response.bytes.toString()), head_sha: 'd'.repeat(40) };
    json(changed.response, document, 200, changed.incoming);
    expect(changed.response.status).toBe(200);
    expect(changed.response.headers.ETag).not.toBe(tag);
    expect(JSON.parse(changed.response.bytes.toString())).toEqual(document);
    const absent = exchange('api.github.com', `/repos/${repository}/actions/runs/9999/attempts/1`);
    absent.incoming.headers['if-none-match'] = tag;
    await github.handle(absent.incoming, absent.response);
    expect(absent.response.status).toBe(404);
    expect(absent.response.headers.ETag).toBeUndefined();
  });
  test('retains exact configured producing-run identities after a later invocation starts', async () => {
    const github = createGitHubFixture('workflow');
    github.configure(0, 1101);
    github.configure(1, 1102);
    for (const id of [1101, 1102]) {
      const call = exchange('api.github.com', `/repos/${repository}/actions/runs/${id}/attempts/1`);
      await github.handle(call.incoming, call.response);
      expect(JSON.parse(call.response.bytes.toString())).toMatchObject({
        id,
        run_attempt: 1,
        head_sha: workflowSha,
      });
    }
    const absent = exchange('api.github.com', `/repos/${repository}/actions/runs/9999/attempts/1`);
    await github.handle(absent.incoming, absent.response);
    expect(absent.response.status).toBe(404);
  });

  test('serves distinct linked source heads and original tar bytes matching Git blob identities', async () => {
    const github = createGitHubFixture('workflow');
    let previous = workflowSha;
    for (const [ordinal, head] of github.heads.entries()) {
      github.configure(ordinal, 1101 + ordinal);
      const commit = exchange('api.github.com', `/repos/${repository}/git/commits/${head}`);
      await github.handle(commit.incoming, commit.response);
      const document = JSON.parse(commit.response.bytes.toString());
      expect(document.parents).toEqual([{ sha: previous }]);
      previous = head;
      const changed = exchange(
        'api.github.com',
        `/repos/${repository}/pulls/147/files?per_page=100&page=1`,
      );
      await github.handle(changed.incoming, changed.response);
      const changes = JSON.parse(changed.response.bytes.toString());
      expect(changes).toHaveLength(ordinal === 2 ? 4 : 3);
      for (const change of changes) {
        expect(change.status).toBe('added');
        expect(change.filename).not.toContain('.github/');
        const original = exchange('api.github.com', `/repos/${repository}/git/blobs/${change.sha}`);
        await github.handle(original.incoming, original.response);
        const source = Buffer.from(
          JSON.parse(original.response.bytes.toString()).content,
          'base64',
        ).toString();
        expect(change.patch).toContain('+' + source.trimEnd());
      }
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

  test('holds known-commit readback only after the current invocation writes', async () => {
    const github = createGitHubFixture('workflow');
    const route = `/repos/${repository}/issues/comments/701`;
    const seed = exchange('api.github.com', `/repos/${repository}/issues/147/comments`, 'POST', {
      body: 'previous accepted comment',
    });
    await github.handle(seed.incoming, seed.response);
    github.holdReadback(true);
    const before = exchange('api.github.com', route);
    await github.handle(before.incoming, before.response);
    expect(before.response.status).toBe(200);
    expect(github.knownCommitBarrier).toBe(false);
    const write = exchange('api.github.com', route, 'PATCH', { body: 'current committed comment' });
    await github.handle(write.incoming, write.response);
    expect(write.response.status).toBe(200);
    const after = exchange('api.github.com', route);
    await github.handle(after.incoming, after.response);
    expect(after.response.status).toBe(0);
    expect(github.knownCommitBarrier).toBe(true);
    expect(github.writes).toBe(2);
    github.holdReadback(false);
    const recovery = exchange('api.github.com', route);
    await github.handle(recovery.incoming, recovery.response);
    expect(JSON.parse(recovery.response.bytes.toString()).body).toBe('current committed comment');
    expect(github.writes).toBe(2);
  });

  test.each(['missing-usage', 'incomplete-tools', 'malformed-final'])(
    'applies the intended %s fault to actual provider response bytes',
    async (mode) => {
      const provider = createProviderFixture(createGitHubFixture('workflow'));
      provider.configure(mode, null);
      const body = {
        model: 'deepseek-flash',
        max_tokens: 1024,
        stream: false,
        thinking: { type: 'enabled' },
        reasoning_effort: 'high',
        tools: ['list_changed_files', 'list_files', 'read_diff', 'read_file', 'search_text'].map(
          (name) => ({ type: 'function', function: { name } }),
        ),
        messages: [{ role: 'user', content: 'review' }],
      };
      const first = exchange('api.deepseek.com', '/chat/completions', 'POST', body);
      await provider.handle(first.incoming, first.response);
      const response = JSON.parse(first.response.bytes.toString());
      expect(first.response.status).toBe(200);
      expect(response.choices[0].message.tool_calls).toHaveLength(3);
      if (mode === 'missing-usage') expect(response).not.toHaveProperty('usage');
      else expect(response.usage.total_tokens).toBe(15);
      if (mode === 'incomplete-tools') expect(response.choices[0].finish_reason).toBe('length');
      if (mode === 'malformed-final') {
        body.messages.push(response.choices[0].message);
        for (let index = 0; index < 3; index++)
          body.messages.push({
            role: 'tool',
            content: JSON.stringify({
              observation_id: 'unit-observation',
              path: 'Caller.cs',
              content: 'Lookup.Find(key).Trim()',
            }),
          });
        const final = exchange('api.deepseek.com', '/chat/completions', 'POST', body);
        await provider.handle(final.incoming, final.response);
        expect(JSON.parse(final.response.bytes.toString()).choices[0].message.content).toBe('{');
        expect(provider.receipts.filter((receipt) => receipt.kind === 'completed-round')).toEqual(
          [],
        );
      }
    },
  );

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
