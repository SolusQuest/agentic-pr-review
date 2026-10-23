import { execFile } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { build } from 'esbuild';
import { expect, it } from 'vitest';

const run = promisify(execFile);

// A fresh process has no test-runner error listener to hide an unhandled EPIPE.
it.each(['peer-disconnect', 'write-error', 'close-before-finish', 'success'])(
  'owns frame-writer errors through actual process settlement: %s',
  async (mode) => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-upload-frame-'));
    try {
      const entry = path.join(root, 'repro.mjs');
      await build({
        stdin: {
          resolveDir: process.cwd(),
          contents: `
import assert from 'node:assert/strict';
import { createConnection } from 'node:net';
import { Duplex } from 'node:stream';
import { createUploadServer } from './src/action-wrapper/artifact-bridge/upload-server.ts';
import { writeUploadFrame } from './src/action-wrapper/artifact-bridge/upload-framing.ts';
import { encodeUploadFrame, UPLOAD_PROTOCOL } from './src/action-wrapper/artifact-bridge/upload-protocol.ts';
const mode = process.argv[2];
if (mode === 'peer-disconnect') {
  let begin, release;
  const began = new Promise(resolve => { begin = resolve; });
  const held = new Promise(resolve => { release = resolve; });
  const endpoint = ${JSON.stringify(path.join(root, 's'))};
  const server = createUploadServer({ endpoint, buildDiscriminator:'test', executor: {
    async execute(request) { begin(); await held; return { protocol:UPLOAD_PROTOCOL, build_discriminator:'test', correlation_id:request.correlation_id, dispatch:'dispatched', failure:'outcome_unknown', receipt:null }; },
    sealAdmission() {}, async awaitQuiescence() { return true; }, async dispose() {},
  }});
  await new Promise(resolve => server.listen(endpoint, resolve));
  const client = createConnection({path:endpoint,allowHalfOpen:true});
  await new Promise(resolve => client.once('connect',resolve));
  client.end(encodeUploadFrame({protocol:UPLOAD_PROTOCOL,build_discriminator:'test',correlation_id:'once',run_id:'1',run_attempt:'1',physical_name:'apr-object-'+'a'.repeat(64)+'-'+'b'.repeat(64),source_relative_path:'source',source_size:'1',source_sha256:'a'.repeat(64),retention_days:'1',timeout_ms:'1000'}));
  await began;
  client.destroy();
  await new Promise(resolve => client.once('close',resolve));
  release();
  await new Promise(resolve => setTimeout(resolve,50));
  await new Promise(resolve => server.close(resolve));
} else {
  let write;
  const stream = new Duplex({read(){},write(_bytes,_encoding,callback){write=callback;if(mode==='write-error')callback(new Error('private-write-error'));else if(mode==='success')callback();}});
  const writing=writeUploadFrame(stream,{safe:true});
  if(mode==='close-before-finish') { stream.destroy(); await assert.rejects(writing,{message:'upload_frame_write_failed'}); write(); }
  else if(mode==='write-error') await assert.rejects(writing,{message:'upload_frame_write_failed'});
  else { await writing; assert.equal(stream.writableFinished,true); assert.equal(stream.readable,true); stream.destroy(); }
  await new Promise(resolve => setImmediate(resolve));
  for(const event of ['error','close','finish','end']) assert.equal(stream.listenerCount(event),0,event+' listeners released');
}
process.stdout.write('FRAME_WRITER_SETTLED\\n');
`,
        },
        bundle: true,
        platform: 'node',
        format: 'esm',
        outfile: entry,
      });
      const result = await run(process.execPath, [entry, mode], { timeout: 5_000 });
      expect(result.stdout).toBe('FRAME_WRITER_SETTLED\n');
      expect(result.stderr).toBe('');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  },
);
