// Isolated maintained SDK adapter. Parent captures/drains logs and confirms child close.
// The staging write token is never passed to this process.
import { DefaultArtifactClient } from '@actions/artifact';
import { readFile, writeFile } from 'node:fs/promises';
const [configPath, resultPath] = process.argv.slice(2);
let checkpoint = 'config',
  code = 'unknown';
try {
  const c = JSON.parse(await readFile(configPath, 'utf8'));
  const client = new DefaultArtifactClient();
  let result;
  if (c.operation === 'upload') {
    checkpoint = 'transfer';
    const r = await client.uploadArtifact(c.name, [c.file], c.directory, {
      skipArchive: true,
      retentionDays: c.retentionDays,
    });
    result = { id: String(r.id) };
  } else if (c.operation === 'download') {
    checkpoint = 'transfer';
    const r = await client.downloadArtifact(Number(c.id), {
      path: c.directory,
      skipDecompress: c.raw,
      expectedHash: 'sha256:' + c.sha256,
      findBy: {
        token: process.env.GITHUB_TOKEN,
        repositoryOwner: 'SolusQuest',
        repositoryName: 'agentic-pr-review',
        workflowRunId: Number(c.runId),
      },
    });
    checkpoint = 'readback';
    if (r.digestMismatch !== false || r.downloadPath !== c.directory) {
      code = 'sdk_readback_denied';
      throw new Error('sdk_readback_denied');
    }
    result = { downloaded: true };
  } else {
    code = 'sdk_operation_denied';
    throw new Error('sdk_operation_denied');
  }
  checkpoint = 'result';
  await writeFile(resultPath, JSON.stringify(result), { flag: 'wx', mode: 0o600 });
} catch {
  try {
    // Fixed private fields only. SDK exceptions and streams never enter this file.
    await writeFile(resultPath, JSON.stringify({ checkpoint, code }), { flag: 'wx', mode: 0o600 });
  } catch {
    /* An absent result is still an unknown failure, never a retry permit. */
  }
  process.exitCode = 1;
}
