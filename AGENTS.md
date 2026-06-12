# Agent Operating Rules

This file defines operating rules for AI agents working in `SolusQuest/agentic-pr-review`.

## Scope

You are working only in `SolusQuest/agentic-pr-review`. Use only this repository, the current task prompt, and public context.

## Context boundaries

- Do not assume access to private repositories, private issue trackers, private logs, private prompts, or private credentials.
- If required information is missing, ask for a public-safe clarification.
- Do not include non-public issue links, private repository details, prompts, transcripts, workflow logs, credentials, or secrets in files, commits, PR bodies, comments, CI logs, or artifacts.

## CI rules

- `pull_request` / `push` CI must run without provider secrets.
- Use synthetic fixtures or placeholder validation unless a task explicitly defines otherwise.
- Do not use `pull_request_target` without explicit security review.

## PR rules

- PR body must be public-safe and technically self-contained.
- PR body should summarize objective, behavior changes, and validation results.
- Do not paste external task documents or task prompts verbatim into PR body.

## Issue rules

- Issues in this repo should be describable using public information only.
- Do not reference private PM issue numbers, private repo names, or private planning context.
