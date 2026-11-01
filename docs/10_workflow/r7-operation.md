# Managed Experimental Operator Lifecycle

No experimental release or supported downstream installation is currently available. This guide describes the admitted lifecycle for a future separately published experiment. Start with [Action usage](action-usage.md); only the exact H1 templates and released Action SHA are supported. These procedures grant no provider, secret, release or repository mutation authority by themselves.

## Continuation And Refusal

Keep the same immutable trusted workflow source, configuration/instructions, provider/model, tools, limits and payload identity when expecting continuation. The Host owns GitHub authorization, snapshot selection, encrypted state and publication; tools are read-only. The PR's current head is a transition fact, not a caller-supplied session identifier.

| Authenticated observation                                                                         | Operator interpretation                                                   |
| ------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| True absence of current-format state after complete discovery                                     | Observable bootstrap under the Host's initial epoch                       |
| Only Host-classified historical non-current state                                                 | Bootstrap without selecting or converting that historical state           |
| Same reviewed head and compatible selected current                                                | Restore; recovery can complete without another provider review            |
| Verified descendant head and valid base/SESSION transition                                        | Continue the accepted lineage with new current-run work                   |
| Verified expiry cleanup and unique expiry successor                                               | Observable bootstrap after the verified transition                        |
| Diverged, unrelated, force-pushed or unproven ancestry                                            | Refuse; consider an independently authorized explicit reset               |
| Selected-current incompatibility, malformed data, authentication failure or missing required data | Refuse before Agent/provider admission; never reinterpret it as absence   |
| Incomplete discovery, multiple roots/successors or conflicting state                              | Stop and preserve evidence; ambiguity is not bootstrap or reset authority |

Use `state-mode: auto` for ordinary manual continuation of the same installed workflow, or retain the automatic alternative's trigger. Review failures and capacity refusals remain visible. There is no automatic compaction, history truncation, reset, compatibility conversion or fallback to the retired execution loop. Successful prior reviews do not promise indefinite continuation.

## Explicit Reset

First determine whether the problem is an intended lineage restart rather than missing credentials, a malformed workflow or unresolved artifact ambiguity. Reset does not bypass authorization, authenticate damaged state, repair a lost key or convert an incompatible SESSION.

For an eligible same-repository open nondraft PR, an authorized write/admin actor dispatches the installed workflow on its admitted default-branch identity with that `pr-number` and `state-mode: reset`. Keep the exact template, Action SHA and shared concurrency definition. Do not create a second reset workflow or edit configuration to request reset.

The Host verifies and cleans the active epoch's scoped state/transaction records, preserves the repository locator-root sentinel and bounded lineage history, records/readbacks the unique reset successor and only then starts a new epoch. Partial deletion, competing successors or uncertain enumeration fails closed. A reset can perform a new review and publish a new result; separately authorize its provider-visible content and bounded execution.

Check the final run conclusion, summary and publication/state outcome. An interrupted reset is not permission to delete arbitrary artifacts, replay uncertain writes, change workflow identity or silently switch to `auto` to bypass the failure. The existing Host reconciliation rules decide whether an exact retry can proceed.

## Rotate The State Key

Pause new reviews and settle in-flight writers before changing key assignments. Keep the old current key securely available. Provision a fresh independent base64-encoded 32-byte current key; put the former current key in `AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY` and the new key in `AGENTIC_PR_REVIEW_STATE_KEY`. Do not change provider credentials, configuration or template bytes as part of this rotation.

An authorized run supplies both keys through the unchanged template. The previous key authenticates old records read-only; every new record uses the current key. Rotation is not complete merely because the secret values were saved. The Host must authenticate the unique sentinel successor, read it back, re-enumerate and prune exact superseded sentinel identities, preserving the same locator root. Interrupted rotation or uncertain pruning remains a refusal/reconciliation condition.

Remove the previous key **only after** the current-key root has been established and no unexpired state or transaction record still requires the old key. A green review alone does not certify that every other PR lineage or pending transaction has retired its old key. Retain the previous key when that condition cannot be established; there is no public key-retirement flag or a supported shortcut of deleting the root. Do not infer retirement solely from a requested artifact-retention value or an arbitrary waiting period. The [key and locator contracts](../20_architecture/r4-actionhost-wrapper-plan.md#r4-d07-key-provisioning-and-rotation) own these verification gates.

Missing, malformed, wrong, lost or revoked keys fail before SESSION/provider admission where authentication is required. If retained state exists but neither key authenticates the live root, the Host does not claim first use. Stop new execution, preserve permitted metadata and investigate the key/record ownership; do not publish keys or private state in an issue. This guide supplies no destructive lost-key recovery procedure.

## Privacy And Retention

The trusted review sends selected private source, repository rules and tool results to the selected provider when authorized; a private GitHub repository does not make processing local-only. Provider retention and data use need the operator's separate review. Do not place credentials, unrelated private data or secret-management instructions in repository review rules.

Persisted continuation and transaction content is authenticated ciphertext. Repository/provider credentials and state keys remain outside model-visible inputs. Public observers may still see opaque artifact names, artifact metadata, provenance/digests, encrypted bytes and the generic locator-root name. Published review comments and workflow summaries are separate visible surfaces; encryption is not a promise of metadata invisibility or erasure of provider-visible data.

Logical review/transaction retention is fixed at seven days, not a public configuration option. Transport requests include margins: scoped artifacts request at least eight days and the repository sentinel at least ten days, with a proven 24-hour sentinel-expiry margin over dependent records. Requested days do not prove retention. Returned platform expiry must satisfy the Host's invariant; restrictive repository retention can produce refusal rather than silently shorten safety windows.

Expired records become absent for bootstrap only after verified cleanup and the authenticated head transition. Platform retention can outlast logical expiry. Disabling a workflow or removing secrets does not immediately delete artifacts; deleting the repository-level sentinel out of band is unsupported destructive state loss, not a product reset. See [restricted state transport](../20_architecture/distribution.md#restricted-state-transport) and [retention transitions](../20_architecture/r4-actionhost-wrapper-plan.md#r4-d08-scope-session-reset-and-retention).

## Upgrade And Roll Back

Treat an upgrade as an independently selected immutable release and state-compatibility decision, not replacement of a moving tag. Before changing anything, record the current exact version/Action SHA, trusted configuration/instructions revision and permitted operational outcomes without copying private SESSION or secrets into public records. Settle in-flight work and inspect the new release's published identity and compatibility notes.

Install the new release's exact admitted template with only its authorized full `T` substitution. Action/payload build, model, policy, instructions, tools, limits, workflow file and trusted source identities can change the authenticated scope. The two template alternatives share an installed path but are not interchangeable state identities. Neither byte pinning nor returning to a familiar version proves selected-current compatibility.

The Host decides whether an observed state is true absence, a permitted non-current family, compatible current or incompatible selected current. It may bootstrap only under its existing rules; it must visibly refuse incompatible selected current. Use explicit reset only where independently authorized and applicable. There is no converter or promise that a new release will continue an older SESSION.

A rollback selects the exact prior published `T` and its admitted template/configuration, without rebuilding or replacing its release assets. It does not rewind accepted GitHub state, publication or provider exposure. Old payloads may not accept new state; an old workflow identity can expose a distinct retained lineage rather than the latest one. Stop on refusal or uncertainty and assess the applicable reset/new-scope policy. Do not overwrite artifacts, restore arbitrary ciphertext as current, change budgets to force compatibility, or use the unmaintained legacy `v0.1.0` line as a managed rollback.

## Stop And Uninstall

1. Disable the installed review workflow through the repository's workflow controls, preventing new automatic and manual dispatches. Do not edit the admitted template into a partial “disabled” variant and treat it as supported.
2. Inspect queued/running reviews. Cancel where appropriate, then wait for actual termination and the Host's bounded reconciliation/cleanup. Cancellation is not proof that provider work, artifact upload or publication did not happen; reconcile the final run and visible publication before proceeding.
3. Once no supported execution remains active, remove the installed workflow if uninstalling and revoke/remove its dedicated provider credential when no other authorized consumer needs it. Removing configuration/instructions is a separate repository-content decision.
4. Retain the current/previous state keys securely for as long as authorized state reconciliation or cleanup still requires them. If keys are removed, remaining ciphertext can become unreadable; secret removal does not erase that ciphertext. Do not remove shared repository secrets used by other workflows.
5. Review remaining artifacts, comments and provider-side data under their respective ownership and retention rules. There is no automatic uninstall cleanup or supported blanket artifact/sentinel deletion command. Any administrative deletion requires its own exact scope and accepts destructive state loss; it is not a documented continuation/reset mechanism.

Disabling/removing APR does not undo earlier comments, revoke source already sent to a provider, or establish deletion from backups and platform retention. Re-enabling later is subject to the same immutable admission, key, expiry and selected-current compatibility checks as any other run.

## Support And Evidence Boundary

The [managed source profile](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md#v3-managed-review-admission) and exact-source technical proofs document bounded engineering behavior. They are not observed installation, live-provider quality, billing, an availability guarantee or broad production support. A documentation-only revision can reuse applicable unchanged-code evidence while retaining the original source/run/byte identities; it must not claim new exact-byte execution.

The normal path contains no SDK install, build, private restore, protected proof workflow, prepared-directory fallback or alternate CLI flags. The DLL's `run` command alone does not authorize GitHub review. P9 publishes the actual version/download identity and minimal current-installation reference; V4 separately records the first installation result. Until those events, this remains a release-ready guide for an unavailable experiment.
