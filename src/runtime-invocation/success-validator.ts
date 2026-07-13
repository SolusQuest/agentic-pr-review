import { validateReviewResultV1, type ReviewResultV1 } from '../protocol/review-result.js';
import { validateReviewTraceV1, type ReviewTraceV1 } from '../protocol/review-trace.js';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { RuntimeInvocationError } from './runtime-errors.js';
import type { RuntimeInvocationSuccess } from './runtime-command.js';
import {
  decodeStrictUtf8,
  readSafeOutputBytes,
  sha256Hex,
  statSafeOutputFile,
  type FsSeams,
} from './runtime-files.js';

/**
 * Inputs required to validate the exit-0 output pair and assemble a
 * {@link RuntimeInvocationSuccess}. `resultPath` and `tracePath` are the exact
 * paths the orchestrator passed to the runtime CLI; the validator does not
 * reconstruct filenames from an invocation-directory base.
 */
export interface SuccessValidationInput {
  resultPath: string;
  tracePath: string;
  input: ReviewInputV1;
  inputSha256: string;
  seams: FsSeams;
}

/**
 * Perform the exit-0 result and trace validation and assemble the success value.
 *
 * Validation order (first failure wins):
 * 1. `result.json` stat (safe: not a symlink, regular file, within cap).
 * 2. `trace.json` stat.
 * 3. `result.json` read.
 * 4. `trace.json` read.
 * 5. UTF-8 decode result / trace.
 * 6. JSON parse result / trace.
 * 7. Schema validate result / trace.
 * 8. M2 CLI field rules: `result.inputSha256` present; `result.trace` present;
 *    `result.trace.sha256` present; `result.trace.path` absent;
 *    `trace.resultSha256` absent.
 * 9. Hash invariants: `result.inputSha256` matches adapter-computed
 *    `inputSha256`; `trace.inputSha256` matches; `result.trace.sha256` matches
 *    `sha256(traceBytes)`.
 * 10. Version invariants: `result.runtimeVersion === trace.runtimeVersion`;
 *     `requestedRuntimeVersion` (when non-null) matches `result.runtimeVersion`.
 * 11. Assemble and return {@link RuntimeInvocationSuccess}.
 *
 * `host-io-failed` is propagated by the underlying `statSafeOutputFile` /
 * `readSafeOutputBytes` helpers on filesystem failures; the caller does not
 * need to wrap those.
 */
export async function validateSuccessAndBuildResult(
  args: SuccessValidationInput,
): Promise<RuntimeInvocationSuccess> {
  const { resultPath, tracePath, input, inputSha256, seams } = args;

  await statSafeOutputFile('result', resultPath, seams, { silentOnFailure: false });
  await statSafeOutputFile('trace', tracePath, seams, { silentOnFailure: false });

  const resultBytes = await readSafeOutputBytes('result', resultPath, seams, {
    silentOnFailure: false,
  });
  const traceBytes = await readSafeOutputBytes('trace', tracePath, seams, {
    silentOnFailure: false,
  });

  let resultText: string;
  let traceText: string;
  try {
    resultText = decodeStrictUtf8(resultBytes);
  } catch {
    throw new RuntimeInvocationError({
      kind: 'result-invalid',
      message: 'result.json is not valid UTF-8.',
    });
  }
  try {
    traceText = decodeStrictUtf8(traceBytes);
  } catch {
    throw new RuntimeInvocationError({
      kind: 'trace-invalid',
      message: 'trace.json is not valid UTF-8.',
    });
  }

  let resultParsed: unknown;
  let traceParsed: unknown;
  try {
    resultParsed = JSON.parse(resultText);
  } catch {
    throw new RuntimeInvocationError({
      kind: 'result-invalid',
      message: 'result.json is not valid JSON.',
    });
  }
  try {
    traceParsed = JSON.parse(traceText);
  } catch {
    throw new RuntimeInvocationError({
      kind: 'trace-invalid',
      message: 'trace.json is not valid JSON.',
    });
  }

  const resultValidation = validateReviewResultV1(resultParsed);
  if (!resultValidation.ok) {
    const count = resultValidation.errors?.length ?? 0;
    throw new RuntimeInvocationError({
      kind: 'result-invalid',
      message: `ReviewResultV1 schema validation failed (${count} errors).`,
    });
  }
  const traceValidation = validateReviewTraceV1(traceParsed);
  if (!traceValidation.ok) {
    const count = traceValidation.errors?.length ?? 0;
    throw new RuntimeInvocationError({
      kind: 'trace-invalid',
      message: `ReviewTraceV1 schema validation failed (${count} errors).`,
    });
  }
  const result = resultParsed as ReviewResultV1;
  const trace = traceParsed as ReviewTraceV1;

  if (
    trace.mode !== 'deterministic-fixture' ||
    !trace.fixture ||
    trace.provider !== undefined ||
    trace.usage !== undefined ||
    trace.toolCalls.length !== 0
  ) {
    throw new RuntimeInvocationError({
      kind: 'trace-invalid',
      message: 'deterministic trace failed semantic validation.',
    });
  }

  if (result.inputSha256 === undefined) {
    throw new RuntimeInvocationError({
      kind: 'process-contract-violation',
      message: 'result.inputSha256 must be present on the M2 CLI success path.',
    });
  }
  if (result.trace === undefined) {
    throw new RuntimeInvocationError({
      kind: 'process-contract-violation',
      message: 'result.trace must be present on the M2 CLI success path.',
    });
  }
  if (result.trace.sha256 === undefined) {
    throw new RuntimeInvocationError({
      kind: 'process-contract-violation',
      message: 'result.trace.sha256 must be present on the M2 CLI success path.',
    });
  }
  if (result.trace.path !== undefined) {
    throw new RuntimeInvocationError({
      kind: 'process-contract-violation',
      message: 'result.trace.path must be absent on the M2 CLI success path.',
    });
  }
  if (trace.resultSha256 !== undefined) {
    throw new RuntimeInvocationError({
      kind: 'process-contract-violation',
      message: 'trace.resultSha256 must be absent on the M2 CLI success path.',
    });
  }

  if (result.inputSha256 !== inputSha256) {
    throw new RuntimeInvocationError({
      kind: 'hash-mismatch',
      message: 'result.inputSha256 does not match adapter-computed inputSha256.',
    });
  }
  if (trace.inputSha256 !== inputSha256) {
    throw new RuntimeInvocationError({
      kind: 'hash-mismatch',
      message: 'trace.inputSha256 does not match adapter-computed inputSha256.',
    });
  }
  const traceBytesSha = sha256Hex(traceBytes);
  if (result.trace.sha256 !== traceBytesSha) {
    throw new RuntimeInvocationError({
      kind: 'hash-mismatch',
      message: 'result.trace.sha256 does not match sha256(traceBytes).',
    });
  }
  if (result.runtimeVersion !== trace.runtimeVersion) {
    throw new RuntimeInvocationError({
      kind: 'version-mismatch',
      message: 'result.runtimeVersion and trace.runtimeVersion differ.',
    });
  }
  if (
    input.requestedRuntimeVersion !== null &&
    input.requestedRuntimeVersion !== result.runtimeVersion
  ) {
    throw new RuntimeInvocationError({
      kind: 'version-mismatch',
      message: 'result.runtimeVersion does not match requestedRuntimeVersion.',
    });
  }

  return {
    result,
    trace,
    inputSha256,
    resultBytes,
    traceBytes,
    runtimeVersion: result.runtimeVersion,
  };
}
