import path from 'node:path';
import type { InvokeRuntimeOptions } from './runtime-command.js';
import { RuntimeInvocationError } from './runtime-errors.js';

function isPositiveSafeInt(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function isNonNullObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isStringArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every((entry) => typeof entry === 'string');
}

export function isValidAbortSignal(value: unknown): value is AbortSignal {
  if (!isNonNullObject(value)) return false;
  const candidate = value as {
    aborted?: unknown;
    addEventListener?: unknown;
    removeEventListener?: unknown;
  };
  return (
    typeof candidate.aborted === 'boolean' &&
    typeof candidate.addEventListener === 'function' &&
    typeof candidate.removeEventListener === 'function'
  );
}

export function assertOptionsShape(options: InvokeRuntimeOptions): void {
  if (!isNonNullObject(options)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'invokeRuntime requires a non-null options object.',
    });
  }
  const { command, timeoutMs, tempRoot, input, signal } = options;

  if (!isNonNullObject(command)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.command must be a non-null object.',
    });
  }
  if (input === undefined || input === null) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.input is required.',
    });
  }
  if (!isPositiveSafeInt(timeoutMs)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.timeoutMs must be a positive safe integer.',
    });
  }
  if (tempRoot !== undefined) {
    if (typeof tempRoot !== 'string' || tempRoot.length === 0 || !path.isAbsolute(tempRoot)) {
      throw new RuntimeInvocationError({
        kind: 'options-invalid',
        message: 'options.tempRoot must be an absolute host-owned path.',
      });
    }
  }
  if (typeof command.executablePath !== 'string' || command.executablePath.length === 0) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.executablePath must be a non-empty string.',
    });
  }
  if (!path.isAbsolute(command.executablePath)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.executablePath must be an absolute path.',
    });
  }
  if (command.prefixArgs !== undefined && !isStringArray(command.prefixArgs)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.prefixArgs must be an array of strings when provided.',
    });
  }
  if (signal !== undefined && !isValidAbortSignal(signal)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.signal must be a valid AbortSignal.',
    });
  }
}
