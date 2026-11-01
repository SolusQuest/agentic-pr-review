import { createHash } from 'node:crypto';
import { decimal, REPOSITORY, requireThat } from './candidate.mjs';
import { STAGE_LIMITS } from './stage-candidate.format.mjs';

const ROOT = `/repos/${REPOSITORY}`;
// Role-specific fixed routes. No retries, arbitrary URLs, settings writes,
// release creation, upload, delete, ref update or credential-bearing redirects.
export function promotionHttp(token, role) {
  requireThat(
    ['publisher', 'protection', 'public', 'preflight'].includes(role) &&
      (role === 'public' ? token === null : typeof token === 'string' && token.length > 0),
    'promotion_transport_denied',
  );
  const end = Date.now() + 20 * 60 * 1000;
  function allowed(method, path) {
    if (role === 'protection')
      return (
        method === 'GET' &&
        (path === ROOT + '/immutable-releases' ||
          new RegExp('^' + ROOT + '/rulesets/[1-9][0-9]*$').test(path))
      );
    if (role === 'preflight') return method === 'GET' && path === ROOT + '/environments/r7-release';
    if (method === 'PATCH')
      return role === 'publisher' && new RegExp('^' + ROOT + '/releases/[1-9][0-9]*$').test(path);
    if (method === 'POST') return role === 'publisher' && path === ROOT + '/git/refs';
    return (
      method === 'GET' &&
      (path === ROOT ||
        path.startsWith(ROOT + '/releases') ||
        (role === 'publisher' &&
          (path.startsWith(ROOT + '/actions/') ||
            path.startsWith(ROOT + '/git/') ||
            path === ROOT + '/environments/r7-release')))
    );
  }
  async function request(method, path, body, binary = false) {
    requireThat(Date.now() < end && allowed(method, path), 'promotion_route_denied');
    const headers = {
      Accept: binary ? 'application/octet-stream' : 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body) headers['Content-Type'] = 'application/json';
    try {
      return await fetch('https://api.github.com' + path, {
        method,
        headers,
        body: body && JSON.stringify(body),
        redirect: 'manual',
        signal: AbortSignal.timeout(30000),
      });
    } catch {
      throw new Error('promotion_transport_unknown');
    }
  }
  async function bytes(response, cap) {
    requireThat(response.body, 'promotion_response_invalid');
    const chunks = [];
    let count = 0;
    try {
      for await (const chunk of response.body) {
        count += chunk.length;
        requireThat(count <= cap, 'promotion_response_limit');
        chunks.push(Buffer.from(chunk));
      }
    } catch {
      throw new Error('promotion_response_invalid');
    }
    return Buffer.concat(chunks);
  }
  async function get(path, absent = false) {
    const response = await request('GET', path);
    if (absent && response.status === 404) return null;
    requireThat(response.status === 200, 'promotion_read_denied');
    try {
      return JSON.parse(
        new TextDecoder('utf8', { fatal: true }).decode(await bytes(response, 1048576)),
      );
    } catch {
      throw new Error('promotion_response_invalid');
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
      requireThat(Array.isArray(rows) && rows.length <= 100, 'promotion_pagination_invalid');
      if (field) {
        requireThat(
          Number.isSafeInteger(value.total_count) &&
            value.total_count >= 0 &&
            (total === undefined || total === value.total_count),
          'promotion_pagination_invalid',
        );
        total = value.total_count;
      }
      all.push(...rows);
      if (rows.length < 100) {
        requireThat(!field || total === all.length, 'promotion_pagination_incomplete');
        return all;
      }
    }
    throw new Error('promotion_pagination_limit');
  }
  async function write(method, path, body) {
    const r = await request(method, path, body),
      raw = await bytes(r, 1048576);
    requireThat(r.status === (method === 'PATCH' ? 200 : 201), 'promotion_write_unknown');
    try {
      return JSON.parse(raw.toString('utf8'));
    } catch {
      throw new Error('promotion_write_unknown');
    }
  }
  async function assetBytes(id, size) {
    decimal(id);
    let r = await request('GET', `${ROOT}/releases/assets/${id}`, null, true);
    if (r.status === 302) {
      let url;
      try {
        url = new URL(r.headers.get('location') ?? '');
      } catch {
        throw new Error('promotion_redirect_denied');
      }
      requireThat(
        url.protocol === 'https:' &&
          !url.username &&
          !url.password &&
          !url.port &&
          ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(
            url.hostname,
          ),
        'promotion_redirect_denied',
      );
      try {
        r = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(30000) });
      } catch {
        throw new Error('promotion_download_failed');
      }
    }
    requireThat(
      r.status === 200 && Number.isSafeInteger(size) && size > 0 && size <= 67108864,
      'promotion_download_denied',
    );
    const value = await bytes(r, size);
    requireThat(value.length === size, 'promotion_asset_size_drift');
    return value;
  }
  return {
    get,
    list,
    patch: (p, b) => write('PATCH', p, b),
    post: (p, b) => write('POST', p, b),
    assetBytes,
    assetHash: async (id, size) =>
      createHash('sha256')
        .update(await assetBytes(id, size))
        .digest('hex'),
  };
}

export async function verifyPublicRuntime(policy) {
  // policy was authenticated by the unchanged P3 parser against T. Narrow the
  // network boundary again; no configurable URL or installed runtime fallback.
  requireThat(
    /^https:\/\/builds\.dotnet\.microsoft\.com\/dotnet\/Runtime\/10\.0\.[0-9]+\/dotnet-runtime-10\.0\.[0-9]+-linux-x64\.tar\.gz$/.test(
      policy.url,
    ) &&
      policy.url ===
        `https://builds.dotnet.microsoft.com/dotnet/Runtime/${policy.version}/dotnet-runtime-${policy.version}-linux-x64.tar.gz` &&
      /^[a-f0-9]{128}$/.test(policy.sha512) &&
      Number.isSafeInteger(policy.archiveBytes) &&
      policy.archiveBytes > 0 &&
      policy.archiveBytes <= 67108864,
    'promotion_runtime_policy_denied',
  );
  try {
    const r = await fetch(policy.url, { redirect: 'error', signal: AbortSignal.timeout(120000) });
    requireThat(r.status === 200 && r.body, 'promotion_runtime_unavailable');
    const hash = createHash('sha512');
    let count = 0;
    for await (const chunk of r.body) {
      count += chunk.length;
      requireThat(count <= policy.archiveBytes, 'promotion_runtime_size_drift');
      hash.update(chunk);
    }
    requireThat(
      count === policy.archiveBytes && hash.digest('hex') === policy.sha512,
      'promotion_runtime_drift',
    );
  } catch {
    throw new Error('promotion_runtime_denied');
  }
}
