import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { build } from 'esbuild';

export const ACTION_BUNDLE_RELATIVE_PATH = '.github/actions/agentic-pr-review/dist/index.js';
export const SDK_BUNDLE_RELATIVE_PATH = '.github/actions/agentic-pr-review/dist/sdk-upload.js';
export const WRAPPER_BUILD_DISCRIMINATOR = 'r7-d0';
export const TRUSTED_PROOF_WRAPPER_BUILD_DISCRIMINATOR = 'r4-w2';
export const FRAMEWORK_FIXTURE_WRAPPER_BUILD_DISCRIMINATOR = 'r4-h1';

const scriptPath = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(scriptPath), '..');
const virtualEntryPoint = 'scripts/action-entrypoint.generated.ts';
const optionalDebugColorStubSource = 'module.exports = false;\n';
const optionalDebugColorStub = {
  name: 'optional-debug-color-stub',
  setup(buildContext) {
    buildContext.onResolve({ filter: /^supports-color$/ }, () => ({
      namespace: 'action-build-stub',
      path: 'supports-color',
    }));
    buildContext.onLoad({ filter: /^supports-color$/, namespace: 'action-build-stub' }, () => ({
      contents: optionalDebugColorStubSource,
      loader: 'js',
    }));
  },
};
function entryPointSource(wrapperBuildDiscriminator, historicalFixture = false) {
  if (wrapperBuildDiscriminator === WRAPPER_BUILD_DISCRIMINATOR) {
    return `
import { runProductionActionWrapper } from '../src/action-wrapper/index.js';

const WRAPPER_BUILD_DISCRIMINATOR = ${JSON.stringify(wrapperBuildDiscriminator)};

void runProductionActionWrapper(new URL('../', import.meta.url), WRAPPER_BUILD_DISCRIMINATOR).then(
  (exitCode) => { process.exitCode = exitCode; },
  () => { process.exitCode = 1; },
);
`;
  }
  const privateImport = historicalFixture
    ? "import { runHistoricalPrivateFixtureWrapper as runPrivateActionWrapper } from '../src/action-wrapper/historical-wrapper.fixture.js';"
    : "import { runPrivateActionWrapper } from '../src/action-wrapper/index.js';";
  return `
${privateImport}

const WRAPPER_BUILD_DISCRIMINATOR = ${JSON.stringify(wrapperBuildDiscriminator)};

void runPrivateActionWrapper({
  trustedRoot: process.env.AGENTIC_PR_REVIEW_PREPARED_ROOT,
  executableRelativePath: process.env.AGENTIC_PR_REVIEW_PREPARED_EXECUTABLE,
  payloadSha256: process.env.AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256,
  actionSourceSha: process.env.AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA,
  buildDiscriminator: process.env.AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR,
  wrapperBuildDiscriminator: WRAPPER_BUILD_DISCRIMINATOR,
}).then(
  (exitCode) => {
    process.exitCode = exitCode;
  },
  () => {
    process.exitCode = 1;
  },
);
`;
}

export async function generateActionBundle(repoRoot = defaultRepoRoot) {
  return await generateActionBundleForDiscriminator(repoRoot, WRAPPER_BUILD_DISCRIMINATOR);
}

export async function generateSdkUploadBundle(
  repoRoot = defaultRepoRoot,
  buildDiscriminator = WRAPPER_BUILD_DISCRIMINATOR,
) {
  const entry = `import {runSdkUploadChild} from '../src/action-wrapper/artifact-bridge/sdk-upload-child.js';\nvoid runSdkUploadChild(${JSON.stringify(buildDiscriminator)});\n`;
  const result = await build({
    absWorkingDir: path.resolve(repoRoot),
    banner: {
      js: "// Generated SDK upload child. Do not edit.\nimport {createRequire as __createRequire} from 'node:module';\nconst require=__createRequire(import.meta.url);",
    },
    bundle: true,
    charset: 'utf8',
    format: 'esm',
    legalComments: 'none',
    logLevel: 'silent',
    metafile: true,
    outfile: SDK_BUNDLE_RELATIVE_PATH,
    packages: 'bundle',
    platform: 'node',
    plugins: [optionalDebugColorStub],
    preserveSymlinks: true,
    sourcemap: false,
    stdin: {
      contents: entry,
      loader: 'ts',
      resolveDir: path.join(path.resolve(repoRoot), 'scripts'),
      sourcefile: 'sdk-entrypoint.generated.ts',
    },
    target: ['node24'],
    treeShaking: true,
    write: false,
  });
  if (result.outputFiles.length !== 1 || !result.metafile)
    throw new Error('sdk_bundle_generation_invalid');
  for (const output of Object.values(result.metafile.outputs)) {
    if (output.imports.some((item) => /^(?:node:)?(?:child_process|cluster)$/u.test(item.path)))
      throw new Error('sdk_bundle_process_creation_import');
  }
  // The pinned closure uses worker_threads only for data/clone helpers. A
  // future SDK change that can create another lifetime owner requires review.
  const workerImportsRemoved = result.outputFiles[0].text.replace(
    /var \{ (?:markAsUncloneable|MessagePort) \} = __require\("node:worker_threads"\);/gu,
    '',
  );
  if (workerImportsRemoved.includes('worker_threads'))
    throw new Error('sdk_bundle_worker_creation_import');
  const digest = await actionSourceDigestForEntryPoint(
    repoRoot,
    result.metafile,
    entry,
    'scripts/sdk-entrypoint.generated.ts',
  );
  return {
    bytes: Buffer.concat([
      Buffer.from(result.outputFiles[0].contents),
      Buffer.from(`// SDK source inventory sha256: ${digest}\n`),
    ]),
    metafile: result.metafile,
  };
}

async function generateActionBundleForDiscriminator(
  repoRoot,
  wrapperBuildDiscriminator,
  historicalFixture = false,
) {
  const generatedEntryPoint = entryPointSource(wrapperBuildDiscriminator, historicalFixture);
  const result = await build({
    absWorkingDir: path.resolve(repoRoot),
    banner: {
      js: [
        '// Generated by npm run build:action. Do not edit.',
        "import { createRequire as __actionCreateRequire } from 'node:module';",
        'const require = __actionCreateRequire(import.meta.url);',
      ].join('\n'),
    },
    bundle: true,
    charset: 'utf8',
    format: 'esm',
    legalComments: 'none',
    logLevel: 'silent',
    metafile: true,
    outfile: ACTION_BUNDLE_RELATIVE_PATH,
    packages: 'bundle',
    platform: 'node',
    plugins: [optionalDebugColorStub],
    preserveSymlinks: true,
    sourcemap: false,
    stdin: {
      contents: generatedEntryPoint,
      loader: 'ts',
      resolveDir: path.join(path.resolve(repoRoot), 'scripts'),
      sourcefile: path.basename(virtualEntryPoint),
    },
    target: ['node24'],
    treeShaking: true,
    write: false,
  });
  if (result.outputFiles.length !== 1 || !result.metafile) {
    throw new Error('action_bundle_generation_invalid');
  }
  const sourceDigest = await actionSourceDigestForEntryPoint(
    repoRoot,
    result.metafile,
    generatedEntryPoint,
  );
  return {
    bytes: Buffer.concat([
      Buffer.from(result.outputFiles[0].contents),
      Buffer.from(`// Action source inventory sha256: ${sourceDigest}\n`, 'utf8'),
    ]),
    metafile: result.metafile,
  };
}

export async function actionSourceDigest(repoRoot, metafile) {
  return await actionSourceDigestForEntryPoint(
    repoRoot,
    metafile,
    entryPointSource(WRAPPER_BUILD_DISCRIMINATOR),
  );
}

async function actionSourceDigestForEntryPoint(
  repoRoot,
  metafile,
  generatedEntryPoint,
  entryPath = virtualEntryPoint,
) {
  const sources = new Map([
    [entryPath, Buffer.from(generatedEntryPoint, 'utf8')],
    ['action-build-stub:supports-color', Buffer.from(optionalDebugColorStubSource, 'utf8')],
    ['scripts/build-action.mjs', await readFile(path.join(repoRoot, 'scripts/build-action.mjs'))],
    [
      '.github/actions/agentic-pr-review/payload-map.json',
      await readFile(path.join(repoRoot, '.github/actions/agentic-pr-review/payload-map.json')),
    ],
  ]);
  for (const source of Object.keys(metafile.inputs)) {
    if (sources.has(source)) continue;
    sources.set(source, await readFile(path.join(repoRoot, source)));
  }
  const digest = createHash('sha256');
  for (const [source, bytes] of [...sources].sort(([left], [right]) => left.localeCompare(right))) {
    const name = Buffer.from(source, 'utf8');
    const boundary = Buffer.allocUnsafe(8);
    boundary.writeUInt32BE(name.byteLength, 0);
    boundary.writeUInt32BE(bytes.byteLength, 4);
    digest.update(boundary);
    digest.update(name);
    digest.update(bytes);
  }
  return digest.digest('hex');
}

export async function writeActionBundle(repoRoot = defaultRepoRoot) {
  const root = path.resolve(repoRoot);
  const outputPath = path.join(root, ACTION_BUNDLE_RELATIVE_PATH);
  await writeGeneratedActionBundle(outputPath, await generateActionBundle(root));
  await writeGeneratedActionBundle(
    path.join(root, SDK_BUNDLE_RELATIVE_PATH),
    await generateSdkUploadBundle(root),
  );
}

/**
 * Generates the private E1 framework wrapper only. It is deliberately not a
 * configurable CLI surface and is never written into the checked Action tree.
 */
export async function writeFrameworkFixtureActionBundle(outputPath, repoRoot = defaultRepoRoot) {
  await writeGeneratedActionBundle(
    path.resolve(outputPath),
    await generateFrameworkFixtureActionBundle(repoRoot),
  );
}

async function writeGeneratedActionBundle(outputPath, generated) {
  const temporaryPath = `${outputPath}.tmp-${process.pid}-${randomUUID()}`;
  await mkdir(path.dirname(outputPath), { recursive: true });
  try {
    await writeFile(temporaryPath, generated.bytes, { flag: 'wx' });
    try {
      await rename(temporaryPath, outputPath);
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.code !== 'EPERM') throw error;
      await rm(outputPath, { force: true });
      await rename(temporaryPath, outputPath);
    }
  } finally {
    await rm(temporaryPath, { force: true });
  }
}

export async function generateFrameworkFixtureActionBundle(repoRoot = defaultRepoRoot) {
  return await generateActionBundleForDiscriminator(
    repoRoot,
    FRAMEWORK_FIXTURE_WRAPPER_BUILD_DISCRIMINATOR,
    true,
  );
}

/** Fixed repository-only proof artifact; never a product entrypoint selector. */
export async function generateTrustedProofActionBundle(repoRoot = defaultRepoRoot) {
  return await generateActionBundleForDiscriminator(
    repoRoot,
    TRUSTED_PROOF_WRAPPER_BUILD_DISCRIMINATOR,
  );
}

export async function writeTrustedProofActionBundle(outputPath, repoRoot = defaultRepoRoot) {
  const generated = await generateTrustedProofActionBundle(repoRoot);
  await writeGeneratedActionBundle(path.resolve(outputPath), generated);
  await writeGeneratedActionBundle(
    path.join(path.dirname(path.resolve(outputPath)), 'sdk-upload.js'),
    await generateSdkUploadBundle(repoRoot, TRUSTED_PROOF_WRAPPER_BUILD_DISCRIMINATOR),
  );
  // Verify the installed artifact that the protected consumer will execute.
  if (!(await readFile(path.resolve(outputPath))).equals(generated.bytes)) {
    throw new Error('trusted_proof_bundle_readback_invalid');
  }
}

/** Explicit six-operation synthetic proof only; never the protected production builder. */
export async function generateHistoricalTrustedProofFixtureActionBundle(
  repoRoot = defaultRepoRoot,
) {
  return generateActionBundleForDiscriminator(
    repoRoot,
    TRUSTED_PROOF_WRAPPER_BUILD_DISCRIMINATOR,
    true,
  );
}

export async function writeHistoricalTrustedProofFixtureActionBundle(
  outputPath,
  repoRoot = defaultRepoRoot,
) {
  const generated = await generateHistoricalTrustedProofFixtureActionBundle(repoRoot);
  await writeGeneratedActionBundle(path.resolve(outputPath), generated);
  if (!(await readFile(path.resolve(outputPath))).equals(generated.bytes))
    throw new Error('historical_fixture_bundle_readback_invalid');
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  await writeActionBundle();
}
