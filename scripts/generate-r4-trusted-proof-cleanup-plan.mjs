import fs from 'node:fs';
import path from 'node:path';

import { canonicalJson, generateCleanupPlan } from './r4-trusted-proof-contract.mjs';

function invalid() {
  throw new Error('APR_R4_E3_CLEANUP_PLAN_INVALID');
}

function readCanonical(pathname) {
  const bytes = fs.readFileSync(pathname);
  if (bytes.length < 2 || bytes.length > 256 * 1024 || bytes.at(-1) !== 0x0a) invalid();
  const value = JSON.parse(bytes.toString('utf8'));
  if (canonicalJson(value) !== bytes.toString('utf8')) invalid();
  return value;
}

try {
  if (
    process.argv.length !== 3 ||
    !path.isAbsolute(process.argv[2]) ||
    !fs.statSync(process.argv[2]).isFile()
  ) {
    invalid();
  }
  const generated = generateCleanupPlan(readCanonical(process.argv[2]));
  process.stdout.write(generated.canonical);
} catch {
  process.stderr.write('APR_R4_E3_CLEANUP_PLAN_INVALID\n');
  process.exitCode = 1;
}
