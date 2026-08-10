export class ArtifactBridgeStrictJsonError extends Error {
  constructor() {
    super('artifact_bridge_json_invalid');
    this.name = 'ArtifactBridgeStrictJsonError';
  }
}

export function strictParseArtifactBridgeJson(text: string): unknown {
  try {
    const value = JSON.parse(text);
    const scanner = new Scanner(text);
    scanner.skipWhitespace();
    scanValue(scanner);
    scanner.skipWhitespace();
    if (!scanner.eof()) throw new ArtifactBridgeStrictJsonError();
    return value;
  } catch {
    throw new ArtifactBridgeStrictJsonError();
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
    const character = this.peek();
    this.position += 1;
    return character;
  }

  skipWhitespace(): void {
    while (!this.eof() && ' \t\n\r'.includes(this.peek())) this.position += 1;
  }

  expect(character: string): void {
    if (this.peek() !== character) throw new ArtifactBridgeStrictJsonError();
    this.position += 1;
  }
}

function scanValue(scanner: Scanner): void {
  scanner.skipWhitespace();
  if (scanner.peek() === '{') scanObject(scanner);
  else if (scanner.peek() === '[') scanArray(scanner);
  else if (scanner.peek() === '"') readString(scanner);
  else {
    while (!scanner.eof() && !',}] \t\n\r'.includes(scanner.peek())) scanner.advance();
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
    if (keys.has(key)) throw new ArtifactBridgeStrictJsonError();
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
      if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new ArtifactBridgeStrictJsonError();
      value += String.fromCharCode(Number.parseInt(hex, 16));
    } else throw new ArtifactBridgeStrictJsonError();
  }
  throw new ArtifactBridgeStrictJsonError();
}
