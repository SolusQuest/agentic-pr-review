import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import { probeUnauthorizedNoLaunch } from './probe-r4-e2p-unauthorized-no-launch.mjs';

const workflowPath = path.join(process.cwd(), '.github', 'workflows', 'r4-trusted-proof.yml');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

describe('R4 E2P unauthorized no-launch probe', () => {
  it('runs the current inline preflight with revoked authorization and observes zero protected starts', async () => {
    await expect(probeUnauthorizedNoLaunch(workflowPath)).resolves.toEqual({
      schema: 'apr.r4.e2p.unauthorized-no-launch.v1',
      preflight_admitted: false,
      public_preflight_requests: 1,
      preflight_authorization_header_present: false,
      workflow_run_review_eligible: false,
      workflow_dispatch_review_eligible: false,
      starts: {
        payload: 0,
        wrapper: 0,
        provider: 0,
        state: 0,
        publisher: 0,
        csharp_payload_receipt: 0,
        node_artifact_receipt: 0,
        embedded_control_receipt: 0,
        external_control_receipt: 0,
      },
    });
  });

  it('fails closed when either protected job loses the exact false-output gate', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-no-launch-'));
    temporaryRoots.push(root);
    const copiedWorkflow = path.join(root, 'r4-trusted-proof.yml');
    fs.writeFileSync(
      copiedWorkflow,
      fs
        .readFileSync(workflowPath, 'utf8')
        .replace("needs.authorization-preflight.outputs.authorized == 'true'", 'true'),
    );
    await expect(probeUnauthorizedNoLaunch(copiedWorkflow)).rejects.toThrow(
      'APR_R4_E2P_NO_LAUNCH_GATE_INVALID',
    );
  });
});
