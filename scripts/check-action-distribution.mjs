import { builtinModules } from 'node:module';
import { lstat, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { parseDocument } from 'yaml';

import {
  ACTION_BUNDLE_RELATIVE_PATH,
  WRAPPER_BUILD_DISCRIMINATOR,
  generateActionBundle,
} from './build-action.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(scriptPath), '..');
const actionRootRelativePath = '.github/actions/agentic-pr-review';
const actionMetadataRelativePath = `${actionRootRelativePath}/action.yml`;
const retiredAction = `./${actionRootRelativePath}`;
const exactInputs = Object.freeze({
  'github-token': { required: false },
  'provider-api-key': { required: false },
  'state-key': { required: false },
  'previous-state-key': { required: false },
  'config-path': { required: false, default: '.github/agentic-pr-review.json' },
  'pr-number': { required: false },
  'state-mode': { required: false, default: 'auto' },
});
const exactRuntimeDependencies = Object.freeze({
  '@actions/artifact': '6.2.1',
  '@actions/core': '3.0.1',
  '@actions/github': '9.1.1',
});
const exactEsbuildDeclaration = '^0.28.1';
const exactEsbuildResolution = '0.28.1';
const allowedBuiltins = new Set(
  builtinModules.flatMap((name) => [name, name.startsWith('node:') ? name : `node:${name}`]),
);

export async function inspectActionDistribution(repoRoot = defaultRepoRoot, options = {}) {
  const root = path.resolve(repoRoot);
  const violations = [];
  const report = (relativePath, kind) =>
    violations.push({ relativePath: toPosix(relativePath), kind });

  await inspectActionInventory(root, report);
  await inspectMetadata(root, report);
  await inspectPackageOwnership(root, report);
  if (!options.testOnlySkipGeneratedBundle) await inspectGeneratedBundle(root, report);
  await rejectExistingPath(root, 'action.yml', 'retired-root-action-alias', report);
  await rejectExistingPath(root, 'scripts/build.mjs', 'retired-bundle-builder', report);
  await rejectExistingPath(root, 'scripts/run-local-synthetic.mjs', 'retired-local-runner', report);
  await inspectWorkflows(root, '.github/workflows', report);

  return violations.sort((left, right) => {
    const pathOrder = left.relativePath.localeCompare(right.relativePath);
    return pathOrder || left.kind.localeCompare(right.kind);
  });
}

async function inspectActionInventory(repoRoot, report) {
  const root = path.join(repoRoot, actionRootRelativePath);
  const discovered = [];
  let stat;
  try {
    stat = await lstat(root);
  } catch (error) {
    report(
      actionRootRelativePath,
      error?.code === 'ENOENT' ? 'action-root-missing' : 'action-root-unreadable',
    );
    return;
  }
  if (!stat.isDirectory() || stat.isSymbolicLink()) {
    report(actionRootRelativePath, 'action-root-not-directory');
    return;
  }
  await walkActionDirectory(root, '', discovered, report);
  const expected = ['action.yml', 'dist/index.js'];
  for (const relativePath of expected) {
    if (!discovered.includes(relativePath)) {
      report(`${actionRootRelativePath}/${relativePath}`, 'action-file-missing');
    }
  }
  for (const relativePath of discovered) {
    if (!expected.includes(relativePath)) {
      report(`${actionRootRelativePath}/${relativePath}`, 'unexpected-action-file');
    }
  }
}

async function walkActionDirectory(absoluteDirectory, relativeDirectory, discovered, report) {
  let entries;
  try {
    entries = await readdir(absoluteDirectory, { withFileTypes: true });
  } catch {
    report(path.join(actionRootRelativePath, relativeDirectory), 'action-directory-unreadable');
    return;
  }
  entries.sort((left, right) => left.name.localeCompare(right.name));
  for (const entry of entries) {
    const relativePath = path.join(relativeDirectory, entry.name);
    const reportedPath = path.join(actionRootRelativePath, relativePath);
    if (entry.isSymbolicLink()) {
      report(reportedPath, 'action-entry-symlink');
    } else if (entry.isDirectory()) {
      await walkActionDirectory(
        path.join(absoluteDirectory, entry.name),
        relativePath,
        discovered,
        report,
      );
    } else if (entry.isFile()) {
      discovered.push(toPosix(relativePath));
    } else {
      report(reportedPath, 'action-entry-not-regular');
    }
  }
}

async function inspectMetadata(repoRoot, report) {
  const source = await readRegularFile(repoRoot, actionMetadataRelativePath, report);
  if (source === undefined) return;
  let metadata;
  try {
    const document = parseDocument(source, { uniqueKeys: true });
    if (document.errors.length > 0 || document.warnings.length > 0) {
      report(actionMetadataRelativePath, 'action-metadata-yaml-invalid');
      return;
    }
    metadata = document.toJS({ maxAliasCount: 0 });
  } catch {
    report(actionMetadataRelativePath, 'action-metadata-yaml-invalid');
    return;
  }
  if (!isRecord(metadata) || !sameKeys(metadata, ['name', 'description', 'inputs', 'runs'])) {
    report(actionMetadataRelativePath, 'action-metadata-shape-invalid');
    return;
  }
  if (!boundedDescription(metadata.name, 128) || !boundedDescription(metadata.description, 256)) {
    report(actionMetadataRelativePath, 'action-metadata-description-invalid');
  }
  if (!isRecord(metadata.inputs) || !sameKeys(metadata.inputs, Object.keys(exactInputs))) {
    report(actionMetadataRelativePath, 'action-input-set-invalid');
  } else {
    for (const [name, expected] of Object.entries(exactInputs)) {
      const actual = metadata.inputs[name];
      const expectedKeys = ['description', ...Object.keys(expected)];
      if (
        !isRecord(actual) ||
        !sameKeys(actual, expectedKeys) ||
        !boundedDescription(actual.description, 384) ||
        Object.entries(expected).some(([key, value]) => actual[key] !== value)
      ) {
        report(actionMetadataRelativePath, `action-input-${name}-invalid`);
      }
    }
  }
  if (
    !isRecord(metadata.runs) ||
    !sameKeys(metadata.runs, ['using', 'main']) ||
    metadata.runs.using !== 'node24' ||
    metadata.runs.main !== 'dist/index.js'
  ) {
    report(actionMetadataRelativePath, 'action-runtime-invalid');
  }
}

async function inspectPackageOwnership(repoRoot, report) {
  const packageJson = await readJson(repoRoot, 'package.json', report);
  const packageLock = await readJson(repoRoot, 'package-lock.json', report);
  const installedEsbuild = await readJson(repoRoot, 'node_modules/esbuild/package.json', report);
  if (packageJson) {
    if (packageJson.type !== 'module') {
      report('package.json', 'action-package-module-scope-invalid');
    }
    if (!sameStringRecord(packageJson.dependencies, exactRuntimeDependencies)) {
      report('package.json', 'action-runtime-dependencies-invalid');
    }
    if (
      !isRecord(packageJson.devDependencies) ||
      packageJson.devDependencies.esbuild !== exactEsbuildDeclaration
    ) {
      report('package.json', 'action-build-dependency-invalid');
    }
    if (
      !isRecord(packageJson.scripts) ||
      packageJson.scripts['build:action'] !== 'node scripts/build-action.mjs' ||
      packageJson.scripts['dist:check'] !== 'node scripts/check-action-distribution.mjs'
    ) {
      report('package.json', 'action-package-scripts-invalid');
    }
  }
  if (packageLock) {
    const root = isRecord(packageLock.packages) ? packageLock.packages[''] : undefined;
    const esbuild = isRecord(packageLock.packages)
      ? packageLock.packages['node_modules/esbuild']
      : undefined;
    if (!isRecord(root) || !sameStringRecord(root.dependencies, exactRuntimeDependencies)) {
      report('package-lock.json', 'action-lock-runtime-dependencies-invalid');
    }
    if (
      !isRecord(root) ||
      !isRecord(root.devDependencies) ||
      root.devDependencies.esbuild !== exactEsbuildDeclaration
    ) {
      report('package-lock.json', 'action-lock-build-dependency-invalid');
    }
    if (
      !isRecord(esbuild) ||
      esbuild.version !== exactEsbuildResolution ||
      typeof esbuild.integrity !== 'string' ||
      esbuild.integrity.length === 0
    ) {
      report('package-lock.json', 'action-lock-build-resolution-invalid');
    }
  }
  if (!installedEsbuild || installedEsbuild.version !== exactEsbuildResolution) {
    report('node_modules/esbuild/package.json', 'installed-action-builder-invalid');
  }
}

async function inspectGeneratedBundle(repoRoot, report) {
  const checked = await readRegularFile(repoRoot, ACTION_BUNDLE_RELATIVE_PATH, report);
  let generated;
  try {
    generated = await generateActionBundle(repoRoot);
  } catch {
    report(ACTION_BUNDLE_RELATIVE_PATH, 'action-bundle-generation-failed');
    return;
  }
  if (checked !== undefined && !Buffer.from(checked).equals(generated.bytes)) {
    report(ACTION_BUNDLE_RELATIVE_PATH, 'action-bundle-not-reproducible');
  }
  if (!generated.bytes.includes(Buffer.from(WRAPPER_BUILD_DISCRIMINATOR, 'utf8'))) {
    report(ACTION_BUNDLE_RELATIVE_PATH, 'action-build-discriminator-missing');
  }
  inspectBundleInputs(generated.metafile, report);
  inspectBundleImports(generated.metafile, report);
}

function inspectBundleInputs(metafile, report) {
  const inputs = Object.keys(metafile.inputs).map(toPosix);
  const runtimePackages = new Set();
  for (const source of inputs) {
    if (source === 'scripts/action-entrypoint.generated.ts') continue;
    if (source === 'action-build-stub:supports-color') continue;
    if (source.startsWith('node_modules/')) {
      for (const name of Object.keys(exactRuntimeDependencies)) {
        if (source.startsWith(`node_modules/${name}/`)) runtimePackages.add(name);
      }
      continue;
    }
    if (source.startsWith('src/action-wrapper/')) {
      if (
        /(?:\.test|\.fixture)\.ts$/u.test(source) ||
        source.endsWith('/synthetic-fixture-server.ts')
      ) {
        report(source, 'test-source-in-action-bundle');
      }
      continue;
    }
    report(source, 'unowned-source-in-action-bundle');
  }
  for (const name of Object.keys(exactRuntimeDependencies)) {
    if (!runtimePackages.has(name)) report('package.json', `runtime-package-${name}-not-bundled`);
  }
}

function inspectBundleImports(metafile, report) {
  for (const [outputPath, output] of Object.entries(metafile.outputs)) {
    for (const imported of output.imports ?? []) {
      if (!imported.external || !allowedBuiltins.has(imported.path)) {
        report(outputPath, `external-action-import-${imported.path}`);
      }
    }
  }
}

async function readJson(repoRoot, relativePath, report) {
  const source = await readRegularFile(repoRoot, relativePath, report);
  if (source === undefined) return undefined;
  try {
    const parsed = JSON.parse(source);
    if (!isRecord(parsed)) throw new Error('json_root_invalid');
    return parsed;
  } catch {
    report(relativePath, 'json-invalid');
    return undefined;
  }
}

async function readRegularFile(repoRoot, relativePath, report) {
  const absolutePath = path.join(repoRoot, relativePath);
  try {
    const stat = await lstat(absolutePath);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      report(relativePath, 'expected-file-not-regular');
      return undefined;
    }
    return await readFile(absolutePath, 'utf8');
  } catch (error) {
    report(
      relativePath,
      error?.code === 'ENOENT' ? 'expected-file-missing' : 'expected-file-unreadable',
    );
    return undefined;
  }
}

async function rejectExistingPath(repoRoot, relativePath, kind, report) {
  try {
    await lstat(path.join(repoRoot, relativePath));
    report(relativePath, kind);
  } catch (error) {
    if (error?.code !== 'ENOENT') report(relativePath, 'path-inspection-failed');
  }
}

async function inspectWorkflows(repoRoot, relativeDirectory, report) {
  const absoluteDirectory = path.join(repoRoot, relativeDirectory);
  let directory;
  try {
    directory = await lstat(absoluteDirectory);
  } catch (error) {
    if (error?.code !== 'ENOENT') report(relativeDirectory, 'workflow-root-unreadable');
    return;
  }
  if (!directory.isDirectory() || directory.isSymbolicLink()) {
    report(relativeDirectory, 'workflow-root-not-directory');
    return;
  }
  await walkWorkflowDirectory(absoluteDirectory, relativeDirectory, report);
}

async function walkWorkflowDirectory(absoluteDirectory, relativeDirectory, report) {
  let entries;
  try {
    entries = await readdir(absoluteDirectory, { withFileTypes: true });
  } catch {
    report(relativeDirectory, 'workflow-directory-unreadable');
    return;
  }
  entries.sort((left, right) => left.name.localeCompare(right.name));
  for (const entry of entries) {
    const relativePath = path.join(relativeDirectory, entry.name);
    const absolutePath = path.join(absoluteDirectory, entry.name);
    if (entry.isSymbolicLink()) {
      report(relativePath, 'workflow-entry-symlink');
    } else if (entry.isDirectory()) {
      if (isWorkflowFile(entry.name)) report(relativePath, 'workflow-file-not-regular');
      else await walkWorkflowDirectory(absolutePath, relativePath, report);
    } else if (isWorkflowFile(entry.name)) {
      if (!entry.isFile()) {
        report(relativePath, 'workflow-file-not-regular');
        continue;
      }
      try {
        if (containsRetiredActionInvocation(await readFile(absolutePath, 'utf8'))) {
          report(relativePath, 'retired-local-action-invocation');
        }
      } catch {
        report(relativePath, 'workflow-file-unreadable');
      }
    }
  }
}

export function containsRetiredActionInvocation(source) {
  const lines = source.split(/\r?\n/u);
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const uncommented = stripYamlComment(line).trim();
    if (!uncommented) continue;
    const blockHeader = uncommented.match(
      /^(?:-\s+)?([A-Za-z0-9_-]+)\s*:\s*([|>])(?:[+-]?[1-9]?|[1-9][+-]?)$/u,
    );
    if (blockHeader) {
      const block = collectBlockScalar(lines, index + 1, mappingKeyIndentation(line));
      index = block.nextIndex - 1;
      if (blockHeader[1] === 'uses' && isRetiredActionScalar(block.value, false)) return true;
      continue;
    }
    const simple = uncommented.match(/^(?:-\s+)?uses\s*:\s*(.*?)\s*$/u);
    if (simple && isRetiredActionScalar(simple[1])) return true;
    if (!/^(?:-\s+)?\{/u.test(uncommented)) continue;
    const flow = uncommented.match(/\buses\s*:\s*([^,}]+)[,\}]/u);
    if (flow && isRetiredActionScalar(flow[1])) return true;
  }
  return false;
}

function collectBlockScalar(lines, startIndex, headerIndentation) {
  const body = [];
  let nextIndex = startIndex;
  while (nextIndex < lines.length) {
    const line = lines[nextIndex];
    if (line.trim() !== '' && indentationOf(line) <= headerIndentation) break;
    body.push(line);
    nextIndex += 1;
  }
  const contentIndentation = body
    .filter((line) => line.trim() !== '')
    .reduce((minimum, line) => Math.min(minimum, indentationOf(line)), Number.POSITIVE_INFINITY);
  return {
    nextIndex,
    value: Number.isFinite(contentIndentation)
      ? body.map((line) => line.slice(Math.min(line.length, contentIndentation))).join('\n')
      : '',
  };
}

function indentationOf(line) {
  return line.match(/^ */u)[0].length;
}

function mappingKeyIndentation(line) {
  let indentation = indentationOf(line);
  if (line[indentation] !== '-' || line[indentation + 1] !== ' ') return indentation;
  indentation += 1;
  while (line[indentation] === ' ') indentation += 1;
  return indentation;
}

function stripYamlComment(line) {
  let quote = '';
  for (let index = 0; index < line.length; index += 1) {
    const character = line[index];
    if (!quote && (character === "'" || character === '"')) quote = character;
    else if (quote === "'" && character === "'" && line[index + 1] === "'") index += 1;
    else if (quote === '"' && character === '\\') index += 1;
    else if (quote && character === quote) quote = '';
    else if (!quote && character === '#') return line.slice(0, index);
  }
  return line;
}

function isRetiredActionScalar(value, stripQuotes = true) {
  let normalized = value.trim();
  const first = normalized[0];
  if (stripQuotes && (first === "'" || first === '"') && normalized.at(-1) === first) {
    normalized = normalized.slice(1, -1).trim();
  }
  return normalized.replace(/\/+$/u, '') === retiredAction;
}

function isWorkflowFile(name) {
  return ['.yml', '.yaml'].includes(path.extname(name).toLowerCase());
}

function boundedDescription(value, maximum) {
  return (
    typeof value === 'string' &&
    value.trim().length > 0 &&
    Buffer.byteLength(value, 'utf8') <= maximum &&
    !/[\r\n\0]/u.test(value)
  );
}

function isRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function sameKeys(value, expected) {
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  return actual.length === wanted.length && actual.every((key, index) => key === wanted[index]);
}

function sameStringRecord(value, expected) {
  return (
    isRecord(value) &&
    sameKeys(value, Object.keys(expected)) &&
    Object.entries(expected).every(([key, expectedValue]) => value[key] === expectedValue)
  );
}

function toPosix(relativePath) {
  return relativePath.split(path.sep).join('/');
}

function requestedRepoRoot() {
  if (process.argv.length === 2) return defaultRepoRoot;
  if (process.argv.length === 4 && process.argv[2] === '--repo-root') return process.argv[3];
  throw new Error('usage: node scripts/check-action-distribution.mjs [--repo-root <path>]');
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const violations = await inspectActionDistribution(requestedRepoRoot());
  if (violations.length > 0) {
    console.error('Action distribution validation failed:');
    for (const violation of violations) {
      console.error(`- ${violation.relativePath} (${violation.kind})`);
    }
    process.exitCode = 1;
  }
}
