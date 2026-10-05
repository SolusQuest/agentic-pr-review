import { createHash } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import { request as httpsRequest } from 'node:https';
import path from 'node:path';

import { readBoundedFile } from '../../../scripts/release/build-payload.format.mjs';
import { parsePayloadMap, PAYLOAD_REPOSITORY } from './payload-map.js';
import type { RepositoryTestPayloadTransport } from './release-payload.js';
import { parseStrictJson } from './strict-json.js';
import { fail, lowerHex } from './validation.js';

const ACTION_PATH = '.github/actions/agentic-pr-review';
const METADATA_LIMIT = 256 * 1024;
const installedFiles = [
  ['action.yml', 64 * 1024],
  ['payload-map.json', 16 * 1024],
  ['dist/index.js', 8 * 1024 * 1024],
] as const;

export interface InstalledActionSourceRequest {
  readonly actionRoot: string;
  readonly mapBytes: Uint8Array;
  readonly actionRepository: string | undefined;
  readonly actionRef: string | undefined;
  readonly signal: AbortSignal;
}

/** Runner context supplies the ref. Tag use requires the R7 immutable-tag publication invariant. */
export async function resolveInstalledActionSource(
  request: InstalledActionSourceRequest,
): Promise<string> {
  return resolve(request, (url, options, callback) => httpsRequest(url, options, callback));
}

/** Explicit repository transport seam; no input/environment selects it in the product. */
export function createRepositoryTestActionSourceResolver(
  transport: RepositoryTestPayloadTransport,
): typeof resolveInstalledActionSource {
  return (request) => resolve(request, transport);
}

async function resolve(
  request: InstalledActionSourceRequest,
  transport: RepositoryTestPayloadTransport,
): Promise<string> {
  const { actionRoot, actionRepository, actionRef, signal } = request;
  const mapBytes = Buffer.from(request.mapBytes);
  const map = parsePayloadMap(mapBytes);
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
    for (const [relative, maximum] of installedFiles) {
      const bytes =
        relative === 'payload-map.json'
          ? mapBytes
          : await readBoundedFile(path.join(actionRoot, relative), maximum);
      signal.throwIfAborted();
      const info = record(
        await metadata(
          `/contents/${ACTION_PATH}/${relative}?ref=${source}`,
          'application/vnd.github.object+json',
          transport,
          signal,
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
      (declared !== undefined &&
        (!/^[1-9][0-9]*$/u.test(declared) || Number(declared) > METADATA_LIMIT))
    ) {
      fail('wrapper_action_source_invalid');
    }
    const chunks: Buffer[] = [];
    let size = 0;
    for await (const value of response) {
      const bytes = Buffer.from(value as Uint8Array);
      size += bytes.length;
      if (size > METADATA_LIMIT) fail('wrapper_action_source_invalid');
      chunks.push(bytes);
    }
    if (!response.complete || size === 0) fail('wrapper_action_source_invalid');
    return parseStrictJson(Buffer.concat(chunks, size), METADATA_LIMIT);
  } finally {
    response.destroy();
  }
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value))
    fail('wrapper_action_source_invalid');
  return value as Record<string, unknown>;
}
