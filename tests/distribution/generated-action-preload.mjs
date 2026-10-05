// Repository supervision only. This file is never bundled or selected by Action inputs.
import fs from 'node:fs';
import https from 'node:https';
import http from 'node:http';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import path from 'node:path';

const config = JSON.parse(fs.readFileSync(process.env.APR_D3_FIXTURE_CONFIG, 'utf8'));
const append = fs.appendFileSync.bind(fs);
const log = (value) => append(config.audit, JSON.stringify(value) + '\n');
const active = new Map();
const parents = [];
let native;
const originalMkdtemp = fs.promises.mkdtemp.bind(fs.promises);
fs.promises.mkdtemp = async (...args) => {
  const result = await originalMkdtemp(...args);
  if (path.basename(String(args[0])) === 'apr-payload-') {
    parents.push(result);
    log({ kind: 'payload-parent', path: result, mode: fs.statSync(result).mode & 0o777 });
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
  return result;
};
https.request = (url, options, callback) => {
  log({ kind: 'request', url: String(url), headers: options.headers, method: options.method });
  const logical = new URL(url);
  return http.request(new URL(logical.pathname + logical.search, config.origin), options, callback);
};
const originalSpawn = childProcess.spawn.bind(childProcess);
childProcess.spawn = (command, args, options) => {
  const child = originalSpawn(command, args, options);
  if (command === '/proc/self/fd/3') {
    const fd = options.stdio[3];
    const stat = fs.fstatSync(fd);
    const executable = active.get(fd);
    native = {
      pid: child.pid,
      fd,
      executable,
      payloadRoot: path.dirname(path.dirname(executable)),
      stagingParent: path.dirname(path.dirname(path.dirname(executable))),
      bridgeRoot: options.cwd,
    };
    log({
      kind: 'spawn',
      ...native,
      command,
      args,
      wrapperPid: process.pid,
      env: options.env,
      dev: stat.dev,
      ino: stat.ino,
      parentMode: fs.statSync(native.stagingParent).mode & 0o777,
    });
    const originalEnd = child.stdin.end.bind(child.stdin);
    child.stdin.end = (...endArgs) => {
      const bytes = endArgs[0];
      log({ kind: 'launch', document: JSON.parse(bytes.subarray(4, 4 + bytes.readUInt32BE(0))) });
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
      log({ kind: 'native-close', code, signal, completion });
    });
  }
  return child;
};
fs.appendFileSync = (...args) => {
  if (args[0] === process.env.GITHUB_OUTPUT) {
    log({
      kind: 'presentation',
      payloadGone: !native || !fs.existsSync(native.payloadRoot),
      payloadParentGone: parents.every((parent) => !fs.existsSync(parent)),
      bridgeGone: !native || !fs.existsSync(native.bridgeRoot),
      admittedHandleClosed: !native || !active.has(native.fd),
    });
  }
  return append(...args);
};
syncBuiltinESMExports();
log({ kind: 'wrapper', pid: process.pid });
