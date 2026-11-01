import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import {
  generateActionBundle,
  generatePreparationBundle,
  generateSdkUploadBundle,
} from '../../scripts/build-action.mjs';
import { canonicalJson } from '../../scripts/release/build-payload.format.mjs';

const root = '.github/actions/agentic-pr-review';
test('binds the independently approved original canonical map and payload identity', () => {
  const bytes = readFileSync(`${root}/payload-map.json`);
  expect(createHash('sha256').update(bytes).digest('hex')).toBe(
    '7a83ac625d4d85140a5e9ed37c9d7704115e1b1784a397b8dbe77c11806a2edc',
  );
  const map = JSON.parse(bytes.toString());
  expect(bytes.toString()).toBe(canonicalJson(map));
  expect(map).toMatchObject({
    repository: 'SolusQuest/agentic-pr-review',
    tag: 'payload-v0.2.0-internal.1',
    builder: {
      workflowCommit: '75a771908983cfb881a3fac4b2c57a69f53f6a60',
      workflowPath: '.github/workflows/release.yml',
    },
    payload: {
      archiveName: 'agentic-pr-review-v0.2.0-internal.1-linux-x64-managed.tar.gz',
      archiveSha256: '51c1ea4a82d9e87610b437ec6cb1282c4700e5157c9dc6bb73ac420840508a4d',
      archiveSize: 1911124,
      identity: {
        sourceCommit: '75a771908983cfb881a3fac4b2c57a69f53f6a60',
        releaseVersion: 'v0.2.0-internal.1',
      },
    },
  });
});
test('reproduces all four complete bundles while preserving historical executable bytes and YAML', async () => {
  const history = (name: string) =>
    execFileSync(
      'git',
      ['--no-replace-objects', 'show', `5207bf8058b3d1301c0a6e805e7f21334832223e:${root}/${name}`],
      { maxBuffer: 9 * 1024 * 1024 },
    );
  const generated = [
    await generateActionBundle(process.cwd()),
    await generateSdkUploadBundle(process.cwd()),
    await generatePreparationBundle(process.cwd()),
    await generatePreparationBundle(process.cwd(), true),
  ];
  const names = ['index.js', 'sdk-upload.js', 'prepare.js', 'cleanup.js'];
  for (let i = 0; i < names.length; i++) {
    const name = `internal/dist/${names[i]}`;
    const checked = readFileSync(`${root}/${name}`);
    expect(checked).toEqual(generated[i].bytes);
    const before = history(name);
    const marker = Buffer.from(
      names[i] === 'sdk-upload.js'
        ? '// SDK source inventory sha256: '
        : '// Action source inventory sha256: ',
    );
    expect(checked.subarray(0, checked.lastIndexOf(marker))).toEqual(
      before.subarray(0, before.lastIndexOf(marker)),
    );
    expect(checked).not.toEqual(before);
  }
  for (const name of ['action.yml', 'internal/action.yml', 'internal/prepare/action.yml'])
    expect(readFileSync(`${root}/${name}`)).toEqual(history(name));
}, 15000);
