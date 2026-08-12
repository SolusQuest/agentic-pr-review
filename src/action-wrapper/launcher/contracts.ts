import type { ActionHostInputsDocument } from './inputs.js';
import { parseStrictJson } from './strict-json.js';
import {
  boundedSecret,
  boundedStructure,
  buildDiscriminator,
  canonicalPositiveDecimal,
  exactRecord,
  fail,
  lowerHex,
} from './validation.js';

export const H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES = 159_542;
export const H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES = 16 * 1024;

const LAUNCH_KEYS = Object.freeze([
  'inputs',
  'event_json_path',
  'event_json_sha256',
  'repository_name',
  'repository_id',
  'run_id',
  'run_attempt',
  'workflow_path',
  'workflow_ref',
  'workflow_sha',
  'action_source_sha',
  'payload_sha256',
  'build_discriminator',
  'cancellation',
  'artifact_bridge_endpoint',
]);

const INPUT_KEYS = Object.freeze([
  'github_token',
  'provider_api_key',
  'state_key',
  'previous_state_key',
  'config_path',
  'pr_number',
  'state_mode',
]);

export interface ActionRuntimeFacts {
  readonly eventJsonPath: string;
  readonly repositoryName: string;
  readonly repositoryId: string;
  readonly runId: string;
  readonly runAttempt: string;
  readonly workflowPath: string;
  readonly workflowRef: string;
  readonly workflowSha: string;
}

export interface PreparedPayloadIdentity {
  readonly actionSourceSha: string;
  readonly payloadSha256: string;
  readonly buildDiscriminator: string;
}

export interface ActionHostLaunchDocument {
  readonly inputs: ActionHostInputsDocument;
  readonly event_json_path: string;
  readonly event_json_sha256: string;
  readonly repository_name: string;
  readonly repository_id: string;
  readonly run_id: string;
  readonly run_attempt: string;
  readonly workflow_path: string;
  readonly workflow_ref: string;
  readonly workflow_sha: string;
  readonly action_source_sha: string;
  readonly payload_sha256: string;
  readonly build_discriminator: string;
  readonly cancellation: 'active' | 'requested';
  readonly artifact_bridge_endpoint: string;
}

export function parseProductionWorkflowRef(
  repositoryName: string,
  workflowRef: string,
): { readonly workflowPath: string; readonly workflowRef: string } {
  const prefix = `${repositoryName}/`;
  const separator = workflowRef.lastIndexOf('@');
  if (!workflowRef.startsWith(prefix) || separator <= prefix.length) {
    fail('wrapper_runtime_facts_invalid');
  }
  const workflowPath = workflowRef.slice(prefix.length, separator);
  const ref = workflowRef.slice(separator + 1);
  if (
    !boundedStructure(workflowPath, 1024) ||
    !boundedStructure(ref, 1024) ||
    !ref.startsWith('refs/')
  ) {
    fail('wrapper_runtime_facts_invalid');
  }
  return { workflowPath, workflowRef };
}

export function validateRuntimeFacts(facts: ActionRuntimeFacts): void {
  if (
    !boundedStructure(facts.eventJsonPath, 4 * 1024) ||
    !repositoryName(facts.repositoryName) ||
    !canonicalPositiveDecimal(facts.repositoryId, 19, 9_223_372_036_854_775_807n) ||
    !canonicalPositiveDecimal(facts.runId, 19, 9_223_372_036_854_775_807n) ||
    !canonicalPositiveDecimal(facts.runAttempt, 10, 2_147_483_647n) ||
    !boundedStructure(facts.workflowPath, 1024) ||
    !boundedStructure(facts.workflowRef, 1024) ||
    !lowerHex(facts.workflowSha, 40)
  ) {
    fail('wrapper_runtime_facts_invalid');
  }
}

export function buildLaunchDocument(input: {
  readonly inputs: ActionHostInputsDocument;
  readonly runtimeFacts: ActionRuntimeFacts;
  readonly eventJsonSha256: string;
  readonly prepared: PreparedPayloadIdentity;
  readonly artifactBridgeEndpoint: string;
  readonly cancellation: 'active' | 'requested';
}): ActionHostLaunchDocument {
  const document: ActionHostLaunchDocument = {
    inputs: input.inputs,
    event_json_path: input.runtimeFacts.eventJsonPath,
    event_json_sha256: input.eventJsonSha256,
    repository_name: input.runtimeFacts.repositoryName,
    repository_id: input.runtimeFacts.repositoryId,
    run_id: input.runtimeFacts.runId,
    run_attempt: input.runtimeFacts.runAttempt,
    workflow_path: input.runtimeFacts.workflowPath,
    workflow_ref: input.runtimeFacts.workflowRef,
    workflow_sha: input.runtimeFacts.workflowSha,
    action_source_sha: input.prepared.actionSourceSha,
    payload_sha256: input.prepared.payloadSha256,
    build_discriminator: input.prepared.buildDiscriminator,
    cancellation: input.cancellation,
    artifact_bridge_endpoint: input.artifactBridgeEndpoint,
  };
  validateLaunchDocument(document);
  return document;
}

export function serializeLaunchDocument(document: ActionHostLaunchDocument): Buffer {
  validateLaunchDocument(document);
  const bytes = Buffer.from(JSON.stringify(document), 'utf8');
  if (bytes.byteLength > H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES) fail('wrapper_launch_invalid');
  return bytes;
}

export function parseLaunchDocument(bytes: Uint8Array): ActionHostLaunchDocument {
  const parsed = parseStrictJson(bytes, H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES);
  validateLaunchDocument(parsed);
  return parsed;
}

export function validateLaunchDocument(value: unknown): asserts value is ActionHostLaunchDocument {
  const root = exactRecord(value, LAUNCH_KEYS, 'wrapper_launch_invalid');
  const inputs = exactRecord(root.inputs, INPUT_KEYS, 'wrapper_launch_invalid');
  validateInputs(inputs);
  const runtimeFacts: ActionRuntimeFacts = {
    eventJsonPath: root.event_json_path as string,
    repositoryName: root.repository_name as string,
    repositoryId: root.repository_id as string,
    runId: root.run_id as string,
    runAttempt: root.run_attempt as string,
    workflowPath: root.workflow_path as string,
    workflowRef: root.workflow_ref as string,
    workflowSha: root.workflow_sha as string,
  };
  validateRuntimeFacts(runtimeFacts);
  if (
    !lowerHex(root.event_json_sha256, 64) ||
    !lowerHex(root.action_source_sha, 40) ||
    !lowerHex(root.payload_sha256, 64) ||
    !buildDiscriminator(root.build_discriminator) ||
    (root.cancellation !== 'active' && root.cancellation !== 'requested') ||
    !boundedStructure(root.artifact_bridge_endpoint, 2 * 1024)
  ) {
    fail('wrapper_launch_invalid');
  }
}

function validateInputs(inputs: Record<string, unknown>): void {
  for (const name of [
    'github_token',
    'provider_api_key',
    'state_key',
    'previous_state_key',
  ] as const) {
    const value = inputs[name];
    if (value !== null && !boundedSecret(value, 4 * 1024)) fail('wrapper_launch_invalid');
  }
  if (inputs.config_path !== null && !boundedStructure(inputs.config_path, 1024)) {
    fail('wrapper_launch_invalid');
  }
  if (
    inputs.pr_number !== null &&
    !canonicalPositiveDecimal(inputs.pr_number, 19, 9_223_372_036_854_775_807n)
  ) {
    fail('wrapper_launch_invalid');
  }
  if (inputs.state_mode !== 'auto' && inputs.state_mode !== 'reset') {
    fail('wrapper_launch_invalid');
  }
}

function repositoryName(value: unknown): value is string {
  if (!boundedStructure(value, 256)) return false;
  const slash = value.indexOf('/');
  return (
    slash > 0 &&
    slash === value.lastIndexOf('/') &&
    slash < value.length - 1 &&
    /^[A-Za-z0-9._/-]+$/u.test(value)
  );
}
