export class ActionWrapperContractError extends Error {
  constructor(readonly code: string) {
    super(code);
    this.name = 'ActionWrapperContractError';
  }
}

const utf8 = new TextEncoder();

export function fail(code: string): never {
  throw new ActionWrapperContractError(code);
}

export function utf8Length(value: string): number {
  if (!isWellFormed(value)) fail('wrapper_string_invalid');
  return utf8.encode(value).byteLength;
}

export function boundedStructure(value: unknown, maximumBytes: number): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    utf8Length(value) <= maximumBytes &&
    !/[\u0000-\u001f\u007f-\u009f]/u.test(value)
  );
}

export function boundedSecret(value: unknown, maximumBytes: number): value is string {
  return typeof value === 'string' && value.length > 0 && utf8Length(value) <= maximumBytes;
}

export function canonicalPositiveDecimal(
  value: unknown,
  maximumDigits: number,
  maximumValue: bigint,
): value is string {
  return (
    typeof value === 'string' &&
    value.length <= maximumDigits &&
    /^[1-9][0-9]*$/u.test(value) &&
    BigInt(value) <= maximumValue
  );
}

export function lowerHex(value: unknown, length: number): value is string {
  return typeof value === 'string' && value.length === length && /^[0-9a-f]+$/u.test(value);
}

export function buildDiscriminator(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 128 && /^[a-z0-9][a-z0-9._-]*$/u.test(value);
}

export function exactRecord(
  value: unknown,
  expectedKeys: readonly string[],
  code = 'wrapper_document_invalid',
): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) fail(code);
  const record = value as Record<string, unknown>;
  const actual = Object.keys(record).sort();
  const expected = [...expectedKeys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    fail(code);
  }
  return record;
}

export function isWorkflowPresentationText(value: unknown, maximumBytes: number): value is string {
  return (
    boundedStructure(value, maximumBytes) &&
    !value.includes('::') &&
    !/%0a/iu.test(value) &&
    !/%0d/iu.test(value)
  );
}

export function isWellFormed(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const unit = value.charCodeAt(index);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      index += 1;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return false;
    }
  }
  return true;
}
