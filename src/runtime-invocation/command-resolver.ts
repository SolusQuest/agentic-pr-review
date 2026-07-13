import { lstat, realpath } from 'node:fs/promises';
import path from 'node:path';
import type { RuntimeCommand } from './runtime-command.js';

export interface ResolvedRuntimeCommand {
  command: RuntimeCommand;
}

/** Resolve only workflow-owned command configuration; review data never enters this function. */
export async function resolveTrustedRuntimeCommand(
  env: NodeJS.ProcessEnv,
): Promise<ResolvedRuntimeCommand> {
  const executable = env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE?.trim();
  if (!executable || !path.isAbsolute(executable)) {
    throw new Error('command-unavailable: AGENTIC_REVIEW_RUNTIME_EXECUTABLE must be absolute');
  }
  const workspace = env.GITHUB_WORKSPACE?.trim();
  if (!workspace || !path.isAbsolute(workspace)) {
    throw new Error('command-unavailable: GITHUB_WORKSPACE must be an absolute path');
  }

  const workspaceReal = await resolveRegularPath(workspace, 'GITHUB_WORKSPACE', false, true);
  const executableReal = await resolveRegularPath(executable, 'runtime executable', true, false);
  if (isWithin(executableReal, workspaceReal)) {
    throw new Error('command-unavailable: runtime executable must be outside GITHUB_WORKSPACE');
  }

  const prefixArgs = parsePrefixArgs(env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON);
  const resolvedPrefixArgs: string[] = [];
  for (const arg of prefixArgs) {
    if (!path.isAbsolute(arg)) {
      resolvedPrefixArgs.push(arg);
      continue;
    }
    const argReal = await resolveRegularPath(arg, 'runtime prefix argument', true, false);
    if (isWithin(argReal, workspaceReal)) {
      throw new Error(
        'command-unavailable: absolute runtime prefix path is inside GITHUB_WORKSPACE',
      );
    }
    resolvedPrefixArgs.push(argReal);
  }

  return {
    command: {
      executablePath: executableReal,
      ...(resolvedPrefixArgs.length > 0 ? { prefixArgs: resolvedPrefixArgs } : {}),
    },
  };
}

function parsePrefixArgs(value: string | undefined): string[] {
  if (!value || value.trim() === '') {
    return [];
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error('config-invalid: AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON must be valid JSON');
  }
  if (!Array.isArray(parsed) || parsed.some((item) => typeof item !== 'string')) {
    throw new Error(
      'config-invalid: AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON must be a JSON string array',
    );
  }
  return [...parsed];
}

async function resolveRegularPath(
  candidate: string,
  label: string,
  rejectSymlink: boolean,
  allowDirectory: boolean,
): Promise<string> {
  let info;
  try {
    info = await lstat(candidate);
  } catch {
    throw new Error(`command-unavailable: ${label} does not exist`);
  }
  if (rejectSymlink && info.isSymbolicLink()) {
    throw new Error(`command-unavailable: ${label} must not be a symlink`);
  }
  if (!info.isFile() && !(allowDirectory && info.isDirectory())) {
    throw new Error(`command-unavailable: ${label} is not a regular path`);
  }
  try {
    return await realpath(candidate);
  } catch {
    throw new Error(`command-unavailable: ${label} cannot be realpath-resolved`);
  }
}

function isWithin(candidate: string, root: string): boolean {
  const normalize = (value: string) => {
    const resolved = path.resolve(value).replace(/[\\/]+$/, '');
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
  };
  const child = normalize(candidate);
  const parent = normalize(root);
  if (child === parent) {
    return true;
  }
  const relative = path.relative(parent, child);
  return relative !== '' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}
