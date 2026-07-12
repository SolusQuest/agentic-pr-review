import { runInvocation, type RuntimeInvocationTestSeams } from './internal-runner.js';
import type { InvokeRuntimeOptions, RuntimeInvocationSuccess } from './runtime-command.js';

export type { RuntimeInvocationTestSeams };

/**
 * Test-only entrypoint. Consumed by `src/runtime-invocation/*.test.ts` (and any
 * future co-located harness) to exercise host-I/O, cleanup, and process seams
 * without touching the production `invokeRuntime` API surface.
 *
 * Do not import this module from action wiring, #34 integration, or any release
 * build path. The action bundle (built from `src/main.ts`) never reaches this file.
 *
 * @internal
 */
export function invokeRuntimeForTests(
  options: InvokeRuntimeOptions,
  seams: RuntimeInvocationTestSeams,
): Promise<RuntimeInvocationSuccess> {
  return runInvocation(options, seams);
}
