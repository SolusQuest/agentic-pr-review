// Repository supervision only. This file is never bundled or selected by Action inputs.
import fs from 'node:fs';
import https from 'node:https';
import http from 'node:http';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import path from 'node:path';

const config = JSON.parse(fs.readFileSync(process.env.APR_D4_FIXTURE_CONFIG, 'utf8'));
const append = fs.appendFileSync.bind(fs);
const log = (value) => append(config.audit, JSON.stringify(value) + '\n');
const active = new Map();
const ownedParents = [];
let host;
const originalMkdtemp = fs.promises.mkdtemp.bind(fs.promises);
fs.promises.mkdtemp = async (...args) => {
  const result = await originalMkdtemp(...args);
  const prefix = path.basename(String(args[0]));
  if (['apr-payload-', 'apr-dotnet-', 'apr-w1-'].includes(prefix)) {
    ownedParents.push(result);
    log({
      kind:
        prefix === 'apr-payload-'
          ? 'payload-parent'
          : prefix === 'apr-dotnet-'
            ? 'runtime-parent'
            : 'bridge-parent',
      path: result,
      mode: fs.statSync(result).mode & 0o777,
    });
  }
  return result;
};
const originalOpen = fs.promises.open.bind(fs.promises);
fs.promises.open = async (...args) => {
  const handle = await originalOpen(...args);
  active.set(handle.fd, String(args[0]));
  const stat = fs.fstatSync(handle.fd);
  log({ kind: 'open', fd: handle.fd, path: String(args[0]), dev: stat.dev, ino: stat.ino });
  const close = handle.close.bind(handle);
  const fd = handle.fd;
  handle.close = async (...closeArgs) => {
    try {
      return await close(...closeArgs);
    } finally {
      active.delete(fd);
      log({ kind: 'close', fd });
    }
  };
  return handle;
};
const originalRm = fs.promises.rm.bind(fs.promises);
fs.promises.rm = async (...args) => {
  const result = await originalRm(...args);
  log({ kind: 'removed', path: String(args[0]), absent: !fs.existsSync(args[0]) });
  if (path.basename(String(args[0])) === 'runtime.tar.gz') {
    if (config.unsupportedRuntime) {
      const core = path.join(
        path.dirname(args[0]),
        'runtime/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll',
      );
      await originalRm(core);
      log({
        kind: 'resource-fault',
        target: 'runtime-prerequisite',
        applied: !fs.existsSync(core),
      });
    }
    if (config.payloadSubstitution) {
      const parent = ownedParents.find((named) => path.basename(named).startsWith('apr-payload-'));
      const roots = fs
        .readdirSync(parent)
        .filter((name) => name.startsWith('apr-managed-package-'));
      if (roots.length !== 1) throw new Error('fixture_managed_root_ambiguous');
      const member = path.join(
        parent,
        roots[0],
        'agentic-pr-review/AgenticPrReview.Runtime.deps.json',
      );
      const original = fs.readFileSync(member);
      const changed = Buffer.from(original);
      changed[changed.length - 1] ^= 1;
      const originalMode = fs.statSync(member).mode & 0o777;
      // D2 makes verified files 0400. The explicit owner-controlled test fault
      // must also work as the non-root hosted runner, then restore that mode.
      try {
        fs.chmodSync(member, originalMode | 0o200);
        try {
          fs.writeFileSync(member, changed);
        } finally {
          fs.chmodSync(member, originalMode);
        }
      } catch (error) {
        log({
          kind: 'resource-fault-error',
          target: 'verified-nonentry',
          code:
            typeof error.code === 'string' && /^[A-Z_]+$/u.test(error.code)
              ? error.code
              : 'fixture_fault_failed',
        });
        throw error;
      }
      log({
        kind: 'resource-fault',
        target: 'verified-nonentry',
        applied: !fs.readFileSync(member).equals(original),
        originalMode,
        restoredMode: fs.statSync(member).mode & 0o777,
      });
    }
  }
  return result;
};
https.request = (url, options, callback) => {
  log({ kind: 'request', url: String(url), headers: options.headers, method: options.method });
  const logical = new URL(url);
  return http.request(new URL(logical.pathname + logical.search, config.origin), options, callback);
};
const originalSpawn = childProcess.spawn.bind(childProcess);
const originalFetch = globalThis.fetch;
globalThis.fetch = (input, options) => {
  const logical = new URL(input instanceof Request ? input.url : input);
  if (logical.origin === 'https://api.github.com')
    return originalFetch(new URL(logical.pathname + logical.search, config.origin), options);
  if (logical.origin !== config.origin) throw new Error('fixture_unexpected_fetch_origin');
  return originalFetch(input, options);
};
childProcess.spawn = (command, args, options) => {
  const executedCommand =
    config.failedExec && command === '/proc/self/fd/3' && args[0] === 'exec'
      ? '/definitely-absent-d4-host-exec'
      : command;
  const child = originalSpawn(executedCommand, args, options);
  if (command === '/proc/self/fd/3' && args[0] === 'exec') {
    const fd = options.stdio[3];
    const stat = fs.fstatSync(fd);
    const executable = fs.realpathSync('/proc/self/fd/' + fd);
    const treeFd = options.stdio[4];
    const payloadRoot = fs.realpathSync('/proc/self/fd/' + treeFd);
    const runtimeParent = path.dirname(path.dirname(executable));
    host = {
      pid: child.pid,
      fd,
      executable,
      payloadRoot,
      stagingParent: path.dirname(payloadRoot),
      runtimeParent,
      treeFd,
      bridgeRoot: options.cwd,
    };
    log({
      kind: 'spawn',
      ...host,
      command,
      executedCommand,
      args,
      wrapperPid: process.pid,
      env: options.env,
      dev: stat.dev,
      ino: stat.ino,
      parentMode: fs.statSync(host.stagingParent).mode & 0o777,
    });
    const originalEnd = child.stdin.end.bind(child.stdin);
    child.stdin.end = (...endArgs) => {
      const bytes = endArgs[0];
      log({ kind: 'launch', document: JSON.parse(bytes.subarray(4, 4 + bytes.readUInt32BE(0))) });
      if (config.bridgeProbe) {
        // The real child waits for its unchanged launch frame while the driver
        // exercises this same generated process's production official executor.
        const wait = setInterval(() => {
          if (!fs.existsSync(config.bridgeRelease)) return;
          clearInterval(wait);
          originalEnd(...endArgs);
        }, 10);
        child.once('close', () => clearInterval(wait));
        return child.stdin;
      }
      return originalEnd(...endArgs);
    };
    const originalKill = child.kill.bind(child);
    child.kill = (...killArgs) => {
      log({ kind: 'forwarded', signal: killArgs[0] });
      return originalKill(...killArgs);
    };
    const chunks = [];
    let size = 0;
    child.stdout.on('data', (bytes) => {
      size += bytes.length;
      if (size <= 20 * 1024) chunks.push(Buffer.from(bytes));
    });
    child.once('close', (code, signal) => {
      const bytes = Buffer.concat(chunks);
      let completion;
      if (bytes.length >= 4 && bytes.length === bytes.readUInt32BE(0) + 4)
        completion = JSON.parse(bytes.subarray(4));
      log({ kind: 'managed-close', code, signal, completion });
    });
  }
  return child;
};
fs.appendFileSync = (...args) => {
  if (args[0] === process.env.GITHUB_OUTPUT) {
    log({
      kind: 'presentation',
      payloadGone: !host || !fs.existsSync(host.payloadRoot),
      ownedParentsGone: ownedParents.every((parent) => !fs.existsSync(parent)),
      bridgeGone: !host || !fs.existsSync(host.bridgeRoot),
      admittedHandleClosed: !host || (!active.has(host.fd) && !active.has(host.treeFd)),
      runtimeGone: !host || !fs.existsSync(host.runtimeParent),
    });
  }
  return append(...args);
};
syncBuiltinESMExports();
log({ kind: 'wrapper', pid: process.pid });
