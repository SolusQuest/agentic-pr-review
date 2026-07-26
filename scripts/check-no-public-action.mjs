import { lstat, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const retiredAction = './.github/actions/agentic-pr-review';
const violations = [];

await rejectExistingPath('.github/actions/agentic-pr-review', 'retired-action-path');
await rejectExistingPath('scripts/build.mjs', 'retired-bundle-builder');
await rejectExistingPath('scripts/run-local-synthetic.mjs', 'retired-local-runner');
await inspectWorkflows('.github/workflows');

if (violations.length > 0) {
  violations.sort((left, right) => {
    const pathOrder = left.relativePath.localeCompare(right.relativePath);
    return pathOrder || left.kind.localeCompare(right.kind);
  });
  console.error('Retired public Action surface detected:');
  for (const violation of violations) {
    console.error(`- ${violation.relativePath} (${violation.kind})`);
  }
  process.exitCode = 1;
}

async function rejectExistingPath(relativePath, kind) {
  try {
    await lstat(path.join(repoRoot, relativePath));
    violations.push({ relativePath: toPosix(relativePath), kind });
  } catch (error) {
    if (error?.code === 'ENOENT') return;
    violations.push({
      relativePath: toPosix(relativePath),
      kind: 'path-inspection-failed',
    });
  }
}

async function inspectWorkflows(relativeDirectory) {
  const absoluteDirectory = path.join(repoRoot, relativeDirectory);
  let directory;
  try {
    directory = await lstat(absoluteDirectory);
  } catch (error) {
    if (error?.code === 'ENOENT') return;
    violations.push({
      relativePath: toPosix(relativeDirectory),
      kind: 'workflow-root-unreadable',
    });
    return;
  }

  if (!directory.isDirectory() || directory.isSymbolicLink()) {
    violations.push({
      relativePath: toPosix(relativeDirectory),
      kind: 'workflow-root-not-directory',
    });
    return;
  }

  await walkWorkflowDirectory(absoluteDirectory, relativeDirectory);
}

async function walkWorkflowDirectory(absoluteDirectory, relativeDirectory) {
  let entries;
  try {
    entries = await readdir(absoluteDirectory, { withFileTypes: true });
  } catch {
    violations.push({
      relativePath: toPosix(relativeDirectory),
      kind: 'workflow-directory-unreadable',
    });
    return;
  }

  entries.sort((left, right) => left.name.localeCompare(right.name));
  for (const entry of entries) {
    const relativePath = path.join(relativeDirectory, entry.name);
    const absolutePath = path.join(absoluteDirectory, entry.name);
    if (entry.isSymbolicLink()) {
      violations.push({
        relativePath: toPosix(relativePath),
        kind: 'workflow-entry-symlink',
      });
      continue;
    }
    if (entry.isDirectory()) {
      if (isWorkflowFile(entry.name)) {
        violations.push({
          relativePath: toPosix(relativePath),
          kind: 'workflow-file-not-regular',
        });
        continue;
      }
      await walkWorkflowDirectory(absolutePath, relativePath);
      continue;
    }
    if (!isWorkflowFile(entry.name)) continue;
    if (!entry.isFile()) {
      violations.push({
        relativePath: toPosix(relativePath),
        kind: 'workflow-file-not-regular',
      });
      continue;
    }

    let source;
    try {
      source = await readFile(absolutePath, 'utf8');
    } catch {
      violations.push({
        relativePath: toPosix(relativePath),
        kind: 'workflow-file-unreadable',
      });
      continue;
    }
    if (containsRetiredActionInvocation(source)) {
      violations.push({
        relativePath: toPosix(relativePath),
        kind: 'retired-local-action-invocation',
      });
    }
  }
}

function containsRetiredActionInvocation(source) {
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
    if (simple) {
      if (isRetiredActionScalar(simple[1])) return true;
      continue;
    }

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
  const value = Number.isFinite(contentIndentation)
    ? body.map((line) => line.slice(Math.min(line.length, contentIndentation))).join('\n')
    : '';
  return { nextIndex, value };
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
    if (!quote && (character === "'" || character === '"')) {
      quote = character;
      continue;
    }
    if (quote === "'" && character === "'" && line[index + 1] === "'") {
      index += 1;
      continue;
    }
    if (quote === '"' && character === '\\') {
      index += 1;
      continue;
    }
    if (quote && character === quote) {
      quote = '';
      continue;
    }
    if (!quote && character === '#') return line.slice(0, index);
  }
  return line;
}

function isRetiredActionScalar(value, stripQuotes = true) {
  let normalized = value.trim();
  const first = normalized[0];
  const last = normalized.at(-1);
  if (stripQuotes && (first === "'" || first === '"') && last === first) {
    normalized = normalized.slice(1, -1).trim();
  }
  normalized = normalized.replace(/\/+$/u, '');
  return normalized === retiredAction;
}

function isWorkflowFile(name) {
  const extension = path.extname(name).toLowerCase();
  return extension === '.yml' || extension === '.yaml';
}

function toPosix(relativePath) {
  return relativePath.split(path.sep).join('/');
}
