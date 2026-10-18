import { expect, test } from 'vitest';
import { auditNetwork } from './network-audit.mjs';

const probe = [
  'socket(AF_INET6, SOCK_DGRAM|SOCK_CLOEXEC, IPPROTO_IP) = 40',
  'close(40) = 0',
  'socket(AF_INET, SOCK_DGRAM|SOCK_CLOEXEC, IPPROTO_IP) = 40',
  'close(40) = 0',
  'socket(AF_UNIX, SOCK_DGRAM|SOCK_CLOEXEC, 0) = 40',
  'close(40) = 0',
].join('\n');

test('admits only the complete unused platform capability sequence and local bridge traffic', () => {
  expect(auditNetwork([''])).toEqual({ activity: false, capabilityProbes: 0 });
  const local =
    'socket(AF_UNIX, SOCK_STREAM|SOCK_CLOEXEC, 0) = 37\n' +
    probe +
    '\nconnect(37, {sa_family=AF_UNIX, sun_path="synthetic"}, 27) = 0';
  expect(auditNetwork([local, 'close(5) = 0'])).toEqual({ activity: false, capabilityProbes: 1 });
});

test.each([
  'socket(AF_INET, SOCK_STREAM|SOCK_CLOEXEC, IPPROTO_TCP) = 40',
  'socket(AF_INET, SOCK_DGRAM|SOCK_CLOEXEC, IPPROTO_IP) = 40\nclose(40) = 0',
  'connect(40, {sa_family=AF_INET, sin_port=htons(9)}, 16) = -1 ECONNREFUSED',
  'sendto(40, "synthetic", 9, 0, {sa_family=AF_INET6}, 28) = -1 ENETUNREACH',
  'sendmsg(40, {msg_name={sa_family=AF_INET}}, 0) = -1 ENETUNREACH',
  'sendmmsg(40, [{msg_hdr={msg_name={sa_family=AF_INET6}}}], 1, 0) = -1 ENETUNREACH',
])('detects Internet activity even alongside a capability probe: %s', (activity) => {
  expect(auditNetwork([probe, activity]).activity).toBe(true);
});

test.each([
  probe.replace('close(40) = 0', 'close(41) = 0'),
  probe.replace('close(40) = 0', 'close(40) = -1 EBADF'),
  probe.replace('IPPROTO_IP', 'IPPROTO_UDP'),
  probe.split('\n').slice(0, 4).join('\n'),
  probe.replace('close(40) = 0', 'connect(40, {sa_family=AF_INET6}, 28) = 0\nclose(40) = 0'),
  probe + '\n' + probe,
])('fails closed for changed, addressed, incomplete or repeated capability probes', (trace) => {
  expect(auditNetwork([trace]).activity).toBe(true);
});

test('counts capability sequences across all threads in the launched process', () => {
  expect(auditNetwork([probe, probe])).toEqual({ activity: true, capabilityProbes: 2 });
});
