# Reset publication handoff

This contract supplements R4-D08–D10 for issue #248 under the maintainer's explicit authorization to preserve publication-target evidence across a reset. It adds no public Action input, automatic reset, model tool, provider access, or key-rotation mechanism.

## Boundary

A reset ends the selected review session. The old SESSION, tool records, provider continuation and accepted-review authority do not become part of the next session. A narrowly scoped publication handoff may preserve proof of which existing sticky comment was previously published, so a fresh review can update that same comment through the ordinary transaction protocol.

The handoff is target evidence, not a completed review, a publication intent, a blanket permission to write, or an instruction to the Agent. It is never materialized into provider input. A public marker by itself cannot create a handoff.

## Persisted evidence

The existing reset intent and its authenticated lineage successor carry an optional bounded record. The record contains the repository and pull-request identity, exact comment ID and URL, publication scope digest, body digest, reviewed head, source acceptance identity and epoch, and an absolute expiry. It contains no rendered review body or restricted session bytes. Existing canonical binary readers/writers encode the closed record with bounded lengths and reject malformed or trailing data. The record participates in semantic head identity and equivalence; refreshing a physical head preserves it.

The enclosing authenticated reset intent binds the record to the prior head/epoch and authorized workflow run/attempt. The successor carries the identical record obtained from the durable intent. Its publication permission is usable only through the uniquely selected successor in the same base scope. An equivalent retry cannot replace the target or extend its expiry. A handoff remains bounded by its original source acceptance window and is not renewed merely because a reset is retried or repeated.

## Before deletion

The producer uses the production accepted-state selector and validates the selected receipt's publication binding. It does not accept a caller-supplied receipt, a copied marker, or an arbitrary authenticated candidate as proof of an accepted target. A fresh inventory must still match the inventory from which the reset capability and handoff were issued when the first reset intent is created.

An existing current accepted generation supersedes an inherited handoff. Where there is no new acceptance, a still-valid handoff on the selected reset head may be carried into another explicitly authorized reset without extending its original expiry. The absence of any prior target is distinct from a malformed, expired or unresolved target.

Unaccepted publication work must be classified before its evidence is destroyed. A candidate that has not crossed a publication-intent boundary must not become evidence of a successful publication. An uncertain or otherwise unresolved publication attempt must not be converted into a known clean predecessor target. The implementation uses existing candidate-family and publication-record ownership checks; conservative rejection preserves unresolved evidence instead of guessing that an old accepted body is still the remote result. The selected admission rule rejects an unaccepted candidate with any unresolved publication intent, readback, recovery or write-anchor descendant before the first reset mutation. A candidate without such evidence may be discarded by reset, but contributes no target permission. Malformed, contradictory or unknown records fail admission; they do not become a null target. Pending and completed reset retries recover the durable intent or head instead of reselecting partly deleted source records.

The reset intent is durably written and read back before old state is deleted. Interrupted deletion and successor publication resume using that frozen intent, including its target. Process memory is never the only remaining source of target authority.

## Fresh publication and recovery

The retained-state owner exposes the handoff separately from current acceptance and current accepted publication, only from the authenticated selected head and while no accepted successor has taken over. Scope and origin are checked at this boundary. The original expiry remains explicit; an expired target is never converted to absence. Current-epoch transaction recovery takes precedence over denying a branch that requires new permission from the old target.

P5 performs a complete fresh discovery against the saved scope/body/head and exact comment identity. An absent, copied, replaced, changed or ambiguous target does not pass the existing-target match. Digest equality does not excuse a different comment ID or URL. A read-only recovery request can use the saved digest identity without restoring an old review body or requiring the old reviewed head to equal the new PR head.

The expected target is carried through the single-use publication authorization into the publisher. Its final complete discovery must still select the same comment ID, canonical URL, scope, body and reviewed head before mutation. A target expiry is checked with the same trusted clock at that boundary. A proven-absence grant cannot adopt a comment that appeared since admission. This bounds the last controllable check; it does not claim an atomic conditional GitHub write. Ordinary previous-accepted publication uses the same target pin where applicable.

Only the exact verified target may be classified as the previous publication target. The fresh candidate still needs normal ownership admission, a durable new publication intent, current-head revalidation, the actual GitHub write/readback, and accepted-state confirmation. An old body equal to the new body is still predecessor evidence before the new intent; it must not be mistaken for a write performed by the new candidate. An uncertain new write follows existing P5 recovery and is never blindly repeated.

After a fresh generation is accepted, that acceptance supplies ordinary publication authority. The older handoff is no longer consulted. Immutable handoff bytes remain only in bounded lineage evidence until its existing expiry/pruning rules remove them; retiring its permission does not require mutating a semantic head in place.

## Required evidence

- A real Host-grown, capacity-limited accepted session resets into a fresh accepted generation and independently continues, while the persistent prior sticky remains present.
- Exact reset retries, including process interruption before the first fresh acceptance, preserve one successor and one target; distinct authorized run/attempt remains distinct.
- Intent upload, deletion and successor/readback failures preserve their actual committed, not-committed or unknown outcomes.
- A copied/replaced comment, changed body/head/scope, duplicate or missing target, expired or tampered handoff, and cross-reset reuse do not authorize publication.
- Pending uncertain publication cannot be erased and reinterpreted as a clean target. A candidate-only case remains distinct from an intent that may have crossed a write boundary.
- Fresh acceptance takes over and ordinary continuation no longer depends on the obsolete handoff.
- Old tool and continuation markers are absent from new Agent input and accepted SESSION; a separately fresh public fact remains permissible.
- Framework tests identify the actual production owners. Native execution proves the affected reset/codec owner without relabeling framework-only full-Host tests as native evidence.

A new sticky written under valid authority can finish ordinary acceptance recovery after the old target expires, provided its own candidate, intent and exact new-publication evidence remain valid. This path performs no provider call or sticky write and does not compare the new body to the obsolete target. Unknown outcomes retain ordinary fail-closed recovery. Expiry is enforced before new provider/write authorization derived from the handoff and again at the final prewrite check; it does not invalidate an independently established new transaction.
