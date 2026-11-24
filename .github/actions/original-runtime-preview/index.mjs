import { fileURLToPath } from 'node:url';

// Node actions start in GITHUB_WORKSPACE. Match the former control checkout cwd
// without forwarding any caller-supplied arguments or copying the runner environment.
process.chdir(fileURLToPath(new URL('../../../', import.meta.url)));
const driver = new URL('../../../tests/distribution/verify-managed-action.mjs', import.meta.url);
process.argv.splice(
  1,
  process.argv.length - 1,
  fileURLToPath(driver),
  '--test-only-original-runtime-preview',
);
await import(driver.href);
