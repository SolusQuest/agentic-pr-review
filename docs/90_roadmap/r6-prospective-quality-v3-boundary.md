# R6 V5 prospective quality boundary, version 3 (#317)

This is a forward-only decision for `r6-v5-semantic-v3`. The frozen #294
sample, v1 and v2 selectors, completed reports, hashes and blocking verdicts
remain historical evidence. The new rubric needs its own declared five-case
plan and independent review before #271 can consider V5 entry. This document
does not authorize a paid call or #296.

Select it with `"rubric":{"id":"r6-v5-semantic-v3","sha256":"929a6ef16bb8a508b7a31b87c8936cdc29f72624f6771fbd711f8aaa6f84db67"}`
in a newly reviewed finite plan. No historical plan selects v3 implicitly.

## Decision authority

The runner admits an authenticated source, plan, journal and actual returned
observations. It enforces execution bounds, privacy, cleanup and report
binding. An independent human or agent reviewer judges whether a finding is
causally true, severe enough, cited in relevant context, safe for each control
and distinct from other findings. The producing model cannot attest its own
quality. The #271 owner makes the final entry decision from the complete
population and the review record. A machine `candidate_pass` is not that
decision.

An observed failure and its cause are separate fields of the final review.
The reviewer records the observed result first, then assigns one or more
supported causes: model behavior, harness, test contract, provider or
transport. They may record mixed causes or undetermined cause, with confidence
and the run-local evidence that supports each assignment. A missing prescribed
tool call does not itself establish poor review ability. The public report's
`failure_source`, `failure_kind`, gate reason and reviewer receipt are
observations; none is an automatic root-cause verdict. The final #271 note
records the causal assessment without publishing private finding text, tool
results, local paths or credentials. Any unresolved causal question remains
unresolved rather than being attributed to the model by default.

V3 case receipts carry a separate, closed attribution record. Its `causes`
array may contain `model_behavior`, `harness`, `test_contract` and
`provider_transport`; two or more entries represent a mixed cause. The
`confidence` is `confirmed`, `probable` or `undetermined`; `basis` identifies
the kind of supporting evidence, and bounded observation ordinals can point
back to returned observations without exposing their contents. Empty causes
with `undetermined` and `insufficient` are valid and preferred to a guess.
The editable v3 sidecar leaves attribution absent until the independent
reviewer explicitly selects a closed value, including `undetermined` when
appropriate. V1/v2 omit this field. A completed case's reviewer records it;
a failed case with no authenticated subject has no reviewer receipt and must
be attributed separately in the final #271 review.

## Hard-stop inventory

The table inventories the current prospective reader, scorer, adjudicator and
quality gate. "Hard" means no semantic reviewer can reconstruct the missing
fact. "Review" means a mechanical signal alone does not decide quality.

| Current stop or signal                                                                                                                                                  | Protected fact and owner                                                                                                  | V3 disposition                                                                                                                                                                                                                                                |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Invalid source, corpus, rubric, plan, journal, case/execution binding or canonical public report                                                                        | Machine proves which execution produced the evidence.                                                                     | Hard invalid population. A later static inspection cannot establish the missing binding.                                                                                                                                                                      |
| Keyless execution                                                                                                                                                       | Machine distinguishes an offline control from paid live evidence.                                                         | Hard no-live-credit; still useful for offline validation.                                                                                                                                                                                                     |
| Missing authenticated completion, wrong snapshot, unattempted case, failed/invalid execution, unsafe tool dispatch, exceeded run/recovery bound or unqualified recovery | Machine proves all five cases ran safely within declared limits.                                                          | Hard no complete V5 entry. Record any completed case's quality separately where evidence permits. A qualified non-dispatched argument recovery remains eligible and counted.                                                                                  |
| Failed private cleanup, privacy breach, malformed or stale review annotation, wrong reviewer origin, impossible receipt                                                 | Machine protects private material and independent review provenance.                                                      | Hard. The v2 bounded editable-envelope correction remains available before a valid receipt is accepted.                                                                                                                                                       |
| Unknown usage or accounting violation                                                                                                                                   | Machine protects a comparable combined quality-and-cost claim.                                                            | Unknown usage blocks the **combined** entry while a completed, reviewed quality result remains reportable. An accounting violation or inconsistent journal stays hard because the run cannot support the combined claim. No unknown value is imputed as zero. |
| Required `read_file` on a changed source path                                                                                                                           | Machine must prove the decisive changed fact was actually returned in the admitted snapshot. Reviewer judges sufficiency. | A complete, untruncated, first-page-to-end `read_diff` that returned all decisive lines is an equivalent observation. The reviewer checks the finding's causal context. Exact tool choice is diagnostic, not a quality verdict.                               |
| Required observation of unchanged supporting fact                                                                                                                       | Machine proves the causal support was actually returned.                                                                  | Retain an actual `read_file` covering all declared lines in this corpus. Search/list hits, rejected calls and later source inspection do not fill the gap. An expanded equivalence rule would need a separately reviewed source guarantee.                    |
| Citation observation missing, wrong path, wrong identity, incomplete lines or incomplete diff                                                                           | Machine proves the finding cites run-local returned evidence; reviewer judges relevance.                                  | Hard evidence gap. A partial page, truncated source, unrelated observation or search-only substitute for the required changed read is not complete evidence. A wider but grounded citation goes to review.                                                    |
| Earlier exact span, exact severity or `ExpectedFindingMissing` proxy                                                                                                    | Machine reports structural mismatch; reviewer judges focal identity, truth, severity and anchor.                          | Reviewable signal. An actually absent or unresolved focal finding fails after review. No finding is invented.                                                                                                                                                 |
| Safe-line `ProhibitedFinding` overlap or accusation                                                                                                                     | Machine flags every protected use; reviewer checks the entire claim and proposed change.                                  | Reviewable signal. A false, mixed, unsafe or unresolved accusation fails; a true unrelated suggestion that preserves the safe behavior can qualify.                                                                                                           |
| Earlier `DuplicateObservation` or repeated causal group within a case                                                                                                   | Reviewer decides whether causes are identical. Machine counts confirmed repeats.                                          | Confirmed repeats are a usability cost in v3, not an automatic semantic failure. False or contradictory repeated claims still fail semantic review. Cross-case repeats remain a separate cost.                                                                |
| Missing, false, unjustified or unresolved independent review                                                                                                            | Reviewer judges quality; machine requires a complete bound receipt.                                                       | No quality pass. A diagnostic or cause label cannot replace the decision.                                                                                                                                                                                     |

The five frozen cases retain their ground truth: `cs-defect`, `ts-defect` and
`repository-rule` require their actual focal defects; `cs-safe` and `ts-safe`
must retain their protected behaviors. Off-focus findings are reviewed as
separate causes and cannot replace a missing focal defect. For all five, the
changed source fact may use the v3 complete-diff rule, while the unchanged
causal support remains a required returned source read. The reviewer assesses
each cited finding, including whether its proposed change is safe; zero
findings on a safe case is a valid result only after the case's evidence and
review are complete.

## Evidence-equivalence rule

The admitted execution must have returned the observation before the terminal
review. A changed-path `read_file` remains valid. A `read_diff` substitute is
valid only when its canonical, runtime-admitted result says `status=ok`,
`source_truncated=false`, `truncated=false`, and both requested and returned
start hunk are 1. Its returned lines must cover every decisive line in one
observation under the same reviewed identity. The reviewer decides whether
those facts actually support the proposed cause. Tool listing and search
results remain useful discovery signals, not equivalent coverage by default.
This predicate applies to all changed paths in the frozen corpus, not just
`src/client.ts` or one provider.

## Outcome and attribution readback

For every nonpassing case or population, the #271 independent reviewer records:

1. the machine outcome and gate reason, plus any separately reviewable
   semantic result;
2. the decisive returned observation IDs or closed receipt ordinals and the
   rule or diagnostic that produced the stop;
3. one or more supported causal categories, confidence, and a concise causal
   explanation, or `undetermined` with the missing evidence named;
4. whether a new test, harness correction, model comparison or no action is
   warranted.

Use `mixed` when more than one category contributes. Do not reclassify a
provider normalization failure as model behavior without evidence, or a test
contract mismatch as a harness bug merely because code enforced the contract.
The private packet is reviewed during execution and cleaned afterward, so a
later reviewer cannot reconstruct a missing semantic judgment from the public
summary alone. Historical v1/v2 reports are not rescored.
