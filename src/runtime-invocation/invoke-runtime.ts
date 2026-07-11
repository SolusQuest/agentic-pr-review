import { runInvocation } from './internal-runner.js';
import type {
  InvokeRuntimeOptions,
  RuntimeCommand,
  RuntimeInvocationSuccess,
} from './runtime-command.js';

export type { InvokeRuntimeOptions, RuntimeCommand, RuntimeInvocationSuccess };
export {
  RuntimeInvocationError,
  type RuntimeContractViolation,
  type RuntimeExitClass,
  type RuntimeInvocationErrorKind,
} from './runtime-errors.js';

/**
 * Materialize protocol files, invoke the deterministic C# runtime CLI, and return
 * validated result and trace data. All failure paths raise {@link RuntimeInvocationError}
 * with a discriminated `kind`. See docs/20_architecture/runtime-cli-process-contract.md
 * and issue #33 for the complete adapter contract.
 *
 * This entrypoint is strictly single-argument. Test-only seams live in
 * `./invoke-runtime.test-support.ts` and are not accessible from this module.
 */
export function invokeRuntime(options: InvokeRuntimeOptions): Promise<RuntimeInvocationSuccess> {
  return runInvocation(options, undefined);
}
