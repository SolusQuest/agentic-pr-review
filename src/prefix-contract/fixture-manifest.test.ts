import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

/**
 * Closed-contract validation of the prefix-contract fixture manifest
 * (issue #50, D12). The real corpus must pass; synthetic violations must be
 * rejected with a specific rule id.
 */

const FIXTURE_ROOT = path.resolve('protocol/fixtures/prefix-contract/v1');

interface ManifestEntry {
  id: string;
  kind: string;
  file: string;
}

const ALLOWED_MANIFEST_KEYS = new Set([
  'schemaVersion',
  'generatedBy',
  'creationCrossCheck',
  'vectors',
]);
const ALLOWED_ENTRY_KEYS = new Set(['id', 'kind', 'file']);
const HEX = /^[0-9a-f]+$/;

function validateCorpus(root: string): string[] {
  const violations: string[] = [];
  const manifest = JSON.parse(readFileSync(path.join(root, 'manifest.json'), 'utf8')) as Record<
    string,
    unknown
  >;

  for (const key of Object.keys(manifest)) {
    if (!ALLOWED_MANIFEST_KEYS.has(key)) {
      violations.push(`unknown-manifest-field:${key}`);
    }
  }

  if (manifest.schemaVersion !== 1) {
    violations.push('bad-manifest-schemaVersion');
  }
  const generatedBy = manifest.generatedBy as Record<string, unknown> | undefined;
  if (
    generatedBy === undefined ||
    typeof generatedBy.tool !== 'string' ||
    typeof generatedBy.version !== 'number' ||
    Object.keys(generatedBy).some((key) => key !== 'tool' && key !== 'version')
  ) {
    violations.push('bad-metadata:generatedBy');
  }
  const crossCheck = manifest.creationCrossCheck as Record<string, unknown> | undefined;
  if (
    crossCheck === undefined ||
    typeof crossCheck.tool !== 'string' ||
    typeof crossCheck.version !== 'string' ||
    typeof crossCheck.checkedAt !== 'string' ||
    !RFC3339.test(crossCheck.checkedAt) ||
    Object.keys(crossCheck).some(
      (key) => key !== 'tool' && key !== 'version' && key !== 'checkedAt',
    )
  ) {
    violations.push('bad-metadata:creationCrossCheck');
  }

  const entries = manifest.vectors as ManifestEntry[];
  const ids = new Set<string>();
  const files = new Set<string>();
  const byId = new Map<string, ManifestEntry>();

  for (const entry of entries) {
    for (const key of Object.keys(entry)) {
      if (!ALLOWED_ENTRY_KEYS.has(key)) {
        violations.push(`unknown-entry-field:${entry.id}:${key}`);
      }
    }
    if (ids.has(entry.id)) {
      violations.push(`duplicate-id:${entry.id}`);
    }
    if (files.has(entry.file)) {
      violations.push(`duplicate-file:${entry.file}`);
    }
    ids.add(entry.id);
    files.add(entry.file);
    byId.set(entry.id, entry);

    if (
      entry.file.length === 0 ||
      entry.file.includes('\\') ||
      entry.file.includes('..') ||
      entry.file.includes(':') ||
      path.isAbsolute(entry.file)
    ) {
      violations.push(`unsafe-path:${entry.file}`);
    }

    if (!existsQuiet(path.join(root, entry.file))) {
      violations.push(`missing-file:${entry.file}`);
      continue;
    }

    const vector = JSON.parse(readFileSync(path.join(root, entry.file), 'utf8')) as Record<
      string,
      unknown
    >;
    if (vector.id !== entry.id) {
      violations.push(`id-mismatch:${entry.id}`);
    }
    if (vector.kind !== entry.kind) {
      violations.push(`kind-mismatch:${entry.id}`);
    }

    violations.push(...validateVectorShape(entry, vector));

    for (const hexValue of collectHexStrings(vector)) {
      if (hexValue.length % 2 !== 0 || !HEX.test(hexValue)) {
        violations.push(`bad-hex:${entry.id}`);
        break;
      }
    }
  }

  const onDisk = listFiles(root).filter((file) => file !== 'manifest.json');
  for (const file of onDisk) {
    if (!files.has(file)) {
      violations.push(`unlisted-file:${file}`);
    }
  }

  const materializationIds = new Set(
    entries.filter((e) => e.kind === 'materialization-vector').map((e) => e.id),
  );
  for (const entry of entries.filter(
    (e) => e.kind === 'append-vector' || e.kind === 'invalidation-vector',
  )) {
    const vector = existsQuiet(path.join(root, entry.file))
      ? (JSON.parse(readFileSync(path.join(root, entry.file), 'utf8')) as Record<string, unknown>)
      : {};
    for (const refKey of ['baseVectorId', 'successorVectorId']) {
      const referenced = vector[refKey];
      if (typeof referenced !== 'string') {
        continue;
      }
      if (referenced === entry.id) {
        violations.push(`self-reference:${entry.id}`);
      } else if (!byId.has(referenced)) {
        violations.push(`missing-reference:${entry.id}:${referenced}`);
      } else if (!materializationIds.has(referenced)) {
        violations.push(`wrong-reference-kind:${entry.id}:${referenced}`);
      }
    }

    // Materializer-mode invalidation: base/successor inputs may differ only by
    // the named mutation.
    if (
      entry.kind === 'invalidation-vector' &&
      vector.mode === 'materializer' &&
      typeof vector.baseVectorId === 'string' &&
      typeof vector.successorVectorId === 'string' &&
      byId.has(vector.baseVectorId as string) &&
      byId.has(vector.successorVectorId as string)
    ) {
      const baseVector = JSON.parse(
        readFileSync(path.join(root, byId.get(vector.baseVectorId as string)!.file), 'utf8'),
      ) as Record<string, unknown>;
      const successorVector = JSON.parse(
        readFileSync(path.join(root, byId.get(vector.successorVectorId as string)!.file), 'utf8'),
      ) as Record<string, unknown>;
      const diffs = jsonDiffPaths(baseVector.input, successorVector.input);
      violations.push(...validateMutationDiffs(entry.id, vector.mutation, diffs));
    }
  }

  return violations;
}

/** Deep-diff two JSON values; returns the set of differing dotted paths. */
function jsonDiffPaths(a: unknown, b: unknown, prefix = ''): string[] {
  if (typeof a !== 'object' || a === null || typeof b !== 'object' || b === null) {
    return Object.is(a, b) ? [] : [prefix || '$'];
  }
  if (Array.isArray(a) !== Array.isArray(b)) {
    return [prefix || '$'];
  }
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) {
      return [prefix || '$'];
    }
    const out: string[] = [];
    for (let i = 0; i < a.length; i++) {
      out.push(...jsonDiffPaths(a[i], b[i], `${prefix}[${i}]`));
    }
    return out;
  }
  const aObj = a as Record<string, unknown>;
  const bObj = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(aObj), ...Object.keys(bObj)]);
  const out: string[] = [];
  for (const key of keys) {
    if (!(key in aObj) || !(key in bObj)) {
      out.push(prefix === '' ? key : prefix + '.' + key);
      continue;
    }
    out.push(...jsonDiffPaths(aObj[key], bObj[key], prefix === '' ? key : prefix + '.' + key));
  }
  return out;
}

const ENVELOPE_DIGEST_FIELD: Record<string, string> = {
  template: 'templateId',
  policy: 'policyId',
  tools: 'toolDefinitionId',
  cacheConfig: 'cacheConfigId',
  adapter: 'adapterId',
};

function envelopeMutation(diffs: string[], envelopeName: string, digestField: string): boolean {
  const envelopePrefix = `envelopes.${envelopeName}.`;
  const digestPath = `expectedIdentities.${digestField}`;
  return (
    diffs.some(
      (diff) => diff.startsWith(envelopePrefix) && diff !== `${envelopePrefix}schemaVersion`,
    ) &&
    diffs.includes(digestPath) &&
    diffs.every(
      (diff) =>
        (diff.startsWith(envelopePrefix) && diff !== `${envelopePrefix}schemaVersion`) ||
        diff === digestPath,
    )
  );
}

/** Exact per-mutation diff predicates (no prefix wildcards). */
const MUTATION_DIFF_PREDICATES: Record<string, (diffs: string[]) => boolean> = {
  providerId: (d) => d.length === 1 && d[0] === 'expectedIdentities.providerId',
  modelId: (d) => d.length === 1 && d[0] === 'expectedIdentities.modelId',
  'adapter envelope content/version': (d) => envelopeMutation(d, 'adapter', 'adapterId'),
  'cache-config envelope content/version': (d) =>
    envelopeMutation(d, 'cacheConfig', 'cacheConfigId'),
  'template envelope content/version': (d) => envelopeMutation(d, 'template', 'templateId'),
  'policy envelope content/version': (d) => envelopeMutation(d, 'policy', 'policyId'),
  'tools envelope content/version/order': (d) => envelopeMutation(d, 'tools', 'toolDefinitionId'),
  'any envelope schemaVersion': (d) => {
    if (d.length !== 2) {
      return false;
    }
    const envelopeDiff = d.find((diff) =>
      /^envelopes\.(template|policy|tools|cacheConfig|adapter)\.schemaVersion$/.test(diff),
    );
    if (envelopeDiff === undefined) {
      return false;
    }
    const envelopeName = envelopeDiff.split('.')[1];
    return d.some((diff) => diff === `expectedIdentities.${ENVELOPE_DIGEST_FIELD[envelopeName]}`);
  },
  'run/provenance metadata': (d) => d.length === 1 && d[0] === 'interaction.interactionId',
};

function validateMutationDiffs(id: string, mutation: unknown, diffs: string[]): string[] {
  if (typeof mutation !== 'string' || !(mutation in MUTATION_DIFF_PREDICATES)) {
    return [`unknown-mutation:${id}:${String(mutation)}`];
  }
  return MUTATION_DIFF_PREDICATES[mutation](diffs) ? [] : [`mutation-diff:${id}`];
}

const RFC3339 = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;
const SHA256_HEX = /^[0-9a-f]{64}$/;

/** Per-kind closed field-set validation plus semantic SHA-256 fields. */
function validateVectorShape(entry: ManifestEntry, vector: Record<string, unknown>): string[] {
  const violations: string[] = [];
  const allowedKeys = new Set(['id', 'kind']);
  const require = (keys: string[]) => {
    for (const key of keys) {
      allowedKeys.add(key);
      if (!(key in vector)) {
        violations.push(`missing-field:${entry.id}:${key}`);
      }
    }
  };
  const shaField = (container: unknown, key: string) => {
    const value = (container as Record<string, unknown> | undefined)?.[key];
    if (typeof value !== 'string' || !SHA256_HEX.test(value)) {
      violations.push(`bad-sha256:${entry.id}:${key}`);
    }
  };
  const closedObject = (
    container: unknown,
    label: string,
    keys: string[],
    valueKind: 'string' | 'number' | 'boolean',
  ) => {
    if (typeof container !== 'object' || container === null) {
      violations.push(`bad-shape:${entry.id}:${label}`);
      return;
    }
    const record = container as Record<string, unknown>;
    for (const key of keys) {
      if (!(key in record) || typeof record[key] !== valueKind) {
        violations.push(`bad-shape:${entry.id}:${label}.${key}`);
      }
    }
    for (const key of Object.keys(record)) {
      if (!keys.includes(key)) {
        violations.push(`unknown-expected-field:${entry.id}:${label}.${key}`);
      }
    }
  };
  const requireExpected = (expected: unknown, keys: string[]) => {
    if (typeof expected !== 'object' || expected === null) {
      violations.push(`expected-shape:${entry.id}`);
      return;
    }
    for (const key of keys) {
      if (!(key in (expected as Record<string, unknown>))) {
        violations.push(`missing-field:${entry.id}:expected.${key}`);
      }
    }
    for (const key of Object.keys(expected as Record<string, unknown>)) {
      if (!keys.includes(key)) {
        violations.push(`unknown-expected-field:${entry.id}:${key}`);
      }
    }
  };
  const exactObject = (
    container: unknown,
    label: string,
    fields: Record<string, 'string' | 'number' | 'boolean' | 'object' | 'array'>,
    optional: Record<string, 'string' | 'number' | 'boolean' | 'object' | 'array'> = {},
  ): Record<string, unknown> | undefined => {
    if (typeof container !== 'object' || container === null || Array.isArray(container)) {
      violations.push(`bad-shape:${entry.id}:${label}`);
      return undefined;
    }
    const record = container as Record<string, unknown>;
    const matches = (value: unknown, kind: string) =>
      kind === 'array'
        ? Array.isArray(value)
        : kind === 'object'
          ? typeof value === 'object' && value !== null && !Array.isArray(value)
          : typeof value === kind;
    for (const [key, kind] of Object.entries(fields)) {
      if (!(key in record) || !matches(record[key], kind)) {
        violations.push(`bad-shape:${entry.id}:${label}.${key}`);
      }
    }
    for (const [key, kind] of Object.entries(optional)) {
      if (key in record && !matches(record[key], kind)) {
        violations.push(`bad-shape:${entry.id}:${label}.${key}`);
      }
    }
    const allowed = new Set([...Object.keys(fields), ...Object.keys(optional)]);
    for (const key of Object.keys(record)) {
      if (!allowed.has(key)) {
        violations.push(`unknown-field:${entry.id}:${label}.${key}`);
      }
    }
    return record;
  };
  const hexField = (container: unknown, key: string, label = key, exactLength?: number) => {
    const value = (container as Record<string, unknown> | undefined)?.[key];
    if (
      typeof value !== 'string' ||
      value.length % 2 !== 0 ||
      !HEX.test(value) ||
      (exactLength !== undefined && value.length !== exactLength)
    ) {
      violations.push(`bad-hex:${entry.id}:${label}`);
    }
  };
  const nonnegativeIntegerField = (container: unknown, key: string, label = key) => {
    const value = (container as Record<string, unknown> | undefined)?.[key];
    if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) {
      violations.push(`bad-integer:${entry.id}:${label}`);
    }
  };

  switch (entry.kind) {
    case 'framing-vector': {
      require(['input', 'expected']);
      const input = vector.input;
      if (typeof input !== 'object' || input === null || Array.isArray(input)) {
        violations.push(`bad-shape:${entry.id}:input`);
        break;
      }
      const keys = Object.keys(input);
      if (keys.length === 1 && keys[0] === 'tag') {
        exactObject(input, 'input', { tag: 'string' });
        const expected = exactObject(vector.expected, 'expected', { preimageHex: 'string' });
        hexField(expected, 'preimageHex', 'expected.preimageHex');
      } else if (keys.length === 1 && keys[0] === 'value') {
        exactObject(input, 'input', { value: 'string' });
        const expected = exactObject(vector.expected, 'expected', { framedHex: 'string' });
        hexField(expected, 'framedHex', 'expected.framedHex');
      } else if (keys.length === 1 && keys[0] === 'payloadHex') {
        const framedInput = exactObject(input, 'input', { payloadHex: 'string' });
        hexField(framedInput, 'payloadHex', 'input.payloadHex');
        const expected = exactObject(vector.expected, 'expected', { framedHex: 'string' });
        hexField(expected, 'framedHex', 'expected.framedHex');
      } else if (
        keys.length === 2 &&
        keys.includes('ledgerSchemaVersion') &&
        keys.includes('prefixContractVersion')
      ) {
        const framingInput = exactObject(input, 'input', {
          ledgerSchemaVersion: 'number',
          prefixContractVersion: 'number',
        });
        nonnegativeIntegerField(framingInput, 'ledgerSchemaVersion', 'input.ledgerSchemaVersion');
        nonnegativeIntegerField(
          framingInput,
          'prefixContractVersion',
          'input.prefixContractVersion',
        );
        const expected = exactObject(vector.expected, 'expected', {
          logicalPrefixSha256: 'string',
        });
        shaField(expected, 'logicalPrefixSha256');
      } else {
        violations.push(`expected-union:${entry.id}:framing-input`);
      }
      break;
    }
    case 'digest-vector':
      require(['tag', 'envelope', 'expected']);
      if (typeof vector.tag !== 'string') violations.push(`bad-shape:${entry.id}:tag`);
      if (
        typeof vector.envelope !== 'object' ||
        vector.envelope === null ||
        Array.isArray(vector.envelope)
      ) {
        violations.push(`bad-shape:${entry.id}:envelope`);
      }
      {
        const expected = exactObject(vector.expected, 'expected', {
          preimageHex: 'string',
          digestHex: 'string',
        });
        hexField(expected, 'preimageHex', 'expected.preimageHex');
        shaField(expected, 'digestHex');
      }
      break;
    case 'interaction-vector':
      require([
        'predecessor',
        'consumedInputSha256',
        'currentHeadSha',
        'interactionOrdinal',
        'expected',
      ]);
      shaField(vector, 'consumedInputSha256');
      if (
        typeof vector.currentHeadSha !== 'string' ||
        !/^[0-9a-f]{40}([0-9a-f]{24})?$/.test(vector.currentHeadSha)
      ) {
        violations.push(`bad-sha:${entry.id}:currentHeadSha`);
      }
      nonnegativeIntegerField(vector, 'interactionOrdinal');
      {
        const predecessor = vector.predecessor as Record<string, unknown> | undefined;
        if (predecessor?.bootstrap === true) {
          exactObject(predecessor, 'predecessor', { bootstrap: 'boolean' });
        } else {
          const ledger = exactObject(predecessor, 'predecessor', { ledgerSha256: 'string' });
          shaField(ledger, 'ledgerSha256');
        }
        const expected = exactObject(vector.expected, 'expected', {
          preimageHex: 'string',
          interactionId: 'string',
        });
        hexField(expected, 'preimageHex', 'expected.preimageHex');
        shaField(expected, 'interactionId');
      }
      break;
    case 'materialization-vector': {
      require(['input', 'expected']);
      const expected = vector.expected;
      requireExpected(expected, [
        'logicalStreamHex',
        'providerStreamHex',
        'logicalPrefixSha256',
        'prefixSha256',
        'digests',
        'stableBoundary',
        'dynamicSuffix',
      ]);
      shaField(expected, 'logicalPrefixSha256');
      shaField(expected, 'prefixSha256');
      hexField(expected, 'logicalStreamHex', 'expected.logicalStreamHex');
      hexField(expected, 'providerStreamHex', 'expected.providerStreamHex');
      {
        const digests = (expected as Record<string, unknown> | undefined)?.digests;
        closedObject(
          digests,
          'digests',
          ['templateId', 'policyId', 'toolDefinitionId', 'cacheConfigId', 'adapterId'],
          'string',
        );
        for (const digestKey of [
          'templateId',
          'policyId',
          'toolDefinitionId',
          'cacheConfigId',
          'adapterId',
        ]) {
          shaField(digests, digestKey);
        }
        closedObject(
          (expected as Record<string, unknown> | undefined)?.stableBoundary,
          'stableBoundary',
          ['segmentCount', 'logicalStreamBytes', 'providerStreamBytes'],
          'number',
        );
        for (const boundaryKey of ['segmentCount', 'logicalStreamBytes', 'providerStreamBytes']) {
          nonnegativeIntegerField(
            (expected as Record<string, unknown> | undefined)?.stableBoundary,
            boundaryKey,
            `stableBoundary.${boundaryKey}`,
          );
        }
        closedObject(
          (expected as Record<string, unknown> | undefined)?.dynamicSuffix,
          'dynamicSuffix',
          ['logicalHex', 'providerHex'],
          'string',
        );
        hexField(
          (expected as Record<string, unknown> | undefined)?.dynamicSuffix,
          'logicalHex',
          'dynamicSuffix.logicalHex',
        );
        hexField(
          (expected as Record<string, unknown> | undefined)?.dynamicSuffix,
          'providerHex',
          'dynamicSuffix.providerHex',
        );
      }
      break;
    }
    case 'append-vector':
      require(['baseVectorId', 'successorVectorId', 'expected']);
      if (typeof vector.baseVectorId !== 'string' || typeof vector.successorVectorId !== 'string') {
        violations.push(`bad-shape:${entry.id}:references`);
      }
      exactObject(vector.expected, 'expected', {
        logicalStrictPrefix: 'boolean',
        providerStrictPrefix: 'boolean',
        promotedContextLogicalBytesEqual: 'boolean',
        promotedContextProviderBytesEqual: 'boolean',
      });
      break;
    case 'invalidation-vector': {
      require(['mode', 'mutation', 'expected']);
      const mode = vector.mode;
      if (typeof vector.mutation !== 'string') violations.push(`bad-shape:${entry.id}:mutation`);
      if (mode === 'materializer') {
        require(['baseVectorId', 'successorVectorId']);
        if (
          typeof vector.baseVectorId !== 'string' ||
          typeof vector.successorVectorId !== 'string'
        ) {
          violations.push(`bad-shape:${entry.id}:references`);
        }
        exactObject(vector.expected, 'expected', {
          logicalStreamChanged: 'boolean',
          providerStreamChanged: 'boolean',
          logicalHashChanged: 'boolean',
          prefixHashChanged: 'boolean',
        });
      } else if (mode === 'hash-framing') {
        require(['baseInput', 'mutatedInput']);
        for (const label of ['baseInput', 'mutatedInput'] as const) {
          const hashInput = exactObject(vector[label], label, {
            ledgerSchemaVersion: 'number',
            prefixContractVersion: 'number',
            logicalStreamHex: 'string',
            providerStreamHex: 'string',
          });
          nonnegativeIntegerField(hashInput, 'ledgerSchemaVersion', `${label}.ledgerSchemaVersion`);
          nonnegativeIntegerField(
            hashInput,
            'prefixContractVersion',
            `${label}.prefixContractVersion`,
          );
          hexField(hashInput, 'logicalStreamHex', `${label}.logicalStreamHex`);
          hexField(hashInput, 'providerStreamHex', `${label}.providerStreamHex`);
        }
        const invalidationExpected = exactObject(vector.expected, 'expected', {
          baseLogicalPrefixSha256: 'string',
          mutatedLogicalPrefixSha256: 'string',
          basePrefixSha256: 'string',
          mutatedPrefixSha256: 'string',
          logicalStreamChanged: 'boolean',
          providerStreamChanged: 'boolean',
          logicalHashChanged: 'boolean',
          prefixHashChanged: 'boolean',
        });
        shaField(invalidationExpected, 'baseLogicalPrefixSha256');
        shaField(invalidationExpected, 'mutatedLogicalPrefixSha256');
        shaField(invalidationExpected, 'basePrefixSha256');
        shaField(invalidationExpected, 'mutatedPrefixSha256');
      } else {
        violations.push(`unknown-mode:${entry.id}:${String(mode)}`);
      }
      break;
    }
    case 'invalid-vector': {
      require(['target', 'input', 'expected']);
      const target = vector.target;
      const expected = vector.expected;
      if (typeof target !== 'string' || typeof expected !== 'object' || expected === null) {
        violations.push(`expected-union:${entry.id}`);
        break;
      }
      const CSHARP_ONLY_VECTOR_IDS = new Set([
        'invalid-envelope-duplicate-root',
        'invalid-tools-duplicate-wrapper-property',
        'invalid-canonical-duplicate-open-json',
      ]);
      const scope = vector.scope;
      if (
        scope !== undefined &&
        (scope !== 'csharp-only' || !CSHARP_ONLY_VECTOR_IDS.has(entry.id))
      ) {
        violations.push(`unknown-scope:${entry.id}:${String(scope)}`);
      }
      violations.push(
        ...validateInvalidExpected(
          entry.id,
          target,
          expected as Record<string, unknown>,
          scope === 'csharp-only',
        ),
      );
      allowedKeys.add('scope');
      break;
    }
    default:
      violations.push(`unknown-kind:${entry.id}:${entry.kind}`);
  }

  for (const key of Object.keys(vector)) {
    if (!allowedKeys.has(key)) {
      violations.push(`unknown-vector-field:${entry.id}:${key}`);
    }
  }
  return violations;
}

/** Target-sensitive expected union for invalid-vectors (frozen in the refined issue). */
function validateInvalidExpected(
  id: string,
  target: string,
  expected: Record<string, unknown>,
  csharpOnly: boolean,
): string[] {
  const violations: string[] = [];
  const has = (key: string) => typeof expected[key] === 'string';
  for (const key of Object.keys(expected)) {
    if (!['csharpCode', 'typescriptCode', 'causeCode', 'path'].includes(key)) {
      violations.push(`unknown-expected-field:${id}:${key}`);
    } else if (typeof expected[key] !== 'string') {
      violations.push(`bad-shape:${id}:expected.${key}`);
    }
  }

  const csharpTargets = [
    'materialize',
    'template-id',
    'policy-id',
    'tools-id',
    'config-id',
    'adapter-id',
    'interaction-id',
    'canonical-json',
    'length-guard',
    'stream-guard',
  ];
  const tsTargets = [
    'template-id',
    'policy-id',
    'tools-id',
    'config-id',
    'adapter-id',
    'interaction-id',
    'canonical-json',
  ];

  if (target === 'identity' || target === 'model-snapshot') {
    if (!has('typescriptCode')) {
      violations.push(`expected-union:${id}:typescriptCode`);
    }
    if (has('csharpCode')) {
      violations.push(`expected-union:${id}:csharp-forbidden`);
    }
  } else if (csharpTargets.includes(target)) {
    if (!has('csharpCode')) {
      violations.push(`expected-union:${id}:csharpCode`);
    }
    if (tsTargets.includes(target) && !has('typescriptCode') && !csharpOnly) {
      violations.push(`expected-union:${id}:typescriptCode`);
    }
    if (
      (target === 'materialize' || target === 'length-guard' || target === 'stream-guard') &&
      has('typescriptCode')
    ) {
      violations.push(`expected-union:${id}:typescript-forbidden`);
    }
  } else {
    violations.push(`unknown-target:${id}:${target}`);
  }
  return violations;
}

function collectHexStrings(value: unknown, out: string[] = []): string[] {
  if (typeof value === 'object' && value !== null) {
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if ((key.endsWith('Hex') || key.endsWith('hex')) && typeof child === 'string') {
        out.push(child);
      } else {
        collectHexStrings(child, out);
      }
    }
  }
  return out;
}

function existsQuiet(file: string): boolean {
  try {
    readFileSync(file);
    return true;
  } catch {
    return false;
  }
}

function listFiles(root: string, prefix = ''): string[] {
  const { readdirSync } = require('node:fs') as typeof import('node:fs');
  const out: string[] = [];
  for (const name of readdirSync(path.join(root, prefix), { withFileTypes: true })) {
    const rel = prefix === '' ? name.name : prefix + '/' + name.name;
    if (name.isDirectory()) {
      out.push(...listFiles(root, rel));
    } else {
      out.push(rel);
    }
  }
  return out;
}

// --- synthetic corpus builder ---

let workdir: string | null = null;

afterEach(() => {
  if (workdir !== null) {
    rmSync(workdir, { recursive: true, force: true });
    workdir = null;
  }
});

function buildCorpus(
  mutate: (ctx: { entries: ManifestEntry[]; vectors: Map<string, unknown> }) => void,
): string {
  workdir = mkdtempSync(path.join(tmpdir(), 'prefix-corpus-'));
  const entries: ManifestEntry[] = [
    { id: 'materialization-a', kind: 'materialization-vector', file: 'm/a.json' },
    { id: 'materialization-b', kind: 'materialization-vector', file: 'm/b.json' },
  ];
  const vectors = new Map<string, unknown>([
    ['m/a.json', { id: 'materialization-a', kind: 'materialization-vector' }],
    ['m/b.json', { id: 'materialization-b', kind: 'materialization-vector' }],
  ]);
  mutate({ entries, vectors });
  for (const [file, vector] of vectors) {
    const target = path.join(workdir, file);
    mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, JSON.stringify(vector));
  }
  writeFileSync(
    path.join(workdir, 'manifest.json'),
    JSON.stringify({
      schemaVersion: 1,
      generatedBy: { tool: 'test', version: 1 },
      creationCrossCheck: { tool: 'test', version: '0', checkedAt: '2026-01-01T00:00:00Z' },
      vectors: entries,
    }),
  );
  return workdir;
}

describe('prefix-contract fixture manifest', () => {
  it('real corpus satisfies every manifest rule', () => {
    expect(validateCorpus(FIXTURE_ROOT)).toEqual([]);
  });

  it('rejects duplicate ids', () => {
    const root = buildCorpus(({ entries }) => {
      entries.push({ id: 'materialization-a', kind: 'materialization-vector', file: 'm/c.json' });
    });
    expect(validateCorpus(root)).toContain('duplicate-id:materialization-a');
  });

  it('rejects duplicate file references', () => {
    const root = buildCorpus(({ entries }) => {
      entries.push({ id: 'materialization-c', kind: 'materialization-vector', file: 'm/a.json' });
    });
    expect(validateCorpus(root)).toContain('duplicate-file:m/a.json');
  });

  it('rejects missing listed files', () => {
    const root = buildCorpus(({ entries }) => {
      entries.push({ id: 'materialization-c', kind: 'materialization-vector', file: 'm/c.json' });
    });
    expect(validateCorpus(root)).toContain('missing-file:m/c.json');
  });

  it('rejects unlisted files on disk', () => {
    const root = buildCorpus(({ vectors }) => {
      vectors.set('m/c.json', { id: 'materialization-c', kind: 'materialization-vector' });
    });
    expect(validateCorpus(root)).toContain('unlisted-file:m/c.json');
  });

  it('rejects unsafe paths', () => {
    const root = buildCorpus(({ entries }) => {
      entries.push({ id: 'materialization-c', kind: 'materialization-vector', file: '..\\c.json' });
    });
    expect(validateCorpus(root).some((v) => v.startsWith('unsafe-path'))).toBe(true);
  });

  it('rejects id/kind mismatch with vector content', () => {
    const root = buildCorpus(({ vectors }) => {
      vectors.set('m/a.json', { id: 'materialization-a', kind: 'digest-vector' });
    });
    expect(validateCorpus(root)).toContain('kind-mismatch:materialization-a');
  });

  it('rejects bad references', () => {
    const root = buildCorpus(({ entries, vectors }) => {
      entries.push({ id: 'append-x', kind: 'append-vector', file: 'm/x.json' });
      vectors.set('m/x.json', {
        id: 'append-x',
        kind: 'append-vector',
        baseVectorId: 'materialization-a',
        successorVectorId: 'append-x',
      });
    });
    const violations = validateCorpus(root);
    expect(violations).toContain('self-reference:append-x');
  });

  it('rejects wrong-kind references', () => {
    const root = buildCorpus(({ entries, vectors }) => {
      entries.push({ id: 'append-y', kind: 'append-vector', file: 'm/y.json' });
      entries.push({ id: 'digest-z', kind: 'digest-vector', file: 'm/z.json' });
      vectors.set('m/y.json', { id: 'append-y', kind: 'append-vector', baseVectorId: 'digest-z' });
      vectors.set('m/z.json', { id: 'digest-z', kind: 'digest-vector' });
    });
    expect(validateCorpus(root)).toContain('wrong-reference-kind:append-y:digest-z');
  });

  it('rejects unknown vector fields', () => {
    const root = buildCorpus(({ vectors }) => {
      vectors.set('m/a.json', {
        id: 'materialization-a',
        kind: 'materialization-vector',
        stale: true,
      });
    });
    expect(validateCorpus(root)).toContain('unknown-vector-field:materialization-a:stale');
  });

  it('rejects missing per-kind required fields', () => {
    const root = buildCorpus(({ vectors }) => {
      vectors.set('m/a.json', { id: 'materialization-a', kind: 'materialization-vector' });
    });
    expect(validateCorpus(root)).toContain('missing-field:materialization-a:input');
  });

  it('rejects unknown invalidation mode', () => {
    const root = buildCorpus(({ entries, vectors }) => {
      entries.push({ id: 'inv-x', kind: 'invalidation-vector', file: 'm/x.json' });
      vectors.set('m/x.json', {
        id: 'inv-x',
        kind: 'invalidation-vector',
        mode: 'sideways',
        mutation: 'm',
        expected: {},
      });
    });
    expect(validateCorpus(root)).toContain('unknown-mode:inv-x:sideways');
  });

  it('rejects canonical vectors missing typescriptCode', () => {
    const root = buildCorpus(({ entries, vectors }) => {
      entries.push({ id: 'invalid-c', kind: 'invalid-vector', file: 'm/c.json' });
      vectors.set('m/c.json', {
        id: 'invalid-c',
        kind: 'invalid-vector',
        target: 'canonical-json',
        input: {},
        expected: { csharpCode: 'prefix_canonical_input_rejected' },
      });
    });
    expect(validateCorpus(root)).toContain('expected-union:invalid-c:typescriptCode');
  });

  it('rejects invalid expected union on invalid-vectors', () => {
    const root = buildCorpus(({ entries, vectors }) => {
      entries.push({ id: 'invalid-x', kind: 'invalid-vector', file: 'm/x.json' });
      vectors.set('m/x.json', {
        id: 'invalid-x',
        kind: 'invalid-vector',
        target: 'identity',
        input: {},
        expected: {},
      });
    });
    expect(validateCorpus(root)).toContain('expected-union:invalid-x:typescriptCode');
  });

  it('rejects wrong recursive value types in framing and append unions', () => {
    expect(
      validateVectorShape(
        { id: 'framing-x', kind: 'framing-vector', file: 'x.json' },
        {
          id: 'framing-x',
          kind: 'framing-vector',
          input: { tag: 1 },
          expected: { preimageHex: '00' },
        },
      ),
    ).toContain('bad-shape:framing-x:input.tag');
    expect(
      validateVectorShape(
        { id: 'append-x', kind: 'append-vector', file: 'x.json' },
        {
          id: 'append-x',
          kind: 'append-vector',
          baseVectorId: 'a',
          successorVectorId: 'b',
          expected: {
            logicalStrictPrefix: 'true',
            providerStrictPrefix: true,
            promotedContextLogicalBytesEqual: true,
            promotedContextProviderBytesEqual: true,
          },
        },
      ),
    ).toContain('bad-shape:append-x:expected.logicalStrictPrefix');
  });

  it('rejects malformed nested materialization fields and invalid diagnostics', () => {
    const materializationViolations = validateVectorShape(
      { id: 'materialization-x', kind: 'materialization-vector', file: 'x.json' },
      {
        id: 'materialization-x',
        kind: 'materialization-vector',
        input: {},
        expected: {
          logicalStreamHex: '0',
          providerStreamHex: 'zz',
          logicalPrefixSha256: '00',
          prefixSha256: '00',
          digests: {
            templateId: '00',
            policyId: '00',
            toolDefinitionId: '00',
            cacheConfigId: '00',
            adapterId: '00',
          },
          stableBoundary: {
            segmentCount: 1.5,
            logicalStreamBytes: 0,
            providerStreamBytes: 0,
          },
          dynamicSuffix: { logicalHex: '00', providerHex: '00' },
        },
      },
    );
    expect(materializationViolations).toContain(
      'bad-hex:materialization-x:expected.logicalStreamHex',
    );
    expect(materializationViolations).toContain(
      'bad-integer:materialization-x:stableBoundary.segmentCount',
    );

    expect(
      validateInvalidExpected('invalid-x', 'identity', { typescriptCode: 'bad', path: 42 }, false),
    ).toContain('bad-shape:invalid-x:expected.path');
  });

  it('requires envelope mutations and their matching digest updates together', () => {
    expect(
      validateMutationDiffs('inv-x', 'template envelope content/version', [
        'envelopes.template.definition',
      ]),
    ).toEqual(['mutation-diff:inv-x']);
    expect(
      validateMutationDiffs('inv-x', 'template envelope content/version', [
        'expectedIdentities.templateId',
      ]),
    ).toEqual(['mutation-diff:inv-x']);
    expect(
      validateMutationDiffs('inv-x', 'template envelope content/version', [
        'envelopes.template.definition',
        'expectedIdentities.templateId',
      ]),
    ).toEqual([]);
  });
});
