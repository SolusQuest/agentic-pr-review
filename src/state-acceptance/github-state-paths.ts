import { digestId, SHA256_HEX } from './hash.js';
import type { CandidateId, CompetingScope, MarkerId, StateKeyV2 } from './types.js';

const ROOT = 'm4-state/v1';

export function stateKeyDigest(stateKey: StateKeyV2): string {
  return digestId('agentic-pr-review/m4/state-key-path/v1', stateKey);
}

export function competingScopeDigest(scope: CompetingScope): string {
  return digestId('agentic-pr-review/m4/competing-scope-path/v1', scope);
}

export const gitStatePaths = {
  sentinel: `${ROOT}/store.json`,
  candidate: (candidateId: CandidateId) => `${ROOT}/candidates/${candidateId}`,
  candidateFile: (
    candidateId: CandidateId,
    file: 'manifest.json' | 'ledger.json' | 'provider-run-metadata.json',
  ) => `${ROOT}/candidates/${candidateId}/${file}`,
  counter: (stateKey: StateKeyV2) => `${ROOT}/states/${stateKeyDigest(stateKey)}/counter.json`,
  selector: (stateKey: StateKeyV2) =>
    `${ROOT}/states/${stateKeyDigest(stateKey)}/selectors/current.json`,
  registration: (scope: CompetingScope, sequence: string, registrationId: string) =>
    `${ROOT}/states/${stateKeyDigest(scope.stateKey)}/registrations/${competingScopeDigest(scope)}/${sequence}-${registrationId}.json`,
  marker: (markerId: MarkerId) => `${ROOT}/markers/${markerId}.json`,
  receipt: (markerId: MarkerId, runId: string, attempt: number) =>
    `${ROOT}/receipts/${markerId}/${runId}-${attempt}.json`,
  probe: (runId: string, attempt: number) => `${ROOT}/probes/${runId}-${attempt}.json`,
} as const;

const allowed = new RegExp(
  `^${ROOT.replaceAll('/', '\\/')}\\/(?:store\\.json|candidates\\/[a-f0-9]{64}\\/(?:manifest|ledger|provider-run-metadata)\\.json|states\\/[a-f0-9]{64}\\/(?:counter\\.json|selectors\\/current\\.json|registrations\\/[a-f0-9]{64}\\/[1-9][0-9]*-[a-f0-9]{64}\\.json)|markers\\/[a-f0-9]{64}\\.json|receipts\\/[a-f0-9]{64}\\/[1-9][0-9]*-[1-9][0-9]*\\.json|probes\\/[1-9][0-9]*-[1-9][0-9]*\\.json)$`,
  'u',
);

export function isAllowedGitStatePath(path: string): boolean {
  return allowed.test(path);
}

export function isStateDigest(value: string): boolean {
  return SHA256_HEX.test(value);
}
