import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

describe('action contract', () => {
  const action = readFileSync('.github/actions/agentic-pr-review/action.yml', 'utf8');

  it('uses nested node24 JavaScript action', () => {
    expect(action).toContain('using: node24');
    expect(action).toContain('main: dist/index.js');
  });

  it('declares core public inputs and outputs', () => {
    for (const name of [
      'runtime_backend',
      'runtime_provider',
      'target_mode',
      'review_mode',
      'api_key_mode',
      'tool_mode',
      'claude_max_turns',
      'test_runtime_fixture',
      'max_findings',
      'inline_comments',
      'max_inline_comments',
      'inline_min_severity',
      'inline_min_confidence',
      'max_uncached_input_tokens',
      'max_cached_input_tokens',
      'max_output_tokens',
      'disable_prompt_caching',
      'debug_acknowledgement',
      'artifact_name',
      'review_phase',
      'structured_result_path',
      'rendered_review_markdown_path',
      'structured_output_status',
      'findings_input_count',
      'findings_post_cap_count',
      'findings_rendered_count',
      'findings_truncated',
      'findings_truncation_reason',
      'inline_comments_enabled',
      'inline_comments_candidate_count',
      'inline_comments_effective_cap',
      'inline_comments_cap_exceeded_count',
      'inline_comments_posted_count',
      'inline_comments_duplicate_count',
      'inline_comments_skipped_count',
      'inline_comments_failed_count',
      'observed_turns',
      'observed_turn_source',
      'lineage_observed_turns',
      'lineage_totals_source',
      'lineage_totals_partial',
      'lineage_usage_input_tokens',
      'lineage_usage_cache_read_input_tokens',
      'lineage_usage_cache_creation_input_tokens',
      'lineage_usage_output_tokens',
      'runtime_version',
      'runtime_trace_sha256',
      'runtime_error_kind',
      'runtime_error_class',
      'usage_budget_status',
      'state_transition',
      'state_reason',
      'state_candidate_id',
      'state_marker_id',
      'state_selector_revision',
      'state_session_epoch',
      'state_generation',
      'state_ledger_epoch',
      'state_acceptance_status',
      'state_acceptance_reason',
      'state_publication_status',
      'state_receipt_status',
      'state_cleanup_warnings',
      'state_error_kind',
    ]) {
      expect(action).toContain(name);
    }
  });

  it('wires the checked-in M4 workflows with the action token and frozen ledger inputs', () => {
    for (const workflow of [
      '.github/workflows/m4-stateful-review.yml',
      '.github/workflows/m4-stateful-verification.yml',
    ]) {
      const source = readFileSync(workflow, 'utf8');
      expect(source).toContain('uses: ./.github/actions/agentic-pr-review');
      expect(source).toContain('GITHUB_TOKEN: ${{ github.token }}');
      expect(source).toContain('runtime_backend: ledger-csharp');
      expect(source).toContain('runtime_provider: test');
      expect(source).toContain('target_mode: pull-request');
    }
  });
});
