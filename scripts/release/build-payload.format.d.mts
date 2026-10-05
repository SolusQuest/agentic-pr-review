/// <reference types="node" />

export interface PayloadIdentity {
  readonly releaseVersion: string;
  readonly platform: 'linux-x64';
  readonly sourceCommit: string;
  readonly sourceTree: string;
  readonly buildId: string;
  readonly informationalVersion: string;
}

export interface PayloadMember {
  readonly path: string;
  readonly mode: number;
  readonly size: number;
  readonly sha256: string;
}

export interface PackageReceipt {
  readonly formatVersion: 1;
  readonly archiveName: string;
  readonly archiveSize: number;
  readonly archiveSha256: string;
  readonly identity: PayloadIdentity;
  readonly members: readonly PayloadMember[];
}

export interface PackageManifest extends PayloadIdentity {
  readonly formatVersion: 1;
  readonly launcher: 'r7-d0';
  readonly buildInputs: Readonly<Record<string, unknown>>;
  readonly nativeDependencies: {
    readonly needed: readonly string[];
    readonly runtimeLoaded: readonly string[];
  };
  readonly members: readonly PayloadMember[];
}

export const LIMITS: Readonly<{
  archive: number;
  executable: number;
  notices: number;
  manifest: number;
  expanded: number;
  releaseVersion: number;
}>;
export const MEMBERS: readonly Readonly<{ path: string; mode: number; limit: number }>[];
export function sha256(bytes: string | Uint8Array): string;
export function canonicalJson(value: unknown): string;
export function validateReleaseVersion(version: unknown): string;
export function compiledIdentity(version: string, buildId: string): string;
export function archiveName(version: string): string;
export function validateElf(bytes: Buffer): void;
export function inspectPackage(
  bytes: Buffer,
  expected: unknown,
): { manifest: PackageManifest; receipt: PackageReceipt; files: Buffer[] };
export function readBoundedFile(path: string, cap: number): Promise<Buffer>;
export function materializePackage(
  bytes: Buffer,
  expected: unknown,
  parent: string,
): Promise<{ root: string; executable: string; manifest: PackageManifest }>;
export function encodePackage(
  executable: Buffer,
  notices: Buffer,
  buildInputs: Readonly<Record<string, unknown>>,
  nativeDependencies: PackageManifest['nativeDependencies'],
): { archive: Buffer; receipt: PackageReceipt };
