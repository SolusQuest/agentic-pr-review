import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import {
  assertPublicSafeEvidence,
  canonicalJson,
  projectTrustedProofEvidence,
} from './r4-trusted-proof-contract.mjs';

export { assertPublicSafeEvidence, projectTrustedProofEvidence };

function reject(code) {
  throw new Error(`APR_R4_E3_EVIDENCE_INVALID ${code}`);
}

function readCanonicalHostEvidence(pathname) {
  const bytes = fs.readFileSync(pathname);
  if (
    bytes.length === 0 ||
    bytes.length > 1024 * 1024 ||
    bytes.at(-1) !== 0x0a ||
    bytes.includes(0x0d)
  ) {
    reject('input-encoding');
  }
  let value;
  try {
    value = JSON.parse(bytes.toString('utf8'));
  } catch {
    reject('input-json');
  }
  if (canonicalJson(value) !== bytes.toString('utf8')) reject('input-canonical');
  return value;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    if (process.argv.length !== 3) reject('usage');
    const publicEvidence = projectTrustedProofEvidence(readCanonicalHostEvidence(process.argv[2]));
    process.stdout.write(canonicalJson(publicEvidence));
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E3_EVIDENCE_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
