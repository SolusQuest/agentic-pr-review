# Runtime CLI Process Contract

This document defines the M2 deterministic CLI's externally observable contract. It is the design
output of issue #20: issue #19 implements it, issue #21 exercises it in CI, and issue #33 consumes
it from the TypeScript host.

The contract applies only to:

    review --input <path> --output <path> --trace <path>

It does not select a binary, resolve semver ranges, establish provider retries, or define
host-side timeout, cancellation, process capture, or invocation-directory lifecycle.

## Ownership Boundary

The C# runtime owns command parsing, input validation, output construction and self-validation,
staging, no-replace final commits, rollback of files it created, and best-effort failure traces.

The TypeScript host owns executable resolution, a fresh bounded invocation directory, subprocess
lifetime, timeout/cancellation, bounded stream capture, and removal of its invocation directory
after process exit. On non-zero exit, the host must not return, publish, upload, or otherwise treat
result or trace files as successful output. It may inspect a schema-valid failure/orphan trace for
bounded diagnostics before cleanup.

## Command And Validation Sequence

The review command requires exactly one occurrence of --input, --output, and --trace. Unknown
commands/options, missing values, and duplicate occurrences are invocation errors even when the
duplicated values are identical.

The deterministic sequence is:

1. Parse command and arguments.
2. Resolve paths and validate their relationship.
3. Read input bytes.
4. Parse JSON.
5. Inspect protocolVersion before full V1 validation.
6. Validate complete ReviewInputV1 schema and semantic rules.
7. Compare requestedRuntimeVersion with the binary version.
8. Execute the deterministic/provider runtime.
9. Construct and self-validate result and trace.
10. Stage trace, then stage result.
11. Commit trace, then commit result.
12. Return success.

Missing or non-integer protocolVersion is APR_INPUT_SCHEMA_INVALID. A value other than 1 is
APR_PROTOCOL_VERSION_UNSUPPORTED and is detected before full V1 validation, so a future protocol
input does not produce misleading V1 field errors.

## Runtime Version

The runtime has a non-empty canonical version from build-time metadata, such as an assembly
informational version. Development builds use a stable value such as 0.1.0-dev; a runtime must
not derive it from environment variables or current time.

- requestedRuntimeVersion: null accepts the executing binary version.
- A non-null request must equal the canonical version using ordinal, case-sensitive string
  comparison.
- No trimming, case folding, Unicode normalization, semver range parsing, wildcard, or latest
  semantics apply.
- A mismatch is exit 10 with APR_RUNTIME_VERSION_MISMATCH.

Successful result and trace files report the same canonical binary version.

## Exit Classes

| Exit code | Class                        | Examples                                                            |
| --------- | ---------------------------- | ------------------------------------------------------------------- |
| 0         | success                      | valid result and required trace committed                           |
| 2         | invocation/usage             | unknown command, missing/duplicate option, conflicting paths        |
| 10        | input contract/compatibility | invalid JSON/schema, unsupported protocol, runtime-version mismatch |
| 20        | runtime internal             | unexpected exception, runtime-produced output fails self-validation |
| 30        | provider                     | provider adapter reports a provider-originated failure              |
| 40        | protocol-file I/O            | input unreadable, staging/flush/rename/commit failure               |

All other exit values are reserved and must not be emitted by the runtime. An unknown non-zero exit
observed by issue #33 is generic runtime-process failure and fails closed. Host-enforced timeouts,
signals, and operating-system termination are host/process events, not runtime exit classes.

## Stable Process Diagnostics

The following stable APR\_\* codes apply to stderr and failure-trace diagnostics governed by this
process contract.

| Diagnostic code                   | Exit class |
| --------------------------------- | ---------- |
| APR_USAGE_INVALID                 | 2          |
| APR_INPUT_READ_FAILED             | 40         |
| APR_INPUT_JSON_INVALID            | 10         |
| APR_INPUT_SCHEMA_INVALID          | 10         |
| APR_PROTOCOL_VERSION_UNSUPPORTED  | 10         |
| APR_RUNTIME_VERSION_MISMATCH      | 10         |
| APR_RUNTIME_INTERNAL              | 20         |
| APR_OUTPUT_SELF_VALIDATION_FAILED | 20         |
| APR_PROVIDER_FAILED               | 30         |
| APR_TRACE_WRITE_FAILED            | 40         |
| APR_RESULT_WRITE_FAILED           | 40         |

New detailed reasons extend this table instead of adding exit classes. Successful result/trace
diagnostics remain schema-bounded and may use separately documented provider/tool namespaces; no
namespace may expose raw provider data, prompts, secrets, or authentication material.

Stdout is empty on all runtime-controlled success and failure paths. Stderr is UTF-8, contains one
sanitized diagnostic line plus an optional trailing line feed, and is at most 1000 bytes after
sanitization. The host may retain it as bounded diagnostic context but must never parse it for
control flow.

Diagnostics use static templates. They may include a JSON Pointer, schema keyword, or bounded
count. They must not include rejected values, raw JSON, exception stacks, prompt/patch content,
provider bodies, authentication headers, secrets, environment values, or absolute runner paths.

## Paths And No-Overwrite Commit

The runtime resolves each path to a full lexical path from its working directory and compares them
with the platform's documented path-comparison semantics. It rejects lexical equality between
input, output, and trace. The contract does not claim detection of symlink, junction, hard-link,
Unicode, or other aliases beyond what the implementation actually enforces.

Output and trace final paths must not exist at preflight; an existing destination is invocation
error. Missing destination parents are file I/O error because issue #33 materializes its bounded
host-owned invocation directory before invoking the runtime.

The runtime stages each complete, self-validated JSON file in the same parent as its final path.
It commits with the platform's same-filesystem, no-replace move/rename primitive and never uses a
copy-and-delete fallback. If a destination appears after preflight, or the required no-replace
commit cannot be provided, it returns exit 40. It must never overwrite, truncate, copy over, or
delete a pre-existing destination. It may delete only temporary files and final files created by
its current invocation.

## Output And Hash Contract

The M2 command requires --trace; exit 0 is possible only after result and trace have passed
self-validation and final commit. The final result file is the success marker.

The successful hash shape is intentionally one-way:

- ReviewTraceV1.inputSha256 hashes exact consumed input bytes.
- ReviewTraceV1.resultSha256 is omitted.
- ReviewResultV1.inputSha256 hashes the same exact input bytes.
- ReviewResultV1.trace.sha256 hashes exact staged trace bytes.
- ReviewResultV1.trace.path is omitted.

trace.path is artifact-relative in the schema, while this CLI produces a file at a host-supplied
invocation path and does not own artifact layout. Issue #33 retains that path and verifies its
bytes against ReviewResultV1.trace.sha256. A later host-owned projection may add an artifact-relative
path only if it preserves the validated protocol and ownership boundary. Omitting
ReviewTraceV1.resultSha256 avoids an exact-byte circular dependency.

## Failure States And Failure Traces

The first pipeline failure is primary. Cleanup and best-effort failure-trace failures never replace
it. Explicit provider-originated failures use exit 30; unrecognized exceptions use 20. Input
validation failure uses 10, runtime-generated invalid output uses 20, and otherwise valid content
that cannot be committed uses 40.

| Primary failure point                                                         | Final result | Final trace                   |
| ----------------------------------------------------------------------------- | ------------ | ----------------------------- |
| usage/path preflight, input read, JSON/protocol/schema validation             | absent       | absent                        |
| runtime-version mismatch, provider/internal execution, result self-validation | absent       | optional valid failure trace  |
| trace self-validation, trace staging, or trace commit                         | absent       | absent                        |
| result commit after trace commit                                              | absent       | valid orphan trace may remain |

A failure trace is permitted only after the input fully validates as ReviewInputV1 and its
inputSha256 is known. It omits resultSha256, includes only template-generated sanitized
diagnostics, and must validate as ReviewTraceV1.

Failure traces may be attempted for runtime-version mismatch, explicit provider/internal execution
failure, and result self-validation failure. They are not attempted for usage errors, input-read
failure, JSON parse failure, schema failure, unsupported protocol version, or trace
self-validation/staging/commit failure.

If result commit fails after trace commit, the trace may remain as an orphan. It has no
resultSha256 and does not assert successful review completion. The runtime may attempt rollback
but cannot promise success without weakening no-overwrite guarantees; issue #33 uses the non-zero
exit as authoritative and ignores the orphan as successful output.

## Required Implementation Evidence

Issue #19 implements focused table-driven/unit or process tests covering:

- successful deterministic execution and the one-way hash shape;
- unknown, missing, duplicate, and conflicting arguments;
- missing/unreadable input; invalid JSON; missing/unsupported protocol versions; schema-invalid
  input; and runtime-version mismatch;
- injected runtime-internal and provider failures;
- result/trace self-validation and commit failures;
- primary-error preservation when failure-trace or cleanup work also fails;
- the final-state matrix, including an orphan trace after result commit failure;
- repeated exit/diagnostic-code stability;
- no-overwrite behavior when a destination appears after preflight; and
- sentinel secret, path, and stack-trace non-disclosure.

Issue #21 runs the deterministic fixture path under framework-dependent and Native AOT CI without
provider secrets.
