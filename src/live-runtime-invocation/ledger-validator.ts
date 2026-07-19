import { Ajv } from 'ajv';
import schema from '../../protocol/schemas/provider-session-ledger.v1.json' with { type: 'json' };
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { LEDGER_MAX_BYTES } from '../state-v2/constants.js';
import { LiveRuntimeInvocationError } from './errors.js';

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: false });
const validate = ajv.compile(schema);

export function validateCandidateLedgerForHost(bytes: Uint8Array): Record<string, unknown> {
  if (bytes.byteLength > LEDGER_MAX_BYTES)
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger exceeds the raw byte cap.',
    });
  const owned = new Uint8Array(bytes);
  if (owned[0] === 0xef && owned[1] === 0xbb && owned[2] === 0xbf)
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger has a BOM.',
    });
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(owned));
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is not strict UTF-8 JSON.',
    });
  }
  if (!validate(value))
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger schema validation failed.',
    });
  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(value);
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is outside the canonical JSON domain.',
    });
  }
  if (!equalBytes(canonical, owned))
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is not canonical byte-for-byte.',
    });
  return value as Record<string, unknown>;
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.byteLength === b.byteLength && a.every((byte, index) => byte === b[index]);
}
