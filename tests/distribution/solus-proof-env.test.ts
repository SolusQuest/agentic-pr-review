import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { describe, expect, it } from 'vitest';

const proof = resolve('scripts/release/solus-package-proof.mjs');
const preload = resolve('tests/distribution/solus-proof-env-preload.mjs');
function exercise(mode: string, scenario: string) {
  const root = mkdtempSync(join(tmpdir(), 'apr-solus-env-'));
  try {
    const audit = join(root, 'audit.json');
    const result = spawnSync(
      process.execPath,
      [
        '--import',
        preload,
        ...(scenario === 'producer'
          ? [
              '--input-type=module',
              '-e',
              "import { admitSource } from './scripts/release/build-payload.mjs'; try { admitSource(process.cwd(), 'a'.repeat(40)); } catch {}",
            ]
          : [proof, mode, join(root, 'proof')]),
      ],
      {
        encoding: 'utf8',
        timeout: 30_000,
        env: {
          ...process.env,
          APR_ENV_SCENARIO: scenario,
          APR_ENV_AUDIT: audit,
          RUNNER_TEMP: root,
          EXPECTED_HEAD: 'a'.repeat(40),
          GITHUB_ACTOR: 'synthetic',
          GITHUB_TOKEN: scenario === 'missing-auth' ? '' : 'synthetic-secret-marker',
          GH_TOKEN: 'synthetic-secret-marker',
          NUGET_AUTH_TOKEN: 'synthetic-secret-marker',
          NuGetPackageSourceCredentials_solus: 'synthetic-secret-marker',
          NuGetPackageSourceCredentials_other: 'synthetic-secret-marker',
        },
      },
    );
    const events: { stage: string; credentials: string[]; token: boolean }[] = JSON.parse(
      readFileSync(audit, 'utf8'),
    );
    return { result, events };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
describe('Solus proof subprocess credential boundary', () => {
  for (const mode of ['collect', 'verify']) {
    it.each(
      mode === 'verify'
        ? [
            'success',
            'missing-auth',
            'early-git',
            'restore',
            'build',
            'unzip',
            'source-commit',
            'late-git',
            'import',
          ]
        : ['success', 'missing-auth', 'early-git', 'restore', 'import'],
    )('%s ' + mode + ' confines credentials to authenticated restore', (scenario) => {
      const { result, events } = exercise(mode, scenario);
      expect(result.status).toBe(scenario === 'success' ? 0 : 1);
      for (const event of events) {
        expect(event.credentials, event.stage).toEqual(
          event.stage === 'authenticated-restore' ? ['NuGetPackageSourceCredentials_solus'] : [],
        );
        expect(event.token, event.stage).toBe(event.stage === 'authenticated-restore');
      }
      const failureStage: Record<string, string> = {
        'missing-auth': 'git:status --porcelain',
        import: 'project-import',
        'early-git': 'git:rev-parse HEAD',
        restore: 'authenticated-restore',
        build: 'build',
        unzip: 'unzip',
        'source-commit': 'unzip',
        'late-git': 'git:status --porcelain',
      };
      if (scenario !== 'success') expect(events.at(-1)?.stage).toBe(failureStage[scenario]);
      if (scenario === 'missing-auth') {
        expect(result.stderr).toContain('APR_SOLUS_AUTH_UNAVAILABLE');
        expect(events.some((e) => e.stage === 'authenticated-restore')).toBe(false);
      }
      if (scenario === 'success') {
        expect(events.some((e) => e.stage === 'authenticated-restore')).toBe(true);
        if (mode === 'verify') {
          expect(events.filter((e) => e.stage === 'unzip')).toHaveLength(10);
          expect(events.filter((e) => e.stage === 'git:status --porcelain')).toHaveLength(2);
          expect(events.some((e) => e.stage === 'startup')).toBe(true);
        }
      }
      expect(result.stdout + result.stderr).not.toContain('synthetic-secret-marker');
    });
  }
  it('keeps direct managed/native producer source admission credential-free', () => {
    const { result, events } = exercise('verify', 'producer');
    expect(result.status).toBe(0);
    expect(events).toHaveLength(4);
    expect(events.every((e) => !e.token && e.credentials.length === 0)).toBe(true);
  });
});
