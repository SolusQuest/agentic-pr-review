import { lstat, realpath, rm } from 'node:fs/promises';
import type { ClientRequest, IncomingMessage } from 'node:http';
import { request as httpsRequest, type RequestOptions } from 'node:https';
import path from 'node:path';

import {
  MEMBERS,
  materializePackage,
  readBoundedFile,
  sha256,
} from '../../../scripts/release/build-payload.format.mjs';
import { parsePayloadMap, type PayloadMap } from './payload-map.js';
import { verifyPreparedPayload, type VerifiedPreparedPayload } from './prepared-payload.js';
import { parseStrictJson } from './strict-json.js';
import { fail, lowerHex } from './validation.js';

const API_ORIGIN = 'https://api.github.com';
const METADATA_MAXIMUM_BYTES = 256 * 1024;
const ACQUISITION_TIMEOUT_MS = 60_000;
const MAXIMUM_REDIRECTS = 3;
export const PAYLOAD_USER_AGENT = 'agentic-pr-review-payload-resolver';

export interface ReleasePayloadRequest {
  /** Embedded/read only from the trusted Action, never release metadata or reviewed workspace. */
  readonly mapBytes: Uint8Array;
  /** Action T, independent from the payload S and builder W recorded in the map. */
  readonly actionSourceSha: string;
  /** Trusted launcher-owned temporary directory, outside the reviewed workspace. */
  readonly stagingParent: string;
  readonly signal: AbortSignal;
}

export interface VerifiedReleasePayload extends VerifiedPreparedPayload {
  readonly root: string;
  readonly identity: PayloadMap['payload']['identity'];
  readonly builder: PayloadMap['builder'];
  /** Consumer must await this in finally on every outcome, including fatal termination. */
  dispose(): Promise<void>;
}

export type RepositoryTestPayloadTransport = (
  url: URL,
  options: RequestOptions,
  onResponse: (response: IncomingMessage) => void,
) => ClientRequest;

const publicTransport: RepositoryTestPayloadTransport = (url, options, onResponse) =>
  httpsRequest(url, options, onResponse);

/** Ordinary acquisition has no endpoint, credential, environment, or local-path override. */
export async function resolveReleasePayload(
  request: ReleasePayloadRequest,
): Promise<VerifiedReleasePayload> {
  return resolve(request, publicTransport, undefined, ACQUISITION_TIMEOUT_MS);
}

/**
 * Repository-test entry only. Logical production URL policy is still enforced before transport.
 * A local archive bypasses network acquisition, never map/platform/package/opened-file admission.
 * No Action input or environment variable selects this entry or its arguments.
 */
export function createRepositoryTestPayloadResolver(options: {
  readonly transport: RepositoryTestPayloadTransport;
  readonly localArchivePath?: string;
  readonly timeoutMs?: number;
}): (request: ReleasePayloadRequest) => Promise<VerifiedReleasePayload> {
  const { transport, localArchivePath, timeoutMs = ACQUISITION_TIMEOUT_MS } = options;
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > ACQUISITION_TIMEOUT_MS) {
    fail('wrapper_release_payload_invalid');
  }
  return (request) => resolve(request, transport, localArchivePath, timeoutMs);
}

async function resolve(
  request: ReleasePayloadRequest,
  transport: RepositoryTestPayloadTransport,
  localArchivePath: string | undefined,
  timeoutMs: number,
): Promise<VerifiedReleasePayload> {
  // Snapshot caller-owned values before the first await. Parsed nested identities are frozen.
  const map = parsePayloadMap(Buffer.from(request.mapBytes));
  const { actionSourceSha, stagingParent } = request;
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(timeoutMs)]);
  if (process.platform !== 'linux' || process.arch !== 'x64') {
    fail('wrapper_payload_platform_unsupported');
  }
  if (!lowerHex(actionSourceSha, 40) || !path.isAbsolute(stagingParent)) {
    fail('wrapper_release_payload_invalid');
  }
  let root: string | undefined;
  let verified: VerifiedPreparedPayload | undefined;
  let retained = false;
  try {
    signal.throwIfAborted();
    const parentStat = await lstat(stagingParent);
    if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) {
      fail('wrapper_release_payload_invalid');
    }
    const parent = await realpath(stagingParent);
    const archive =
      localArchivePath === undefined
        ? await download(map, transport, signal)
        : await readBoundedFile(localArchivePath, map.payload.archiveSize);
    signal.throwIfAborted();
    // D1 validates the complete archive against independent map authority before any extraction.
    const materialized = await materializePackage(archive, map.payload, parent);
    root = materialized.root;
    signal.throwIfAborted();
    verified = await verifyPreparedPayload({
      trustedRoot: root,
      executableRelativePath: MEMBERS[0].path,
      payloadSha256: map.payload.members[0].sha256,
      actionSourceSha,
      buildDiscriminator: materialized.manifest.launcher,
      wrapperBuildDiscriminator: 'r7-d0',
    });
    signal.throwIfAborted();
    const ownedRoot = root;
    const ownedHandle = verified.executableHandle;
    let disposing: Promise<void> | undefined;
    const dispose = (): Promise<void> => {
      disposing ??= (async () => {
        try {
          await ownedHandle.close();
        } finally {
          await rm(ownedRoot, { recursive: true, force: true });
        }
      })();
      return disposing;
    };
    retained = true;
    return { ...verified, root, identity: map.payload.identity, builder: map.builder, dispose };
  } catch {
    return fail(signal.aborted ? 'wrapper_payload_cancelled' : 'wrapper_release_payload_invalid');
  } finally {
    if (!retained) {
      try {
        await verified?.executableHandle.close();
      } finally {
        if (root !== undefined) await rm(root, { recursive: true, force: true });
      }
    }
  }
}

async function download(
  map: PayloadMap,
  transport: RepositoryTestPayloadTransport,
  signal: AbortSignal,
): Promise<Buffer> {
  const prefix = `${API_ORIGIN}/repos/${map.repository}/releases`;
  const release = record(await metadata(`${prefix}/tags/${map.tag}`, transport, signal));
  if (!positiveId(release.id) || release.tag_name !== map.tag || release.draft !== false) {
    fail('wrapper_release_payload_invalid');
  }
  const assets: unknown = await metadata(
    `${prefix}/${release.id}/assets?per_page=100`,
    transport,
    signal,
  );
  // A full page cannot prove uniqueness; do not silently truncate or fall back to first match.
  if (!Array.isArray(assets) || assets.length >= 100) fail('wrapper_release_payload_invalid');
  const entries = assets.map(record);
  const matches = entries.filter((asset) => asset.name === map.payload.archiveName);
  const expectedUrl = `https://github.com/${map.repository}/releases/download/${map.tag}/${map.payload.archiveName}`;
  if (
    matches.length !== 1 ||
    !positiveId(matches[0].id) ||
    matches[0].state !== 'uploaded' ||
    matches[0].size !== map.payload.archiveSize ||
    matches[0].browser_download_url !== expectedUrl
  ) {
    fail('wrapper_release_payload_invalid');
  }
  let url = new URL(expectedUrl);
  for (let redirects = 0; redirects <= MAXIMUM_REDIRECTS; redirects += 1) {
    signal.throwIfAborted();
    const response = await get(url, false, transport, signal);
    try {
      if ([301, 302, 303, 307, 308].includes(response.statusCode ?? 0)) {
        const location = response.headers.location;
        if (redirects === MAXIMUM_REDIRECTS || !location) fail('wrapper_release_payload_invalid');
        const next = new URL(location, url);
        if (
          next.protocol !== 'https:' ||
          next.hostname !== 'release-assets.githubusercontent.com' ||
          next.port !== '' ||
          next.username !== '' ||
          next.password !== '' ||
          next.hash !== ''
        ) {
          fail('wrapper_release_payload_invalid');
        }
        url = next;
        continue;
      }
      if (response.statusCode !== 200) fail('wrapper_release_payload_invalid');
      const archive = await readBody(response, map.payload.archiveSize, true);
      if (sha256(archive) !== map.payload.archiveSha256) fail('wrapper_release_payload_invalid');
      return archive;
    } finally {
      response.destroy();
    }
  }
  return fail('wrapper_release_payload_invalid');
}

async function metadata(
  url: string,
  transport: RepositoryTestPayloadTransport,
  signal: AbortSignal,
): Promise<unknown> {
  const response = await get(new URL(url), true, transport, signal);
  try {
    if (response.statusCode !== 200 || response.headers.link !== undefined) {
      fail('wrapper_release_payload_invalid');
    }
    return parseStrictJson(
      await readBody(response, METADATA_MAXIMUM_BYTES, false),
      METADATA_MAXIMUM_BYTES,
    );
  } finally {
    response.destroy();
  }
}

function get(
  url: URL,
  api: boolean,
  transport: RepositoryTestPayloadTransport,
  signal: AbortSignal,
): Promise<IncomingMessage> {
  signal.throwIfAborted();
  return new Promise((resolveResponse, reject) => {
    const outgoing = transport(
      url,
      {
        method: 'GET',
        agent: false,
        signal,
        headers: {
          'User-Agent': PAYLOAD_USER_AGENT,
          Accept: api ? 'application/vnd.github+json' : 'application/octet-stream',
          ...(api ? { 'X-GitHub-Api-Version': '2022-11-28' } : {}),
        },
      },
      resolveResponse,
    );
    outgoing.once('error', reject);
    outgoing.end();
  });
}

async function readBody(
  response: IncomingMessage,
  maximum: number,
  exact: boolean,
): Promise<Buffer> {
  const declared = response.headers['content-length'];
  const encoding = response.headers['content-encoding'];
  if (
    (encoding !== undefined && encoding !== 'identity') ||
    (declared !== undefined &&
      (!/^(0|[1-9][0-9]*)$/u.test(declared) ||
        !Number.isSafeInteger(Number(declared)) ||
        Number(declared) > maximum ||
        (exact && Number(declared) !== maximum)))
  ) {
    fail('wrapper_release_payload_invalid');
  }
  let total = 0;
  const chunks: Buffer[] = [];
  for await (const value of response) {
    const chunk = Buffer.from(value as Uint8Array);
    total += chunk.length;
    if (total > maximum) fail('wrapper_release_payload_invalid');
    chunks.push(chunk);
  }
  if (!response.complete || total === 0 || (exact && total !== maximum)) {
    fail('wrapper_release_payload_invalid');
  }
  return Buffer.concat(chunks, total);
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    fail('wrapper_release_payload_invalid');
  }
  return value as Record<string, unknown>;
}

function positiveId(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0;
}
