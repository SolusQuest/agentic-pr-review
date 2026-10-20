// Failure diagnostics only. These literal assertion codes are independently
// bound to authored Check sites; prefixes and arbitrary child text confer no authority.
export const cutoverFailureCodes = Object.freeze([
  'body_privacy_bodies_withheld_secrets',
  'body_privacy_buffer_completed',
  'body_privacy_control_completed',
  'body_privacy_control_environment_cleared',
  'body_privacy_control_initial_absence',
  'body_privacy_control_lineage',
  'body_privacy_control_same_inputs',
  'body_privacy_credentials',
  'body_privacy_dispatch_observed',
  'body_privacy_environment_cleared',
  'body_privacy_first_withheld_fact',
  'body_privacy_headers_authorized',
  'body_privacy_initial_absence',
  'body_privacy_observed_requests',
  'body_privacy_observer_created',
  'body_privacy_read_completed',
  'body_privacy_real_completion',
  'body_privacy_request_bound',
  'body_privacy_request_shape',
  'body_privacy_second_used_fact',
  'body_privacy_single_generation',
  'capture_current_stage',
  'commit_failure_actual_current_record',
  'commit_failure_authenticated_record',
  'commit_failure_durable_acceptance',
  'commit_failure_environment_cleared',
  'commit_failure_exact_accepted_binding',
  'commit_failure_initial_absence',
  'commit_failure_key',
  'commit_failure_no_output_lineage',
  'commit_failure_primary_cause',
  'commit_failure_single_publish',
  'commit_failure_targets',
  'incompatible_current_stage',
  'precancel_classification_unexpected',
  'precancel_classified_conflict',
  'precancel_classified_root_invalid',
  'precancel_environment_cleared',
  'precancel_fresh_not_accepted',
  'precancel_initial_absent',
  'precancel_lineage_unchanged',
  'precancel_no_publication_receipt',
  'precancel_real_current_source',
  'precancel_source_not_burned',
  'precancel_state_unchanged',
  'precancel_valid_control',
  'precancel_zero_work',
  'secret_current_stage',
]);
const allowed = new Set(cutoverFailureCodes);

export function projectCutoverFailureCode(input) {
  try {
    if (typeof input !== 'string' && !(input instanceof Uint8Array)) return null;
    if ((typeof input === 'string' ? Buffer.byteLength(input) : input.byteLength) > 65536)
      return null;
    const text =
      typeof input === 'string'
        ? input
        : new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(input);
    // spawnSync's UTF-8 decoding replaces malformed bytes. Requiring ASCII also
    // rejects that replacement, lone surrogates, ANSI and every control except CR/LF.
    if (/[^\x20-\x7e\r\n]/u.test(text)) return null;
    const line = text.endsWith('\r\n')
      ? text.slice(0, -2)
      : text.endsWith('\n')
        ? text.slice(0, -1)
        : text;
    if (line.length > 256 || /[\r\n]/u.test(line)) return null;
    const match = /^APR_SOLUS_DIRECT_FAULTS_FAILED ([a-z0-9_]+)$/u.exec(line);
    return match && allowed.has(match[1]) ? match[1] : null;
  } catch {
    return null;
  }
}
