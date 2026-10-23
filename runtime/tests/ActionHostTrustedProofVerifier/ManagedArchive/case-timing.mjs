import { GROUP_CLOSE_MS, ROLE_DURATION_MS } from './role-deadline.mjs';
export const CASE_TIMING_PREFIX = 'APR_D5_ARCHIVE_CASE_TIMING ';
export const ARCHIVE_CASE_COUNT = 35;
const keys = [
  'kind',
  'role',
  'ordinal',
  'elapsed_ms',
  'role_elapsed_ms',
  'closed',
  'watchdog_fired',
];
export function caseTimingLine(value) {
  if (
    !value ||
    typeof value !== 'object' ||
    Array.isArray(value) ||
    Object.keys(value).length !== keys.length ||
    keys.some((key) => !Object.hasOwn(value, key)) ||
    value.kind !== 'apr-d5-case-timing-v1' ||
    !['first', 'independent-second'].includes(value.role) ||
    !Number.isInteger(value.ordinal) ||
    value.ordinal < 1 ||
    value.ordinal > ARCHIVE_CASE_COUNT ||
    ![value.elapsed_ms, value.role_elapsed_ms].every(
      (n) => Number.isInteger(n) && n >= 0 && n <= ROLE_DURATION_MS + GROUP_CLOSE_MS,
    ) ||
    typeof value.closed !== 'boolean' ||
    typeof value.watchdog_fired !== 'boolean'
  )
    throw new Error('d5_case_timing_invalid');
  const line =
    CASE_TIMING_PREFIX + JSON.stringify(Object.fromEntries(keys.map((key) => [key, value[key]])));
  if (Buffer.byteLength(line) > 256) throw new Error('d5_case_timing_bound');
  return line;
}
export function extractCaseTimings(stderr, role) {
  const lines = stderr.split('\n').filter((line) => line.startsWith(CASE_TIMING_PREFIX));
  if (lines.length > ARCHIVE_CASE_COUNT) throw new Error('d5_case_timing_bound');
  return lines.map((line, index) => {
    if (Buffer.byteLength(line) > 256) throw new Error('d5_case_timing_bound');
    const value = JSON.parse(line.slice(CASE_TIMING_PREFIX.length));
    if (value.role !== role || value.ordinal !== index + 1) throw new Error('d5_case_timing_order');
    return caseTimingLine(value);
  });
}
