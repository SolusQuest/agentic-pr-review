import { defaultFsSeams, type FsSeams } from '../runtime-invocation/runtime-files.js';
import {
  validateSuccessAndBuildResult,
  type SuccessValidationInput,
} from '../runtime-invocation/success-validator.js';
import type { RuntimeInvocationSuccess } from '../runtime-invocation/runtime-command.js';

/** Validate exact descriptor-backed snapshots without reopening output paths. */
export function validateSuccessAndBuildResultFromSnapshots(
  args: SuccessValidationInput & {
    resultBytesSnapshot: Uint8Array;
    traceBytesSnapshot: Uint8Array;
  },
): Promise<RuntimeInvocationSuccess> {
  const snapshots = new Map<string, Uint8Array>([
    [args.resultPath, new Uint8Array(args.resultBytesSnapshot)],
    [args.tracePath, new Uint8Array(args.traceBytesSnapshot)],
  ]);
  const snapshotKey = (file: unknown): string => String(file);
  const seams: FsSeams = {
    ...defaultFsSeams,
    lstat: async (file) => {
      const bytes = snapshots.get(snapshotKey(file));
      if (!bytes) throw Object.assign(new Error('missing'), { code: 'ENOENT' });
      return {
        isSymbolicLink: () => false,
        isFile: () => true,
        size: bytes.byteLength,
      } as never;
    },
    readFile: (async (file) => {
      const bytes = snapshots.get(snapshotKey(file));
      if (!bytes) throw Object.assign(new Error('missing'), { code: 'ENOENT' });
      return Buffer.from(bytes);
    }) as FsSeams['readFile'],
  };
  return validateSuccessAndBuildResult({ ...args, seams });
}
