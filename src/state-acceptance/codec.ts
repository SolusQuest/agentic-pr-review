import { TextDecoder } from 'node:util';
import {
  CanonicalJsonInputError,
  canonicalJsonBytes,
  type CanonicalJsonValue,
} from '../canonical-json/index.js';
import { sha256Hex } from './hash.js';

export const RECORD_MAX_BYTES = 32 * 1024;

export const RECORD_CODEC_CODES = [
  'byte_limit_exceeded',
  'bom',
  'invalid_utf8',
  'invalid_json',
  'duplicate_key',
  'invalid_unicode',
  'non_canonical',
] as const;

export type RecordCodecCode = (typeof RECORD_CODEC_CODES)[number];

export class RecordCodecError extends Error {
  readonly code: RecordCodecCode;
  readonly path: string;

  constructor(code: RecordCodecCode, path = '') {
    super(`state acceptance record rejected: ${code}`);
    this.name = 'RecordCodecError';
    this.code = code;
    this.path = path;
  }
}

export function encodeRecord(value: unknown, maxBytes = RECORD_MAX_BYTES): Uint8Array {
  try {
    return canonicalJsonBytes(value as CanonicalJsonValue, maxBytes);
  } catch (error) {
    if (error instanceof RecordCodecError) throw error;
    throw new RecordCodecError(
      error instanceof Error && error.message.includes('exceed')
        ? 'byte_limit_exceeded'
        : 'non_canonical',
    );
  }
}

export function decodeRecord<T>(bytes: Uint8Array, maxBytes = RECORD_MAX_BYTES): T {
  return finalizeRecord<T>(bytes, parseRecord(bytes, maxBytes), maxBytes);
}

export function parseRecord(bytes: Uint8Array, maxBytes = RECORD_MAX_BYTES): unknown {
  if (bytes.byteLength > maxBytes) throw new RecordCodecError('byte_limit_exceeded');
  if (bytes.byteLength >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    throw new RecordCodecError('bom');
  }

  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw new RecordCodecError('invalid_utf8');
  }

  let value: unknown;
  try {
    value = new JsonParser(text).parse();
  } catch (error) {
    if (error instanceof RecordCodecError) throw error;
    throw new RecordCodecError('invalid_json');
  }
  return value;
}

export function finalizeRecord<T>(
  bytes: Uint8Array,
  value: unknown,
  maxBytes = RECORD_MAX_BYTES,
): T {
  if (findUnsafeString(value)) throw new RecordCodecError('invalid_unicode');
  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(value, maxBytes);
  } catch (error) {
    if (error instanceof CanonicalJsonInputError) throw new RecordCodecError('invalid_json');
    throw new RecordCodecError('byte_limit_exceeded');
  }
  if (!bytesEqual(bytes, canonical)) throw new RecordCodecError('non_canonical');
  return value as T;
}

export function recordSha256(bytes: Uint8Array): string {
  return sha256Hex(bytes);
}

export function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) return false;
  for (let i = 0; i < left.byteLength; i += 1) {
    if (left[i] !== right[i]) return false;
  }
  return true;
}

function findUnsafeString(value: unknown): boolean {
  if (typeof value === 'string') return hasUnsafeCodeUnit(value);
  if (Array.isArray(value)) return value.some(findUnsafeString);
  if (value !== null && typeof value === 'object') {
    return Object.entries(value).some(
      ([key, child]) => hasUnsafeCodeUnit(key) || findUnsafeString(child),
    );
  }
  return false;
}

function hasUnsafeCodeUnit(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code === 0) return true;
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (next < 0xdc00 || next > 0xdfff) return true;
      index += 1;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true;
    }
  }
  return false;
}

class JsonParser {
  private index = 0;

  constructor(private readonly text: string) {}

  parse(): unknown {
    this.skipWhitespace();
    const value = this.parseValue();
    this.skipWhitespace();
    if (this.index !== this.text.length) throw new Error('trailing data');
    return value;
  }

  private parseValue(): unknown {
    this.skipWhitespace();
    const character = this.text[this.index];
    if (character === '{') return this.parseObject();
    if (character === '[') return this.parseArray();
    if (character === '"') return this.parseString();
    if (character === 't' && this.consume('true')) return true;
    if (character === 'f' && this.consume('false')) return false;
    if (character === 'n' && this.consume('null')) return null;
    return this.parseNumber();
  }

  private parseObject(): Record<string, unknown> {
    this.index += 1;
    const value: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    const keys = new Set<string>();
    this.skipWhitespace();
    if (this.text[this.index] === '}') {
      this.index += 1;
      return value;
    }
    while (true) {
      this.skipWhitespace();
      if (this.text[this.index] !== '"') throw new Error('object key');
      const key = this.parseString();
      if (keys.has(key)) throw new RecordCodecError('duplicate_key');
      keys.add(key);
      this.skipWhitespace();
      if (this.text[this.index] !== ':') throw new Error('object colon');
      this.index += 1;
      value[key] = this.parseValue();
      this.skipWhitespace();
      if (this.text[this.index] === '}') {
        this.index += 1;
        return value;
      }
      if (this.text[this.index] !== ',') throw new Error('object comma');
      this.index += 1;
    }
  }

  private parseArray(): unknown[] {
    this.index += 1;
    const value: unknown[] = [];
    this.skipWhitespace();
    if (this.text[this.index] === ']') {
      this.index += 1;
      return value;
    }
    while (true) {
      value.push(this.parseValue());
      this.skipWhitespace();
      if (this.text[this.index] === ']') {
        this.index += 1;
        return value;
      }
      if (this.text[this.index] !== ',') throw new Error('array comma');
      this.index += 1;
    }
  }

  private parseString(): string {
    const start = this.index;
    this.index += 1;
    let escaped = false;
    while (this.index < this.text.length) {
      const code = this.text.charCodeAt(this.index);
      if (code < 0x20) throw new Error('control in string');
      if (code === 0x22 && !escaped) {
        this.index += 1;
        return JSON.parse(this.text.slice(start, this.index)) as string;
      }
      if (code === 0x5c && !escaped) escaped = true;
      else escaped = false;
      this.index += 1;
    }
    throw new Error('unterminated string');
  }

  private parseNumber(): number {
    const match = this.text.slice(this.index).match(/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/);
    if (!match) throw new Error('number');
    this.index += match[0].length;
    const value = Number(match[0]);
    if (!Number.isFinite(value)) throw new Error('number range');
    return value;
  }

  private consume(expected: string): boolean {
    if (this.text.slice(this.index, this.index + expected.length) !== expected) return false;
    this.index += expected.length;
    return true;
  }

  private skipWhitespace(): void {
    while (
      this.index < this.text.length &&
      /[\u0020\u0009\u000a\u000d]/.test(this.text[this.index])
    ) {
      this.index += 1;
    }
  }
}
