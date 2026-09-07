import { createHash } from 'node:crypto';

// Logical names identify collections; GitHub names identify immutable records.
export function artifactFamily(logicalName: string): string {
  return `apr-object-${createHash('sha256').update('apr-artifact-family\0').update(logicalName).digest('hex')}-`;
}

export function physicalArtifactName(logicalName: string, encryptedDigest: string): string {
  if (!/^[0-9a-f]{64}$/u.test(encryptedDigest)) throw new Error('artifact_digest_invalid');
  return artifactFamily(logicalName) + encryptedDigest;
}

export function physicalArtifactMember(logicalName: string, physicalName: string): boolean {
  return (
    physicalName.startsWith(artifactFamily(logicalName)) &&
    /^[0-9a-f]{64}$/u.test(physicalName.slice(artifactFamily(logicalName).length))
  );
}
