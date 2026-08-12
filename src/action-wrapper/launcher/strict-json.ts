import { fail } from './validation.js';

const strictUtf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false });

export function parseStrictJson(bytes: Uint8Array, maximumBytes: number): unknown {
  if (bytes.byteLength < 1 || bytes.byteLength > maximumBytes) fail('wrapper_document_invalid');
  if (bytes.byteLength >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    fail('wrapper_document_invalid');
  }
  try {
    const text = strictUtf8.decode(bytes);
    const value = JSON.parse(text) as unknown;
    const scanner = new Scanner(text);
    scanner.skipWhitespace();
    scanValue(scanner);
    scanner.skipWhitespace();
    if (!scanner.eof()) fail('wrapper_document_invalid');
    return value;
  } catch {
    fail('wrapper_document_invalid');
  }
}

class Scanner {
  private position = 0;

  constructor(private readonly text: string) {}

  eof(): boolean {
    return this.position >= this.text.length;
  }

  peek(): string {
    return this.text[this.position] ?? '';
  }

  advance(): string {
    const value = this.peek();
    this.position += 1;
    return value;
  }

  skipWhitespace(): void {
    while (!this.eof() && ' \t\n\r'.includes(this.peek())) this.position += 1;
  }

  expect(expected: string): void {
    if (this.peek() !== expected) fail('wrapper_document_invalid');
    this.position += 1;
  }
}

function scanValue(scanner: Scanner): void {
  scanner.skipWhitespace();
  if (scanner.peek() === '{') scanObject(scanner);
  else if (scanner.peek() === '[') scanArray(scanner);
  else if (scanner.peek() === '"') readString(scanner);
  else scanPrimitive(scanner);
}

function scanPrimitive(scanner: Scanner): void {
  let token = '';
  while (!scanner.eof() && !',}] \t\n\r'.includes(scanner.peek())) token += scanner.advance();
  if (/^-?[0-9]/u.test(token) && !/^-?(0|[1-9][0-9]*)$/u.test(token)) {
    fail('wrapper_document_invalid');
  }
}

function scanObject(scanner: Scanner): void {
  scanner.expect('{');
  scanner.skipWhitespace();
  const keys = new Set<string>();
  if (scanner.peek() === '}') {
    scanner.advance();
    return;
  }
  for (;;) {
    scanner.skipWhitespace();
    const key = readString(scanner);
    if (keys.has(key)) fail('wrapper_document_invalid');
    keys.add(key);
    scanner.skipWhitespace();
    scanner.expect(':');
    scanValue(scanner);
    scanner.skipWhitespace();
    if (scanner.peek() === ',') {
      scanner.advance();
      continue;
    }
    scanner.expect('}');
    return;
  }
}

function scanArray(scanner: Scanner): void {
  scanner.expect('[');
  scanner.skipWhitespace();
  if (scanner.peek() === ']') {
    scanner.advance();
    return;
  }
  for (;;) {
    scanValue(scanner);
    scanner.skipWhitespace();
    if (scanner.peek() === ',') {
      scanner.advance();
      continue;
    }
    scanner.expect(']');
    return;
  }
}

function readString(scanner: Scanner): string {
  scanner.expect('"');
  let value = '';
  while (!scanner.eof()) {
    const character = scanner.advance();
    if (character === '"') return value;
    if (character !== '\\') {
      value += character;
      continue;
    }
    const escaped = scanner.advance();
    if ('"\\/'.includes(escaped)) value += escaped;
    else if (escaped === 'b') value += '\b';
    else if (escaped === 'f') value += '\f';
    else if (escaped === 'n') value += '\n';
    else if (escaped === 'r') value += '\r';
    else if (escaped === 't') value += '\t';
    else if (escaped === 'u') {
      const hex = scanner.advance() + scanner.advance() + scanner.advance() + scanner.advance();
      if (!/^[0-9a-fA-F]{4}$/u.test(hex)) fail('wrapper_document_invalid');
      value += String.fromCharCode(Number.parseInt(hex, 16));
    } else fail('wrapper_document_invalid');
  }
  fail('wrapper_document_invalid');
}
