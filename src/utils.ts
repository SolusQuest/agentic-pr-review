import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

export function required(value: string | undefined, name: string): string {
  if (!value || value.trim() === '') {
    throw new Error(`${name} is required`);
  }
  return value.trim();
}

export function parseInteger(value: string | undefined, name: string, fallback: number): number {
  if (!value || value.trim() === '') {
    return fallback;
  }
  if (!/^\d+$/.test(value.trim())) {
    throw new Error(`${name} must be an integer`);
  }
  return Number.parseInt(value, 10);
}

export function parseOptionalInteger(value: string | undefined, name: string): number | undefined {
  if (!value || value.trim() === '') {
    return undefined;
  }
  return parseInteger(value, name, 0);
}

export function parsePositiveInteger(
  value: string | undefined,
  name: string,
  fallback: number,
): number {
  const parsed = parseInteger(value, name, fallback);
  if (parsed <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
  return parsed;
}

export function parseOptionalPositiveInteger(
  value: string | undefined,
  name: string,
): number | undefined {
  if (!value || value.trim() === '') {
    return undefined;
  }
  return parsePositiveInteger(value, name, 1);
}

export function parseBoolean(value: string | undefined, name: string, fallback: boolean): boolean {
  if (!value || value.trim() === '') {
    return fallback;
  }
  const normalized = value.trim().toLowerCase();
  if (normalized === 'true') {
    return true;
  }
  if (normalized === 'false') {
    return false;
  }
  throw new Error(`${name} must be true or false`);
}

export function oneOf<T extends string>(
  value: string | undefined,
  name: string,
  values: readonly T[],
): T {
  const normalized = required(value, name);
  if (!values.includes(normalized as T)) {
    throw new Error(`${name} must be one of: ${values.join(', ')}`);
  }
  return normalized as T;
}

export function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

export function sha256(text: string | Buffer): string {
  return createHash('sha256').update(text).digest('hex');
}

export function normalizeRepoRelativePath(value: string): string {
  const normalized = value.trim().replace(/\\/g, '/');
  if (!normalized || normalized.startsWith('/') || /^[A-Za-z][A-Za-z0-9+.-]*:/.test(normalized)) {
    throw new Error('path must be a safe repo-relative path');
  }
  const segments = normalized.split('/').filter((segment) => segment.length > 0 && segment !== '.');
  if (segments.length === 0 || segments.includes('..')) {
    throw new Error('path must be a safe repo-relative path');
  }
  return segments.join('/');
}

export function sanitizeStateKey(value: string): string {
  const sanitized = value
    .trim()
    .replace(/[^A-Za-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80);
  if (!sanitized) {
    throw new Error('state_key resolves to an empty value after sanitization');
  }
  return sanitized;
}

export function assertWithinLimit(text: string, limit: number, label: string): void {
  if (text.length > limit) {
    throw new Error(`${label} is ${text.length} characters, above max ${limit}`);
  }
}

export function truncateText(text: string, limit: number): string {
  if (text.length <= limit) {
    return text;
  }
  return `${text.slice(0, Math.max(0, limit - 80))}\n\n[truncated to ${limit} characters]`;
}

export function resolveWorkspacePath(workspace: string, inputPath: string): string {
  return path.isAbsolute(inputPath) ? inputPath : path.resolve(workspace, inputPath);
}

export async function readTextFile(filePath: string): Promise<string> {
  return await readFile(filePath, 'utf8');
}

export async function ensureDir(dir: string): Promise<void> {
  await mkdir(dir, { recursive: true });
}

export async function writeTextFile(filePath: string, text: string): Promise<void> {
  await ensureDir(path.dirname(filePath));
  await writeFile(filePath, text, 'utf8');
}

export async function writeJsonFile(filePath: string, value: unknown): Promise<void> {
  await writeTextFile(filePath, `${JSON.stringify(value, null, 2)}\n`);
}

export async function readJsonFile<T>(filePath: string): Promise<T> {
  return JSON.parse(await readTextFile(filePath)) as T;
}

export async function walkFiles(root: string): Promise<string[]> {
  const entries = await readdir(root);
  const files: string[] = [];
  for (const entry of entries) {
    const fullPath = path.join(root, entry);
    const info = await stat(fullPath);
    if (info.isDirectory()) {
      files.push(...(await walkFiles(fullPath)));
    } else if (info.isFile()) {
      files.push(fullPath);
    }
  }
  return files;
}

export function relativePosix(root: string, filePath: string): string {
  return path.relative(root, filePath).split(path.sep).join('/');
}
