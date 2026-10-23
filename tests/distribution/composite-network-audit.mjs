import assert from 'node:assert/strict';
/** No payload strings are needed: strace uses -s 0 and the audit reads destinations only. */
export function inspectCompositeNetwork(text, expectedPort) {
  assert.ok(Number.isInteger(expectedPort) && expectedPort > 0 && expectedPort <= 65535);
  let inet = 0;
  for (const line of text.split('\n')) {
    if (!/\b(?:connect|sendto|sendmsg)\(/u.test(line) || !/AF_INET6?\b/u.test(line)) continue;
    inet++;
    const ports = [...line.matchAll(/sin6?_port=htons\((\d+)\)/gu)].map((match) =>
      Number(match[1]),
    );
    assert.ok(
      ports.length > 0 && ports.every((port) => port === expectedPort),
      'composite_uncontrolled_loopback_service',
    );
    const addresses = [
      ...line.matchAll(/sin_addr=inet_addr\("([^"]+)"\)|inet_pton\(AF_INET6,\s*"([^"]+)"/gu),
    ].map((match) => match[1] ?? match[2]);
    assert.ok(
      addresses.length > 0 && addresses.every((ip) => ip === '127.0.0.1' || ip === '::1'),
      'composite_external_service_destination',
    );
  }
  return { inetOperations: inet, externalServiceDestinations: 0 };
}
