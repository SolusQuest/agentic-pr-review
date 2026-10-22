import { DefaultArtifactClient } from '@actions/artifact';
import { Socket } from 'node:net';
import path from 'node:path';
import { isSdkChildRequest } from './sdk-child-protocol.js';
import { readUploadFrame, writeUploadFrame } from './upload-framing.js';
import { isUploadReceipt, UPLOAD_PROTOCOL, type UploadReply } from './upload-protocol.js';

/** Called only by the generated, inventoried SDK entry. stdio 0..2 are OS-discarded. */
export async function runSdkUploadChild(buildDiscriminator: string): Promise<never> {
  const channel = new Socket({ fd: 3, readable: true, writable: true, allowHalfOpen: true });
  try {
    const request = await readUploadFrame(channel, new AbortController().signal);
    if (!isSdkChildRequest(request) || request.build_discriminator !== buildDiscriminator)
      throw new Error('sdk_child_request_invalid');
    let reply: UploadReply = {
      protocol: UPLOAD_PROTOCOL,
      build_discriminator: request.build_discriminator,
      correlation_id: request.correlation_id,
      dispatch: 'dispatched',
      failure: 'outcome_unknown',
      receipt: null,
    };
    try {
      const result = await new DefaultArtifactClient().uploadArtifact(
        request.physical_name,
        [path.join(request.operation_directory, 'artifact-envelope.json')],
        request.operation_directory,
        { compressionLevel: 0, retentionDays: Number(request.retention_days) },
      );
      const receipt = {
        artifact_id: String(result.id),
        archive_sha256: result.digest,
        archive_size: String(result.size),
      };
      if (isUploadReceipt(receipt)) reply = { ...reply, failure: 'none', receipt };
    } catch (error) {
      const code: unknown =
        typeof error === 'object' && error !== null
          ? (error as { code?: unknown }).code
          : undefined;
      if (code === 6 || code === 'already_exists') reply = { ...reply, failure: 'conflict' };
    }
    await writeUploadFrame(channel, reply);
    // SDK result and OS resource termination are separate. The parent confirms
    // this exit and channel close before releasing the copy or accepting reply.
    process.exit(0);
  } catch {
    channel.destroy();
    process.exit(1);
  }
}
