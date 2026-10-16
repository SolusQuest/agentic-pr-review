import { describe, expect, test } from 'vitest';
import {
  PAYLOAD_MAP_MAXIMUM_BYTES,
  parsePayloadMap,
  parseManagedPayloadMap,
  MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES,
} from '../../src/action-wrapper/launcher/payload-map.js';
import { ACTION_INPUT_NAMES } from '../../src/action-wrapper/launcher/inputs.js';
import { ACTION_SOURCE, releaseFixture } from './release-payload-fixture.js';

describe('R7-D2 trusted Action map', () => {
  test('preserves separate S/W/T, archive/member/build/launcher identities and freezes all authority', () => {
    const fixture = releaseFixture();
    const map = parsePayloadMap(fixture.mapBytes);
    expect(map.payload).toEqual(fixture.receipt);
    expect(
      new Set([map.payload.identity.sourceCommit, map.builder.workflowCommit, ACTION_SOURCE]).size,
    ).toBe(3);
    expect(
      new Set([
        map.payload.archiveSha256,
        map.payload.members[0].sha256,
        map.payload.identity.buildId,
        'r7-d0',
      ]).size,
    ).toBe(4);
    for (const value of [
      map,
      map.builder,
      map.payload,
      map.payload.identity,
      map.payload.members,
      ...map.payload.members,
    ]) {
      expect(Object.isFrozen(value)).toBe(true);
    }
    fixture.mapBytes.fill(0);
    expect(map.payload.identity.sourceCommit).toBe('a'.repeat(40));
  });

  test.each([
    ['formatVersion', 2],
    ['repository', 'attacker/repo'],
    ['tag', 'latest'],
    ['builder.repository', 'attacker/repo'],
    ['builder.workflowPath', '../release.yml'],
    ['builder.workflowPath', '.github/workflows/a/b.yml'],
    ['builder.workflowCommit', 'main'],
    ['payload.formatVersion', 2],
    ['payload.archiveName', 'payload.zip'],
    ['payload.archiveSize', 0],
    ['payload.archiveSize', 33554433],
    ['payload.archiveSha256', 'A'.repeat(64)],
    ['payload.identity.releaseVersion', 'v01.0.0-internal.1'],
    ['payload.identity.releaseVersion', 'v0.0.0-internal.0'],
    ['payload.identity.platform', 'win-x64'],
    ['payload.identity.sourceCommit', 'HEAD'],
    ['payload.identity.sourceTree', 'b'.repeat(39)],
    ['payload.identity.buildId', 'c'.repeat(63)],
    ['payload.identity.informationalVersion', '0.1.0-dev'],
    ['payload.members', []],
    ['payload.members.0.path', '../escape'],
    ['payload.members.0.mode', 420],
    ['payload.members.0.size', 67108865],
    ['payload.members.1.size', 65537],
    ['payload.members.2.size', 1048577],
    ['payload.members.1.sha256', 'f'.repeat(63)],
    ['endpoint', 'https://attacker.invalid'],
    ['builder.extra', true],
    ['payload.extra', true],
    ['payload.identity.extra', true],
    ['payload.members.0.extra', true],
  ])('rejects invalid or unknown %s', (field, value) => {
    const map = JSON.parse(releaseFixture().mapBytes.toString()) as Record<string, unknown>;
    const fields = field.split('.');
    let target = map;
    for (const part of fields.slice(0, -1)) target = target[part] as Record<string, unknown>;
    target[fields.at(-1)!] = value;
    expect(() => parsePayloadMap(Buffer.from(JSON.stringify(map)))).toThrow(
      'wrapper_payload_map_invalid',
    );
  });

  test.each(['builder', 'payload', 'tag', 'repository', 'formatVersion'])(
    'requires %s',
    (field) => {
      const value = JSON.parse(releaseFixture().mapBytes.toString()) as Record<string, unknown>;
      delete value[field];
      expect(() => parsePayloadMap(Buffer.from(JSON.stringify(value)))).toThrow();
    },
  );

  test('rejects duplicates, escaped aliases, exponent/fraction, invalid UTF8, BOM and byte overflow', () => {
    const text = releaseFixture().mapBytes.toString();
    for (const bytes of [
      Buffer.from(text.replace('"formatVersion":1', '"formatVersion":1,"formatVersion":1')),
      Buffer.from(text.replace('"formatVersion":1', '"formatVersion":1,"format\\u0056ersion":1')),
      Buffer.from(text.replace('"formatVersion":1', '"formatVersion":1e0')),
      Buffer.from(text.replace('"formatVersion":1', '"formatVersion":1.0')),
      Buffer.from([0xff]),
      Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text)]),
      Buffer.alloc(PAYLOAD_MAP_MAXIMUM_BYTES + 1, 32),
      Buffer.alloc(0),
    ])
      expect(() => parsePayloadMap(bytes)).toThrow('wrapper_payload_map_invalid');
    expect(
      parsePayloadMap(Buffer.from(text.padEnd(PAYLOAD_MAP_MAXIMUM_BYTES, ' '))).formatVersion,
    ).toBe(1);
  });

  test('ordinary public inputs have no path, release, endpoint or test override', () => {
    expect(ACTION_INPUT_NAMES).toEqual([
      'github-token',
      'provider-api-key',
      'state-key',
      'previous-state-key',
      'config-path',
      'pr-number',
      'state-mode',
    ]);
  });
});

describe('CR1-D2 managed trusted map', () => {
  function fixtureMap() {
    const map = JSON.parse(releaseFixture().mapBytes.toString());
    map.payload.formatVersion = 'managed-v1';
    map.payload.archiveName = map.payload.archiveName.replace('.tar.gz', '-managed.tar.gz');
    map.payload.members = map.payload.members
      .map((member: Record<string, unknown>) => ({ ...member, mode: 0o644 }))
      .sort((a: { path: string }, b: { path: string }) => (a.path < b.path ? -1 : 1));
    return map;
  }
  test('uses the authoritative managed receipt validator and freezes complete independent identity', () => {
    const value = fixtureMap();
    const bytes = Buffer.from(JSON.stringify(value));
    const map = parseManagedPayloadMap(bytes);
    expect(map.payload).toEqual(value.payload);
    for (const object of [
      map,
      map.builder,
      map.payload,
      map.payload.identity,
      map.payload.members,
      ...map.payload.members,
    ])
      expect(Object.isFrozen(object)).toBe(true);
    bytes.fill(0);
    expect(map.payload.identity.sourceCommit).toBe('a'.repeat(40));
    expect(() => parsePayloadMap(Buffer.from(JSON.stringify(value)))).toThrow();
    expect(() => parseManagedPayloadMap(releaseFixture().mapBytes)).toThrow();
  });
  test.each([
    ['formatVersion', 2],
    ['repository', 'other/repo'],
    ['tag', 'latest'],
    ['builder.workflowPath', '../release.yml'],
    ['builder.workflowCommit', 'main'],
    ['builder.extra', true],
    ['payload.archiveSize', 67108865],
    ['payload.archiveSha256', 'A'.repeat(64)],
    ['payload.identity.platform', 'win-x64'],
    ['payload.identity.sourceCommit', 'HEAD'],
    ['payload.identity.sourceTree', 'HEAD'],
    ['payload.identity.buildId', 'f'.repeat(63)],
    ['payload.identity.informationalVersion', 'fake'],
    ['payload.identity.extra', true],
    ['payload.members.0.path', '../escape'],
    ['payload.members.0.mode', 0o755],
    ['payload.members.0.size', -1],
    ['payload.members.0.sha256', 'bad'],
    ['payload.members.0.extra', true],
    ['payload.extra', true],
    ['extra', true],
    ['payload.members', []],
  ])('rejects invalid managed authority: %s', (field, value) => {
    const map = fixtureMap();
    let target = map;
    const fields = field.split('.');
    for (const key of fields.slice(0, -1)) target = target[key];
    target[fields.at(-1)!] = value;
    expect(() => parseManagedPayloadMap(Buffer.from(JSON.stringify(map)))).toThrow(
      'wrapper_payload_map_invalid',
    );
  });
  test('rejects duplicates, case aliases and limits without silently pruning a larger managed map', () => {
    const map = fixtureMap();
    const text = JSON.stringify(map);
    for (const bytes of [
      Buffer.from('null'),
      Buffer.from(text.replace('"formatVersion":1', '"formatVersion":1,"formatVersion":1')),
      Buffer.alloc(MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES + 1, 32),
    ])
      expect(() => parseManagedPayloadMap(bytes)).toThrow();
    expect(
      parseManagedPayloadMap(Buffer.from(text.padEnd(PAYLOAD_MAP_MAXIMUM_BYTES + 1, ' '))).payload
        .members,
    ).toHaveLength(3);
    map.payload.members.push({ ...map.payload.members[0] });
    expect(() => parseManagedPayloadMap(Buffer.from(JSON.stringify(map)))).toThrow();
  });
});
