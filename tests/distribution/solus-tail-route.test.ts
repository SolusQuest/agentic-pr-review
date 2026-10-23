import { readFile } from 'node:fs/promises';
import { parse } from 'yaml';
import { expect, test } from 'vitest';
const tail = [
  'node tests/distribution/verify-managed-action.mjs',
  'node runtime/tests/SolusStateEnvelopeFixture/verify.mjs',
  'node runtime/tests/SolusProviderAccountingFixture/verify.mjs',
  'node runtime/tests/SolusContextAuthorityFixture/verify.mjs',
  'node runtime/tests/SolusToolAdapterFixture/verify.mjs',
  'node runtime/tests/ActionHostFixture/verify-solus-fresh.mjs',
  'node runtime/tests/SolusFreshStateCommitFixture/verify.mjs',
  'node runtime/tests/ActionHostFixture/verify-solus-restored.mjs',
  'bash runtime/scripts/verify-r5-evaluation.sh managed',
  'sudo apt-get update\nsudo apt-get install -y strace\nbash runtime/scripts/verify-r6-economics.sh managed\n',
  'bash runtime/scripts/verify-r7-capacity.sh managed',
];
function selected(condition: string | undefined, mode: string): boolean {
  if (!condition) return true;
  return condition.split(' || ').some((clause) =>
    clause.split(' && ').every((atom) => {
      if (atom === 'always()' || atom === "steps.a1_setup.outcome == 'success'") return true;
      const match = /^inputs\.mode (==|!=) '([a-z-]+)'$/u.exec(atom);
      if (!match) throw new Error('unreviewed_route_condition');
      return match[1] === '==' ? mode === match[2] : mode !== match[2];
    }),
  );
}
async function workflow() {
  return parse(await readFile('.github/workflows/solus-agent-package-proof.yml', 'utf8'));
}
test('finite tail includes every remaining proof and omits the already qualified original archive, inventory and loader routes', async () => {
  const w = await workflow();
  const steps = w.jobs['install-proof'].steps.filter((s) =>
    selected(s.if, 'generated-action-tail'),
  );
  const commands = steps.filter((s) => s.run).map((s) => s.run);
  expect(commands.filter((s: string) => tail.includes(s))).toEqual(tail);
  expect(commands.join('\n')).not.toMatch(
    /verify-archive\.mjs|build-payload\.test|release-payload\.test|managed-host-process\.test|verify-network\.mjs|FullyQualifiedName~ManagedSelection/u,
  );
  expect(w.jobs['install-proof']['timeout-minutes'] ?? 360).toBe(360);
  expect(w.permissions).toEqual({ contents: 'read', packages: 'read' });
  expect(
    steps.find((s) => s.name === 'Cold-cache actual APR managed construction and pin proof').env
      .PROOF_MODE,
  ).toBe(
    "${{ (inputs.mode == 'generated-action-tail' || inputs.mode == 'composite-action') && 'verify' || inputs.mode }}",
  );
});
test('old verify, collect and network selections retain their separated proof boundaries', async () => {
  const w = await workflow();
  const commands = (mode: string) =>
    w.jobs['install-proof'].steps.filter((s) => selected(s.if, mode)).map((s) => s.run ?? '');
  expect(commands('verify').filter((s: string) => tail.includes(s))).toEqual(tail);
  expect(commands('verify')).toContain('bash runtime/scripts/verify-runtime.sh all');
  expect(commands('verify')).toContain('npm run runtime:integration');
  expect(commands('verify')).not.toContain('sudo apt-get update\nsudo apt-get install -y strace\n');
  for (const mode of ['collect', 'network-prerequisite'])
    expect(commands(mode).filter((s: string) => tail.includes(s))).toEqual([]);
  expect(commands('network-prerequisite').join('\n')).not.toMatch(
    /solus-package-proof|verify-archive|verify-managed-action/u,
  );
  expect(commands('collect').join('\n')).toContain('APRManagedPackage=true');
});

test('finite composite route uses immutable actual Actions and never retains credentials or calls the real artifact service', async () => {
  const w = await workflow();
  const steps = w.jobs['install-proof'].steps.filter((s) => selected(s.if, 'composite-action'));
  expect(
    steps.filter((s) => s.uses?.startsWith('SolusQuest/agentic-pr-review/')).map((s) => s.uses),
  ).toEqual(
    Array(4).fill(
      'SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@e96876af96255e54f1097fe19d4fd51ef23ddb55',
    ),
  );
  expect(steps.some((s) => s.uses?.startsWith('actions/upload-artifact@'))).toBe(false);
  expect(steps.filter((s) => s.run && tail.includes(s.run)).map((s) => s.run)).toEqual([tail[0]]);
  expect(steps.some((s) => s.run === 'node tests/distribution/composite-hosted-verify.mjs')).toBe(
    true,
  );
  expect(w.permissions).toEqual({ contents: 'read', packages: 'read' });
});
