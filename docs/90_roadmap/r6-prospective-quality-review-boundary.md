# R6 prospective V5 independent review boundary (#317)

This is a forward-only `r6-v5-semantic-v2` decision. The v1 rubric, its archived
reports and their blocking verdicts remain historical evidence. A v2 candidate
requires a new declared five-case run; it does not grant R6 entry or start #296.
The v2 digest is
`e06ee430f2e8386d5b5dbe0849c168e2a8b40edc5c55230b9fcf21b9c3d4ece2`.

## Who decides what

The runner and strict reader establish source, corpus, plan and journal identity;
five authenticated completed subjects; bounded calls and non-dispatched tool
argument recovery; actual returned source observations; cleanup; and known
usage for a combined quality-and-cost entry. A reviewer cannot replace a
missing subject, read, completion, accounting fact or cleanup with a later
source inspection. An unknown cost remains unknown and cannot support the
combined entry, even if the model's quality can be discussed separately.

The independent reviewer reads each complete private finding, its proposed
change, frozen source and returned observations. They judge causal truth,
severity, relevant citation context, safe-control behavior and defect identity.
The producing model cannot supply its own review origin. A mechanical source
overlap or narrow-span mismatch calls for this review; it is not proof that the
finding is false. A false, missing or unresolved finding still fails after
review. The final #271 owner must explicitly accept a complete eligible
population; `candidate_pass` is only a machine-checked candidate.

## Gate inventory

| Existing stop or signal                                                                       | v2 treatment                                                                                                                                                                                       |
| --------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Invalid rubric, plan, source, corpus, journal or execution binding; forged outcome or receipt | Invalid population or report. These establish which run produced the evidence.                                                                                                                     |
| Keyless execution                                                                             | Useful offline control, never live entry evidence.                                                                                                                                                 |
| Fewer than five authenticated completions; unsafe dispatch; failed cleanup or privacy control | Invalid population. No semantic exception.                                                                                                                                                         |
| Unknown usage or accounting violation                                                         | No combined R6 quality-and-cost entry; cost is not inferred from a quality judgment.                                                                                                               |
| Missing or malformed private annotation, wrong origin, or stale execution binding             | One bounded correction before a receipt is accepted; a second invalid attempt or stop remains incomplete. No provider recall.                                                                      |
| Missing declared `read_file` facts or ungrounded citation                                     | Ineligible. Frozen #294 observation requirements and #314 supporting reads remain mandatory.                                                                                                       |
| `ExpectedFindingMissing` from a narrower exact-span proxy                                     | May be explained only by an independently confirmed focal finding with a grounded, relevant changed-line anchor and the declared returned reads. No absent finding is invented.                    |
| `ProhibitedFinding` from safe-line overlap                                                    | May be explained once per finding only when each affected use is reviewed and the entire true, unrelated suggestion preserves the protected behavior. False, mixed or unresolved accusations fail. |
| Within-case repeated causal defect                                                            | Reviewer establishes identity; confirmed repetition blocks. Cross-case repetition is recorded as cost.                                                                                             |
| Recovery failure or excess recovery; any other structural failure                             | Blocks. A successful bounded non-dispatched argument recovery is recorded, not automatically failed.                                                                                               |

The complete current public reason vocabulary maps into those rows:
`rubric_identity_invalid`, `plan_binding_invalid`, `case_binding_invalid`,
`origin_count_mismatch`, `finding_shape_invalid`, `case_receipts_missing` and
`origin_invalid` protect identity or receipt integrity;
`population_ineligible` protects completion, safety, privacy, cleanup and
accounting; `keyless_run` marks a control rather than a live entry;
`recovery_count_invalid` and `recovery_ineligible` protect bounded recovery;
`finding_ineligible`, `expected_finding_missing`, `within_case_duplicate` and
`prohibited_evidence_unexplained` protect the independently reviewed finding
dispositions; `legacy_override_invalid` limits explanations to the two named
old-scorer signals. A reviewed semantic decision never changes the reason
vocabulary or grants a generic override.

The scheduled focal credits remain exactly `cs-null-deref` in `cs-defect`,
`ts-zero-timeout` in `ts-defect`, and `repository-token-log` in
`repository-rule`; safe cases receive zero focal credits. An off-focus authored
finding cannot replace a missing scheduled one. Authored off-focus findings
also require their unchanged causal support to have been returned by
`read_file`, alongside the scheduled case's own declared reads.

For `src/SafeCaller.cs:5`, the protected behavior is the null-safe call and
fallback if `Lookup.Find` returns null. For `src/safe-client.ts:2`, it is the
meaningful `timeoutMs: 0` through `??`. Every safe-line evidence occurrence
has one private assessment tied to its actual evidence ordinal. The reviewer
also explicitly records whether severity is justified and the selected anchor
is relevant. An accusation remains an accusation in the public receipt even
when the reviewer confirms a true unrelated issue. The public report retains
only closed verdicts, ordinals, counts and digests, never the private finding
text or tool results. The v2 candidate also records cost knowledge separately
as known, unknown, unverified or not evaluable; v1 has no such field.

V2 retains prior case packets and accepted annotations inside the private
directory until all five reviews finish, so the reviewer can compare causal
groups across cases. The whole directory is then cleaned. One malformed
annotation may be corrected while the same packet, subject and review deadline
remain live. A second invalid annotation, stop or timeout records
`review_incomplete`; accepted receipts cannot be reopened.

Select v2 in a separately reviewed finite plan with
`"rubric":{"id":"r6-v5-semantic-v2","sha256":"e06ee430f2e8386d5b5dbe0849c168e2a8b40edc5c55230b9fcf21b9c3d4ece2"}`.
The private annotation must fill each finding's evidence-ordinal safe-line
assessments, selected anchor ordinal, `severity_assessment` and
`anchor_assessment`. A confirmed finding requires `justified` severity and a
`relevant` anchor; rejected or unresolved findings still get closed decisions
and do not receive quality credit. The assessor checks the citation against
actual returned observations, while the reviewer checks its semantic fit.

Run a keyless five-case control and strict report readback before proposing a
new paid plan. A paid v2 population needs its own declaration, review and
finite budget. Neither this offline implementation nor an earlier v1 result
supplies that population.
