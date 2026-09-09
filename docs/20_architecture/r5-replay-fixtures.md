# R5 synthetic replay fixture admission

R5-R1 (`#245`) supplies an internal directory reader in `ReviewEvaluationFixture/Replay/Admission`. `ReplayAdmission.Load` returns either a complete `AdmittedReplayFixture` or a bounded failure code with no partial fixture. Its direct tests consume the admitted repository through the real snapshot read/diff tools and materialize its trusted synthetic policy. Agent execution, completed-subject scoring and fresh-process orchestration remain with R5-Q2/R5-R2.

## Authored bundle

The checked-in example is `runtime/tests/fixtures/agent/r5/replay-seed/valid`. Its parent `.gitattributes` disables Git text conversion so the manifest's lengths and hashes describe the same bytes on Windows and Linux. The parent attribute file is outside the admitted bundle.

`manifest.json` has exactly these fields:

- `format`: `apr.r5.synthetic-replay.v1`, the discriminator for this internal reader.
- `source_kind`: `authored-synthetic`.
- `configuration`: explicit `workflow_identity`, `provider_id`, `model_id` and `adapter_id` settings.
- `files`: an ordinally sorted, complete table of `path`, `role`, raw byte `length` and lowercase SHA-256 `sha256`.
- `runs`: ordered run descriptors with unique `id` and `case_id`, `transition`, `previous_run_id`, Q1 `reviewed_identity`, repository path/file mappings, and typed `diff`, `policy`, `context`, `script` and `assertions` file references.

The first run uses `initial` and an explicit null predecessor. Later runs name the immediately preceding run and use `same_head` with the same reviewed identity, or `verified_ahead` with a changed head. All runs retain one synthetic repository and review target. These are authored scenario relationships; an ahead label supplies no production ancestry or continuation authority. A later runner must establish its actual Runtime transition through existing contracts.

Every declared member must be used in its declared role. Runs may share immutable members of the same role. Repository mappings may intentionally reuse source bytes at distinct tracked paths. Duplicate run/case IDs, tracked paths, file names, case aliases, conflicting directory prefixes and cross-role references reject.

| Role         | Admitted value                                                                                        | Consumer boundary                                               |
| ------------ | ----------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| `repository` | Strict UTF-8 head text mapped to normalized repository paths                                          | Memory-backed `IReviewedFileAccess` only                        |
| `diff`       | Structured changes, hunks and line coordinates                                                        | Existing `ReviewedDiffSource` and `ReviewedSnapshot` validators |
| `policy`     | Nonempty trusted synthetic policy bytes                                                               | `CreateTrustedRequest` returns an owned byte copy               |
| `context`    | Nonempty initial context string                                                                       | Explicit `InitialContext` property                              |
| `script`     | Ordered authored provider turns, tool-call IDs/names/argument strings and synthetic reasoning strings | Separate `Script` property for a deterministic runner           |
| `assertions` | Exact named Q1 outcome code, defects, required observations and prohibited findings                   | Separate Q1 `EvaluationCase` and `ExpectedCode`                 |

The loader does not append script or assertion text to policy, initial context or repository tools. An authored label cannot establish that arbitrary prose is public-safe; fixtures must be authored synthetic examples. There is no production SESSION/STATE import, decryption, provider HTTP response import, archive extraction, live call, credential lookup or GitHub lookup.

Scripts validate bounded representation, not successful Agent behavior. Unknown tools, repeated provider tool-call IDs and malformed argument JSON can intentionally exercise later rejection paths. A script is neither a completed result nor accepted tool evidence. The loader does not require its declared expected outcome to be successful.

## Bounds and validation

Windows and Linux x64 are supported with caller-provided bundles staged on a local filesystem. Other platforms fail admission. Root checks reject URL/UNC syntax on both platforms and drives reported as network drives on Windows. Linux does not inspect mount origin: NFS, CIFS or other remote transports can appear under ordinary paths and are not guaranteed to reject. Local staging is a caller precondition, not a mount-origin authorization guarantee; network-filesystem identity, mutation and IO-liveness behavior are outside this reader's supported validation boundary.

The reader pins every ancestor and declared directory, refuses reparse points and symlinks, checks regular-file type and single-link count before reading, and uses nonblocking file opens. Linux operations use pinned descriptor paths; Windows holds read handles without delete sharing and supports long local paths. A final directory identity/inventory and manifest-byte check precedes publication.

| Boundary                           | Maximum                                                             |
| ---------------------------------- | ------------------------------------------------------------------- |
| Manifest                           | 64 KiB                                                              |
| Declared files, excluding manifest | 64                                                                  |
| One member                         | 64 KiB                                                              |
| All members, excluding manifest    | 512 KiB                                                             |
| Runs                               | 16                                                                  |
| Bundle/repository path             | 256 UTF-8 bytes and 8 segments                                      |
| JSON nesting                       | 12                                                                  |
| Provider turns / calls per turn    | Existing `AgentLimits.ModelCalls` / `ToolCallsPerResponse`          |
| Tool argument string               | Existing `AgentLimits.ToolArgumentsBytes`                           |
| Diff, assertion and text fields    | Existing Runtime/Q1 bounds, additionally bounded by the member size |

All JSON objects use source-generated closed contracts: missing required fields, unknown or duplicate properties, unsupported enum strings, wrong types and disallowed nulls reject. Explicit nulls are required for nullable coordinates/previous paths/predecessors when appropriate. Invalid UTF-8 rejects before deserialization or text use. Member paths reject traversal, absolute paths, backslashes, empty/dot segments, portable device names and case aliases. Only directories implied by declared file prefixes are allowed, including rejection of extra empty directories.

Bytes are read within their declared limits, length/hash checked and copied into immutable storage. Missing, extra, changed, truncated, oversized, linked and special entries yield no admitted fixture. Repository text rejects NUL and lone CR; BOM/CRLF interpretation follows the snapshot tool's line model while identity retains the original bytes. Represented addition/context lines must match the corresponding head-file lines. `removed` files must be absent from the head. Existing Runtime lifecycle rules also validate `added`, `modified`, `changed`, `renamed` and `copied` changes.

Cancellation and IO failures produce stable result codes without paths, exception messages or fixture contents. Capturing verified bytes provides an immutable input snapshot, not a transactional filesystem guarantee against an author consistently rewriting an entire bundle during capture. After successful admission, later tools never reopen the physical bundle; caller mutation of returned policy/read byte arrays and subsequent bundle changes cannot alter retained inputs.

## Identity

Corpus SHA-256 uses the `apr.r5.replay.corpus` domain over source-generated canonical manifest bytes. The manifest binds every file's raw digest, length, role and path, explicit settings, and ordered runs. JSON formatting of the manifest is not identity; formatting of a referenced JSON member is part of that member's raw-byte identity. The table is sorted; run order remains semantic.

Assertion documents omit corpus identity to avoid a circular hash. Admission supplies the derived corpus and run reviewed identity to the existing Q1 case contract; Q1 derives the case SHA. Configuration uses Q1's existing `EvaluationAttempt.ConfigurationIdentity` projection over the materialized policy/settings, toolset and limits, with an explicit deterministic provider-settings digest. Source commit/tree, cases, scripts and run history are not configuration. Execution source provenance belongs to the later actual attempt.

The minimal seed identities are pinned in the direct test:

- Corpus: `3dc214862fc636bdca23bd0acb94ff0f15e632910d6e0557f3034b0be4df8d0c`
- Case: `db6a077771c3c87335e6726e0934ab4b50c849d125b222671f875cd3ce2a67da`
- Configuration: `4506af9bdefc3dd8b6eaa2993415236b8989c471f2630c578c43dd6f706a8041`

These identities bind authored test input, not production authority. Current SESSION/state versions and privacy contracts are unchanged.

## Validation and later handoff

```bash
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter FullyQualifiedName~R5ReplayAdmissionTests
```

Run on Windows and Linux x64: Windows tests exercise real junctions, hard links and directory rename denial; Linux tests exercise symlinked files/directories/roots/ancestors, hard links, FIFO/socket rejection within a timeout and pinned-root replacement. Platform-specific cases execute on their named platform. Other cases cover exact identities, closed fields, role separation, actual tools, immutable ownership, lifecycle coherence, run ordering and finite boundaries.

The full Runtime suite and `npm run check` retain existing Q1/R3/R4 evidence. The fixture retains source-generated JSON with reflection serialization disabled and NativeAOT compatibility. R1 adds no dispatcher or project wiring: R5-Q2 owns deterministic execution/corpus composition, R5-R2 owns fresh-process replay, and R5-C1 owns the complete R5 NativeAOT/CI gate. An isolated uncommitted Linux NativeAOT probe can exercise this loader directly without adding a second public entrypoint.
