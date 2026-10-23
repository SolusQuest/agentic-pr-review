import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { SdkProcessOwner } from './sdk-process-owner.js';
import { SDK_CHILD_PROTOCOL, type SdkChildRequest } from './sdk-child-protocol.js';

const roots: string[] = [];
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
const request: SdkChildRequest = {
  protocol: SDK_CHILD_PROTOCOL,
  build_discriminator: 'test',
  correlation_id: 'once',
  physical_name: `apr-object-${'a'.repeat(64)}-${'b'.repeat(64)}`,
  operation_directory: '/private/operation',
  retention_days: '1',
};
const env = { ACTIONS_RUNTIME_TOKEN: 'synthetic', ACTIONS_RESULTS_URL: 'http://127.0.0.1:1' };

async function fixture(
  body: string,
  environment: NodeJS.ProcessEnv = env,
): Promise<SdkProcessOwner> {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-sdk-owner-'));
  roots.push(root);
  const file = path.join(root, 'child.mjs');
  await writeFile(
    file,
    `import {Socket} from 'node:net';
const channel=new Socket({fd:3,readable:true,writable:true,allowHalfOpen:true});
let chunks=[];channel.on('data',b=>chunks.push(b));channel.on('end',()=>{
 const request=JSON.parse(Buffer.concat(chunks).subarray(4));
 const reply={protocol:'apr.sdk.upload.cr2',build_discriminator:request.build_discriminator,correlation_id:request.correlation_id,dispatch:'dispatched',failure:'none',receipt:{artifact_id:'1',archive_sha256:'a'.repeat(64),archive_size:'512'}};
 const send=()=>{const bytes=Buffer.from(JSON.stringify(reply));const frame=Buffer.alloc(bytes.length+4);frame.writeUInt32BE(bytes.length);bytes.copy(frame,4);channel.end(frame);};
 ${body}
});`,
  );
  return new SdkProcessOwner(file, environment);
}

describe('SDK process lifetime owner', () => {
  it('requires confirmed clean termination after receipt and discards child output', async () => {
    const owner = await fixture(
      "process.stdout.write('secret-canary');process.stderr.write('secret-canary');send();setTimeout(()=>process.exit(0),80);",
    );
    const started = performance.now();
    const result = await owner.upload(request, 2000);
    expect(performance.now() - started).toBeGreaterThanOrEqual(70);
    expect(result.terminated).toBe(true);
    expect(result.reply.failure).toBe('none');
    expect(await owner.awaitTermination(10)).toBe(true);
  });
  it('keeps mutation unknown when a valid receipt precedes a crash', async () => {
    const owner = await fixture('send();setTimeout(()=>process.exit(1),30);');
    const result = await owner.upload(request, 2000);
    expect(result.terminated).toBe(true);
    expect(result.reply).toMatchObject({
      dispatch: 'dispatched',
      failure: 'outcome_unknown',
      receipt: { artifact_id: '1' },
    });
  });
  it('retains the close observer after observation timeout and permits a later request after termination', async () => {
    const owner = await fixture('setInterval(()=>{},1000);');
    const result = await owner.upload(request, 100);
    expect(result.reply.failure).toBe('outcome_unknown');
    await result.termination;
    const next = await owner.upload({ ...request, correlation_id: 'different' }, 100);
    expect(next.reply.dispatch).toBe('dispatched');
    await next.termination;
  });
  it('refuses ambient injection before dispatch', async () => {
    const owner = await fixture('send();process.exit(0);', {
      ...env,
      NODE_OPTIONS: '--require=/untrusted',
    });
    expect((await owner.upload(request, 2000)).reply.dispatch).toBe('not_dispatched');
  });
  it('excludes unrelated credentials from the child environment', async () => {
    const owner = await fixture(
      'if(process.env.GITHUB_TOKEN||process.env.STATE_KEY||process.env.PROVIDER_KEY)process.exit(1);send();setTimeout(()=>process.exit(0),10);',
      { ...env, GITHUB_TOKEN: 'secret', STATE_KEY: 'secret', PROVIDER_KEY: 'secret' },
    );
    expect((await owner.upload(request, 2000)).reply.failure).toBe('none');
  });
});
