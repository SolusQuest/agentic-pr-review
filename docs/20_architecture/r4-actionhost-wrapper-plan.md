# R4 ActionHost and thin-wrapper plan

Status: prospective normative R4 contract under refinement issue [#138](https://github.com/SolusQuest/agentic-pr-review/issues/138). This document does not claim that a public Action exists on the development head. The checked-in Action, wrapper, production artifact store, and trusted proof appear only through the execution issues published after this plan is merged.

Baseline: `main` at `736508728acc6b2064b75f2e1da81b9342aac70b`, the final R3 implementation and protected no-publish proof commit. R3 supplies the six-tool Agent, exact DeepSeek thinking continuation, authenticated SESSION encryption, a local state-store conformance implementation, and framework-dependent plus Linux x64 Native AOT fresh-process evidence. R4 owns the production GitHub Host, immutable snapshot, downstream-owned artifact state, deterministic publisher, thin wrapper, and replacement of retained TypeScript business paths.

## Decision summary

R4 keeps one Native AOT C# business application. A bundled Node 24 wrapper reads the small Action surface, masks secrets, launches an explicitly prepared and build-identity-checked payload, forwards cancellation, performs a closed set of official Actions artifact operations requested by the Host, renders bounded workflow presentation, and returns the Host exit status. It does not interpret a pull request, choose state, construct a review, validate findings, or decide publication.

The supported credential-bearing entrypoints are a trusted default-branch `workflow_run` workflow and a trusted default-branch `workflow_dispatch` workflow. Direct credential-bearing execution from `pull_request` is not supported because the proposed workflow can modify its own workflow code. R4 reviews same-repository open, non-draft pull requests only; fork-origin work is an observable no-provider/no-state/no-write skip. The Host never executes or imports reviewed repository code.

Each downstream repository owns its encrypted continuation state in its own GitHub Actions artifacts. GitHub Actions artifacts are R4's sole built-in production/default state-store adapter. In a public repository, artifact existence, opaque names, sizes, digests, timestamps, run provenance, and ciphertext may be public. The protected assets are plaintext, state keys, scope-sensitive logical identifiers, accepted lineage, mutation authority, provider admission, and publication authority. A future Supabase adapter is plausible, but R4 publishes neither that adapter nor a dynamic .NET plugin or external wire compatibility contract.

Sticky publication is enabled by default. Inline comments are an explicit trusted-config opt-in and are limited to grounded high or critical findings. State acceptance follows verified sticky publication; optional inline publication follows acceptance and cannot roll accepted state back. Every cross-service ambiguity is reconciled by exact identities and readback, never by assuming success.

The alternatives rejected for R4 are a permanent TypeScript business Host, direct secret-bearing `pull_request` execution, `pull_request_target`, a reviewed-head checkout as Host authority, GitHub cache or Git refs as continuation state, plaintext artifacts, a hosted central state service, an R4 Supabase dependency, arbitrary dynamically loaded .NET adapters, automatic release download, and implicit `latest` payload selection.

## R4-D01: pre-R7 Action claim and location

R4 restores the Action at `.github/actions/agentic-pr-review/action.yml` with a generated Node 24 bundle at `.github/actions/agentic-pr-review/dist/index.js`. Reusing the historical nested path gives the one identified downstream consumer a deliberate migration path, but it does not preserve the historical input/output contract. The downstream consumer currently pins the immutable historical commit `beb88d5e09e4220ab1f82ad0eafefbf2b10cab8a`; R4 does not alter that pin.

Before R7, the new Action is supported only by repository-controlled proof workflows against an explicitly prepared trusted `linux-x64` payload. The Action metadata must not advertise a runnable downstream example or an automatic download input. `README.md` continues to state that the development head is unsupported as an Action. R7 owns tagged release assets, checksums, exact Action-to-payload mapping, automatic download, compatibility notes, and default promotion. R7 must either retain the nested path or publish an explicit path migration; R4 does not create a root `action.yml` alias.

The wrapper-restoration implementation changes `npm run dist:check` atomically from the current absence guard to generated-bundle reproducibility, checked metadata, and retired-surface drift checks. This design PR leaves the absence guard unchanged.

## R4-D02: invocation and trust-domain matrix

| Entry                                                                      | R4 behavior                                                                                                                                                                                                                                | Secret and write authority                                                                                                       |
| -------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| `workflow_run.completed` from the configured unprivileged trigger workflow | Supported when the privileged workflow exists on the default branch, the triggering workflow identity and conclusion are accepted, exactly one associated same-repository PR is open and non-draft, and the trusted source SHA is verified | Provider key, state key, `actions: write`, `contents: read`, and `pull-requests: write` may be supplied only to this trusted job |
| `workflow_dispatch` on the default branch with `pr-number`                 | Supported when the actor has write-or-admin repository permission and the selected PR is same-repository, open, non-draft, and has an available exact head                                                                                 | Same authority as the trusted `workflow_run` route                                                                               |
| `pull_request` or `pull_request_review`                                    | Unsupported for the credential-bearing product route; return `skipped_untrusted_event` before Host key resolution, state mutation, provider construction, or publication                                                                   | Callers must not pass product secrets                                                                                            |
| fork-origin PR from any entry                                              | Return `skipped_fork` in R4                                                                                                                                                                                                                | No state key, provider key, trusted state mutation, or publication authority                                                     |
| closed, merged, draft, missing, ambiguous, or unavailable-head PR          | Return the specific bounded skip or failure before provider work                                                                                                                                                                           | No state mutation or publication                                                                                                 |
| `push`, `schedule`, `issue_comment`, or other events                       | Unsupported                                                                                                                                                                                                                                | No product authority                                                                                                             |

GitHub documents that a `workflow_run` workflow exists on the default branch and can receive secrets and write tokens even when its predecessor cannot. It also warns that executing untrusted code in this privileged workflow is dangerous. R4 therefore treats every triggering payload field and reviewed byte as untrusted data, restores no cache or artifact produced by the trigger workflow, and obtains the reviewed snapshot through Host-owned GitHub reads. See [GitHub's `workflow_run` event documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_run) and [secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

The trusted workflow pins the Action source and prepared payload to immutable identities. `workflow_run` name alone is not authority: the Host verifies repository ID, workflow file identity, triggering run ID/attempt, event, associated PR, source/head repositories, current PR facts, trusted workflow source SHA, and token permissions before resolving state or provider secrets. No `pull_request_target` route is added.

## R4-D03: immutable reviewed snapshot

The C# Host obtains one authoritative pull-request record, freezes repository ID, pull-request number, base SHA, head SHA, base/head repositories, state, draft flag, and merge status, and then materializes the same-repository head tree by Git object SHA. It walks trees non-recursively so GitHub's recursive-tree truncation cannot be mistaken for completeness, accepts only regular blob modes, and rejects incomplete, duplicate, invalid, over-depth, over-count, or over-metadata results. The existing 20,000 tracked-file and 8 MiB metadata bounds remain authoritative.

Changed-file identity comes from the fully paginated pull-request files endpoint and is capped by the existing product limit of 200 changed files, below GitHub's documented 3,000-file endpoint maximum. Any incomplete pagination or exceeded product/platform limit returns an unsupported-size outcome; it does not review a prefix. The Host fetches exact base and head blobs and builds the bounded diff snapshot itself. GitHub-provided patch text is evidence only and is never the sole byte authority. Binary, submodule, symlink, missing, and over-bound content receives a deterministic unavailable classification rather than becoming a partial regular file. GitHub documents both the pull-file limit and paginated shape in the [pull requests REST API](https://docs.github.com/en/rest/pulls/pulls#list-pull-requests-files), and tree truncation behavior in the [Git trees REST API](https://docs.github.com/en/rest/git/trees#get-a-tree).

The tool root is a Host-created read-only materialization containing only validated regular blobs from the frozen head tree. It excludes `.git`, credentials, workflow scratch space, downloaded artifacts, wrapper files, payload files, symlinks, junctions, submodules, and special files. The Host re-fetches the PR immediately before the first GitHub mutation and requires the exact frozen head SHA. A head change observed before that barrier produces no publication and no state acceptance. Cross-API atomicity with a head change after the final read is not claimed; every public review and receipt names the exact reviewed SHA so a later run can supersede it safely.

Private-fork and public-fork snapshot support is deferred because R4 rejects fork-origin review before provider admission. A later fork-support decision must prove exact head-byte access without granting reviewed code secrets or execution.

## R4-D04: trusted configuration and policy

The direct `config-path` input defaults to `.github/agentic-pr-review.json`. The Host resolves the path only from the trusted workflow source commit, normally the default-branch commit that owns the privileged workflow run. It never reads configuration or instructions from the reviewed head, ambient checkout, mutable branch name, model output, trigger-workflow artifact, or cache.

The initial closed JSON shape is:

```json
{
  "schema": "agentic-pr-review.config.v1",
  "instructionsPath": ".github/agentic-pr-review/instructions.md",
  "publication": {
    "mode": "sticky",
    "inlineMinSeverity": "high"
  }
}
```

The exact allowed keys are `schema`, `instructionsPath`, and `publication`; the exact publication keys are `mode` and `inlineMinSeverity`. `mode` is `sticky` or `sticky_and_inline`. `inlineMinSeverity` is `high` or `critical`, defaults to `high`, and is rejected when an unknown value is supplied. The instruction path is one normalized repository-relative regular file under `.github/agentic-pr-review/`, with no symlink traversal, and is capped at 64 KiB of strict UTF-8. The complete config is capped at 16 KiB, rejects duplicate or unknown keys, and has no environment interpolation.

DeepSeek, model `deepseek-v4-flash`, the current adapter identity, thinking mode, the six-tool set, the central Agent limits, seven-day state retention, five-inline-comment cap, provider endpoint, prompt caching, and security policy are product-owned R4 constants rather than configuration fields. Test providers, arbitrary endpoints, raw-response capture, tool selection, generic budgets, state migration, and secret names are not public configuration. The exact trusted config and instruction bytes, source commit, provider/model/adapter, toolset, limits, and build identities participate in stable session scope.

## R4-D05: Action inputs, secrets, outputs, and completion presentation

The public direct-input allowlist is:

| Input                | Contract                                                                                              |
| -------------------- | ----------------------------------------------------------------------------------------------------- |
| `github-token`       | Required secret on supported trusted routes; distinct GitHub client only                              |
| `provider-api-key`   | Required DeepSeek secret; provider client only                                                        |
| `state-key`          | Required base64 encoding of exactly 32 random bytes; current write and read key                       |
| `previous-state-key` | Optional base64 encoding of exactly 32 previous bytes; read-only rotation window                      |
| `config-path`        | Optional trusted-source path; defaults as defined in R4-D04                                           |
| `pr-number`          | Required only for `workflow_dispatch`; rejected as an override when the event already supplies the PR |
| `state-mode`         | `auto` by default or `reset`; `reset` is accepted only on authorized `workflow_dispatch`              |

The wrapper obtains these through the Actions input mechanism, immediately masks every secret value, and passes typed bounded values to the Host without logging them. There is no runtime provider, model, endpoint, tool mode, artifact ID, state run ID, arbitrary session ID, raw instruction, payload path, debug, or limit input. The prepared-payload location is a private repository-proof seam and never appears in `action.yml`.

R4 publishes no stable Action outputs. The known historical downstream workflow does not consume the earlier outputs, and no current automation has demonstrated a decision that requires one. Reviewed SHA, publication URL, finding count, state action, and internal receipts appear only in a bounded step summary or diagnostics appropriate to their privacy class. A later stable output requires a named consumer, decision, stability/privacy contract, and migration behavior.

Exit zero covers `reviewed`, `reviewed_with_inline_warnings`, `skipped_untrusted_event`, `skipped_fork`, `skipped_draft`, and `skipped_closed`. Configuration, authorization ambiguity, missing required trusted-route credentials, snapshot incompleteness, provider failure, invalid Agent result, stale head observed before publication, state conflict, sticky publication failure, or unresolved outcome ambiguity fails the step. The wrapper renders one bounded summary and only Host-approved warning/error annotations; it never emits state plaintext, ciphertext, keys, provider content, tool results, raw exceptions, or untrusted strings as workflow commands.

The historical `v0.1.0` inputs and outputs are not aliases. The first post-removal release documents the breaking reset. No compatibility reader, converter, or dual surface is added.

## R4-D06: downstream-owned encrypted artifact store

The selected state class is `workflow-encrypted-completed-review`. Every downstream repository stores its own state through a built-in GitHub Actions artifact adapter. The repository's visibility controls platform visibility of artifact metadata and encrypted bytes. Public ciphertext exposure is acceptable; plaintext exposure is not.

The existing internal `IRestrictedStateStore` becomes an asynchronous transport-neutral opaque-snapshot seam. Its adapter operations are limited to complete bounded listing by an opaque exact name, metadata/provenance read, encrypted-byte download, immutable upload, exact readback, and authorized physical deletion. `RestrictedStateService` remains the sole owner of workflow authorization, state-key resolution, opaque-name derivation, encryption/authentication, SESSION admission, scope, accepted lineage, prepare/reconcile/accept, expiry, reset, and stable outcome mapping. A shared adapter conformance suite is the internal extension contract; the interface is not a public binary ABI.

The Host derives separate candidate and acceptance artifact names from a domain-separated HMAC keyed by the state key over the stable scope and object class. During rotation it derives read locators under both the current and authorized previous key, while every new write uses only the current-key locator. Names expose no raw repository, workflow, pull-request, session, provider, model, adapter, policy, or lineage fields. Every artifact contains one bounded encrypted object and nothing executable. The wrapper sees only the opaque name, bounded file path, expected digest, retention request, and operation result; it cannot decrypt or choose lineage.

Each created artifact is an immutable append record. The bridge never uses `overwrite: true`; same-name success, collision, or rejection is not a repository-wide lock or conditional-create primitive. A prepare uploads one candidate bound to the observed accepted predecessor. A run owns publication only when a complete enumeration finds its candidate as the sole valid next child of that predecessor and finds no accepted successor; zero, multiple, incomplete, or ambiguous candidates mean no owner and no publication. After verified sticky publication, acceptance uploads a separate encrypted receipt that binds candidate identity, predecessor, exact reviewed SHA, publication receipt, producing run ID/attempt, and trusted timestamp. The Host reports acceptance success only after another complete enumeration finds its exact receipt as the sole valid accepted successor of the expected predecessor. Multiple valid candidates or acceptance successors are `state_conflict`; none silently replaces the predecessor. The prior accepted lineage remains current until conflict cleanup or explicit reset. Workflow concurrency is required defense in depth for supported workflows, while the Host's sole-candidate publication rule, predecessor check, unique-successor rule, exact readback, and conflict outcome remain authoritative.

This is CAS-equivalent safety over immutable append records, not a claim that GitHub artifacts provide a transactional mutable row, repository-wide conditional create, or cross-run name lock. Outcome-unknown upload is reconciled only by exact opaque name, artifact ID, producing run identity, platform archive digest, project encrypted-envelope digest, and authenticated receipt. The archive digest and raw envelope digest are distinct identities unless the bridge proves byte equivalence. Incomplete pagination, delayed or ambiguous readback, duplicate identity, expired download, missing digest, or deletion ambiguity is a non-success result. No operation assumes that absence in a partial list is authoritative.

The service retains the accepted generation and immediate predecessor for seven days from Host-trusted acceptance. The Action requests seven-day platform retention, ignores expired state during selection, and deletes expired candidates, acceptance receipts, and superseded predecessors when its `actions: write` token permits. Repository retention may be longer; logical expiry is authoritative. GitHub documents artifact list/get/download/delete behavior, public-resource reads, and `Actions` write permission for deletion in the [Actions artifacts REST API](https://docs.github.com/en/rest/actions/artifacts). `actions/upload-artifact` documents v4 immutable uploads and returned artifact digest in its [official repository](https://github.com/actions/upload-artifact).

GitHub Actions cache is rejected because its sharing and poisoning properties are designed for dependency reuse rather than accepted lineage. Git refs are rejected for encrypted session payloads. The adapter never restores trigger-workflow artifacts or caches into the privileged execution root.

R4 has no arbitrary dynamically loaded .NET adapter assembly, user-provided adapter type, or stable HTTP/sidecar protocol. A future maintained Supabase adapter should first prove Postgres transactional RPC, Row Level Security, credential federation, retention, deletion, and operational ownership. Raw Supabase Storage is not presumed to provide compare-and-swap. Only after a second real adapter demonstrates a common boundary may the project consider a narrow opaque-state external protocol. Supabase documents [Postgres Row Level Security](https://supabase.com/docs/guides/database/postgres/row-level-security); its current [third-party authentication overview](https://supabase.com/docs/guides/auth/third-party/overview) does not by itself establish a GitHub Actions OIDC route.

## R4-D07: key provisioning and rotation

`state-key` receives the downstream secret convention `AGENTIC_PR_REVIEW_STATE_KEY`, containing base64 for exactly 32 random bytes. The Host derives the non-secret key identifier as a domain-separated SHA-256 digest of the decoded key; users do not type an independent key ID. `previous-state-key` receives `AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY`, when supplied, and is read-only for an unexpired current or predecessor object. Every new prepare uses the current key. The next accepted generation naturally rotates the state; R4 does not rewrite artifacts in place.

The state key is separate from the provider key, `GITHUB_TOKEN`, payload identity, and artifact bytes. There is no generated, repository-derived, provider-derived, or plaintext fallback. Authorization and event/source verification occur before decoding or resolving key inputs. Missing or malformed current key fails the supported trusted route before state listing or provider construction. Unknown key ID, wrong material, authentication failure, cross-scope substitution, revoked old key, or multiple matching keys fails before SESSION admission. Temporary decoded key buffers are cleared where the runtime permits.

The repository owner creates and rotates secrets outside R4 code. Selecting these prospective names is documentation authority only; this design authorizes neither a repository/organization/environment secret mutation nor a protected environment. The production setup documentation must explain current-plus-previous rotation and removal after the seven-day window. Fork and unsupported event routes receive neither key.

## R4-D08: scope, session, reset, and retention

Stable scope contains repository ID, trusted workflow file identity, trusted source commit identity, pull-request number, derived session ID, provider/model/adapter, exact config and instruction digest, toolset digest, limits digest, and payload build identity. Current base/head SHAs remain transition facts and encrypted provenance rather than stable scope. Different workflows or configurations reviewing the same pull request have independent lineages.

The Host derives the session ID from the stable scope; there is no caller-supplied session ID. `auto` restores the unique current accepted lineage, bootstraps observably when none exists or an automatically discovered current-format candidate is incompatible, and fails closed when enumeration, authenticity, accepted-current presence, or lineage is ambiguous. Same-head restore is allowed. Head advancement is allowed only after the Host verifies that the new head descends from the accepted prior head and the base transition satisfies SESSION rules. Diverged, force-pushed, unrelated, or unknown ancestry starts an observable new root rather than continuing old bytes.

`reset` is an authorized `workflow_dispatch` operation. It compare-deletes every artifact in the exact opaque scope, verifies bounded absence, and starts a new root only after cleanup succeeds. A PR event, config value, model output, possession of ciphertext, or possession of a key alone cannot request reset. Seven-day logical retention is fixed for R4 and is not a public configuration field.

## R4-D09: deterministic publication

The sticky comment is the complete public review. It is enabled for every successful review, including an empty result, and is capped at 50,000 characters after the Agent's existing 20-finding bound. The body names the exact reviewed head, renders only validated grounded findings, states truncation explicitly, and ends with one R4 marker containing an opaque publication-scope digest, body digest, and reviewed head. The Host updates only the unique exact R4 marker for its publication scope. It does not adopt historical `agentic-pr-review:v1` or M4 state markers; multiple matching R4 markers fail closed before write.

Finding fingerprints are domain-separated digests over the validated severity, title, message, and ordered grounded evidence projection. They exclude state IDs, run IDs, model prose outside the admitted finding, and mutable comment IDs. Inline keys add the exact path and mapped right-side line. Existing exact R4 inline keys suppress duplicates.

Inline publication is off by default. In `sticky_and_inline` mode the Host considers at most five high-or-critical findings that map exactly to current right-side diff lines. A valid grounded finding that cannot map remains sticky-only. The Host attempts one batch review, re-lists before any documented individual fallback, and never hides the sticky review because an inline operation fails. Partial inline failure produces `reviewed_with_inline_warnings` after accepted state and records only bounded reason counts.

Sticky create/update and inline writes require exact API response validation plus readback. Network failure after a request is `outcome_unknown` until exact marker/body reconciliation succeeds. Empty review updates the sticky body to an explicit no-actionable-findings result rather than deleting history. All renderer text is escaped or emitted as inert Markdown; untrusted content never becomes a workflow command.

## R4-D10: publication, state, and concurrency transaction

The authoritative sequence is:

1. verify trusted invocation, resolve immutable configuration, build the exact snapshot, restore state, run the Agent/provider, and validate the terminal candidate;
2. re-fetch the PR and require the exact reviewed head;
3. prepare and immutably upload encrypted candidate state, reconcile an unknown upload by exact digest and provenance, completely re-enumerate, and proceed only when this run's candidate is the sole valid next child and therefore the publication owner;
4. re-fetch the PR, require the exact reviewed head and publication ownership, then create or update the R4 sticky comment and verify exact readback;
5. re-fetch the PR again, require the exact reviewed head, completely recheck that the same candidate still owns publication with no accepted successor, and upload the encrypted acceptance receipt using the observed predecessor;
6. completely re-enumerate and report accepted only when this receipt is the unique valid successor; reconcile unknown acceptance without re-encrypting or republishing;
7. optionally publish deduplicated inline comments and report bounded partial warnings;
8. prune expired or superseded artifacts without changing the known committed outcome.

A failure before verified sticky publication leaves the candidate unaccepted. A sticky success followed by unknown acceptance is recovered from the exact public marker plus encrypted candidate/receipt; a retry must not duplicate the sticky comment or call the provider merely to discover the previous outcome. Acceptance never precedes sticky readback, so the product does not advance continuation past an unpublished complete review. Inline failure cannot invalidate accepted state because the sticky comment already carries the complete result.

If competing candidates share a predecessor, only a unique verified acceptance successor can advance. Multiple successors, lost workflow ownership, changed head observed at either barrier, partial enumeration, or ambiguous mutation fails closed and leaves the prior lineage current. The supported workflow config uses an opaque per-scope concurrency group with `cancel-in-progress: false`; it reduces expected contention but is not a substitute for Host validation.

Cancellation before the first external write cancels normally. After an upload or comment request may have crossed a commit boundary, bounded reconciliation ignores caller cancellation long enough to classify the outcome. Once acceptance is known, the result cannot claim cancellation or rollback; cleanup and inline warnings are post-commit outcomes.

## R4-D11: launcher, result, and artifact bridge

The wrapper and Host ship atomically and perform a build-identity handshake before any privileged operation. Raw wrapper-to-Host facts are limited to exact event JSON path and digest, repository/run/workflow identifiers, trusted source/payload identity, bounded Action inputs, secret channels, cancellation, and a private artifact bridge endpoint. The Host-to-wrapper result is a closed status, exit class, bounded summary model, bounded annotations, and optional private artifact commands. Neither direction carries a review-domain SESSION format.

The private artifact bridge supports only exact-name list, metadata, download, immutable upload, readback, and delete operations with fixed size/count/time limits. A request carries an opaque name, local bounded path or destination, expected digest, retention request, and correlation ID. The wrapper rejects path escape, symlink/reparse traversal, unknown operations, duplicate correlation IDs, response overflow, and build mismatch. The Host chooses every logical operation and validates every result; the wrapper never opens encrypted state as a SESSION object.

The exact framing, file names, DTO names, and standard-stream or private-pipe mechanism belong to the focused launcher leaf, provided the seam remains private, closed, bounded, cancellation-aware, and atomic with the bundle. It has one build discriminator for mismatch rejection, not an independently supported protocol version family. The bridge is not the future adapter-extension interface.

Secret values, provider requests/responses, SESSION plaintext, tool data, findings before public validation, state keys, decrypted artifacts, arbitrary environment dictionaries, ambient paths, and GitHub client objects never cross into wrapper presentation or Agent/model-visible objects. The wrapper registers masks before launch and exposes only the minimum child environment.

## R4-D12: migration and deletion disposition

| Current family                                                                                                   | R4 disposition                                                                                             | Last responsible deletion gate                                                                                  |
| ---------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| C# `Agent/**`, tools, SESSION, DeepSeek adapter, restricted envelope, local conformance store                    | Surviving target behavior; compose into ActionHost and migrate the store seam asynchronously               | Never deleted by R4 except replacement-local refactors with conformance parity                                  |
| C# `Ledger/**`, `Prefix/**`, `Protocol/**`, deterministic CLI/application paths                                  | Replacement evidence for stable validation, canonicalization, process, and publisher behavior              | Delete only after the owning C# Host/state/publisher replacement test passes in framework and Native AOT modes  |
| `src/runtime-invocation/**` and `src/live-runtime-invocation/**`                                                 | Replace with the C# Host plus private launcher contract                                                    | Delete after end-to-end Host launch, cancellation, result, privacy, and build-identity proofs                   |
| `src/live-provider/**`                                                                                           | Retained only where it is the oracle for request privacy and removed R3 route evidence                     | Delete after the C# DeepSeek route owns all remaining request/canary assertions                                 |
| `src/state-v2/**`, `src/state-acceptance/**`, state manifests/selectors/receipts, and `src/ledger-csharp.ts`     | Replacement evidence for adversarial provenance, outcome-unknown, stale-writer, and publication sequencing | Delete in focused slices after the new encrypted artifact adapter and transaction coordinator pass mapped tests |
| `src/comments.ts`, `src/inline-comments.ts`, fingerprint/target/rendering families                               | Port protective sticky, inline, marker, duplicate, fallback, and readback behavior into C#                 | Delete after C# publisher end-to-end coverage; do not adopt old M4 marker/state identity                        |
| TypeScript canonicalization, prefix, provider-metadata, protocol schemas, fixtures, and residual-reference rules | Classify per consumer as replacement oracle, historical contract, or obsolete parity fixture               | Delete only after the mapped C# owner and exact residual-reference audit pass                                   |
| Historical `v0.1.0` tag and docs                                                                                 | Immutable historical release evidence                                                                      | Never rewrite; document the breaking R4/R7 migration                                                            |
| `src/no-public-action.test.ts` and current `dist:check` absence guard                                            | Temporary current-state protection                                                                         | Replace atomically when Action metadata and generated wrapper land                                              |

The checked [`r3-single-shot-removal-handoff.md`](./r3-single-shot-removal-handoff.md) remains the path-level starting inventory. Every deletion PR refreshes live imports, scripts, workflows, fixtures, public docs, downstream pins, and residual allowlists; a missing R3 call site is not sufficient deletion evidence, and a test-only import is not sufficient retention evidence.

No superseded pre-rebaseline state conversion, compatibility reader, dual writer, input alias, comment adoption, or parallel public runtime is added. Automatically discovered non-current state bootstraps observably under the current SESSION rule; explicitly supplied incompatible state remains unsupported and fails closed.

## R4-D13: proof layers

R4 uses three separate proof layers.

1. Deterministic conformance covers Host authorization, snapshot traversal and diff construction, config resolution, the internal state-store suite, artifact pagination and ambiguity, key rotation, publisher rendering/readback, transaction recovery, wrapper framing, cancellation, canaries, and retained replacement oracles. PR and push CI use only synthetic providers and credentials.
2. Framework-dependent and `linux-x64` Native AOT integration executes the complete Host/Agent/DeepSeek-fake/state/publisher path from published output. It verifies source-generated serialization, trimming, child environment, provider/GitHub client separation, artifact bridge behavior, and exact payload build identity.
3. One maintainer-authorized same-repository fixture PR and trusted default-branch workflow prove two genuinely independent workflow runs. Run one bootstraps, publishes the sticky result, and accepts encrypted artifact state. Run two selects that state, decrypts and admits SESSION, proves verified-ahead continuation using prior-only evidence, publishes deterministically, advances lineage, and leaves public-safe readback. The pair also exercises one bounded stale-head or stale-writer case coherent with the fixture.

The trusted proof uses an explicitly prepared payload pinned to the merged implementation commit, an opaque concurrency group, a deterministic provider unless a separately authorized live-provider run closes a named risk, and dedicated canaries. Evidence records run IDs, workflow/action/payload SHAs, reviewed SHAs, public comment URLs/markers, non-linkable artifact counts, bounded pass/fail state outcomes, cleanup, and absence of keys/plaintext. Host-restricted evidence may retain artifact IDs, names, and digests needed for reconciliation, but the public record must not map any visible artifact identifier or producing run to the currently accepted lineage. It never publishes encrypted bytes merely as proof, never exposes secret values, and never claims that two jobs in one run satisfy independence.

Any proof PR, comment, review, artifact, workflow dispatch, secret/environment change, or repository metadata mutation requires explicit maintainer authorization at execution time. The implementation graph may prepare scripts and workflows without exercising credential-bearing mutation.

## R4-D14: activation and implementation graph

This docs-only PR may add this prospective contract and align current architecture, security, distribution, roadmap, and README statements. It must not add `action.yml`, wrapper code, production config examples, workflow dispatches, repository settings, secrets, or execution issues. It remains Draft and Relay-reviewed on its exact pushed head; only the maintainer may merge it.

After merge, #138 publishes the following hierarchy. Tracker issues own no implementation PR. Each leaf has one primary PR, exact paths, acceptance criteria, local commands, and a Relay gate.

| Tracker                  | Focused leaf                                                                                                   | Primary ownership                                                                  | Blocks                                            |
| ------------------------ | -------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------- |
| Host trust and snapshot  | Freeze Action inputs, trusted event/config materialization, and bounded Host result codes                      | new C# ActionHost contracts/config plus contract tests                             | snapshot, state integration, wrapper              |
| Host trust and snapshot  | Implement GitHub identity authorization and exact-SHA tree/diff snapshot                                       | C# GitHub client, snapshot materializer, tool-root integration                     | publisher transaction, integrated proof           |
| Encrypted artifact state | Migrate `IRestrictedStateStore` to async opaque operations and extract shared conformance                      | C# state contracts/service/local store/tests                                       | artifact adapter                                  |
| Encrypted artifact state | Implement the private Node artifact bridge and C# GitHub artifact adapter                                      | wrapper bridge modules plus C# adapter/conformance                                 | production state integration, wrapper restoration |
| Encrypted artifact state | Integrate downstream key resolution, opaque naming, scope, rotation, restore/reset, and accepted lineage       | C# ActionHost state composition/tests                                              | transaction coordinator, integrated proof         |
| Deterministic publisher  | Port R4 sticky renderer, marker, fingerprint, upsert, and readback                                             | C# publisher modules/tests                                                         | transaction coordinator                           |
| Deterministic publisher  | Port opt-in inline mapping, batching, duplicate suppression, fallback, and partial warnings                    | C# publisher modules/tests                                                         | final migration, integrated proof                 |
| Deterministic publisher  | Implement state/publication ordering, receipts, reconciliation, head barriers, ownership, and cancellation     | C# ActionHost coordinator/tests                                                    | wrapper restoration, proof                        |
| Wrapper and migration    | Restore nested Action metadata and generated thin Node wrapper with build handshake and reproducibility checks | `.github/actions/agentic-pr-review/**`, build scripts, wrapper tests, `dist:check` | integrated proof                                  |
| Wrapper and migration    | Remove replaced runtime/live-runtime/provider TypeScript business paths                                        | named `src/**` families and tests after replacement evidence                       | final residual audit                              |
| Wrapper and migration    | Remove replaced M4 state/acceptance/ledger/publisher/protocol families and close residual allowlists           | named `src/**`, schemas, fixtures, docs after mapped conformance                   | final residual audit                              |
| Wrapper and migration    | Run the final repository-wide migration, public-boundary, README, and distribution cutover audit               | package scripts, docs, CI, residual tests                                          | trusted proof                                     |
| Trusted proof and exit   | Add provider-secret-free framework and Linux x64 Native AOT integrated CI                                      | runtime integration fixtures/workflows                                             | trusted two-run proof                             |
| Trusted proof and exit   | Execute and record the maintainer-authorized two-independent-run product proof                                 | dedicated trusted workflow and fixture evidence                                    | R4 milestone close                                |

Dependencies are reciprocal: every leaf records what it blocks and what blocks it. The async store contract precedes the artifact adapter; event/config and adapter precede state integration; snapshot, state, and sticky publisher precede the transaction coordinator; the bridge and coordinator precede wrapper restoration; all replacements precede their deletion slice; the final residual audit and provider-secret-free integrated CI precede the trusted two-run proof.

After the docs PR merges, #138 refreshes `main`, publishes and remotely verifies native Task types, R4 milestone assignment, parent/sub-issue relationships, and dependency edges, posts the accepted decision/Relay lineage record, and closes only after the complete graph exists. R4 remains open until the implementation leaves and proof exit criteria complete.

## Security, privacy, and operational impact

The design intentionally allows public observers of a public repository to discover encrypted state artifacts and download ciphertext. Artifact names are opaque, plaintext is AEAD-protected and scope-bound, keys are downstream secrets, and untrusted routes cannot mutate trusted lineage or publish. This is a narrower and more honest guarantee than pretending public artifact existence can be hidden.

The highest residual risk is the lack of a transactional mutable artifact row. R4 handles it with immutable candidate/acceptance records, unique-successor selection, exact readback, explicit conflict, prior-lineage preservation, and workflow concurrency. It does not call this a platform-provided linearizable CAS. If bounded proof shows delayed listing or duplicate-successor behavior cannot meet the selected recovery model, implementation stops for a maintainer decision rather than weakening the contract silently.

The second residual risk is the privileged `workflow_run` boundary. R4 does not execute trigger artifacts, caches, scripts, checked-out reviewed code, or model-generated commands. All trigger and repository content remains data under strict bounds. Any future fork support, `pull_request_target`, external state service, OIDC federation, public adapter protocol, or automatic payload download requires a separate threat and compatibility decision.

## Validation and activation gates

This planning PR runs:

```bash
npm run check
npm run dist:check
```

At this point `dist:check` must still prove that the retired Action surface is absent. The wrapper-restoration leaf owns the later atomic semantic change.

R4 implementation is complete only when provider-secret-free PR/push CI, framework tests, Linux x64 Native AOT execution, state adapter conformance, exact snapshot tests, publisher/reconciliation tests, wrapper reproducibility, secret canaries, replacement/deletion audits, and the maintainer-authorized two-independent-run proof all pass. Public evidence may include opaque artifact metadata and ciphertext digests but never keys, plaintext SESSION bytes, provider continuation content, repository tool results, credentials, or raw provider bodies.
