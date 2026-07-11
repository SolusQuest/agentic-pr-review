#!/usr/bin/env node
// Fake runtime for issue #33 adapter tests. Not part of the shipped action; consumed
// only by src/runtime-invocation/*.test.ts via process.execPath.
//
// Behavior is selected via the FAKE_RUNTIME_SCENARIO environment variable. See tests
// for the catalog of supported scenarios.

import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, symlinkSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';

function args() {
  const cliArgs = process.argv.slice(2);
  if (cliArgs[0] !== 'review') fail(2, 'APR_USAGE_INVALID: unexpected command');
  const parsed = { input: null, output: null, trace: null };
  for (let i = 1; i < cliArgs.length; i += 2) {
    const flag = cliArgs[i];
    const value = cliArgs[i + 1];
    if (flag === '--input') parsed.input = value;
    else if (flag === '--output') parsed.output = value;
    else if (flag === '--trace') parsed.trace = value;
    else fail(2, `APR_USAGE_INVALID: unknown option ${flag}`);
  }
  if (!parsed.input || !parsed.output || !parsed.trace)
    fail(2, 'APR_USAGE_INVALID: missing option');
  return parsed;
}

function fail(code, line) {
  process.stderr.write(`${line}\n`);
  process.exit(code);
}

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function loadInput(inputPath) {
  const bytes = readFileSync(inputPath);
  const parsed = JSON.parse(bytes.toString('utf8'));
  return { bytes, parsed };
}

function baseResult(input, runtimeVersion, overrides = {}) {
  return {
    protocolVersion: 1,
    runtimeVersion,
    inputSha256: sha256(input.bytes),
    summary: overrides.summary ?? 'Fake runtime deterministic summary.',
    findings: [],
    limitations: [],
    warnings: [],
    diagnostics: [],
  };
}

function baseTrace(input, runtimeVersion) {
  return {
    protocolVersion: 1,
    runtimeVersion,
    inputSha256: sha256(input.bytes),
    mode: 'deterministic-fixture',
    toolCalls: [],
    warnings: [],
    diagnostics: [{ code: 'FAKE_OK', message: 'fake runtime executed', level: 'info' }],
  };
}

function commit(paths, resultObj, traceObj) {
  const traceBytes = Buffer.from(`${JSON.stringify(traceObj, null, 2)}\n`, 'utf8');
  const finalResult = { ...resultObj, trace: { sha256: sha256(traceBytes) } };
  writeFileSync(paths.trace, traceBytes);
  writeFileSync(paths.output, `${JSON.stringify(finalResult, null, 2)}\n`);
}

function commitWithSummary(paths, input, rv, summary) {
  const trace = baseTrace(input, rv);
  const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
  const result = { ...baseResult(input, rv, { summary }), trace: { sha256: sha256(traceBytes) } };
  writeFileSync(paths.trace, traceBytes);
  writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
}

function scenario() {
  return process.env.FAKE_RUNTIME_SCENARIO ?? 'success';
}

function runtimeVersion() {
  return process.env.FAKE_RUNTIME_VERSION ?? '0.1.0-dev';
}

const paths = args();
const input = loadInput(paths.input);
const s = scenario();
const rv = runtimeVersion();

switch (s) {
  case 'success': {
    commit(paths, baseResult(input, rv), baseTrace(input, rv));
    process.exit(0);
  }
  case 'success-with-requested-version': {
    const requested = input.parsed.requestedRuntimeVersion ?? rv;
    commit(paths, baseResult(input, requested), baseTrace(input, requested));
    process.exit(0);
  }
  case 'exit-2': {
    fail(2, 'APR_USAGE_INVALID: forced usage error');
    break;
  }
  case 'exit-2-mismatched-apr': {
    // exit 2 with a mismatched APR code (provider class); adapter must drop diagnosticCode.
    fail(2, 'APR_PROVIDER_FAILED: mismatched code on usage exit');
    break;
  }
  case 'exit-10': {
    fail(10, 'APR_RUNTIME_VERSION_MISMATCH: forced mismatch');
    break;
  }
  case 'exit-10-input-read': {
    fail(10, 'APR_INPUT_READ_FAILED: forced read failure'); // maps to file-io class per contract
    break;
  }
  case 'exit-10-input-json': {
    fail(10, 'APR_INPUT_JSON_INVALID: forced json failure');
    break;
  }
  case 'exit-10-protocol-version': {
    fail(10, 'APR_PROTOCOL_VERSION_UNSUPPORTED: forced protocol version failure');
    break;
  }
  case 'exit-20': {
    fail(20, 'APR_RUNTIME_INTERNAL: forced internal failure');
    break;
  }
  case 'exit-20-self-validation': {
    fail(20, 'APR_OUTPUT_SELF_VALIDATION_FAILED: forced self-validation failure');
    break;
  }
  case 'exit-30': {
    fail(30, 'APR_PROVIDER_FAILED: forced provider failure');
    break;
  }
  case 'exit-40': {
    fail(40, 'APR_RESULT_WRITE_FAILED: forced file-io failure');
    break;
  }
  case 'exit-40-trace-write': {
    fail(40, 'APR_TRACE_WRITE_FAILED: forced trace write failure');
    break;
  }
  case 'exit-77': {
    fail(77, 'APR_UNKNOWN: forced unknown exit code');
    break;
  }
  case 'exit-10-with-failure-trace': {
    const trace = baseTrace(input, rv);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    fail(10, 'APR_INPUT_SCHEMA_INVALID: forced schema failure');
    break;
  }
  case 'exit-20-with-mismatched-failure-trace': {
    const trace = baseTrace(input, rv);
    trace.inputSha256 = 'a'.repeat(64);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    fail(20, 'APR_RUNTIME_INTERNAL: schema-valid trace with wrong input hash');
    break;
  }
  case 'orphan-trace-exit-40': {
    const trace = baseTrace(input, rv);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    fail(40, 'APR_RESULT_WRITE_FAILED: orphan trace after result-commit failure');
    break;
  }
  case 'missing-result': {
    const trace = baseTrace(input, rv);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    process.exit(0);
  }
  case 'missing-trace': {
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(Buffer.from('x')) } };
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'symlink-result': {
    const target = path.join(path.dirname(paths.output), 'result-target.json');
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(traceBytes) } };
    writeFileSync(target, `${JSON.stringify(result, null, 2)}\n`);
    writeFileSync(paths.trace, traceBytes);
    try {
      symlinkSync(target, paths.output);
    } catch {
      // Skip on platforms without symlink privilege.
      writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    }
    process.exit(0);
  }
  case 'directory-trace': {
    // Create a directory at trace.json so lstat().isFile() === false.
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(Buffer.from('x')) } };
    if (!existsSync(paths.trace)) mkdirSync(paths.trace);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'oversized-result': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const filler = 'x'.repeat(Number(process.env.FAKE_RUNTIME_FILLER_BYTES ?? '65536'));
    const result = {
      ...baseResult(input, rv, { summary: filler }),
      trace: { sha256: sha256(traceBytes) },
    };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'invalid-utf8-result': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, Buffer.from([0xff, 0xfe, 0xfd, 0x00]));
    process.exit(0);
  }
  case 'invalid-json-result': {
    const trace = baseTrace(input, rv);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    writeFileSync(paths.output, 'this is not JSON');
    process.exit(0);
  }
  case 'schema-invalid-result': {
    const trace = baseTrace(input, rv);
    writeFileSync(paths.trace, `${JSON.stringify(trace, null, 2)}\n`);
    writeFileSync(paths.output, JSON.stringify({ protocolVersion: 1 }));
    process.exit(0);
  }
  case 'invalid-json-trace': {
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(Buffer.from('x')) } };
    writeFileSync(paths.trace, 'not json');
    writeFileSync(paths.output, JSON.stringify(result));
    process.exit(0);
  }
  case 'missing-result-inputsha': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = baseResult(input, rv);
    delete result.inputSha256;
    result.trace = { sha256: sha256(traceBytes) };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'missing-result-trace': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = baseResult(input, rv);
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'missing-result-trace-sha': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: {} };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'result-trace-path-present': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = {
      ...baseResult(input, rv),
      trace: { sha256: sha256(traceBytes), path: 'artifact-relative/trace.json' },
    };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'trace-result-sha-present': {
    const trace = baseTrace(input, rv);
    trace.resultSha256 = 'b'.repeat(64);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(traceBytes) } };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'result-inputsha-mismatch': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = {
      ...baseResult(input, rv),
      inputSha256: 'c'.repeat(64),
      trace: { sha256: sha256(traceBytes) },
    };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'trace-inputsha-mismatch': {
    const trace = baseTrace(input, rv);
    trace.inputSha256 = 'd'.repeat(64);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(traceBytes) } };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'trace-sha-mismatch': {
    const trace = baseTrace(input, rv);
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: { sha256: 'e'.repeat(64) } };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'result-trace-version-mismatch': {
    const trace = baseTrace(input, rv);
    trace.runtimeVersion = `${rv}-different`;
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = { ...baseResult(input, rv), trace: { sha256: sha256(traceBytes) } };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'requested-version-mismatch': {
    const trace = baseTrace(input, rv);
    trace.runtimeVersion = 'some-other-version';
    const traceBytes = Buffer.from(`${JSON.stringify(trace, null, 2)}\n`, 'utf8');
    const result = {
      ...baseResult(input, rv),
      runtimeVersion: 'some-other-version',
      trace: { sha256: sha256(traceBytes) },
    };
    writeFileSync(paths.trace, traceBytes);
    writeFileSync(paths.output, `${JSON.stringify(result, null, 2)}\n`);
    process.exit(0);
  }
  case 'stdout-leak-small': {
    process.stdout.write('leak');
    commit(paths, baseResult(input, rv), baseTrace(input, rv));
    process.exit(0);
  }
  case 'stdout-leak-no-output': {
    // Exit 0 with stdout leak and no result/trace; adapter must classify as
    // process-contract-violation, not missing-output.
    process.stdout.write('leak\n');
    process.exit(0);
  }
  case 'stderr-over-contract-success': {
    process.stderr.write('x'.repeat(1500));
    commit(paths, baseResult(input, rv), baseTrace(input, rv));
    process.exit(0);
  }
  case 'stderr-non-utf8': {
    process.stderr.write(Buffer.from([0xff, 0xfe, 0xfd]));
    fail(20, 'APR_RUNTIME_INTERNAL: emitted non-utf8 sanitizer test');
    break;
  }
  case 'stderr-control-chars': {
    process.stderr.write('APR_RUNTIME_INTERNAL:\x01\x02control\x1bchars trailing\n');
    process.exit(20);
  }
  case 'stderr-path-leak': {
    // Emit stderr containing the invocation directory path (its CWD).
    const line = `APR_RUNTIME_INTERNAL: leaked path ${process.cwd()}\n`;
    process.stderr.write(line);
    process.exit(20);
  }
  case 'stdout-flood': {
    const chunk = Buffer.alloc(4096, 0x41);
    const interval = setInterval(() => {
      process.stdout.write(chunk);
    }, 5);
    setTimeout(() => clearInterval(interval), 60000);
    break;
  }
  case 'stderr-flood': {
    const chunk = Buffer.alloc(4096, 0x42);
    const interval = setInterval(() => {
      process.stderr.write(chunk);
    }, 5);
    setTimeout(() => clearInterval(interval), 60000);
    break;
  }
  case 'hang': {
    setInterval(() => {}, 60000);
    break;
  }
  case 'ignore-sigterm': {
    process.on('SIGTERM', () => {
      // swallow SIGTERM so the adapter has to escalate to SIGKILL on POSIX.
    });
    setInterval(() => {}, 60000);
    break;
  }
  case 'self-signal': {
    if (process.platform !== 'win32') {
      process.kill(process.pid, 'SIGKILL');
    } else {
      process.exit(1);
    }
    break;
  }
  case 'env-dump-success': {
    // Report only presence of security-sensitive variables so the summary stays within maxLength.
    const flags = [];
    for (const name of ['GITHUB_TOKEN', 'ANTHROPIC_API_KEY', 'AGENTIC_REVIEW_API_KEY']) {
      flags.push(`${name}=${process.env[name] ?? 'absent'}`);
    }
    commitWithSummary(paths, input, rv, flags.join('|'));
    process.exit(0);
  }
  case 'env-dump-required-vars': {
    const summary = [
      `NO_COLOR=${process.env.NO_COLOR}`,
      `DOTNET_NOLOGO=${process.env.DOTNET_NOLOGO}`,
      `DOTNET_CLI_TELEMETRY_OPTOUT=${process.env.DOTNET_CLI_TELEMETRY_OPTOUT}`,
    ].join('|');
    commitWithSummary(paths, input, rv, summary);
    process.exit(0);
  }
  case 'exit-0-empty': {
    process.exit(0);
  }
  default:
    fail(2, `APR_USAGE_INVALID: unknown FAKE_RUNTIME_SCENARIO=${s}`);
}
