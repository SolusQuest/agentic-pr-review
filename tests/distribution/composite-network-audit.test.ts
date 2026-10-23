import { expect, test } from 'vitest';
import { inspectCompositeNetwork } from './composite-network-audit.mjs';
test('accepts only the exact controlled loopback destination across parent and SDK operations', () => {
  const trace =
    '100 connect(4, {sa_family=AF_INET, sin_port=htons(32123), sin_addr=inet_addr("127.0.0.1")},16) = -1 EINPROGRESS\n101 sendmsg(5, {msg_name={sa_family=AF_INET6, sin6_port=htons(32123), inet_pton(AF_INET6, "::1", &sin6_addr)}},0) = 20\n100 connect(6, {sa_family=AF_UNIX},16) = 0';
  expect(inspectCompositeNetwork(trace, 32123)).toEqual({
    inetOperations: 2,
    externalServiceDestinations: 0,
  });
});
test.each([
  'connect(4, {sa_family=AF_INET, sin_port=htons(32123), sin_addr=inet_addr("192.0.2.1")},16)',
  'sendto(4, ""...,20,0,{sa_family=AF_INET, sin_port=htons(32123), sin_addr=inet_addr("192.0.2.1"),other="127.0.0.1"},16)',
  'connect(4, {sa_family=AF_INET, sin_port=htons(53), sin_addr=inet_addr("127.0.0.1")},16)',
  'connect(4, {sa_family=AF_INET6, sin6_port=htons(32123), inet_pton(AF_INET6, "2001:db8::1", &sin6_addr)},16)',
  'connect(4, {sa_family=AF_INET, address=unknown},16)',
])('rejects external or uncontrolled/ambiguous network destination', (trace) => {
  expect(() => inspectCompositeNetwork(trace, 32123)).toThrow();
});
