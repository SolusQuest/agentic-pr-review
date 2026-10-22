import { EventEmitter } from 'node:events';
import { Duplex } from 'node:stream';
import { beforeEach, expect, it, vi } from 'vitest';
const controls = vi.hoisted(() => ({ spawn: vi.fn() }));
vi.mock('node:child_process', () => ({ spawn: controls.spawn }));
import { SdkProcessOwner } from './sdk-process-owner.js';
import { SDK_CHILD_PROTOCOL, type SdkChildRequest } from './sdk-child-protocol.js';

const request: SdkChildRequest = {
  protocol: SDK_CHILD_PROTOCOL,
  build_discriminator: 'test',
  correlation_id: 'once',
  physical_name: `apr-object-${'a'.repeat(64)}-${'b'.repeat(64)}`,
  operation_directory: '/owned/copy',
  retention_days: '1',
};
beforeEach(() => controls.spawn.mockReset());
it.each([false, true])(
  'retains admission after kill request (throws=%s) until process and channels close',
  async (throws) => {
    const channel = new Duplex({
      read() {},
      write(_chunk, _encoding, callback) {
        callback();
      },
    });
    const child = Object.assign(new EventEmitter(), {
      stdio: [null, null, null, channel],
      kill: vi.fn(() => {
        if (throws) throw new Error('signal failed');
        return true;
      }),
    });
    controls.spawn.mockImplementation(() => {
      queueMicrotask(() => child.emit('spawn'));
      return child;
    });
    const owner = new SdkProcessOwner('/trusted/sdk.mjs', {
      ACTIONS_RUNTIME_TOKEN: 'synthetic',
      ACTIONS_RESULTS_URL: 'http://127.0.0.1:1',
    });
    const result = await owner.upload(request, 10);
    expect(result).toMatchObject({
      terminated: false,
      reply: { dispatch: 'dispatched', failure: 'outcome_unknown' },
    });
    expect(child.kill).toHaveBeenCalledOnce();
    child.emit('exit', null, 'SIGKILL');
    expect((await owner.upload({ ...request, correlation_id: 'two' }, 10)).reply.dispatch).toBe(
      'not_dispatched',
    );
    expect(controls.spawn).toHaveBeenCalledTimes(1);
    let closed = false;
    void result.termination.then(() => {
      closed = true;
    });
    await Promise.resolve();
    expect(closed).toBe(false);
    channel.destroy();
    child.emit('close', null, 'SIGKILL');
    await result.termination;
    expect(result.reply.failure).toBe('outcome_unknown');
    expect(await owner.awaitTermination(10)).toBe(true);
  },
);
