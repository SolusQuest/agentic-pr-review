import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import YAML from 'yaml';
import { actionSource, cases } from './composite-proof-source.mjs';
import { inspectCompositeNetwork } from './composite-network-audit.mjs';
const config = JSON.parse(await readFile(process.env.APR_A1_COMPOSITE_CONFIG, 'utf8'));
assert.equal(config.actionSource, actionSource);
assert.equal(config.workflowSource, process.env.EXPECTED_HEAD);
const names = Object.keys(
  YAML.parse(
    await readFile(join(config.repo, '.github/actions/agentic-pr-review/action.yml'), 'utf8'),
  ).outputs,
);
assert.equal(names.length, 14);
const receipts = [];
for (const name of cases) {
  assert.equal(
    process.env['APR_A1_OUTCOME_' + name.toUpperCase().replaceAll('-', '_')],
    name === 'valid' ? 'success' : 'failure',
  );
  const output = JSON.parse(
    process.env['APR_A1_OUTPUT_' + name.toUpperCase().replaceAll('-', '_')],
  );
  assert.ok(Object.keys(output).every((key) => names.includes(key)));
  const directory = join(config.work, name);
  const runner = (await readFile(join(directory, 'runner.jsonl'), 'utf8'))
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line));
  assert.ok(runner.some((row) => row.phase === 'prepare.js'));
  let operations = 0;
  for (const row of runner) {
    assert.equal(row.actionSource, actionSource);
    assert.equal(row.workflowSource, config.workflowSource);
    assert.equal(row.syntheticBeforeNetwork, true);
    assert.equal(row.fixedAssetsMatched, 7);
    assert.equal(row.runtimeTokenPresent, true);
    assert.equal(row.resultsUrlPresent, true);
    const audit = inspectCompositeNetwork(
      await readFile(row.trace, 'utf8'),
      Number(new URL(config.origin).port),
    );
    operations += audit.inetOperations;
  }
  const audit = (await readFile(join(directory, 'audit.jsonl'), 'utf8'))
    .trim()
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line));
  const close = audit.find((row) => row.kind === 'managed-close');
  if (name === 'valid' || name === 'invalid-input') {
    assert.ok(close?.completion);
    for (const key of names) assert.equal(output[key] ?? '', close.completion.outputs[key] ?? '');
    assert.equal(
      output.status,
      name === 'valid' ? 'skipped_untrusted_event' : 'configuration_invalid',
    );
    assert.equal((await readdir(join(directory, 'runner-temp'))).length, 0);
  } else if (name === 'unbound') {
    assert.equal(close, undefined);
    assert.equal(
      audit.some((row) => row.kind === 'spawn'),
      false,
    );
    for (const key of names) assert.equal(output[key] ?? '', '');
  } else {
    assert.equal(close, undefined);
    assert.equal(
      audit.some((row) => row.kind === 'spawn'),
      false,
    );
    assert.equal(output.status, 'failed');
    assert.equal(output['usage-completeness'], 'unavailable');
    for (const key of names.filter(
      (key) =>
        ![
          'status',
          'termination-reason',
          'usage-completeness',
          'attempt-accounting-completeness',
        ].includes(key),
    ))
      assert.equal(output[key] ?? '', '');
  }
  if (name === 'valid') {
    assert.ok(runner.some((row) => row.phase === 'prepare.js' && row.originalMapObserved));
    assert.ok(audit.some((row) => row.kind === 'sdk-spawn'));
    assert.ok(
      audit.some((row) => row.kind === 'sdk-close' && row.code === 0 && row.signal === null),
    );
  }
  receipts.push({
    case: name,
    status: output.status ?? '',
    inetOperations: operations,
    externalServiceDestinations: 0,
    post: 'pending-job-post',
  });
}
const stopped = await fetch(config.origin + '/control/stop', { method: 'POST' });
assert.equal(stopped.status, 200);
const service = JSON.parse(await readFile(join(config.work, 'service-result.json'), 'utf8'));
assert.deepEqual(service.errors, []);
assert.equal(service.activeRequests, 0);
assert.ok(service.controlledCalls.some((route) => route.endsWith('/CreateArtifact')));
assert.ok(service.controlledCalls.some((route) => route.endsWith('/FinalizeArtifact')));
console.log(
  'A1_COMPOSITE_HOSTED ' +
    JSON.stringify({
      workflowSource: config.workflowSource,
      actionSource,
      originalArchiveSha256: config.map.payload.archiveSha256,
      syntheticMap: true,
      checkedSourceMapUnbound: true,
      fixedAssetsSha256: config.assetSha256,
      proofOnlyDiff: config.proofOnly,
      cases: receipts,
    }),
);
