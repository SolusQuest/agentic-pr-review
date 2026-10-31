import { createHash } from 'node:crypto';
import { requireThat, REPOSITORY } from './candidate.mjs';
import { STAGE_LIMITS } from './stage-candidate.format.mjs';

const ROOT = `/repos/${REPOSITORY}`;
// No Octokit retry plugin: a rejected/lost POST is reconciled by GET, never replayed.
export function stageHttp(token) {
  requireThat(typeof token === 'string' && token.length > 0, 'stage_token_missing');
  const end = Date.now() + 20 * 60 * 1000;
  async function request(method, path, body, upload = false, binary = false) {
    requireThat(
      Date.now() < end && (path === ROOT || path.startsWith(ROOT + '/')),
      'stage_http_denied',
    );
    const headers = {
      Authorization: `Bearer ${token}`,
      Accept: binary ? 'application/octet-stream' : 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    };
    if (body) headers['Content-Type'] = upload ? 'application/octet-stream' : 'application/json';
    try {
      return await fetch(
        (upload ? 'https://uploads.github.com' : 'https://api.github.com') + path,
        {
          method,
          headers,
          body: body && (upload ? body : JSON.stringify(body)),
          redirect: 'manual',
          signal: AbortSignal.timeout(30000),
        },
      );
    } catch {
      throw new Error('stage_transport_unknown');
    }
  }
  async function bytes(response, cap) {
    requireThat(response.body, 'stage_response_invalid');
    const chunks = [];
    let count = 0;
    try {
      for await (const chunk of response.body) {
        count += chunk.length;
        requireThat(count <= cap, 'stage_response_limit');
        chunks.push(Buffer.from(chunk));
      }
    } catch {
      throw new Error('stage_response_invalid');
    }
    return Buffer.concat(chunks);
  }
  async function get(path, absent = false) {
    const response = await request('GET', path);
    if (absent && response.status === 404) return null;
    requireThat(response.status === 200, 'stage_read_denied');
    try {
      return JSON.parse(
        new TextDecoder('utf8', { fatal: true }).decode(await bytes(response, 1048576)),
      );
    } catch {
      throw new Error('stage_response_invalid');
    }
  }
  async function list(path, field = null) {
    const all = [];
    let total;
    for (let page = 1; page <= STAGE_LIMITS.pages; page++) {
      const value = await get(
        path + (path.includes('?') ? '&' : '?') + `per_page=100&page=${page}`,
      );
      const rows = field ? value?.[field] : value;
      requireThat(Array.isArray(rows) && rows.length <= 100, 'stage_pagination_invalid');
      if (field) {
        requireThat(
          Number.isSafeInteger(value.total_count) &&
            value.total_count >= 0 &&
            (total === undefined || total === value.total_count),
          'stage_pagination_invalid',
        );
        total = value.total_count;
      }
      all.push(...rows);
      if (rows.length < 100) {
        requireThat(!field || total === all.length, 'stage_pagination_incomplete');
        return all;
      }
    }
    throw new Error('stage_pagination_limit');
  }
  async function post(path, body, upload = false) {
    const r = await request('POST', path, body, upload);
    // Drain boundedly, but never expose arbitrary server messages or retry a rejected write.
    const raw = await bytes(r, 1048576);
    requireThat(r.status === 201, 'stage_write_unknown');
    try {
      return JSON.parse(raw.toString('utf8'));
    } catch {
      throw new Error('stage_write_unknown');
    }
  }
  async function assetHash(id, size) {
    let r = await request('GET', `${ROOT}/releases/assets/${id}`, null, false, true);
    if (r.status === 302) {
      const url = new URL(r.headers.get('location') ?? '');
      requireThat(
        url.protocol === 'https:' &&
          !url.username &&
          !url.password &&
          !url.port &&
          ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(
            url.hostname,
          ),
        'stage_asset_redirect_denied',
      );
      // Deliberately no staging Authorization on the signed storage redirect.
      try {
        r = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(30000) });
      } catch {
        throw new Error('stage_asset_download_failed');
      }
    }
    requireThat(r.status === 200 && r.body, 'stage_asset_download_failed');
    const hash = createHash('sha256');
    let count = 0;
    try {
      for await (const chunk of r.body) {
        count += chunk.length;
        requireThat(count <= size, 'stage_asset_size_drift');
        hash.update(chunk);
      }
    } catch {
      throw new Error('stage_asset_download_failed');
    }
    requireThat(count === size, 'stage_asset_size_drift');
    return hash.digest('hex');
  }
  return { get, list, post, assetHash };
}
