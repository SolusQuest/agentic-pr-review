import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import { outputs } from '../../scripts/release/prepare-candidate.mjs';

const roots: string[] = [];
async function outputFile() {
  const root = await mkdtemp(join(tmpdir(), 'apr-preparation-outputs-'));
  roots.push(root);
  return join(root, 'GITHUB_OUTPUT');
}
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe('preparation production GITHUB_OUTPUT writer', () => {
  test.each([
    {
      path: 'build',
      values: {
        archive_name: 'agentic-pr-review-v0.2.0-internal.1-linux-x64.tar.gz',
        archive_sha256: 'a'.repeat(64),
        receipt_sha256: 'b'.repeat(64),
      },
      expected:
        'archive_name=agentic-pr-review-v0.2.0-internal.1-linux-x64.tar.gz\n' +
        `archive_sha256=${'a'.repeat(64)}\nreceipt_sha256=${'b'.repeat(64)}\n`,
    },
    {
      path: 'workflow-record',
      values: { candidate_sha256: 'c'.repeat(64), map_sha256: 'd'.repeat(64) },
      expected: `candidate_sha256=${'c'.repeat(64)}\nmap_sha256=${'d'.repeat(64)}\n`,
    },
  ])('writes every $path output consumed by release.yml', async ({ values, expected }) => {
    const file = await outputFile();
    await writeFile(file, 'previous=value\n');
    await outputs(values, { GITHUB_OUTPUT: file });
    expect(await readFile(file, 'utf8')).toBe('previous=value\n' + expected);
  });

  test('preserves existing names and the exact value length boundary', async () => {
    const file = await outputFile();
    await outputs(
      { name: 'a'.repeat(160), _private_name: 'v1.2+build-3_4' },
      { GITHUB_OUTPUT: file },
    );
    expect(await readFile(file, 'utf8')).toBe(
      `name=${'a'.repeat(160)}\n_private_name=v1.2+build-3_4\n`,
    );
  });

  const invalidEntries: [string, unknown][] = [
    ['', 'valid'],
    ['1name', 'valid'],
    ['Name', 'valid'],
    ['bad-name', 'valid'],
    ['name\nother', 'valid'],
    ['name=other', 'valid'],
    ['name', ''],
    ['name', 'a'.repeat(161)],
    ['name', 'value\ninjected=output'],
    ['name', 'value\rother'],
    ['name', 'value\0other'],
    ['name', 'value=other'],
    ['name', 'path/value'],
    ['name', 'value other'],
    ['name', '值'],
    ['name', null],
    ['name', 123],
    ['name', true],
    ['name', {}],
    ['name', ['value']],
  ];
  test.each(invalidEntries)(
    'rejects invalid entry %j / %j without a partial write',
    async (key, value) => {
      for (const existing of [false, true]) {
        const file = await outputFile();
        if (existing) await writeFile(file, 'previous=value\n');
        await expect(
          outputs({ valid_name: 'valid', [key]: value }, { GITHUB_OUTPUT: file }),
        ).rejects.toThrow('invalid_output');
        if (existing) expect(await readFile(file, 'utf8')).toBe('previous=value\n');
        else await expect(stat(file)).rejects.toMatchObject({ code: 'ENOENT' });
      }
    },
  );

  test('has no side effect without GITHUB_OUTPUT', async () => {
    await expect(outputs({ name: 'value\ninjected=output' }, {})).resolves.toBeUndefined();
  });
});
