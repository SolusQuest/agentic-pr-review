/**
 * Bounded, allowlisted extraction of the `code` property from an unknown cause
 * (typically a Node Error). Only strings matching /^[A-Z0-9_]{1,32}$/ are
 * returned; anything else becomes undefined. This is the single sanitizer for
 * cross-module error-code exposure so no raw Error, path, or stack is ever
 * exposed through a `spawnErrorCode`, `diagnosticCode`, or any other adapter
 * result / error field.
 */
export function sanitizeErrorCode(cause: unknown): string | undefined {
  if (cause === null || typeof cause !== 'object') return undefined;
  const code = (cause as { code?: unknown }).code;
  return typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : undefined;
}
