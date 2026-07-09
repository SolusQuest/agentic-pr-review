# agentic-pr-review

Session-aware agentic PR review tooling for GitHub Actions.

The action uses a structured-first review contract. Runtime providers must emit a
`ModelReviewContentV1` JSON object; the action validates it, injects trusted workflow metadata,
generates finding fingerprints, caps findings, writes a structured result artifact, and renders the
sticky top-level PR comment from the validated structured data.

This repository is experimental public tooling. The initial v0.x line is not a stable public API.

The action uses the GitHub Actions `node24` runtime.

## Project Docs

- `docs/00_project/project-context.md` describes the project role and source-of-truth model.
- `docs/20_architecture/architecture.md` describes the long-term TypeScript adapter plus C# runtime direction.
- `docs/20_architecture/security-boundary.md` describes token, side-effect, and artifact boundaries.
- `docs/50_ai/agent-context.md` is the public agent context entrypoint.
- `docs/90_roadmap/roadmap-seed.md` describes the public roadmap, near-term milestones, and issue
  plan.

## Usage

```yaml
name: Agentic PR Review

on:
  pull_request:

permissions:
  contents: read
  pull-requests: read
  actions: read

jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@<sha>
        with:
          runtime_provider: test
          target_mode: pull-request
          review_mode: auto
        env:
          GITHUB_TOKEN: ${{ github.token }}
```

`GITHUB_TOKEN` must be passed explicitly because the JavaScript action reads
`process.env.GITHUB_TOKEN`.

Path inputs such as `instructions_path`, `bootstrap_context_path`, and `incremental_context_path`
are read from `GITHUB_WORKSPACE`. When a workflow uses live provider secrets, check out a trusted
ref such as the repository default branch before using path inputs. If `tool_mode=readonly` should
inspect pull request files, place the reviewed head checkout in the review workspace separately from
trusted instruction and context files.

For a live runtime, pass provider configuration as inputs and the API key as an env secret:

```yaml
- uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@<sha>
  with:
    runtime_provider: claude-code-cli
    target_mode: pull-request
    review_mode: auto
    model_base_url: ${{ vars.AGENTIC_REVIEW_CLAUDE_CODE_BASE_URL }}
    model_name: ${{ vars.AGENTIC_REVIEW_CLAUDE_CODE_MODEL }}
    small_model_name: ${{ vars.AGENTIC_REVIEW_CLAUDE_CODE_SMALL_MODEL }}
    api_key_mode: ${{ vars.AGENTIC_REVIEW_CLAUDE_CODE_API_KEY_MODE || 'auth-token' }}
    claude_code_version: ${{ vars.AGENTIC_REVIEW_CLAUDE_CODE_VERSION }}
    tool_mode: none
    claude_max_turns: '6'
    max_uncached_input_tokens: '0'
    max_cached_input_tokens: '0'
    max_output_tokens: '0'
    disable_prompt_caching: 'false'
    instructions_path: .github/agentic-pr-review-instructions.md
  env:
    GITHUB_TOKEN: ${{ github.token }}
    AGENTIC_REVIEW_API_KEY: ${{ secrets.AGENTIC_REVIEW_CLAUDE_CODE_API_KEY }}
```

## Permissions

Without PR comments:

```yaml
permissions:
  contents: read
  pull-requests: read
  actions: read
```

With `post_comment: "true"`:

```yaml
permissions:
  contents: read
  pull-requests: write
  actions: read
```

With `inline_comments: "true"`, use the same write permission:

```yaml
permissions:
  contents: read
  pull-requests: write
  actions: read
```

## Inputs

| Input                                              | Default        | Notes                                                                                                                                                                                                                 |
| -------------------------------------------------- | -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `runtime_provider`                                 | `test`         | `test` or `claude-code-cli`                                                                                                                                                                                           |
| `target_mode`                                      | `pull-request` | `pull-request` or `synthetic-fixture`                                                                                                                                                                                 |
| `review_mode`                                      | `auto`         | `auto`, `bootstrap`, or `incremental`                                                                                                                                                                                 |
| `pr_number`                                        | inferred       | Required for pull-request mode outside pull request events                                                                                                                                                            |
| `state_key`                                        | derived        | Defaults to the target and runtime                                                                                                                                                                                    |
| `state_artifact_run_id`                            | empty          | Optional explicit run id to restore from                                                                                                                                                                              |
| `artifact_retention_days`                          | `7`            | Clamped to 1 through 7                                                                                                                                                                                                |
| `post_comment`                                     | `false`        | Creates or updates a sticky top-level PR comment                                                                                                                                                                      |
| `model_base_url`                                   | empty          | Required for `claude-code-cli`                                                                                                                                                                                        |
| `model_name`                                       | empty          | Required for `claude-code-cli`                                                                                                                                                                                        |
| `small_model_name`                                 | empty          | Optional small/background model                                                                                                                                                                                       |
| `api_key_mode`                                     | `auth-token`   | `auth-token`, `api-key`, or `both`                                                                                                                                                                                    |
| `claude_code_version`                              | empty          | Required explicit semver for `claude-code-cli`                                                                                                                                                                        |
| `tool_mode`                                        | `none`         | `none` disables runtime tools; `readonly` allows only Claude Code `Read`, `Glob`, and `Grep` for live runtime                                                                                                         |
| `claude_max_turns`                                 | `6`            | Positive integer passed to Claude Code `--max-turns`                                                                                                                                                                  |
| `instructions` / `instructions_path`               | empty          | Mutually exclusive stable review instructions                                                                                                                                                                         |
| `bootstrap_context` / `bootstrap_context_path`     | empty          | Mutually exclusive bootstrap-only context                                                                                                                                                                             |
| `incremental_context` / `incremental_context_path` | empty          | Mutually exclusive incremental-only context                                                                                                                                                                           |
| `max_context_chars`                                | `60000`        | Per instruction/context block                                                                                                                                                                                         |
| `max_patch_chars`                                  | `120000`       | PR patch context bound                                                                                                                                                                                                |
| `max_review_chars`                                 | `12000`        | Rendered review markdown bound for the posted current review and structured artifacts                                                                                                                                 |
| `max_findings`                                     | `50`           | Maximum normalized findings included in the sticky comment and structured result                                                                                                                                      |
| `inline_comments`                                  | `false`        | Posts eligible structured findings as inline PR review comments. Requires `post_comment=true` because the sticky review remains the source of truth                                                                   |
| `max_inline_comments`                              | `5`            | Maximum inline comments to post, clamped to 0 through 10                                                                                                                                                              |
| `inline_min_severity`                              | `medium`       | Minimum severity for inline comments: `low`, `medium`, or `high`                                                                                                                                                      |
| `inline_min_confidence`                            | `high`         | Minimum confidence for inline comments: `medium` or `high`                                                                                                                                                            |
| `test_runtime_fixture`                             | `valid`        | Structured fixture for `runtime_provider=test`: `valid`, `no_findings`, `null_location`, `many_findings`, `inline_commentable`, `inline_non_commentable`, `inline_many_findings`, `invalid_json`, or `schema_invalid` |
| `max_uncached_input_tokens`                        | `0`            | Current-run `input_tokens` watchdog; `0` disables                                                                                                                                                                     |
| `max_cached_input_tokens`                          | `0`            | Current-run cache-read/cache-hit token watchdog; `0` disables                                                                                                                                                         |
| `max_output_tokens`                                | `0`            | Current-run `output_tokens` watchdog; `0` disables                                                                                                                                                                    |
| `disable_prompt_caching`                           | `false`        | Sets `DISABLE_PROMPT_CACHING=1` for live runtime                                                                                                                                                                      |
| `debug_capture_raw_api_bodies`                     | `false`        | Restricted trusted manual diagnostic mode                                                                                                                                                                             |
| `debug_acknowledgement`                            | empty          | Required acknowledgement phrase for diagnostic mode                                                                                                                                                                   |

`tool_mode=readonly` is only meaningful for `runtime_provider=claude-code-cli`. It restricts the
Claude Code built-in tool surface to `Read`, `Glob`, and `Grep`; shell, network, edit/write,
subagent, skill, and MCP tool surfaces are not enabled by this mode. Readonly tools supplement the
deterministic PR metadata, current PR file list, bounded patch context, and incremental PR diff
snapshot delta that the action already supplies. They do not replace that deterministic context or
expand review scope beyond current PR files.

When live provider secrets are available, the checked-out workspace is a caller-controlled trust
boundary. If a downstream workflow wants readonly tools to inspect reviewed code, check out the
reviewed head by immutable SHA into the review workspace before running the action.

Usage watchdogs parse Claude Code `stream-json` usage records during the live run. Delta-style usage
records are summed; cumulative records replace earlier cumulative totals and take precedence. Cache-hit
field names such as `prompt_cache_hit_tokens` are normalized into `cache_read_input_tokens`. Missing
categories are treated as zero, so a budget only constrains categories observed or inferred from the
stream. If a non-zero budget is exceeded, the action terminates the process and fails with a sanitized
`usage_budget_exceeded` diagnostic. If any usage budget is non-zero and the live runtime exposes no usage records, the action
fails closed after the run. The watchdog acts after observed usage, so it stops later turns but cannot
preempt the provider call that emitted the over-budget usage record.

## Outputs

| Output                                      | Notes                                                                                   |
| ------------------------------------------- | --------------------------------------------------------------------------------------- |
| `state_key`                                 | Resolved state key                                                                      |
| `review_mode`                               | Requested review mode                                                                   |
| `phase`                                     | Actual phase: `bootstrap` or `incremental`                                              |
| `review_phase`                              | Provider result: `bootstrap`, `incremental`, or `skipped-identical`                     |
| `runtime_provider`                          | Runtime used                                                                            |
| `session_id`                                | Runtime session id                                                                      |
| `reviewed_head_sha`                         | Reviewed target head SHA                                                                |
| `artifact_name`                             | Uploaded state artifact name                                                            |
| `artifact_id`                               | Uploaded state artifact id when available                                               |
| `artifact_url`                              | Uploaded state artifact URL when available                                              |
| `artifact_retention_days`                   | Effective retention                                                                     |
| `structured_result_path`                    | Validated structured review result JSON path                                            |
| `rendered_review_markdown_path`             | Rendered markdown path generated from the structured result                             |
| `structured_output_status`                  | Structured validation status: `valid`, `extracted`, `invalid_json`, or `schema_invalid` |
| `findings_input_count`                      | Normalized finding count before cap                                                     |
| `findings_post_cap_count`                   | Finding count after `max_findings` and before rendered markdown fitting                 |
| `findings_rendered_count`                   | Finding count rendered in the sticky comment and structured artifacts                   |
| `findings_truncated`                        | Whether findings were truncated by `max_findings` or `max_review_chars`                 |
| `findings_truncation_reason`                | `max_findings`, `max_review_chars`, `both`, or empty when not truncated                 |
| `inline_comments_enabled`                   | Whether inline PR review comments were enabled                                          |
| `inline_comments_candidate_count`           | Eligible inline candidates before applying `max_inline_comments`                        |
| `inline_comments_effective_cap`             | Effective inline comment cap after clamping                                             |
| `inline_comments_cap_exceeded_count`        | Eligible inline candidates omitted by the cap                                           |
| `inline_comments_posted_count`              | Inline comments posted by this run                                                      |
| `inline_comments_duplicate_count`           | Inline candidates suppressed by existing duplicate markers                              |
| `inline_comments_skipped_count`             | Inline candidates skipped before posting                                                |
| `inline_comments_failed_count`              | Inline posting failures recorded without hiding sticky findings                         |
| `comment_url`                               | Sticky comment URL when comment posting is enabled                                      |
| `lineage_action`                            | Sticky comment lineage action                                                           |
| `lineage_reason`                            | Sticky comment lineage reason                                                           |
| `debug_artifact_name`                       | Restricted diagnostic artifact name when enabled                                        |
| `debug_artifact_id`                         | Restricted diagnostic artifact id when enabled                                          |
| `debug_artifact_url`                        | Restricted diagnostic artifact URL when enabled                                         |
| `observed_turns`                            | Current-run observed agent turns (empty string when unavailable)                        |
| `observed_turn_source`                      | Turn count source: `unique_assistant_message_ids`, `not_applicable`, or `unavailable`   |
| `lineage_observed_turns`                    | Lineage cumulative observed turns                                                       |
| `lineage_totals_source`                     | Lineage data source                                                                     |
| `lineage_totals_partial`                    | Whether lineage totals are partial (legacy manifest)                                    |
| `lineage_usage_input_tokens`                | Lineage cumulative input tokens                                                         |
| `lineage_usage_cache_read_input_tokens`     | Lineage cumulative cache-read tokens                                                    |
| `lineage_usage_cache_creation_input_tokens` | Lineage cumulative cache-creation tokens                                                |
| `lineage_usage_output_tokens`               | Lineage cumulative output tokens                                                        |

## Observed Turns and Lineage Totals

The action tracks agent turns and cumulative lineage totals across review runs.

**Current-run turn tracking**: The action counts distinct assistant message IDs from the
`stream-json` output. Each top-level `type: "assistant"` record contributes its `message.id`
to a distinct set. The count is exposed as `observed_turns` (null when no assistant records
are observed) with `observed_turn_source` set to `unique_assistant_message_ids`,
`not_applicable` (test runtime), or `unavailable`.

**Lineage totals** accumulate turns and token usage across runs within a review lineage:

- `current_run_only` -- bootstrap or first run in a lineage
- `restored_manifest_plus_current_run` -- incremental run adding current to prior lineage totals
- `restored_manifest_preserved_for_skipped` -- skipped-identical; prior lineage preserved unchanged
- `legacy_manifest_fallback` -- old manifest without lineage data; partial totals from current run only
- `unavailable` -- no data available

**Token field normalization**: The `cacheReadInputTokens` field is the normalized cache-hit
total. The legacy `prompt_cache_hit_tokens` field name in stream records is read and normalized
into `cacheReadInputTokens` but is no longer exposed separately.

## Structured Review Contract

Runtime output is no longer Markdown-first. Successful runtime output must be JSON:

```json
{
  "schemaVersion": 1,
  "summary": "Concise review summary.",
  "findings": [],
  "limitations": []
}
```

Each finding includes `severity`, `confidence`, `category`, `title`, `body`, `path`, `startLine`,
`endLine`, and optional `suggestedAction`. `path` must be `null` or a safe repo-relative path; absolute
paths, drive-qualified paths, protocol-looking paths, current-dir-only paths, and `..` path segments
are rejected. When both line values are present, `endLine` must be greater than or equal to
`startLine`. `confidence` accepts only `medium` or `high`; low-confidence observations should be
omitted by the model. The model must not provide workflow facts or finding fingerprints.

The action-owned `StructuredReviewEnvelopeV1` injects trusted `phase`, `baseSha`, `headSha`,
`previousReviewedHeadSha`, structured `reviewedRange`, `toolMode`, `runtimeProvider`, session, usage,
turn, lineage, finding-count, and truncation metadata. Bootstrap `reviewedRange.fromSha` is `null`;
incremental ranges use the prior reviewed head when available. Findings are normalized and
fingerprinted by the action before comment rendering or artifact upload. For pull request targets,
non-null finding paths outside the current PR files returned by GitHub are dropped before rendering,
artifacts, sticky comments, or inline comment selection; `path=null` remains allowed for PR-level
observations.

The action caps findings before writing `structured-result.json`, `rendered-review.md`, or the sticky
comment. It first applies `max_findings`, then further reduces findings if needed so the rendered
current review fits `max_review_chars`. The structured result artifact and sticky comment therefore
use the same final finding set; artifacts do not retain extra findings hidden from the posted review.

Inline comments are disabled by default. `inline_comments=true` requires `post_comment=true`; otherwise
inline posting is skipped and findings remain in the structured artifacts without inline review threads.
When enabled with sticky comment posting, the action selects findings only from this final validated
finding set, filters them by severity and confidence, verifies that each location maps to a current-side
line in the current PR diff, suppresses existing marker duplicates, and posts at most the effective cap.
Findings without a repo-relative path and current-head start line, outside the diff, in binary or
missing-patch files, or on deleted-line-only locations stay in the sticky review only. If the PR head
changes before posting, or diff/comment pagination reaches GitHub-supported limits, inline posting is
skipped while the sticky review and state artifact flow continue.

Inline comment bodies include only validated structured finding content plus a hidden generic marker:

```text
<!-- agentic-pr-review:inline:v1 key=<sha256> -->
```

The duplicate key is derived from the action-owned finding fingerprint, state key, path, and line/range.
It does not include model names, runtime session ids, provider session ids, workflow run ids, or sticky
comment lineage ids. Inline comment metadata is written under `inlineComments` in `structured-result.json`
and under `structuredOutput.inlineComments` in the state manifest.

For deterministic downstream smoke tests with `runtime_provider=test`, use `inline_commentable` to emit
a finding on the first available current-side PR diff line, `inline_non_commentable` to keep a finding
sticky-only, and `inline_many_findings` to exercise inline caps and duplicate suppression.

If model JSON cannot be parsed or schema-validated after deterministic local cleanup such as trimming
whitespace or extracting a fenced JSON object, the action fails closed. Invalid output does not update
the sticky comment, upload successful state, advance the reviewed head, or write raw invalid model text
to normal summaries, comments, manifests, rendered review markdown, or structured result artifacts.

## State Artifacts

The action owns state restore and upload. It discovers the latest matching state artifact, restores the
runtime session for incremental runs, writes a sanitized state bundle, and uploads a new state artifact.

State artifact names use:

```text
agentic-pr-review-state-${state_key}
```

The state bundle includes a manifest, `structured-result.json`, `rendered-review.md`, and the sanitized
runtime session directory needed for the next incremental run. Normal state artifacts reject raw/debug
files, configured secret values, high-risk token prefixes, and unredacted auth headers.

For pull request targets, the current effective GitHub PR diff from `pulls.listFiles` is the
authoritative review scope. Bootstrap runs store a normalized PR diff snapshot in the state manifest.
Incremental runs restore the previous compatible snapshot, build a current snapshot from the current PR
files, and send only changed current PR diff entries plus incremental context. Unchanged current PR
diff entries remain in scope metadata but do not consume bounded patch budget. Files that existed only
in a raw commit compare range are not prompt patch context. Snapshot entries store patch hashes when
GitHub provides patch text and the PR file SHA when available, so patch-unavailable files can still be
detected as changed. If a patch-unavailable file lacks a stable file SHA, the action treats it
conservatively as changed.

If previous snapshot-compatible PR state is missing or incompatible, both `review_mode=auto` and forced
`review_mode=incremental` start a bootstrap phase under the current state schema. The job summary and
state manifest record the requested mode, executed phase, phase reason such as
`snapshot_state_missing`, and effective diff source. If previous and current PR diff snapshot entries
are equal, the action uploads refreshed state and sets `review_phase=skipped-identical` without calling
the provider.

Fork pull requests are not supported. The action reads PR metadata and patches through GitHub APIs, but
session continuity and comment lineage are scoped to same-repository pull requests.

Public CI covers no-secret action wiring with the `test` runtime, including local artifact restore and
real action artifact upload in the synthetic fixture workflow. Full provider execution and provider
session resume are covered by the manual live smoke workflow after repository variables and secrets are
configured.

## Live Smoke

`.github/workflows/live-smoke.yml` is a manual `workflow_dispatch` workflow for trusted maintainers.
It uses `target_mode=synthetic-fixture`, `runtime_provider=claude-code-cli`, and `post_comment=false`.
The workflow fails unless it is dispatched from the repository default branch.

Required repository variables:

- `AGENTIC_REVIEW_CLAUDE_CODE_BASE_URL`
- `AGENTIC_REVIEW_CLAUDE_CODE_MODEL`
- `AGENTIC_REVIEW_CLAUDE_CODE_VERSION`
- `AGENTIC_REVIEW_CLAUDE_CODE_SMALL_MODEL` (optional)
- `AGENTIC_REVIEW_CLAUDE_CODE_API_KEY_MODE` (optional)

Required repository secret:

- `AGENTIC_REVIEW_CLAUDE_CODE_API_KEY`

## Raw Body Diagnostic Mode

Raw provider diagnostic capture is disabled by default and is not enabled in examples.

It is only allowed when all gates are true for synthetic diagnostics:

- `runtime_provider=claude-code-cli`
- `target_mode=synthetic-fixture`
- workflow event is `workflow_dispatch`
- `debug_capture_raw_api_bodies=true`
- `debug_acknowledgement=allow-raw-provider-debug`

For same-repository public pull request diagnostics, use `target_mode=pull-request`,
`workflow_dispatch`, and `debug_acknowledgement=allow-raw-provider-debug-public-pr`.
Do not use this mode with private repository context, private instructions, or fork pull requests.

The diagnostic artifact is separate from the normal state artifact, its name includes `raw`, and its
retention is exactly 1 day. This mode is for trusted manual diagnostic runs only.

## Public-Safe Usage Constraints

Do not print or upload full prompts, raw requests, raw responses, secrets, or credentials in normal logs,
comments, summaries, or state artifacts. Keep project-specific review instructions in the caller
repository and pass them through `instructions_path` or the direct instruction/context inputs.
