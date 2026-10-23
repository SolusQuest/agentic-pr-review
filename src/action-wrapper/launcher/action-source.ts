import { createHash } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import { request as httpsRequest } from 'node:https';
import path from 'node:path';

import { readBoundedFile } from '../../../scripts/release/build-payload.format.mjs';
import {
  MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES,
  parseManagedPayloadMap,
  parseHistoricalNativePayloadMapForFixture,
  PAYLOAD_REPOSITORY,
} from './payload-map.js';
import type { RepositoryTestPayloadTransport } from './release-payload.js';
import { parseStrictJson } from './strict-json.js';
import { fail, lowerHex } from './validation.js';

const ACTION_PATH = '.github/actions/agentic-pr-review';
const METADATA_LIMIT = 256 * 1024;
// GitHub's object response includes Base64 content for a <=1 MiB map.
const MANAGED_MAP_METADATA_LIMIT = 512 * 1024;
const installedFiles = [
  ['action.yml', 64 * 1024],
  ['payload-map.json', 16 * 1024],
  ['dist/index.js', 8 * 1024 * 1024],
] as const;

export const managedInstalledFiles = [
  ['action.yml', 64 * 1024],
  ['payload-map.json', MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES],
  ['internal/action.yml', 64 * 1024],
  ['internal/prepare/action.yml', 64 * 1024],
  ['internal/dist/index.js', 8 * 1024 * 1024],
  ['internal/dist/sdk-upload.js', 8 * 1024 * 1024],
  ['internal/dist/prepare.js', 8 * 1024 * 1024],
  ['internal/dist/cleanup.js', 8 * 1024 * 1024],
] as const;

export interface InstalledActionSourceRequest {
  readonly actionRoot: string;
  readonly mapBytes: Uint8Array;
  readonly actionRepository: string | undefined;
  readonly actionRef: string | undefined;
  readonly signal: AbortSignal;
}

/** Historical native source fixture only. Runner context supplies the ref. Tag use requires the R7 immutable-tag publication invariant. */
export async function resolveHistoricalInstalledActionSourceForFixture(
  request: InstalledActionSourceRequest,
): Promise<string> {
  return resolve(
    request,
    (url, options, callback) => httpsRequest(url, options, callback),
    parseHistoricalNativePayloadMapForFixture,
    false,
  );
}

/** The ordinary managed Action selects this capability explicitly, never by fallback. */
export async function resolveInstalledManagedActionSource(
  request: InstalledActionSourceRequest,
): Promise<string> {
  return resolve(
    request,
    (url, options, callback) => httpsRequest(url, options, callback),
    parseManagedPayloadMap,
    true,
  );
}

/** Explicit repository transport seam; no input/environment selects it in the product. */
export function createRepositoryTestActionSourceResolver(
  transport: RepositoryTestPayloadTransport,
): typeof resolveHistoricalInstalledActionSourceForFixture {
  return (request) => resolve(request, transport, parseHistoricalNativePayloadMapForFixture, false);
}

export function createRepositoryTestManagedActionSourceResolver(
  transport: RepositoryTestPayloadTransport,
): typeof resolveInstalledManagedActionSource {
  return (request) => resolve(request, transport, parseManagedPayloadMap, true);
}

async function resolve(
  request: InstalledActionSourceRequest,
  transport: RepositoryTestPayloadTransport,
  parseMap: typeof parseManagedPayloadMap | typeof parseHistoricalNativePayloadMapForFixture,
  managed: boolean,
): Promise<string> {
  const { actionRoot, actionRepository, actionRef, signal } = request;
  const mapBytes = Buffer.from(request.mapBytes);
  const map = parseMap(mapBytes);
  if (
    actionRepository !== PAYLOAD_REPOSITORY ||
    !path.isAbsolute(actionRoot) ||
    (actionRef !== map.payload.identity.releaseVersion && !lowerHex(actionRef, 40))
  ) {
    fail('wrapper_action_source_invalid');
  }
  try {
    signal.throwIfAborted();
    const source = lowerHex(actionRef, 40)
      ? actionRef
      : record(
          await metadata(
            `/commits/${map.payload.identity.releaseVersion}`,
            'application/vnd.github+json',
            transport,
            signal,
          ),
        ).sha;
    if (!lowerHex(source, 40)) fail('wrapper_action_source_invalid');
    const selectedFiles: ReadonlyArray<readonly [string, number]> = managed
      ? managedInstalledFiles
      : installedFiles;
    for (const [relative, nativeMaximum] of selectedFiles) {
      const managedMap = managed && relative === 'payload-map.json';
      const maximum = managedMap ? MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES : nativeMaximum;
      const bytes =
        relative === 'payload-map.json'
          ? mapBytes
          : await readBoundedFile(path.join(actionRoot, relative), maximum);
      if (bytes.length > maximum) fail('wrapper_action_source_invalid');
      signal.throwIfAborted();
      const info = record(
        await metadata(
          `/contents/${ACTION_PATH}/${relative}?ref=${source}`,
          'application/vnd.github.object+json',
          transport,
          signal,
          managedMap ? MANAGED_MAP_METADATA_LIMIT : METADATA_LIMIT,
        ),
      );
      const blobSha = createHash('sha1')
        .update(`blob ${bytes.length}\0`)
        .update(bytes)
        .digest('hex');
      if (
        info.type !== 'file' ||
        info.path !== `${ACTION_PATH}/${relative}` ||
        info.size !== bytes.length ||
        info.sha !== blobSha
      ) {
        fail('wrapper_action_source_invalid');
      }
    }
    return source;
  } catch {
    return fail(signal.aborted ? 'wrapper_payload_cancelled' : 'wrapper_action_source_invalid');
  }
}

async function metadata(
  route: string,
  accept: 'application/vnd.github+json' | 'application/vnd.github.object+json',
  transport: RepositoryTestPayloadTransport,
  signal: AbortSignal,
  maximum = METADATA_LIMIT,
): Promise<unknown> {
  signal.throwIfAborted();
  const response: IncomingMessage = await new Promise((resolveResponse, reject) => {
    const outgoing = transport(
      new URL(`https://api.github.com/repos/${PAYLOAD_REPOSITORY}${route}`),
      {
        method: 'GET',
        agent: false,
        signal,
        headers: {
          'User-Agent': 'agentic-pr-review-action-source',
          Accept: accept,
          'X-GitHub-Api-Version': '2022-11-28',
        },
      },
      resolveResponse,
    );
    outgoing.once('error', reject);
    outgoing.end();
  });
  try {
    const declared = response.headers['content-length'];
    if (
      response.statusCode !== 200 ||
      response.headers.link !== undefined ||
      (response.headers['content-encoding'] !== undefined &&
        response.headers['content-encoding'] !== 'identity') ||
      (declared !== undefined && (!/^[1-9][0-9]*$/u.test(declared) || Number(declared) > maximum))
    ) {
      fail('wrapper_action_source_invalid');
    }
    const chunks: Buffer[] = [];
    let size = 0;
    for await (const value of response) {
      const bytes = Buffer.from(value as Uint8Array);
      size += bytes.length;
      if (size > maximum) fail('wrapper_action_source_invalid');
      chunks.push(bytes);
    }
    if (!response.complete || size === 0) fail('wrapper_action_source_invalid');
    return parseStrictJson(Buffer.concat(chunks, size), maximum);
  } finally {
    response.destroy();
  }
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value))
    fail('wrapper_action_source_invalid');
  return value as Record<string, unknown>;
}
