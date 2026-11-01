import { readBoundedFile as bounded } from '../release/build-payload.format.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { parsePlan, preparationSummary, canonicalJson, requireValue, LIMITS } from './protocol.mjs';
import { projectEvidence } from './evidence.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export async function main(args) {
  const [command, ...rest] = args;
  requireValue(['validate', 'dry-run', 'observe'].includes(command), 'command_invalid');
  const names = [
    '--plan',
    '--approved-sha256',
    '--pair',
    '--qualification',
    '--checks',
    '--template',
  ];
  if (command === 'observe') names.push('--observations');
  requireValue(rest.length === names.length * 2, 'arguments_invalid');
  const values = {};
  for (let i = 0; i < rest.length; i += 2) {
    requireValue(
      names.includes(rest[i]) && !Object.hasOwn(values, rest[i]) && rest[i + 1],
      'arguments_invalid',
    );
    values[rest[i]] = rest[i + 1];
  }
  const planBytes = await bounded(values['--plan'], LIMITS.plan);
  const templates = Object.fromEntries(
    await Promise.all(
      ['manual', 'automatic'].map(async (name) => [
        name,
        await bounded(join(repo, 'examples/workflows', name + '.yml.template'), 65536),
      ]),
    ),
  );
  const prepared = parsePlan(planBytes, values['--approved-sha256'], {
    pairBytes: await bounded(values['--pair'], 16384),
    qualificationBytes: await bounded(values['--qualification'], 1048576),
    expectedChecksBytes: await bounded(values['--checks'], 32768),
    templateBytes: await bounded(values['--template'], 65536),
    templates,
  });
  requireValue(
    command !== 'dry-run' || prepared.plan.mode === 'synthetic',
    'dry_run_requires_synthetic',
  );
  return command === 'observe'
    ? projectEvidence(await bounded(values['--observations'], LIMITS.observation), prepared)
    : preparationSummary(prepared);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2))
    .then((result) => {
      // Construct the whole bounded safe document before attempting its only sink.
      const text = canonicalJson(result) + '\n';
      requireValue(Buffer.byteLength(text) <= 65536, 'output_too_large');
      process.stdout.write(text, (error) => {
        if (error) process.exitCode = 1;
      });
    })
    .catch(() => {
      process.stderr.write('adoption_refused\n');
      process.exitCode = 1;
    });
  process.stdout.on('error', () => {
    process.exitCode = 1;
  });
  process.stderr.on('error', () => {
    process.exitCode = 1;
  });
}
