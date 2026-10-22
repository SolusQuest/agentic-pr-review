import { H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES } from '../launcher/contracts.js';
import { parseStrictJson } from '../launcher/strict-json.js';
import {
  buildDiscriminator,
  exactRecord,
  fail,
  isWellFormed,
  isWorkflowPresentationText,
  utf8Length,
} from '../launcher/validation.js';
import { ACTION_OUTPUT_NAMES, type ActionOutputName } from './outputs.js';

const ROOT_KEYS = [
  'protocol',
  'build_discriminator',
  'process_exit_code',
  'outputs',
  'summary',
  'annotations',
] as const;
const REQUIRED_OUTPUTS = [
  'status',
  'termination-reason',
  'attempt-accounting-completeness',
  'usage-completeness',
] as const;
export const RESULT_PROTOCOL = 'apr.cli.result.cr2';
export const MAXIMUM_RENDERED_SUMMARY_BYTES = H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES * 6 + 12;

export interface ActionHostAnnotationDocument {
  readonly code: string;
  readonly severity: 'warning' | 'error';
  readonly message: string;
}
export interface ActionHostCompletionDocument {
  readonly protocol: typeof RESULT_PROTOCOL;
  readonly build_discriminator: string;
  readonly process_exit_code: 0 | 1;
  readonly outputs: Readonly<Record<ActionOutputName, string | null>>;
  readonly summary: string;
  readonly annotations: readonly ActionHostAnnotationDocument[];
}
function singleLine(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    (value === '' || isWorkflowPresentationText(value, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES))
  );
}
function plainSummary(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    isWellFormed(value) &&
    utf8Length(value) <= H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES &&
    !/[\u0000-\u0009\u000b-\u001f\u007f-\u009f]/u.test(value) &&
    !value.includes('::') &&
    !/%0[ad]/iu.test(value)
  );
}
export function parseCompletionDocument(
  bytes: Uint8Array,
  expectedBuildDiscriminator: string,
  actualProcessExitCode: number,
): ActionHostCompletionDocument {
  const root = exactRecord(
    parseStrictJson(bytes, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES),
    ROOT_KEYS,
    'wrapper_completion_invalid',
  );
  if (
    root.protocol !== RESULT_PROTOCOL ||
    !buildDiscriminator(root.build_discriminator) ||
    root.build_discriminator !== expectedBuildDiscriminator ||
    (root.process_exit_code !== 0 && root.process_exit_code !== 1) ||
    root.process_exit_code !== actualProcessExitCode ||
    !plainSummary(root.summary) ||
    !Array.isArray(root.annotations) ||
    root.annotations.length > 1
  )
    fail('wrapper_completion_invalid');
  const outputs = exactRecord(root.outputs, ACTION_OUTPUT_NAMES, 'wrapper_completion_invalid');
  for (const value of Object.values(outputs))
    if (value !== null && !singleLine(value)) fail('wrapper_completion_invalid');
  for (const name of REQUIRED_OUTPUTS)
    if (typeof outputs[name] !== 'string') fail('wrapper_completion_invalid');
  const annotations = root.annotations.map((value: unknown) => {
    const annotation = exactRecord(
      value,
      ['code', 'severity', 'message'],
      'wrapper_completion_invalid',
    );
    if (
      !singleLine(annotation.code) ||
      !singleLine(annotation.message) ||
      (annotation.severity !== 'warning' && annotation.severity !== 'error')
    )
      fail('wrapper_completion_invalid');
    return annotation as unknown as ActionHostAnnotationDocument;
  });
  return {
    protocol: RESULT_PROTOCOL,
    build_discriminator: root.build_discriminator,
    process_exit_code: root.process_exit_code,
    outputs: outputs as unknown as ActionHostCompletionDocument['outputs'],
    summary: root.summary,
    annotations,
  };
}
export function renderStepSummary(completion: ActionHostCompletionDocument): string {
  const escaped = completion.summary
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
  const rendered = '<pre>' + escaped + '</pre>\n';
  if (utf8Length(rendered) > MAXIMUM_RENDERED_SUMMARY_BYTES) fail('wrapper_presentation_invalid');
  return rendered;
}
