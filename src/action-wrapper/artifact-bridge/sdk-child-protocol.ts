import path from 'node:path';
import { boundedUploadText, closedRecord, positiveDecimal } from './upload-protocol.js';

export const SDK_CHILD_PROTOCOL = 'apr.sdk.child.cr2';
export interface SdkChildRequest {
  readonly protocol: typeof SDK_CHILD_PROTOCOL;
  readonly build_discriminator: string;
  readonly correlation_id: string;
  readonly physical_name: string;
  readonly operation_directory: string;
  readonly retention_days: string;
}

export function isSdkChildRequest(value: unknown): value is SdkChildRequest {
  return (
    closedRecord(value, [
      'protocol',
      'build_discriminator',
      'correlation_id',
      'physical_name',
      'operation_directory',
      'retention_days',
    ]) &&
    value.protocol === SDK_CHILD_PROTOCOL &&
    boundedUploadText(value.build_discriminator, 256) &&
    boundedUploadText(value.correlation_id, 256) &&
    boundedUploadText(value.operation_directory, 4096) &&
    path.isAbsolute(value.operation_directory) &&
    path.normalize(value.operation_directory) === value.operation_directory &&
    typeof value.physical_name === 'string' &&
    /^apr-object-[a-f0-9]{64}-[a-f0-9]{64}$/u.test(value.physical_name) &&
    positiveDecimal(value.retention_days, 90)
  );
}
