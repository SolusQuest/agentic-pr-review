import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  computeAdapterId,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
  deriveInteractionId,
  validateIdentity,
  validateModelSnapshot,
  type PredecessorLedgerReference,
} from './index.js';

const FIXTURE_ROOT = path.resolve('protocol/fixtures/prefix-contract/v1');

interface ManifestEntry {
  id: string;
  kind: string;
  file: string;
}

function loadManifest(): ManifestEntry[] {
  const manifest = JSON.parse(readFileSync(path.join(FIXTURE_ROOT, 'manifest.json'), 'utf8'));
  return manifest.vectors as ManifestEntry[];
}

function loadVector(file: string): Record<string, unknown> {
  return JSON.parse(readFileSync(path.join(FIXTURE_ROOT, file), 'utf8'));
}

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

function tagBytes(tag: string): Buffer {
  return Buffer.concat([Buffer.from(tag, 'utf8'), Buffer.from([0])]);
}

function uint32be(value: number): number[] {
  return [(value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff];
}

function encodeIdentity(value: string): number[] {
  const bytes = new TextEncoder().encode(value);
  return [...uint32be(bytes.byteLength), ...bytes];
}

const DIGEST_BY_TAG: Record<string, (envelope: unknown) => { ok: boolean; value?: string }> = {
  'agentic-pr-review/cache-contract/template/v1': computeTemplateId,
  'agentic-pr-review/cache-contract/policy/v1': computePolicyId,
  'agentic-pr-review/cache-contract/tools/v1': computeToolDefinitionId,
  'agentic-pr-review/cache-contract/config/v1': computeCacheConfigId,
  'agentic-pr-review/cache-contract/adapter/v1': computeAdapterId,
};

const DIGEST_BY_KIND: Record<string, (envelope: unknown) => { ok: boolean; value?: string }> = {
  template: computeTemplateId,
  policy: computePolicyId,
  tools: computeToolDefinitionId,
  cacheConfig: computeCacheConfigId,
  adapter: computeAdapterId,
};

describe('prefix-contract golden vectors (TS consumer)', () => {
  it('framing vectors match', () => {
    for (const entry of loadManifest().filter((e) => e.kind === 'framing-vector')) {
      const vector = loadVector(entry.file);
      const input = vector.input as Record<string, unknown>;
      const expected = vector.expected as Record<string, unknown>;

      if (typeof input.tag === 'string') {
        const preimage = Buffer.concat([Buffer.from(input.tag, 'utf8'), Buffer.from([0])]);
        expect(preimage.toString('hex'), entry.id).toBe(expected.preimageHex);
      } else if (typeof input.value === 'string') {
        const framed = Uint8Array.from(encodeIdentity(input.value));
        expect(Buffer.from(framed).toString('hex'), entry.id).toBe(expected.framedHex);
      } else if (Array.isArray(input.values)) {
        const framed = Buffer.concat(
          input.values.map((value) => Buffer.from(encodeIdentity(value as string))),
        );
        expect(framed.toString('hex'), entry.id).toBe(expected.framedHex);
      } else if (typeof input.payloadHex === 'string') {
        const payload = Buffer.from(input.payloadHex as string, 'hex');
        const framed = Buffer.concat([Buffer.from(uint32be(payload.byteLength)), payload]);
        expect(framed.toString('hex'), entry.id).toBe(expected.framedHex);
      } else {
        const preimage = Buffer.concat([
          Buffer.from('agentic-pr-review/logical-prefix/v1'),
          Buffer.from([0]),
          Buffer.from(encodeIdentity(String(input.ledgerSchemaVersion))),
          Buffer.from(encodeIdentity(String(input.prefixContractVersion))),
        ]);
        expect(sha256Hex(preimage), entry.id).toBe(expected.logicalPrefixSha256);
      }
    }

    const left = loadVector('framing/identity-concat-ab-c.json').expected as Record<
      string,
      unknown
    >;
    const right = loadVector('framing/identity-concat-a-bc.json').expected as Record<
      string,
      unknown
    >;
    expect(left.framedHex).not.toBe(right.framedHex);
  });

  it('digest vectors match', () => {
    for (const entry of loadManifest().filter((e) => e.kind === 'digest-vector')) {
      const vector = loadVector(entry.file);
      const tag = vector.tag as string;
      const expected = vector.expected as { preimageHex: string; digestHex: string };
      const result = DIGEST_BY_TAG[tag](vector.envelope);
      expect(result.ok, entry.id).toBe(true);
      if (result.ok) {
        expect(result.value, entry.id).toBe(expected.digestHex);
      }
      const preimage = Buffer.concat([
        Buffer.from(tag, 'utf8'),
        Buffer.from([0]),
        Buffer.from(canonicalJsonBytes(vector.envelope)),
      ]);
      expect(preimage.toString('hex'), entry.id).toBe(expected.preimageHex);
    }
  });

  it('interaction vectors match', () => {
    for (const entry of loadManifest().filter((e) => e.kind === 'interaction-vector')) {
      const vector = loadVector(entry.file);
      const predecessor = vector.predecessor as { bootstrap?: boolean; ledgerSha256?: string };
      const ref: PredecessorLedgerReference =
        predecessor.bootstrap === true
          ? { kind: 'bootstrap' }
          : { kind: 'ledger', sha256Hex: predecessor.ledgerSha256! };
      const result = deriveInteractionId(
        ref,
        vector.consumedInputSha256 as string,
        vector.currentHeadSha as string,
        vector.interactionOrdinal as number,
      );
      expect(result.ok, entry.id).toBe(true);
      if (result.ok) {
        expect(result.value, entry.id).toBe(
          (vector.expected as { interactionId: string }).interactionId,
        );
      }

      const predecessorComponent =
        predecessor.bootstrap === true ? 'bootstrap' : (predecessor.ledgerSha256 as string);
      const preimage = Uint8Array.from([
        ...new TextEncoder().encode('agentic-pr-review/interaction/v1'),
        0,
        ...encodeIdentity(predecessorComponent),
        ...encodeIdentity(vector.consumedInputSha256 as string),
        ...encodeIdentity(vector.currentHeadSha as string),
        ...encodeIdentity(String(vector.interactionOrdinal)),
      ]);
      expect(Buffer.from(preimage).toString('hex'), entry.id).toBe(
        (vector.expected as { preimageHex: string }).preimageHex,
      );
    }
  });

  it('consumes every invalidation vector as a closed oracle', () => {
    const entries = loadManifest();
    const byId = new Map(entries.map((entry) => [entry.id, entry]));
    const bootstrap = loadVector('materialization/bootstrap.json');
    const identities = (bootstrap.input as Record<string, unknown>).expectedIdentities as Record<
      string,
      string
    >;
    const changed = (left: unknown, right: unknown) => left !== right;

    for (const entry of entries.filter((candidate) => candidate.kind === 'invalidation-vector')) {
      const vector = loadVector(entry.file);
      const expected = vector.expected as Record<string, unknown>;
      if (vector.mode === 'materializer') {
        const base = loadVector(byId.get(vector.baseVectorId as string)!.file).expected as Record<
          string,
          unknown
        >;
        const successor = loadVector(byId.get(vector.successorVectorId as string)!.file)
          .expected as Record<string, unknown>;
        expect(changed(base.logicalStreamHex, successor.logicalStreamHex), entry.id).toBe(
          expected.logicalStreamChanged,
        );
        expect(changed(base.providerStreamHex, successor.providerStreamHex), entry.id).toBe(
          expected.providerStreamChanged,
        );
        expect(changed(base.logicalPrefixSha256, successor.logicalPrefixSha256), entry.id).toBe(
          expected.logicalHashChanged,
        );
        expect(changed(base.prefixSha256, successor.prefixSha256), entry.id).toBe(
          expected.prefixHashChanged,
        );
        continue;
      }

      const base = vector.baseInput as Record<string, unknown>;
      const mutated = vector.mutatedInput as Record<string, unknown>;
      const logicalHash = (input: Record<string, unknown>) =>
        sha256Hex(
          Buffer.concat([
            tagBytes('agentic-pr-review/logical-prefix/v1'),
            Buffer.from(encodeIdentity(String(input.ledgerSchemaVersion))),
            Buffer.from(encodeIdentity(String(input.prefixContractVersion))),
            Buffer.from(input.logicalStreamHex as string, 'hex'),
          ]),
        );
      const prefixHash = (input: Record<string, unknown>) =>
        sha256Hex(
          Buffer.concat([
            tagBytes('agentic-pr-review/provider-prefix/v1'),
            Buffer.from(encodeIdentity(String(input.ledgerSchemaVersion))),
            Buffer.from(encodeIdentity(String(input.prefixContractVersion))),
            ...[
              'providerId',
              'modelId',
              'adapterId',
              'templateId',
              'policyId',
              'toolDefinitionId',
              'cacheConfigId',
            ].map((key) => Buffer.from(encodeIdentity(identities[key]))),
            Buffer.from(input.providerStreamHex as string, 'hex'),
          ]),
        );

      const baseLogicalHash = logicalHash(base);
      const mutatedLogicalHash = logicalHash(mutated);
      const basePrefixHash = prefixHash(base);
      const mutatedPrefixHash = prefixHash(mutated);
      expect(baseLogicalHash, entry.id).toBe(expected.baseLogicalPrefixSha256);
      expect(mutatedLogicalHash, entry.id).toBe(expected.mutatedLogicalPrefixSha256);
      expect(basePrefixHash, entry.id).toBe(expected.basePrefixSha256);
      expect(mutatedPrefixHash, entry.id).toBe(expected.mutatedPrefixSha256);
      expect(changed(base.logicalStreamHex, mutated.logicalStreamHex), entry.id).toBe(
        expected.logicalStreamChanged,
      );
      expect(changed(base.providerStreamHex, mutated.providerStreamHex), entry.id).toBe(
        expected.providerStreamChanged,
      );
      expect(changed(baseLogicalHash, mutatedLogicalHash), entry.id).toBe(
        expected.logicalHashChanged,
      );
      expect(changed(basePrefixHash, mutatedPrefixHash), entry.id).toBe(expected.prefixHashChanged);
    }
  });

  it('invalid vectors match (TS-applicable targets)', () => {
    for (const entry of loadManifest().filter((e) => e.kind === 'invalid-vector')) {
      const vector = loadVector(entry.file);
      const target = vector.target as string;
      const input = vector.input as Record<string, unknown>;
      const expected = vector.expected as Record<string, unknown>;

      const expectFailure = (result: {
        ok: boolean;
        errors?: readonly { code: string; path?: string }[];
      }) => {
        expect(result.ok, entry.id).toBe(false);
        if (!result.ok) {
          expect(result.errors![0].code, entry.id).toBe(expected.typescriptCode);
          if (typeof expected.path === 'string') {
            expect(result.errors![0].path, entry.id).toBe(expected.path);
          }
        }
      };

      if (target === 'identity') {
        expectFailure(validateIdentity(input.value));
      } else if (target === 'model-snapshot') {
        expectFailure(validateModelSnapshot(input.value));
      } else if (target === 'interaction-id') {
        const predecessor = input.predecessor as { bootstrap?: boolean; ledgerSha256?: string };
        const ref: PredecessorLedgerReference =
          predecessor.bootstrap === true
            ? { kind: 'bootstrap' }
            : { kind: 'ledger', sha256Hex: predecessor.ledgerSha256! };
        expectFailure(
          deriveInteractionId(
            ref,
            input.consumedInputSha256 as string,
            input.currentHeadSha as string,
            input.interactionOrdinal as number,
          ),
        );
      } else if (target === 'canonical-json') {
        // Skip C#-only duplicate-property vectors (inexpressible in TS).
        if (typeof expected.typescriptCode !== 'string') {
          continue;
        }
        const envelope =
          typeof input.envelopeJson === 'string' ? JSON.parse(input.envelopeJson) : input.envelope;
        expectFailure(DIGEST_BY_KIND[input.envelopeKind as string](envelope));
      } else if (target.endsWith('-id')) {
        // Skip C#-only vectors (e.g. duplicate properties are inexpressible in TS).
        if (typeof expected.typescriptCode !== 'string') {
          continue;
        }
        const kindByTarget: Record<string, string> = {
          'template-id': 'template',
          'policy-id': 'policy',
          'tools-id': 'tools',
          'config-id': 'cacheConfig',
          'adapter-id': 'adapter',
        };
        expectFailure(DIGEST_BY_KIND[kindByTarget[target]](input.envelope));
      }
      // materialize / stream-guard / length-guard are C#-only targets.
    }
  });
});
