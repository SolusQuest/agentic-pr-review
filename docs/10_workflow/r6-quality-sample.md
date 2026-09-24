# R6 quality sample and offline admission

This document records the controlled sample prepared for [R6-V4 issue #294](https://github.com/SolusQuest/agentic-pr-review/issues/294). It is an admission record, not a live model-quality result. The [sandbox PR #1](https://github.com/SolusQuest/agentic-pr-review-sandbox/pull/1) is synthetic, public, intentionally contains three defects and two safe controls, and remains open as a stable source artifact.

## Frozen reviewed source

| Field                 | Value                                                                                                        |
| --------------------- | ------------------------------------------------------------------------------------------------------------ |
| Repository            | `SolusQuest/agentic-pr-review-sandbox`                                                                       |
| Numeric repository ID | `1383960589`                                                                                                 |
| PR target             | `1`                                                                                                          |
| Base commit           | `9eec5432d1e11e7823804f4de61f97dfb07822e6`                                                                   |
| Head commit           | `ac9d7a41f971e4b1071313c979b41b20637bfef4`                                                                   |
| Changed files         | `src/Caller.cs`, `src/SafeCaller.cs`, `src/Upload.cs`, `src/client.ts`, `src/safe-client.ts`                 |
| Frozen corpus         | [`runtime/tests/fixtures/agent/r6/quality-sandbox/`](../../runtime/tests/fixtures/agent/r6/quality-sandbox/) |
| Manifest SHA-256      | `930a17f475e09d665551d8e8d1def08bfa1033f97ede1278a96c9375fbabfc2f`                                           |
| Diff member SHA-256   | `dc08dfdeea4366ec3292d96cb16d6fc2caac6126bde1d46173eea4ad0f8bfee5`                                           |

The head contains ten files. The corpus stores the exact head bytes under `source/<repository path>` and the five added-file hunks in `diff.json`. The manifest lists the length and SHA-256 of every member; its five runs all pin the same repository, PR, base and head identity. A comparison of all ten local Git blob IDs with the public PR head's Git tree matched. Evaluation reads only the admitted frozen corpus; it does not fetch the live PR.

| Sandbox head path    | Corpus member               | SHA-256 of member bytes                                            |
| -------------------- | --------------------------- | ------------------------------------------------------------------ |
| `README.md`          | `source/README.md`          | `dfdff1e6d38af4ce8eb36abd2d286643cb096b6ca6083d1f494c0c0f448fa888` |
| `docs/notes.txt`     | `source/docs/notes.txt`     | `b54b07a1e235690ad5bb51c5173f2514775d6cb779749e827469bfd16d3569ec` |
| `rules/review.md`    | `source/rules/review.md`    | `c6829cbce2e3ad2a70d99b894a4715c0ca76ffb619747727f32e3ae4c6cd5bba` |
| `src/Caller.cs`      | `source/src/Caller.cs`      | `4f672cc8a0ae9afe963d5a87476db4f15c74d6dac77ec973fa44b2ff436f84b7` |
| `src/Lookup.cs`      | `source/src/Lookup.cs`      | `26f842df85e844e212318bbffba989c8bafb008d5e51e1c8510fd4ff5abc1087` |
| `src/SafeCaller.cs`  | `source/src/SafeCaller.cs`  | `dd87e2ba74ebe72aaaf9e3ad3c080f962e36a8733def5901943912f902dab08c` |
| `src/Upload.cs`      | `source/src/Upload.cs`      | `a37bf142680a6b9d07bc1f5a5c4f213a946f42f654af3f99c1e1ce55c67de858` |
| `src/client.ts`      | `source/src/client.ts`      | `abe06c6eed2cef01e3ab03ed8eda75e57503fc8919752bd46aea3b24da27ac09` |
| `src/config.ts`      | `source/src/config.ts`      | `d63aa649545e66117b1e7156c5a209912f21a436168e6e12b9a9e812b30635fe` |
| `src/safe-client.ts` | `source/src/safe-client.ts` | `1013f908ac399af77f5cde7839740c651584078b40f09dae3e90b515a2eeb362` |

## Ground truth fixed before live execution

The existing `r5-plan` command admits only its thirteen-case quality set or this five-case ID order. This corpus deliberately uses the five IDs and order of the latter while replacing the source and expected facts. Each initial case context names only a changed path. The unchanged facts below must be found through actual repository tools; the policy and initial contexts contain no expected answer. Scripts and assertions are admitted offline and never sent to DeepSeek.

| Case              | Expected outcome and changed-line location               | Required returned `read_file` facts                                                                                                    | Invalid or prohibited outcome                                                                                                                                |
| ----------------- | -------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `cs-defect`       | Find the nullable dereference at `src/Caller.cs:5`.      | `Caller.cs:5` calls `Lookup.Find(key).Trim()`; unchanged `Lookup.cs:5` can return null for `missing`.                                  | A finding without both returned facts is ungrounded.                                                                                                         |
| `cs-safe`         | No nullable dereference at `src/SafeCaller.cs:5`.        | `SafeCaller.cs:5` uses `?.Trim() ?? string.Empty`; `Lookup.cs:5` can return null.                                                      | A finding on the guarded line is prohibited.                                                                                                                 |
| `ts-defect`       | Find the zero-value fallback at `src/client.ts:2`.       | `client.ts:2` uses a truthy fallback of `3000`; unchanged `config.ts:1-2` defines zero as a disabled deadline and sets `timeoutMs: 0`. | A finding without zero semantics is ungrounded.                                                                                                              |
| `ts-safe`         | No zero-value fallback defect at `src/safe-client.ts:2`. | `safe-client.ts:2` uses `?? 3000`; `config.ts:1-2` supplies zero semantics.                                                            | A finding on the nullish fallback is prohibited.                                                                                                             |
| `repository-rule` | Find the token-logging violation at `src/Upload.cs:5`.   | `Upload.cs:5-6` passes the token to `Console.Error.WriteLine`; unchanged `rules/review.md:3` forbids logging it on failure.            | A rule-only or diff-only citation does not establish the changed source sink and required returned file coverage. No credential value appears in the source. |

The provider-visible policy contains a general severity rubric, without naming cases or expected answers: `high` for a reachable failure on valid input or exposure of confidential material in output, `medium` for documented behavior changed incorrectly without those consequences, and `low` for cosmetic issues. This makes `cs-defect` and `repository-rule` high and `ts-defect` medium. Exact severity, changed path and line are predeclared because the existing scorer matches them exactly. The two controls prohibit a finding at their safe line. A returned `read_file` observation must cover every listed decisive line; listing, searching or reading the diff may help discovery but cannot substitute for the required source reads. The `repository-rule` case additionally checks that a correct rule interpretation cites the changed `Upload.cs` line rather than an unchanged rule line as the defect location. The focused test runs a diff-only citation variant and requires it to fail the case's evidence gate.

## Offline result and use limit

The manifest was admitted by `r5-plan`, and `live-local --dry-run --plan` ran all five cases through the real snapshot tools, Agent loop and scoring boundary without a key: scheduled 5, attempted 5, completed 5, failed 0, invalid 0, unattempted 0. All cases had passed evidence and scenario status. Positive deterministic findings remain **unadjudicated** as model-quality observations; the controls have no findings. This is proof that the corpus can execute, not proof that a live model will find the defects. The private dry-run plan and report are not checked in.

An independent pre-execution truth review in Relay session `6ab391e1-b638-83e8-9a25-76af9ead56d5` identified two admission gaps: TypeScript coverage initially omitted the line defining zero semantics, and exact positive severity lacked a general rubric. Both were corrected before any paid call. The targeted review task `952041d2-9ee7-4525-82a6-b49a0119ebcf` returned `SAMPLE_TRUTH_READY` for the corrected contract and manifest hash above. It did not assess live model outputs. The exact corpus and plan digest must be rechecked against the later committed source before paid execution.

The R5 evaluation gate now includes this corpus as a separate `quality-sandbox` scenario in framework and Linux x64 Native AOT runs, with declared five-case verification and cross-mode parity. Focused tests pin the PR identity and check the source truths using actual returned tool lines. A later live population belongs to [R6-V5 issue #295](https://github.com/SolusQuest/agentic-pr-review/issues/295), which must predeclare its finite plan and separately assess model quality, safety, failure, reviewer origin and cleanup. Historical R5 and R6 evidence is unchanged.
