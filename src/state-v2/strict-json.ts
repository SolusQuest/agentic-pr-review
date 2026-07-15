/**
 * Strict JSON parser wrapper: rejects duplicate keys and trailing bytes.
 *
 * We use JSON.parse for the value (correct handling of numbers, escapes,
 * surrogate pairs, whitespace) and a separate light scanner over the source
 * text to detect duplicate object keys. Trailing non-whitespace bytes after
 * the top-level value are rejected by JSON.parse itself.
 */

export class StrictJsonParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'StrictJsonParseError';
  }
}

export function strictParseJson(text: string): unknown {
  const value = JSON.parse(text);
  detectDuplicateKeys(text);
  return value;
}

function detectDuplicateKeys(text: string): void {
  const scanner = new Scanner(text);
  scanner.skipWhitespace();
  scanValue(scanner);
  scanner.skipWhitespace();
  if (!scanner.eof()) {
    throw new StrictJsonParseError('trailing non-whitespace after JSON value');
  }
}

class Scanner {
  private readonly text: string;
  private pos = 0;

  constructor(text: string) {
    this.text = text;
  }

  eof(): boolean {
    return this.pos >= this.text.length;
  }

  peek(): string {
    return this.text[this.pos] ?? '';
  }

  advance(): string {
    const ch = this.text[this.pos] ?? '';
    this.pos += 1;
    return ch;
  }

  skipWhitespace(): void {
    while (this.pos < this.text.length) {
      const ch = this.text[this.pos];
      if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r') {
        this.pos += 1;
      } else {
        break;
      }
    }
  }

  expect(ch: string): void {
    if (this.text[this.pos] !== ch) {
      throw new StrictJsonParseError(`expected '${ch}' at position ${this.pos}`);
    }
    this.pos += 1;
  }
}

function scanValue(s: Scanner): void {
  s.skipWhitespace();
  const ch = s.peek();
  if (ch === '{') {
    scanObject(s);
  } else if (ch === '[') {
    scanArray(s);
  } else if (ch === '"') {
    scanString(s);
  } else {
    // number / bool / null: consume until whitespace or a structural char.
    while (!s.eof()) {
      const c = s.peek();
      if (
        c === ',' ||
        c === '}' ||
        c === ']' ||
        c === ' ' ||
        c === '\t' ||
        c === '\n' ||
        c === '\r'
      ) {
        break;
      }
      s.advance();
    }
  }
}

function scanObject(s: Scanner): void {
  s.expect('{');
  s.skipWhitespace();
  const seen = new Set<string>();
  if (s.peek() === '}') {
    s.advance();
    return;
  }
  for (;;) {
    s.skipWhitespace();
    const key = readString(s);
    if (seen.has(key)) {
      throw new StrictJsonParseError(`duplicate key: ${key}`);
    }
    seen.add(key);
    s.skipWhitespace();
    s.expect(':');
    scanValue(s);
    s.skipWhitespace();
    const next = s.peek();
    if (next === ',') {
      s.advance();
      continue;
    }
    if (next === '}') {
      s.advance();
      return;
    }
    throw new StrictJsonParseError(`unexpected '${next}' inside object`);
  }
}

function scanArray(s: Scanner): void {
  s.expect('[');
  s.skipWhitespace();
  if (s.peek() === ']') {
    s.advance();
    return;
  }
  for (;;) {
    scanValue(s);
    s.skipWhitespace();
    const next = s.peek();
    if (next === ',') {
      s.advance();
      continue;
    }
    if (next === ']') {
      s.advance();
      return;
    }
    throw new StrictJsonParseError(`unexpected '${next}' inside array`);
  }
}

function scanString(s: Scanner): void {
  readString(s);
}

function readString(s: Scanner): string {
  if (s.peek() !== '"') {
    throw new StrictJsonParseError(`expected '"' at position, saw '${s.peek()}'`);
  }
  s.advance();
  let out = '';
  while (!s.eof()) {
    const ch = s.advance();
    if (ch === '"') return out;
    if (ch === '\\') {
      const next = s.advance();
      switch (next) {
        case '"':
        case '\\':
        case '/':
          out += next;
          break;
        case 'b':
          out += '\b';
          break;
        case 'f':
          out += '\f';
          break;
        case 'n':
          out += '\n';
          break;
        case 'r':
          out += '\r';
          break;
        case 't':
          out += '\t';
          break;
        case 'u': {
          const hex = s.advance() + s.advance() + s.advance() + s.advance();
          out += String.fromCharCode(parseInt(hex, 16));
          break;
        }
        default:
          throw new StrictJsonParseError(`invalid escape '\\${next}'`);
      }
    } else {
      out += ch;
    }
  }
  throw new StrictJsonParseError('unterminated string');
}
