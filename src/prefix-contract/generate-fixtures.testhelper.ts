/**
 * Test-only TS oracle generator for the prefix-contract golden vectors
 * (issue #50, D12). Implements the full logical/provider materialization to
 * produce the initial golden fixtures; never imported by production code.
 *
 * Run via scripts/regenerate-prefix-contract-fixtures.mjs.
 */

import { createHash } from 'node:crypto';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  validateAdapterEnvelope,
  validateCacheConfigEnvelope,
  validatePolicyEnvelope,
  validateTemplateEnvelope,
  validateToolsEnvelope,
} from './envelopes.js';

// ---------------------------------------------------------------------------
// Shared constants (mirror of the C# primitives; frozen by the design contract)

const TAGS = {
  template: 'agentic-pr-review/cache-contract/template/v1',
  policy: 'agentic-pr-review/cache-contract/policy/v1',
  tools: 'agentic-pr-review/cache-contract/tools/v1',
  config: 'agentic-pr-review/cache-contract/config/v1',
  adapter: 'agentic-pr-review/cache-contract/adapter/v1',
  logicalPrefix: 'agentic-pr-review/logical-prefix/v1',
  providerPrefix: 'agentic-pr-review/provider-prefix/v1',
  interaction: 'agentic-pr-review/interaction/v1',
} as const;

const LEDGER_SCHEMA_VERSION = 1;
const PREFIX_CONTRACT_VERSION = 1;

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

function tagBytes(tag: string): Uint8Array {
  const ascii = new TextEncoder().encode(tag);
  const out = new Uint8Array(ascii.byteLength + 1);
  out.set(ascii);
  out[ascii.byteLength] = 0;
  return out;
}

function concat(...parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((sum, part) => sum + part.byteLength, 0);
  const out = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.byteLength;
  }
  return out;
}

function uint32be(value: number): Uint8Array {
  return Uint8Array.from([
    (value >>> 24) & 0xff,
    (value >>> 16) & 0xff,
    (value >>> 8) & 0xff,
    value & 0xff,
  ]);
}

function encodeIdentity(value: string): Uint8Array {
  const bytes = new TextEncoder().encode(value);
  return concat(uint32be(bytes.byteLength), bytes);
}

function frameSegment(payload: Uint8Array): Uint8Array {
  return concat(uint32be(payload.byteLength), payload);
}

function digestId(tag: string, canonicalBytes: Uint8Array): string {
  return sha256Hex(concat(tagBytes(tag), canonicalBytes));
}

function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString('hex');
}

// ---------------------------------------------------------------------------
// Fixture domain model

interface ExpectedIdentities {
  repository: string;
  headRepository: string;
  pullRequest: number;
  workflowIdentity: string;
  trustedExecutionDomain: string;
  providerId: string;
  modelId: string;
  adapterId: string;
  templateId: string;
  policyId: string;
  toolDefinitionId: string;
  cacheConfigId: string;
}

const ENVELOPES = {
  template: {
    schemaVersion: 1,
    templateVersion: 3,
    definition: { role: 'system', text: 'You are a precise code reviewer.' },
  },
  policy: {
    schemaVersion: 1,
    policyVersion: 2,
    instructions: 'Review the delta carefully.',
    constraints: { maxFindings: 10, tone: 'strict' },
  },
  tools: {
    schemaVersion: 1,
    toolsetVersion: 1,
    definitions: [
      {
        name: 'submit_review',
        description: 'Submit the structured review.',
        inputSchema: {
          type: 'object',
          properties: { summary: { type: 'string' } },
          required: ['summary'],
        },
        policyMetadata: { risk: 'low' },
      },
    ],
  },
  cacheConfig: {
    schemaVersion: 1,
    cacheConfigVersion: 1,
    markerPolicy: 'stable-boundary',
    eligibility: 'min-prefix-1024',
    statelessMode: false,
  },
  adapter: {
    schemaVersion: 1,
    capabilityProfileVersion: 1,
    adapterBuildVersion: '0.0.0-fixture',
  },
} as const;

function computeDigestIds(envelopes: {
  template: unknown;
  policy: unknown;
  tools: unknown;
  cacheConfig: unknown;
  adapter: unknown;
}): { templateId: string; policyId: string; toolDefinitionId: string; cacheConfigId: string; adapterId: string } {
  const template = validateTemplateEnvelope(envelopes.template);
  const policy = validatePolicyEnvelope(envelopes.policy);
  const tools = validateToolsEnvelope(envelopes.tools);
  const cacheConfig = validateCacheConfigEnvelope(envelopes.cacheConfig);
  const adapter = validateAdapterEnvelope(envelopes.adapter);
  if (!template.ok || !policy.ok || !tools.ok || !cacheConfig.ok || !adapter.ok) {
    throw new Error('fixture envelopes must be valid');
  }
  return {
    templateId: digestId(TAGS.template, template.value.canonicalBytes),
    policyId: digestId(TAGS.policy, policy.value.canonicalBytes),
    toolDefinitionId: digestId(TAGS.tools, tools.value.canonicalBytes),
    cacheConfigId: digestId(TAGS.config, cacheConfig.value.canonicalBytes),
    adapterId: digestId(TAGS.adapter, adapter.value.canonicalBytes),
  };
}

function baseIdentities(envelopes = ENVELOPES): ExpectedIdentities {
  const digests = computeDigestIds(envelopes);
  return {
    repository: 'owner/repo',
    headRepository: 'owner/repo',
    pullRequest: 50,
    workflowIdentity: 'ci',
    trustedExecutionDomain: 'trusted',
    providerId: 'provider',
    modelId: 'model-2024-01-01',
    ...digests,
  };
}

const SESSION_EPOCH = 'aaaaaaaaaaaaaaaaaaaaaa';
const LEDGER_EPOCH = 'bbbbbbbbbbbbbbbbbbbbbb';
const PREDECESSOR_LEDGER_EPOCH = 'cccccccccccccccccccccc';

/** #49 cache-contract digest over the seven cache-contract identity fields. */
function cacheContractDigestOf(identities: ExpectedIdentities): string {
  return sha256Hex(
    canonicalJsonBytes({
      adapterId: identities.adapterId,
      cacheConfigId: identities.cacheConfigId,
      modelId: identities.modelId,
      policyId: identities.policyId,
      providerId: identities.providerId,
      templateId: identities.templateId,
      toolDefinitionId: identities.toolDefinitionId,
    }),
  );
}

// ---------------------------------------------------------------------------
// Segments and blocks

type SegmentKind = 'template' | 'policy' | 'tools' | 'review_context' | 'review_outcome';

interface Segment {
  readonly kind: SegmentKind;
  readonly bytes: Uint8Array;
}

function templateSegment(envelope: unknown): Segment {
  const raw = envelope as { templateVersion: number; definition: unknown };
  return {
    kind: 'template',
    bytes: canonicalJsonBytes({
      definition: raw.definition,
      kind: 'template',
      templateVersion: raw.templateVersion,
    }),
  };
}

function policySegment(envelope: unknown): Segment {
  const raw = envelope as { policyVersion: number; instructions: string; constraints: unknown };
  return {
    kind: 'policy',
    bytes: canonicalJsonBytes({
      constraints: raw.constraints,
      instructions: raw.instructions,
      kind: 'policy',
      policyVersion: raw.policyVersion,
    }),
  };
}

function toolsSegment(envelope: unknown): Segment {
  const raw = envelope as { toolsetVersion: number; definitions: unknown };
  return {
    kind: 'tools',
    bytes: canonicalJsonBytes({
      definitions: raw.definitions,
      kind: 'tools',
      toolsetVersion: raw.toolsetVersion,
    }),
  };
}

interface LedgerChangedFile {
  path: string;
  previousPath?: string | null;
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  patch?: { sha256: string; truncated: boolean; maxChars: number } | null;
}

interface LedgerFinding {
  severity: string;
  confidence: string;
  category: string;
  title: string;
  body: string;
  path?: string | null;
  startLine?: number | null;
  endLine?: number | null;
  evidence?: string | null;
  suggestedAction?: string | null;
  inlinePreference?: string | null;
}

interface ContextSource {
  subjectDigest: string;
  reviewedHeadSha: string;
  reviewedBaseSha: string;
  changedFiles: LedgerChangedFile[];
}

function projectChangedFile(file: LedgerChangedFile): Record<string, unknown> {
  const out: Record<string, unknown> = {
    additions: file.additions,
    changes: file.changes,
    deletions: file.deletions,
    path: file.path,
    status: file.status,
  };
  if (file.patch !== null && file.patch !== undefined) {
    out.patch = {
      maxChars: file.patch.maxChars,
      sha256: file.patch.sha256,
      truncated: file.patch.truncated,
    };
  }
  if (file.previousPath !== null && file.previousPath !== undefined) {
    out.previousPath = file.previousPath;
  }
  return out;
}

function projectFinding(finding: LedgerFinding): Record<string, unknown> {
  const out: Record<string, unknown> = {
    body: finding.body,
    category: finding.category,
    confidence: finding.confidence,
    severity: finding.severity,
    title: finding.title,
  };
  if (finding.endLine !== null && finding.endLine !== undefined) {
    out.endLine = finding.endLine;
  }
  if (finding.evidence !== null && finding.evidence !== undefined) {
    out.evidence = finding.evidence;
  }
  if (finding.inlinePreference !== null && finding.inlinePreference !== undefined) {
    out.inlinePreference = finding.inlinePreference;
  }
  if (finding.path !== null && finding.path !== undefined) {
    out.path = finding.path;
  }
  if (finding.startLine !== null && finding.startLine !== undefined) {
    out.startLine = finding.startLine;
  }
  if (finding.suggestedAction !== null && finding.suggestedAction !== undefined) {
    out.suggestedAction = finding.suggestedAction;
  }
  return out;
}

function contextSegment(
  source: ContextSource,
  cacheContractDigest: string,
  interactionOrdinal: number,
): Segment {
  return {
    kind: 'review_context',
    bytes: canonicalJsonBytes({
      cacheContractDigest,
      changedFiles: source.changedFiles.map(projectChangedFile),
      interactionOrdinal,
      kind: 'review_context',
      reviewedBaseSha: source.reviewedBaseSha,
      reviewedHeadSha: source.reviewedHeadSha,
      subjectDigest: source.subjectDigest,
    }),
  };
}

interface OutcomeSource {
  summary: string;
  findings: LedgerFinding[];
  limitations: string[];
}

function outcomeSegment(record: {
  interactionOrdinal: number;
  summary: string;
  findings: LedgerFinding[];
  limitations: string[];
}): Segment {
  return {
    kind: 'review_outcome',
    bytes: canonicalJsonBytes({
      findings: record.findings.map(projectFinding),
      interactionOrdinal: record.interactionOrdinal,
      kind: 'review_outcome',
      limitations: record.limitations,
      summary: record.summary,
    }),
  };
}

const ROLE_BY_KIND: Record<SegmentKind, string> = {
  template: 'system',
  policy: 'system',
  tools: 'system',
  review_context: 'user',
  review_outcome: 'assistant',
};

function mapBlock(segment: Segment): Uint8Array {
  return canonicalJsonBytes({
    content: [{ text: new TextDecoder().decode(segment.bytes), type: 'text' }],
    role: ROLE_BY_KIND[segment.kind],
  });
}

// ---------------------------------------------------------------------------
// Ledger document construction (must be #49-valid)

interface LedgerRecordEntry {
  role: 'review_context' | 'review_outcome';
  interactionId: string;
  interactionOrdinal: number;
  [key: string]: unknown;
}

function ledgerContextRecord(
  source: ContextSource,
  identities: ExpectedIdentities,
  interactionId: string,
  interactionOrdinal: number,
): LedgerRecordEntry {
  return {
    cacheContractDigest: cacheContractDigestOf(identities),
    changedFiles: source.changedFiles,
    interactionId,
    interactionOrdinal,
    reviewedBaseSha: source.reviewedBaseSha,
    reviewedHeadSha: source.reviewedHeadSha,
    role: 'review_context',
    subjectDigest: source.subjectDigest,
  };
}

function ledgerOutcomeRecord(
  source: OutcomeSource,
  interactionId: string,
  interactionOrdinal: number,
): LedgerRecordEntry {
  return {
    findings: source.findings,
    interactionId,
    interactionOrdinal,
    limitations: source.limitations,
    role: 'review_outcome',
    summary: source.summary,
  };
}

function continuationLedger(
  identities: ExpectedIdentities,
  records: LedgerRecordEntry[],
  stateGeneration = 3,
): Record<string, unknown> {
  return {
    header: {
      adapterId: identities.adapterId,
      cacheConfigId: identities.cacheConfigId,
      headRepository: identities.headRepository,
      kind: 'continuation',
      ledgerEpoch: LEDGER_EPOCH,
      modelId: identities.modelId,
      policyId: identities.policyId,
      predecessorLedgerEpoch: PREDECESSOR_LEDGER_EPOCH,
      predecessorLedgerSha256: 'f'.repeat(64),
      predecessorStateGeneration: stateGeneration - 1,
      providerId: identities.providerId,
      pullRequest: identities.pullRequest,
      repository: identities.repository,
      sessionEpoch: SESSION_EPOCH,
      stateGeneration,
      templateId: identities.templateId,
      toolDefinitionId: identities.toolDefinitionId,
      trustedExecutionDomain: identities.trustedExecutionDomain,
      workflowIdentity: identities.workflowIdentity,
    },
    prefixContractVersion: 1,
    records,
    schemaVersion: 1,
  };
}

// ---------------------------------------------------------------------------
// Materialization mirror

interface MaterializeInput {
  history:
    | { kind: 'bootstrap' }
    | { kind: 'continuation'; ledgerHex: string }
    | { kind: 'reset'; ledgerHex: string };
  currentContext: ContextSource;
  interaction: { interactionId: string; interactionOrdinal: number };
  expectedIdentities: ExpectedIdentities;
  sessionEpoch: string;
  envelopes: typeof ENVELOPES;
}

interface MaterializeExpected {
  logicalStreamHex: string;
  providerStreamHex: string;
  logicalPrefixSha256: string;
  prefixSha256: string;
  digests: {
    templateId: string;
    policyId: string;
    toolDefinitionId: string;
    cacheConfigId: string;
    adapterId: string;
  };
  stableBoundary: {
    segmentCount: number;
    logicalStreamBytes: number;
    providerStreamBytes: number;
  };
  dynamicSuffix: { logicalHex: string; providerHex: string };
}

function oracleMaterialize(input: MaterializeInput): MaterializeExpected {
  const identities = input.expectedIdentities;
  const stableSegments: Segment[] = [
    templateSegment(input.envelopes.template),
    policySegment(input.envelopes.policy),
    toolsSegment(input.envelopes.tools),
  ];

  if (input.history.kind === 'continuation') {
    const ledger = JSON.parse(new TextDecoder().decode(Buffer.from(input.history.ledgerHex, 'hex'))) as Record<
      string,
      unknown
    >;
    const records = ledger.records as Array<Record<string, unknown>>;
    for (const record of records) {
      if (record.role === 'review_context') {
        stableSegments.push(
          contextSegment(
            {
              subjectDigest: record.subjectDigest as string,
              reviewedHeadSha: record.reviewedHeadSha as string,
              reviewedBaseSha: record.reviewedBaseSha as string,
              changedFiles: record.changedFiles as LedgerChangedFile[],
            },
            record.cacheContractDigest as string,
            record.interactionOrdinal as number,
          ),
        );
      } else {
        stableSegments.push(
          outcomeSegment({
            interactionOrdinal: record.interactionOrdinal as number,
            summary: record.summary as string,
            findings: record.findings as LedgerFinding[],
            limitations: record.limitations as string[],
          }),
        );
      }
    }
  }

  const dynamicSegments: Segment[] = [
    contextSegment(
      input.currentContext,
      cacheContractDigestOf(identities),
      input.interaction.interactionOrdinal,
    ),
  ];

  const stableLogical = concat(...stableSegments.map((segment) => frameSegment(segment.bytes)));
  const dynamicLogical = concat(...dynamicSegments.map((segment) => frameSegment(segment.bytes)));
  const stableProvider = concat(...stableSegments.map((segment) => frameSegment(mapBlock(segment))));
  const dynamicProvider = concat(...dynamicSegments.map((segment) => frameSegment(mapBlock(segment))));

  const logicalPrefixSha256 = sha256Hex(
    concat(
      tagBytes(TAGS.logicalPrefix),
      encodeIdentity(String(LEDGER_SCHEMA_VERSION)),
      encodeIdentity(String(PREFIX_CONTRACT_VERSION)),
      stableLogical,
    ),
  );

  const prefixSha256 = sha256Hex(
    concat(
      tagBytes(TAGS.providerPrefix),
      encodeIdentity(String(LEDGER_SCHEMA_VERSION)),
      encodeIdentity(String(PREFIX_CONTRACT_VERSION)),
      encodeIdentity(identities.providerId),
      encodeIdentity(identities.modelId),
      encodeIdentity(identities.adapterId),
      encodeIdentity(identities.templateId),
      encodeIdentity(identities.policyId),
      encodeIdentity(identities.toolDefinitionId),
      encodeIdentity(identities.cacheConfigId),
      stableProvider,
    ),
  );

  const digests = computeDigestIds(input.envelopes);
  return {
    logicalStreamHex: hex(stableLogical),
    providerStreamHex: hex(stableProvider),
    logicalPrefixSha256,
    prefixSha256,
    digests,
    stableBoundary: {
      segmentCount: stableSegments.length,
      logicalStreamBytes: stableLogical.byteLength,
      providerStreamBytes: stableProvider.byteLength,
    },
    dynamicSuffix: { logicalHex: hex(dynamicLogical), providerHex: hex(dynamicProvider) },
  };
}

// ---------------------------------------------------------------------------
// Shared fixture inputs

const CONTEXT_ALPHA: ContextSource = {
  subjectDigest: '1'.repeat(64),
  reviewedHeadSha: '0'.repeat(40),
  reviewedBaseSha: '1'.repeat(40),
  changedFiles: [
    {
      path: 'src/index.ts',
      status: 'modified',
      additions: 10,
      deletions: 2,
      changes: 12,
      patch: { sha256: 'a'.repeat(64), truncated: false, maxChars: 20000 },
    },
    {
      path: 'docs/guide.md',
      previousPath: 'docs/old-guide.md',
      status: 'renamed',
      additions: 3,
      deletions: 0,
      changes: 3,
    },
  ],
};

const CONTEXT_BETA: ContextSource = {
  subjectDigest: '2'.repeat(64),
  reviewedHeadSha: '3'.repeat(40),
  reviewedBaseSha: '1'.repeat(40),
  changedFiles: [
    { path: 'src/util.ts', status: 'added', additions: 42, deletions: 0, changes: 42 },
  ],
};

const OUTCOME_ALPHA: OutcomeSource = {
  summary: 'Two issues found.',
  findings: [
    {
      severity: 'high',
      confidence: 'high',
      category: 'correctness',
      title: 'Off-by-one in paging',
      body: 'The pager skips the first row.',
      path: 'src/index.ts',
      startLine: 10,
      endLine: 12,
      evidence: 'page.slice(start)',
      suggestedAction: 'Adjust the index math.',
      inlinePreference: 'preferred',
    },
  ],
  limitations: ['Only static analysis was performed.'],
};

const INTERACTION_ALPHA_ID = 'a1'.repeat(32);
const INTERACTION_BETA_ID = 'b2'.repeat(32);

function continuationLedgerTwoPairsHex(identities: ExpectedIdentities): string {
  return hex(Buffer.from(canonicalJsonBytes(continuationLedger(identities, [
    ledgerContextRecord(CONTEXT_ALPHA, identities, INTERACTION_ALPHA_ID, 0),
    ledgerOutcomeRecord(OUTCOME_ALPHA, INTERACTION_ALPHA_ID, 0),
    ledgerContextRecord(CONTEXT_BETA, identities, INTERACTION_BETA_ID, 1),
    ledgerOutcomeRecord(
      { summary: 'No findings.', findings: [], limitations: [] },
      INTERACTION_BETA_ID,
      1,
    ),
  ]))));
}

// ---------------------------------------------------------------------------
// Vector assembly

interface ManifestEntry {
  id: string;
  kind: string;
  file: string;
}

const entries: ManifestEntry[] = [];
const files = new Map<string, unknown>();

function add(id: string, kind: string, file: string, vector: unknown): void {
  entries.push({ id, kind, file });
  files.set(file, vector);
}

const baseEnvelopes = ENVELOPES;
const identities = baseIdentities();

function buildMaterializationVectors(): void {
  const bootstrapInput: MaterializeInput = {
    history: { kind: 'bootstrap' },
    currentContext: CONTEXT_ALPHA,
    interaction: { interactionId: INTERACTION_ALPHA_ID, interactionOrdinal: 0 },
    expectedIdentities: identities,
    sessionEpoch: SESSION_EPOCH,
    envelopes: baseEnvelopes,
  };
  add('materialization-bootstrap', 'materialization-vector', 'materialization/bootstrap.json', {
    id: 'materialization-bootstrap',
    kind: 'materialization-vector',
    input: bootstrapInput,
    expected: oracleMaterialize(bootstrapInput),
  });

  const continuationInput: MaterializeInput = {
    history: { kind: 'continuation', ledgerHex: continuationLedgerTwoPairsHex(identities) },
    currentContext: CONTEXT_BETA,
    interaction: { interactionId: 'c3'.repeat(32), interactionOrdinal: 2 },
    expectedIdentities: identities,
    sessionEpoch: SESSION_EPOCH,
    envelopes: baseEnvelopes,
  };
  add('materialization-continuation', 'materialization-vector', 'materialization/continuation.json', {
    id: 'materialization-continuation',
    kind: 'materialization-vector',
    input: continuationInput,
    expected: oracleMaterialize(continuationInput),
  });

  const resetInput: MaterializeInput = {
    history: { kind: 'reset', ledgerHex: continuationLedgerTwoPairsHex(identities) },
    currentContext: CONTEXT_ALPHA,
    interaction: { interactionId: 'd4'.repeat(32), interactionOrdinal: 0 },
    expectedIdentities: identities,
    sessionEpoch: SESSION_EPOCH,
    envelopes: baseEnvelopes,
  };
  add('materialization-reset', 'materialization-vector', 'materialization/reset.json', {
    id: 'materialization-reset',
    kind: 'materialization-vector',
    input: resetInput,
    expected: oracleMaterialize(resetInput),
  });
}

function buildDigestVectors(): void {
  const digestCases: Array<[string, string, unknown]> = [
    ['digest-template', TAGS.template, ENVELOPES.template],
    ['digest-policy', TAGS.policy, ENVELOPES.policy],
    ['digest-tools', TAGS.tools, ENVELOPES.tools],
    ['digest-config', TAGS.config, ENVELOPES.cacheConfig],
    ['digest-adapter', TAGS.adapter, ENVELOPES.adapter],
  ];
  for (const [id, tag, envelope] of digestCases) {
    const canonical = canonicalJsonBytes(envelope);
    add(id, 'digest-vector', `digest/${id.replace('digest-', '')}.json`, {
      id,
      kind: 'digest-vector',
      tag,
      envelope,
      expected: { preimageHex: hex(concat(tagBytes(tag), canonical)), digestHex: digestId(tag, canonical) },
    });
  }

  // Empty toolset.
  const emptyTools = { schemaVersion: 1, toolsetVersion: 1, definitions: [] };
  const emptyCanonical = canonicalJsonBytes(emptyTools);
  add('digest-tools-empty', 'digest-vector', 'digest/tools-empty.json', {
    id: 'digest-tools-empty',
    kind: 'digest-vector',
    tag: TAGS.tools,
    envelope: emptyTools,
    expected: {
      preimageHex: hex(concat(tagBytes(TAGS.tools), emptyCanonical)),
      digestHex: digestId(TAGS.tools, emptyCanonical),
    },
  });

  // NUL inside open JSON content is emitted as an RFC 8785 escape.
  const nulTemplate = { schemaVersion: 1, templateVersion: 1, definition: 'line1 line2' };
  const nulCanonical = canonicalJsonBytes(nulTemplate);
  add('digest-template-nul-content', 'digest-vector', 'digest/template-nul-content.json', {
    id: 'digest-template-nul-content',
    kind: 'digest-vector',
    tag: TAGS.template,
    envelope: nulTemplate,
    expected: {
      preimageHex: hex(concat(tagBytes(TAGS.template), nulCanonical)),
      digestHex: digestId(TAGS.template, nulCanonical),
    },
  });

  // Number-domain coverage: -0, mathematical integers, exponent forms.
  const numberPolicy = {
    schemaVersion: 1,
    policyVersion: 1,
    instructions: 'x',
    constraints: { a: 1, b: 1.5, c: -0, d: 1e21, e: 0.000001, f: 1e-7, g: 0.1 },
  };
  const numberCanonical = canonicalJsonBytes(numberPolicy);
  add('digest-policy-number-domain', 'digest-vector', 'digest/policy-number-domain.json', {
    id: 'digest-policy-number-domain',
    kind: 'digest-vector',
    tag: TAGS.policy,
    envelope: numberPolicy,
    expected: {
      preimageHex: hex(concat(tagBytes(TAGS.policy), numberCanonical)),
      digestHex: digestId(TAGS.policy, numberCanonical),
    },
  });
}

function buildFramingVectors(): void {
  for (const [name, tag] of Object.entries(TAGS)) {
    const id = `framing-tag-${name}`;
    add(id, 'framing-vector', `framing/tag-${name}.json`, {
      id,
      kind: 'framing-vector',
      input: { tag },
      expected: { preimageHex: hex(tagBytes(tag)) },
    });
  }

  const identityCases: Array<[string, string]> = [
    ['framing-identity-ascii', 'a'],
    ['framing-identity-multibyte', 'é'],
    ['framing-identity-surrogate-pair', '😀'],
    ['framing-identity-256-bytes', 'x'.repeat(256)],
  ];
  for (const [id, value] of identityCases) {
    add(id, 'framing-vector', `framing/${id.replace('framing-', '')}.json`, {
      id,
      kind: 'framing-vector',
      input: { value },
      expected: { framedHex: hex(encodeIdentity(value)) },
    });
  }

  const payload = canonicalJsonBytes({ kind: 'template' });
  add('framing-frame-segment', 'framing-vector', 'framing/frame-segment.json', {
    id: 'framing-frame-segment',
    kind: 'framing-vector',
    input: { payloadHex: hex(payload) },
    expected: { framedHex: hex(frameSegment(payload)) },
  });

  // Empty stable stream logical hash.
  const emptyLogicalHash = sha256Hex(
    concat(
      tagBytes(TAGS.logicalPrefix),
      encodeIdentity(String(LEDGER_SCHEMA_VERSION)),
      encodeIdentity(String(PREFIX_CONTRACT_VERSION)),
      new Uint8Array(0),
    ),
  );
  add('framing-empty-stream-logical-hash', 'framing-vector', 'framing/empty-stream-logical-hash.json', {
    id: 'framing-empty-stream-logical-hash',
    kind: 'framing-vector',
    input: {
      ledgerSchemaVersion: LEDGER_SCHEMA_VERSION,
      prefixContractVersion: PREFIX_CONTRACT_VERSION,
    },
    expected: { logicalPrefixSha256: emptyLogicalHash },
  });
}

function buildInteractionVectors(): void {
  const consumedInput = 'e5'.repeat(32);
  const headSha = '7'.repeat(40);

  const cases: Array<{
    id: string;
    predecessor: { bootstrap: true } | { ledgerSha256: string };
    ordinal: number;
    head: string;
  }> = [
    { id: 'interaction-bootstrap', predecessor: { bootstrap: true }, ordinal: 0, head: headSha },
    {
      id: 'interaction-reset',
      predecessor: { ledgerSha256: 'f'.repeat(64) },
      ordinal: 0,
      head: headSha,
    },
    { id: 'interaction-continuation', predecessor: { ledgerSha256: 'f'.repeat(64) }, ordinal: 7, head: headSha },
    {
      id: 'interaction-head-sha-64',
      predecessor: { bootstrap: true },
      ordinal: 3,
      head: '9'.repeat(64),
    },
  ];

  for (const testCase of cases) {
    const predecessorComponent =
      'bootstrap' in testCase.predecessor ? 'bootstrap' : testCase.predecessor.ledgerSha256;
    const preimage = concat(
      tagBytes(TAGS.interaction),
      encodeIdentity(predecessorComponent),
      encodeIdentity(consumedInput),
      encodeIdentity(testCase.head),
      encodeIdentity(String(testCase.ordinal)),
    );
    add(testCase.id, 'interaction-vector', `interaction/${testCase.id.replace('interaction-', '')}.json`, {
      id: testCase.id,
      kind: 'interaction-vector',
      predecessor: testCase.predecessor,
      consumedInputSha256: consumedInput,
      currentHeadSha: testCase.head,
      interactionOrdinal: testCase.ordinal,
      expected: { preimageHex: hex(preimage), interactionId: sha256Hex(preimage) },
    });
  }
}

buildDigestVectors();
buildFramingVectors();
buildInteractionVectors();
buildMaterializationVectors();

// ---------------------------------------------------------------------------
// Write corpus

const repoRoot = process.cwd();
const outDir = path.join(repoRoot, 'protocol', 'fixtures', 'prefix-contract', 'v1');

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

for (const [file, vector] of files) {
  const target = path.join(outDir, file);
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, JSON.stringify(vector, null, 2) + '\n');
}

const manifest = {
  schemaVersion: 1,
  generatedBy: { tool: 'src/prefix-contract/generate-fixtures.testhelper.ts', version: 1 },
  creationCrossCheck: { tool: 'node', version: process.version, checkedAt: '2026-07-17T00:00:00Z' },
  vectors: entries,
};
writeFileSync(path.join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');

console.log(`wrote ${files.size} vectors + manifest to ${outDir}`);
