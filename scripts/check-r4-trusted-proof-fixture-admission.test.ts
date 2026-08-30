import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  FROZEN_FIXTURES,
  PROSPECTIVE_FIXTURE_CONTRACT,
  admitProspectiveFixture,
  admitTrustedProofFixtures,
  admitFrozenFixtures,
  materializeFrozenFixtures,
  materializeProspectiveFixture,
} from './check-r4-trusted-proof-fixture-admission.mjs';

const root = path.resolve(import.meta.dirname, '..');

function runGit(args: string[], input?: Buffer) {
  return execFileSync('git', args, {
    cwd: root,
    encoding: 'buffer',
    input,
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
}

describe('R4 exact frozen-fixture admission', () => {
  test('materializes both immutable heads through the exact shared tree and production limits', () => {
    const statusBefore = runGit(['status', '--porcelain=v1', '--untracked-files=all']).toString(
      'utf8',
    );
    let materializedCalls = 0;
    const receipt = admitFrozenFixtures({
      repositoryRoot: root,
      interceptMaterializedGit: (materializedRunGit, args, input) => {
        materializedCalls += 1;
        return materializedRunGit(args, input);
      },
    });
    const statusAfter = runGit(['status', '--porcelain=v1', '--untracked-files=all']).toString(
      'utf8',
    );
    expect(materializedCalls).toBeGreaterThan(0);
    expect(statusAfter).toBe(statusBefore);
    expect(receipt).toMatchObject({
      kind: 'apr-r4-trusted-proof-frozen-fixture-admission-v2',
      normal_head: FROZEN_FIXTURES.normalHead,
      stale_head: FROZEN_FIXTURES.staleHead,
      shared_tree: FROZEN_FIXTURES.sharedTree,
      limits: {
        head_blob_bytes: 8 * 1024 * 1024,
        git_object_response_bytes: 16 * 1024 * 1024,
      },
    });
    for (const metrics of [receipt.normal_metrics, receipt.stale_metrics]) {
      expect(metrics).toMatchObject({
        tree_objects_including_root: 178,
        regular_paths: 921,
        unique_regular_blobs: 910,
        admitted_head_source_authenticated_rest_requests: 180,
        admitted_head_source_blob_rest_requests: 0,
        admitted_head_source_anonymous_codeload_requests: 1,
        admitted_head_source_fallback_requests: 0,
        admitted_head_source_archive_credential_forwarded: false,
        baseline_per_blob_authenticated_rest_requests: 1089,
        maximum_blob_bytes: 3_585_824,
      });
      expect(metrics.head_archive_compressed_bytes).toBeGreaterThan(0);
      expect(metrics.head_archive_compressed_bytes).toBeLessThanOrEqual(
        receipt.limits.git_object_response_bytes,
      );
      expect(metrics.maximum_head_source_response_bytes).toBeLessThanOrEqual(
        receipt.limits.git_object_response_bytes,
      );
      expect(metrics.aggregate_head_source_response_bytes).toBeLessThanOrEqual(
        receipt.limits.aggregate_response_bytes,
      );
      expect(metrics.aggregate_head_blob_bytes).toBeLessThanOrEqual(
        receipt.limits.aggregate_head_blob_bytes,
      );
      expect(metrics.head_archive_inflated_regular_blob_bytes).toBe(
        metrics.aggregate_head_blob_bytes,
      );
      expect(metrics.materialized_root_bytes).toBeLessThanOrEqual(
        receipt.limits.materialized_root_bytes,
      );
      expect(metrics.aggregate_head_source_response_bytes).toBe(
        metrics.commit_json_response_bytes +
          metrics.all_tree_json_response_bytes +
          metrics.head_archive_compressed_bytes,
      );
      expect(metrics.baseline_per_blob_authenticated_rest_requests).not.toBe(
        metrics.admitted_head_source_authenticated_rest_requests,
      );
      expect(metrics.admitted_head_source_authenticated_rest_requests).toBe(
        1 + metrics.tree_objects_including_root + 1,
      );
      expect(metrics.baseline_per_blob_authenticated_rest_requests).toBe(
        1 + metrics.tree_objects_including_root + metrics.unique_regular_blobs,
      );
    }
  }, 30_000);

  test('fails closed when an immutable head is substituted', () => {
    expect(() =>
      admitFrozenFixtures({
        repositoryRoot: root,
        fixtures: { ...FROZEN_FIXTURES, staleHead: '0'.repeat(40) },
      }),
    ).toThrow(/fixture-identity/u);
  });

  test('fails closed when the frozen parent cannot be read from the repository object database', () => {
    expect(() =>
      materializeFrozenFixtures({
        repositoryRoot: root,
        sourceRunGit: (args, input) => {
          if (args[0] === 'rev-parse' && args[1] === '--git-common-dir') {
            return Buffer.from('.codex/tmp/fixture-admission-no-parent\n', 'utf8');
          }
          return runGit(args, input);
        },
      }),
    ).toThrow(/git-object-unavailable/u);
  });

  test('fails closed when frozen author, committer, timing, or message would drift', () => {
    expect(() =>
      materializeFrozenFixtures({
        repositoryRoot: root,
        identity: { name: 'Different', email: 'codex@solusquest.local' },
      }),
    ).toThrow(/fixture-commit-identity/u);
    expect(() =>
      materializeFrozenFixtures({
        repositoryRoot: root,
        commitMetadata: (metadata, role) =>
          role === 'normal'
            ? {
                ...metadata,
                date: '2026-08-28T14:41:43+08:00',
                message: 'test: altered trusted-proof canary\n',
              }
            : metadata,
      }),
    ).toThrow(/fixture-commit-identity/u);
  });

  test('fails closed when the exact-head archive cannot be generated', () => {
    expect(() =>
      admitFrozenFixtures({
        repositoryRoot: root,
        interceptMaterializedGit: (materializedRunGit, args, input) => {
          if (args[0] === 'archive') throw new Error('missing archive');
          return materializedRunGit(args, input);
        },
      }),
    ).toThrow(/git-object-unavailable/u);
  });

  test('does not silently admit the frozen tree under the retired one-mebibyte blob cap', () => {
    expect(() =>
      admitFrozenFixtures({
        repositoryRoot: root,
        limits: {
          TrackedPaths: 20_000,
          TreeMetadataBytes: 8 * 1024 * 1024,
          PathBytes: 1024,
          TreeDepth: 64,
          UniqueTreeAndBlobObjects: 4_000,
          GitObjectRequests: 4_096,
          HeadBlobBytes: 1024 * 1024,
          AggregateHeadBlobBytes: 256 * 1024 * 1024,
          MaterializedRootBytes: 256 * 1024 * 1024,
          GitObjectResponseBytes: 16 * 1024 * 1024,
          AggregateResponseBytes: 512 * 1024 * 1024,
        },
      }),
    ).toThrow(/limits-drift-HeadBlobBytes/u);
  });
});

describe('R4 prospective fixture admission', () => {
  test('keeps the immutable #225/#226 receipt distinct from the pre-merge tree construction', () => {
    const receipt = admitTrustedProofFixtures({ repositoryRoot: root });
    expect(receipt.kind).toBe('apr-r4-trusted-proof-fixture-admission-v3');
    expect(receipt.historical).toMatchObject({
      kind: 'apr-r4-trusted-proof-frozen-fixture-admission-v2',
      normal_head: FROZEN_FIXTURES.normalHead,
      stale_head: FROZEN_FIXTURES.staleHead,
    });
    expect(receipt.prospective).toMatchObject({
      kind: 'apr-r4-trusted-proof-prospective-fixture-admission-v1',
      canary: {
        path: 'proof/apr178-path-canary.txt',
        mode: '100644',
        bytes: Buffer.byteLength('APR178_TOOL_DATA_CANARY\n', 'utf8'),
        sha256: crypto
          .createHash('sha256')
          .update('APR178_TOOL_DATA_CANARY\n', 'utf8')
          .digest('hex'),
      },
      metrics: {
        admitted_head_source_authenticated_rest_requests: 180,
        admitted_head_source_blob_rest_requests: 0,
        admitted_head_source_anonymous_codeload_requests: 1,
        admitted_head_source_archive_credential_forwarded: false,
      },
    });
    expect(receipt.prospective).not.toHaveProperty('merge_sha');
    expect(receipt.prospective).not.toHaveProperty('fixture_head');
    expect(receipt.prospective.metrics.maximum_blob_bytes).toBeLessThanOrEqual(8 * 1024 * 1024);
    expect(receipt.prospective.metrics.maximum_head_source_response_bytes).toBeLessThanOrEqual(
      16 * 1024 * 1024,
    );
  }, 30_000);

  test('fails closed when the frozen canary is already in the base tree', () => {
    expect(() =>
      materializeProspectiveFixture({
        repositoryRoot: root,
        baseHead: FROZEN_FIXTURES.normalHead,
      }),
    ).toThrow(/prospective-canary-already-present/u);
  });

  test('fails closed when construction changes either the base tree or the frozen canary path', () => {
    expect(() =>
      materializeProspectiveFixture({
        repositoryRoot: root,
        interceptMaterializedGit: (runMaterializedGit, args, input) => {
          if (args[0] === 'update-index') {
            const drifted = [...args];
            drifted[3] = String(drifted[3]).replace(
              PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
              'proof/apr178-path-drift.txt',
            );
            return runMaterializedGit(drifted, input);
          }
          return runMaterializedGit(args, input);
        },
      }),
    ).toThrow(/prospective-base-tree-or-path-drift/u);

    let readTreeCalls = 0;
    expect(() =>
      materializeProspectiveFixture({
        repositoryRoot: root,
        interceptMaterializedGit: (runMaterializedGit, args, input) => {
          if (args[0] === 'read-tree') {
            readTreeCalls += 1;
            if (readTreeCalls === 1) {
              return runMaterializedGit(
                ['read-tree', '1a15b59dd21fe0ff04d0e728680acaebfedb1195^{tree}'],
                input,
              );
            }
          }
          return runMaterializedGit(args, input);
        },
      }),
    ).toThrow(/prospective-base-tree-or-path-drift/u);
  });

  test('does not silently admit the prospective tree when a production cap drifts', () => {
    expect(() =>
      admitProspectiveFixture({
        repositoryRoot: root,
        limits: {
          TrackedPaths: 20_000,
          TreeMetadataBytes: 8 * 1024 * 1024,
          PathBytes: 1024,
          TreeDepth: 64,
          UniqueTreeAndBlobObjects: 4_000,
          GitObjectRequests: 4_096,
          HeadBlobBytes: 8 * 1024 * 1024,
          AggregateHeadBlobBytes: 256 * 1024 * 1024,
          MaterializedRootBytes: 256 * 1024 * 1024,
          GitObjectResponseBytes: 8 * 1024 * 1024,
          AggregateResponseBytes: 512 * 1024 * 1024,
        },
      }),
    ).toThrow(/limits-drift-GitObjectResponseBytes/u);
  });
});
