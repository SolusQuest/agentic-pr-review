# agentic-pr-review

Session-aware agentic PR review tooling for GitHub Actions.

This repository is experimental public tooling. The initial v0.x line is not a stable public API.

The action uses the GitHub Actions `node24` runtime.

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

## Inputs

| Input                                              | Default        | Notes                                                      |
| -------------------------------------------------- | -------------- | ---------------------------------------------------------- |
| `runtime_provider`                                 | `test`         | `test` or `claude-code-cli`                                |
| `target_mode`                                      | `pull-request` | `pull-request` or `synthetic-fixture`                      |
| `review_mode`                                      | `auto`         | `auto`, `bootstrap`, or `incremental`                      |
| `pr_number`                                        | inferred       | Required for pull-request mode outside pull request events |
| `state_key`                                        | derived        | Defaults to the target and runtime                         |
| `state_artifact_run_id`                            | empty          | Optional explicit run id to restore from                   |
| `artifact_retention_days`                          | `7`            | Clamped to 1 through 7                                     |
| `post_comment`                                     | `false`        | Creates or updates a sticky top-level PR comment           |
| `model_base_url`                                   | empty          | Required for `claude-code-cli`                             |
| `model_name`                                       | empty          | Required for `claude-code-cli`                             |
| `small_model_name`                                 | empty          | Optional small/background model                            |
| `api_key_mode`                                     | `auth-token`   | `auth-token`, `api-key`, or `both`                         |
| `claude_code_version`                              | empty          | Required explicit semver for `claude-code-cli`             |
| `instructions` / `instructions_path`               | empty          | Mutually exclusive stable review instructions              |
| `bootstrap_context` / `bootstrap_context_path`     | empty          | Mutually exclusive bootstrap-only context                  |
| `incremental_context` / `incremental_context_path` | empty          | Mutually exclusive incremental-only context                |
| `max_context_chars`                                | `60000`        | Per instruction/context block                              |
| `max_patch_chars`                                  | `120000`       | PR patch context bound                                     |
| `max_review_chars`                                 | `12000`        | Review output bound                                        |
| `disable_prompt_caching`                           | `false`        | Sets `DISABLE_PROMPT_CACHING=1` for live runtime           |
| `debug_capture_raw_api_bodies`                     | `false`        | Restricted trusted manual diagnostic mode                  |
| `debug_acknowledgement`                            | empty          | Must be `allow-raw-provider-debug` for diagnostic mode     |

## Outputs

| Output                    | Notes                                                               |
| ------------------------- | ------------------------------------------------------------------- |
| `state_key`               | Resolved state key                                                  |
| `review_mode`             | Requested review mode                                               |
| `phase`                   | Actual phase: `bootstrap` or `incremental`                          |
| `review_phase`            | Provider result: `bootstrap`, `incremental`, or `skipped-identical` |
| `runtime_provider`        | Runtime used                                                        |
| `session_id`              | Runtime session id                                                  |
| `reviewed_head_sha`       | Reviewed target head SHA                                            |
| `artifact_name`           | Uploaded state artifact name                                        |
| `artifact_id`             | Uploaded state artifact id when available                           |
| `artifact_url`            | Uploaded state artifact URL when available                          |
| `artifact_retention_days` | Effective retention                                                 |
| `review_markdown_path`    | Bounded review markdown path                                        |
| `comment_url`             | Sticky comment URL when comment posting is enabled                  |
| `lineage_action`          | Sticky comment lineage action                                       |
| `lineage_reason`          | Sticky comment lineage reason                                       |
| `debug_artifact_name`     | Restricted diagnostic artifact name when enabled                    |
| `debug_artifact_id`       | Restricted diagnostic artifact id when enabled                      |
| `debug_artifact_url`      | Restricted diagnostic artifact URL when enabled                     |

## State Artifacts

The action owns state restore and upload. It discovers the latest matching state artifact, restores the
runtime session for incremental runs, writes a sanitized state bundle, and uploads a new state artifact.

State artifact names use:

```text
agentic-pr-review-state-${state_key}
```

The state bundle includes a manifest, the bounded review markdown, and the sanitized runtime session
directory needed for the next incremental run. Normal state artifacts reject raw/debug files, configured
secret values, high-risk token prefixes, and unredacted auth headers.

`review_mode=incremental` fails if no valid state can be restored. `review_mode=auto` restores when a
matching state exists and otherwise starts a bootstrap phase. For pull request targets, incremental mode
compares the prior reviewed head to the current head:

- compare 404 or diverged history falls back to bootstrap in `auto` and fails in forced `incremental`
- identical ranges upload refreshed state and set `review_phase=skipped-identical` without calling the provider
- non-identical ahead ranges send only the compare-range patch plus incremental context

Fork pull requests are not supported. The action reads PR metadata and patches through GitHub APIs, but
session continuity and comment lineage are scoped to same-repository pull requests.

## Live Smoke

`.github/workflows/live-smoke.yml` is a manual `workflow_dispatch` workflow for trusted maintainers.
It uses `target_mode=synthetic-fixture`, `runtime_provider=claude-code-cli`, and `post_comment=false`.

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

It is only allowed when all gates are true:

- `runtime_provider=claude-code-cli`
- `target_mode=synthetic-fixture`
- workflow event is `workflow_dispatch`
- `debug_capture_raw_api_bodies=true`
- `debug_acknowledgement=allow-raw-provider-debug`

The diagnostic artifact is separate from the normal state artifact, its name includes `raw`, and its
retention is exactly 1 day. This mode is for trusted manual diagnostic runs only.

## Public-Safe Usage Constraints

Do not print or upload full prompts, raw requests, raw responses, secrets, or credentials in normal logs,
comments, summaries, or state artifacts. Keep project-specific review instructions in the caller
repository and pass them through `instructions_path` or the direct instruction/context inputs.
