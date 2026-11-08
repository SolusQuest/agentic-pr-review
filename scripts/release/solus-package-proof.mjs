import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { constants } from 'node:fs';
import {
  chmod,
  cp,
  lstat,
  mkdir,
  mkdtemp,
  open,
  readFile,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join, relative, resolve, sep, isAbsolute } from 'node:path';
import { pathToFileURL } from 'node:url';
// Capture restore authority before loading project modules or running commands.
// Child environments are still explicit: deleting only the parent token would
// leave other inherited NuGet/GitHub credentials available to new call sites.
const token = process.env.GITHUB_TOKEN;
const actor = process.env.GITHUB_ACTOR;
for (const key of Object.keys(process.env)) {
  if (/^(GITHUB_TOKEN|GH_TOKEN|NUGET_AUTH_TOKEN|NuGetPackageSourceCredentials_.*)$/i.test(key))
    delete process.env[key];
}
const commandEnv = { PATH: process.env.PATH, DOTNET_ROOT: process.env.DOTNET_ROOT };
const { SOLUS_VERSION, SOLUS_RELEASE_SOURCE, SOLUS_FEED, PUBLIC_FEED, SOLUS_IDS, packageHashes } =
  await import('./solus-package-contract.mjs');
const { managedDependencyPolicy, verifyDependencies } =
  process.argv[2] === 'registry-readback' ? {} : await import('./build-payload.mjs');

const version = SOLUS_VERSION,
  privateFeed = SOLUS_FEED,
  publicFeed = PUBLIC_FEED,
  ids = SOLUS_IDS;
function requireThat(value, code) {
  if (!value) throw new Error(code);
}

// Metadata collection is preparatory evidence, never final package/startup qualification.
// No package bytes or raw subprocess diagnostics are emitted or uploaded.
async function collect(output, verify) {
  requireThat(process.platform === 'linux' && process.arch === 'x64', 'APR_SOLUS_RUNNER');
  const os = await readFile('/etc/os-release', 'utf8');
  requireThat(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'APR_SOLUS_RUNNER');
  const head = execFileSync('git', ['rev-parse', 'HEAD'], {
    env: commandEnv,
    encoding: 'utf8',
  }).trim();
  requireThat(head === process.env.EXPECTED_HEAD && /^[a-f0-9]{40}$/.test(head), 'APR_SOLUS_HEAD');
  requireThat(
    execFileSync('git', ['status', '--porcelain'], { env: commandEnv, encoding: 'utf8' }) === '',
    'APR_SOLUS_DIRTY',
  );
  requireThat(token && actor && /^[\w-]+(?:\[bot\])?$/.test(actor), 'APR_SOLUS_AUTH_UNAVAILABLE');
  output = resolve(output);
  requireThat(output.startsWith(resolve(process.env.RUNNER_TEMP) + sep), 'APR_SOLUS_OUTPUT');
  await mkdir(output, { mode: 0o700 });
  for (const directory of ['packages', 'http', 'plugins', 'home', 'tmp'])
    await mkdir(join(output, directory), { mode: 0o700 });
  const env = {
    ...commandEnv,
    HOME: join(output, 'home'),
    DOTNET_CLI_HOME: join(output, 'home'),
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: join(output, 'packages'),
    NUGET_HTTP_CACHE_PATH: join(output, 'http'),
    NUGET_PLUGINS_CACHE_PATH: join(output, 'plugins'),
    TMPDIR: join(output, 'tmp'),
  };
  const lock = join(output, 'actual-apr.lock.json');
  const checkedLock = 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json';
  if (verify) await writeFile(lock, await readFile(checkedLock), { flag: 'wx' });
  const args = [
    'restore',
    'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
    '-r',
    'linux-x64',
    '--no-http-cache',
    verify ? '--locked-mode' : '--force-evaluate',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    '-p:APRManagedPackage=true',
    '-p:RestoreLockedMode=' + verify,
    '-p:NuGetLockFilePath=' + lock,
    '-p:NuGetAudit=false',
    '-p:RestoreFallbackFolders=',
  ];
  const restored = spawnSync('dotnet', args, {
    env: {
      ...env,
      NuGetPackageSourceCredentials_solus: `Username=${actor};Password=${token};ValidAuthenticationTypes=Basic`,
    },
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    timeout: 300_000,
  });
  requireThat(restored.status === 0, 'APR_SOLUS_RESTORE_FAILED');
  const assets = JSON.parse(
    await readFile('runtime/src/AgenticPrReview.Runtime/obj/managed/project.assets.json', 'utf8'),
  );
  requireThat(
    Object.keys(assets.packageFolders).length === 1 &&
      resolve(Object.keys(assets.packageFolders)[0]) === env.NUGET_PACKAGES,
    'APR_SOLUS_CACHE',
  );
  const packages = [];
  for (const [identity, library] of Object.entries(assets.libraries)) {
    requireThat(library.type === 'package', 'APR_SOLUS_FOREIGN_PROJECT');
    const [id, resolvedVersion] = identity.split('/');
    const lower = id.toLowerCase();
    requireThat(
      /^[a-z0-9][a-z0-9.-]{0,100}$/.test(lower) && library.path === lower + '/' + resolvedVersion,
      'APR_SOLUS_PATH',
    );
    if (lower.startsWith('solusagent.'))
      requireThat(ids.includes(lower) && resolvedVersion === version, 'APR_SOLUS_PIN');
    const root = join(env.NUGET_PACKAGES, library.path);
    const metadata = JSON.parse(await readFile(join(root, '.nupkg.metadata'), 'utf8'));
    const expectedFeed = ids.includes(lower) ? privateFeed : publicFeed;
    requireThat(metadata.source?.replace(/\/$/, '') === expectedFeed, 'APR_SOLUS_SOURCE');
    const bytes = await readFile(join(root, `${lower}.${resolvedVersion}.nupkg`));
    const archiveSha512 = (
      await readFile(join(root, `${lower}.${resolvedVersion}.nupkg.sha512`), 'utf8')
    ).trim();
    const hashes = packageHashes(bytes, metadata.contentHash, library.sha512, archiveSha512);
    packages.push({
      id: lower,
      version: resolvedVersion,
      ...hashes,
      archiveSha512,
      role: 'managed',
      source: expectedFeed,
    });
  }
  requireThat(
    ids.every((id) => packages.some((item) => item.id === id)),
    'APR_SOLUS_MISSING',
  );
  requireThat(packages.length <= 16, 'APR_SOLUS_INVENTORY');
  packages.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  let constructed = false;
  const packageRepositories = [];
  if (verify) {
    const policy = managedDependencyPolicy(
      JSON.parse(await readFile('scripts/release/build-payload.inputs.json', 'utf8')),
    );
    await verifyDependencies(env.NUGET_PACKAGES, policy, assets);
    requireThat(
      (await readFile(lock)).equals(await readFile(checkedLock)),
      'APR_SOLUS_LOCK_CHANGED',
    );
    const fixture =
      'runtime/tests/SolusStartupFixture/AgenticPrReview.Runtime.SolusStartupFixture.csproj';
    const properties = [
      '-p:APRManagedPackage=true',
      '-p:APRReleaseVersion=v0.0.0-internal.1',
      '-p:APRBuildId=' + '0'.repeat(64),
      '-p:NuGetAudit=false',
    ];
    const cleanRun = (args, code) => {
      const result = spawnSync('dotnet', args, {
        env,
        encoding: 'utf8',
        maxBuffer: 16 * 1024 * 1024,
        timeout: 300_000,
      });
      if (result.status !== 0) {
        const errors = [
          ...new Set(
            ((result.stdout || '') + (result.stderr || '')).match(
              /\b(?:CS|MSB|NU)\d{4}\b|APR_SOLUS_[A-Z_]+/g,
            ) || [],
          ),
        ];
        console.error(JSON.stringify({ stage: code, errorCodes: errors }));
        // The build is credential-free. Emit only bounded C# diagnostics from
        // the APR-owned source tree, never restore output or arbitrary logs.
        if (code === 'APR_SOLUS_FIXTURE_BUILD') {
          const diagnostics = [
            ...new Set(
              (result.stdout || '')
                .split(/\r?\n/)
                .filter((line) => line.includes('/runtime/') && /error CS\d{4}:/.test(line))
                .map((line) => line.slice(line.indexOf('/runtime/') + 1, 2048)),
            ),
          ];
          console.error(JSON.stringify({ diagnostics: diagnostics.slice(0, 20) }));
        }
      }
      requireThat(result.status === 0, code);
      return result.stdout;
    };
    cleanRun(
      [
        'restore',
        fixture,
        '-r',
        'linux-x64',
        '--configfile',
        'scripts/release/build-payload.managed.nuget.config',
        ...properties,
      ],
      'APR_SOLUS_FIXTURE_RESTORE',
    );
    const startup = join(output, 'startup');
    cleanRun(
      [
        'build',
        fixture,
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '--no-restore',
        '--nologo',
        '-o',
        startup,
        ...properties,
      ],
      'APR_SOLUS_FIXTURE_BUILD',
    );
    const proof = cleanRun(
      [join(startup, 'AgenticPrReview.Runtime.SolusStartupFixture.dll')],
      'APR_SOLUS_STARTUP',
    );
    requireThat(
      proof.includes('constructed=true dispatches=0 negative_checks=8 credential_free=true'),
      'APR_SOLUS_STARTUP',
    );
    const names = [
      'SolusAgent.Api',
      'SolusAgent.Tools.Api',
      'SolusAgent.Runtime.Api',
      'SolusAgent.Runtime',
      'SolusAgent.Providers.DeepSeek',
    ];
    for (const name of names) {
      const archive = join(
        env.NUGET_PACKAGES,
        name.toLowerCase(),
        version,
        `${name.toLowerCase()}.${version}.nupkg`,
      );
      const original = execFileSync('/usr/bin/unzip', ['-p', archive, `lib/net10.0/${name}.dll`], {
        env,
        maxBuffer: 64 * 1024 * 1024,
      });
      requireThat(
        original.equals(await readFile(join(startup, name + '.dll'))),
        'APR_SOLUS_ASSEMBLY_BYTES',
      );
      // Signed archive identity is checked above. Bind emitted repository commit
      // metadata as well; when absent, keep publisher/tag provenance explicit.
      const nuspec = execFileSync('/usr/bin/unzip', ['-p', archive, '*.nuspec'], {
        env,
        encoding: 'utf8',
        maxBuffer: 256 * 1024,
      });
      const repositories = [...nuspec.matchAll(/<repository\b[^>]*\/?\s*>/g)];
      requireThat(repositories.length === 1, 'APR_SOLUS_RELEASE_SOURCE');
      const declaration = repositories[0][0];
      const url = /\burl=["']([^"']+)["']/.exec(declaration)?.[1];
      const commit = /\bcommit=["']([^"']+)["']/.exec(declaration)?.[1];
      requireThat(
        url === 'https://github.com/SolusQuest/solus-agent' &&
          (commit === undefined || commit === SOLUS_RELEASE_SOURCE),
        'APR_SOLUS_RELEASE_SOURCE',
      );
      packageRepositories.push({
        id: name.toLowerCase(),
        repository: url,
        commit: commit ?? null,
        provenance: commit ? 'nuspec-commit' : 'publisher-tag',
      });
    }
    const native = JSON.parse(
      cleanRun(
        [
          'msbuild',
          'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
          '-nologo',
          '-p:APRManagedPackage=false',
          '-getItem:PackageReference,Compile',
        ],
        'APR_SOLUS_NATIVE_SELECTION',
      ),
    );
    requireThat(
      !native.Items.PackageReference.some((item) =>
        item.Identity.toLowerCase().startsWith('solusagent.'),
      ) && !native.Items.Compile.some((item) => /(^|[/\\])Solus[/\\]/.test(item.Identity)),
      'APR_SOLUS_NATIVE_CLOSURE',
    );
    for (const property of [
      'PublishAot=true',
      'SelfContained=true',
      'APRReleasePackage=true',
      'SolusAgentVersion=0.1.0-internal.2',
    ]) {
      const rejected = spawnSync(
        'dotnet',
        [
          'msbuild',
          'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
          '-nologo',
          '-t:ValidateSolusManagedProfile',
          '-p:APRManagedPackage=true',
          '-p:' + property,
        ],
        { env, encoding: 'utf8', timeout: 30_000 },
      );
      requireThat(
        rejected.status !== 0 && rejected.stdout.includes('APR_SOLUS_MANAGED_PROFILE_UNSUPPORTED'),
        'APR_SOLUS_PROPERTY_REFUSAL',
      );
    }
    requireThat(
      execFileSync('git', ['status', '--porcelain'], { env, encoding: 'utf8' }) === '',
      'APR_SOLUS_DIRTY',
    );
    constructed = true;
  }
  const facts = {
    qualification: false,
    mode: verify ? 'verify' : 'collect',
    sourceCommit: head,
    publishedSourceAnchor: SOLUS_RELEASE_SOURCE,
    version,
    coldCache: true,
    constructed,
    producerProof: false,
    packages,
    packageRepositories,
    actualAprLock: JSON.parse(await readFile(lock, 'utf8')),
  };
  await writeFile(join(output, 'metadata.json'), JSON.stringify(facts, null, 2) + '\n', {
    flag: 'wx',
  });
  console.log(
    JSON.stringify({
      qualification: false,
      mode: verify ? 'verify' : 'collect',
      sourceCommit: head,
      publishedSourceAnchor: SOLUS_RELEASE_SOURCE,
      version,
      coldCache: true,
      packages: packages.length,
      constructed,
      producerProof: false,
    }),
  );
}
export const registryReadback = Object.freeze({
  version: '0.1.0-internal.5',
  source: 'd059cfb91d63f302894fbfa7f98a500d19bd81e0',
  tree: '69deff34a8b84c67b29682ee31ccdb86d643ee9b',
  repository: 'SolusQuest/solus-agent',
  feed: 'https://nuget.pkg.github.com/SolusQuest/index.json',
  ids: Object.freeze({
    'SolusAgent.Api': 1523190358,
    'SolusAgent.Tools.Api': 1523190395,
    'SolusAgent.Runtime.Api': 1523190444,
    'SolusAgent.Runtime': 1523190493,
    'SolusAgent.Providers.DeepSeek': 1523190537,
  }),
});

export function registryRestoreEnvironment(base, authority = { token, actor }) {
  requireThat(
    authority.token && /^[\w-]+(?:\[bot\])?$/.test(authority.actor ?? ''),
    'APR_SOLUS_AUTH_UNAVAILABLE',
  );
  const clean = Object.fromEntries(
    Object.entries(base).filter(
      ([key]) =>
        !/^(GITHUB_TOKEN|GH_TOKEN|NUGET_AUTH_TOKEN|NuGetPackageSourceCredentials_.*)$/i.test(key),
    ),
  );
  return {
    ...clean,
    NuGetPackageSourceCredentials_selected: `Username=${authority.actor};Password=${authority.token};ValidAuthenticationTypes=Basic`,
  };
}

// Standard-library inspection only. No original member text or binary leaves
// this process: the JSON result contains a closed package-fact projection.
export const registryArchiveProgram = String.raw`
import sys,zipfile,hashlib,json,re,struct,xml.etree.ElementTree as ET
path,name,version,source=sys.argv[1:]
graph={'SolusAgent.Api':[],'SolusAgent.Tools.Api':[],
 'SolusAgent.Runtime.Api':['SolusAgent.Api','SolusAgent.Tools.Api'],
 'SolusAgent.Runtime':['SolusAgent.Api','SolusAgent.Tools.Api','SolusAgent.Runtime.Api'],
 'SolusAgent.Providers.DeepSeek':['SolusAgent.Runtime.Api']}
def need(ok):
 if not ok: raise ValueError('package contract')
try:
 with zipfile.ZipFile(path) as z:
  entries=z.infolist(); names=[v.filename for v in entries]
  need(0<len(entries)<=32 and len(set(names))==len(names))
  need(sum(v.file_size for v in entries)<=64*1024*1024)
  required=[name+'.nuspec','lib/net10.0/'+name+'.dll','lib/net10.0/'+name+'.xml']
  need(all(v in names for v in required))
  inventory=[]
  for v in entries:
   need(v.filename in required or v.filename in ['[Content_Types].xml','_rels/.rels'] or
    re.fullmatch(r'package/services/metadata/core-properties/[a-f0-9]+\.psmdcp',v.filename))
   need(not v.is_dir() and (v.external_attr>>16)&0o170000 != 0o120000)
   need(0<v.file_size<=16*1024*1024)
   data=z.read(v) # zipfile validates each original member CRC
   inventory.append({'path':v.filename,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
  need(z.testzip() is None)
  spec=z.read(name+'.nuspec'); need(len(spec)<=65536 and b'<!DOCTYPE' not in spec.upper())
  metadata=ET.fromstring(spec).find('{*}metadata'); need(metadata is not None)
  need(len(metadata.findall('{*}id'))==len(metadata.findall('{*}version'))==1)
  need(metadata.findtext('{*}id')==name and metadata.findtext('{*}version')==version)
  repos=metadata.findall('{*}repository');need(len(repos)==1)
  repo=repos[0].attrib
  need(repo.get('type')=='git' and repo.get('url')=='https://github.com/SolusQuest/solus-agent' and repo.get('commit')==source)
  deps=[]
  for d in metadata.findall('.//{*}dependency'):
   need(d.get('id') in graph[name] and d.get('version') in [version,'['+version+', )','['+version+',)','['+version+']'])
   deps.append({'id':d.get('id'),'range':d.get('version')})
  need(sorted(v['id'] for v in deps)==sorted(graph[name]))
  dll=z.read('lib/net10.0/'+name+'.dll')
  need(dll[:2]==b'MZ' and len(dll)>=64)
  pe=struct.unpack_from('<I',dll,60)[0];need(dll[pe:pe+4]==b'PE\0\0')
  sections=struct.unpack_from('<H',dll,pe+6)[0];optional=pe+24
  optionalSize=struct.unpack_from('<H',dll,pe+20)[0];magic=struct.unpack_from('<H',dll,optional)[0]
  need(magic in [0x10b,0x20b]);directory=optional+(96 if magic==0x10b else 112)
  need(directory+56<=optional+optionalSize)
  rva,size=struct.unpack_from('<II',dll,directory+48);debug=[]
  if size:
   need(size%28==0 and size<=28*32);offset=None
   for i in range(sections):
    s=optional+optionalSize+40*i;virtualSize,address,rawSize,raw=struct.unpack_from('<IIII',dll,s+8)
    if address<=rva and rva+size<=address+min(virtualSize,rawSize):offset=raw+rva-address
   need(offset is not None and offset+size<=len(dll))
   for i in range(size//28):
    kind,length,pointer=struct.unpack_from('<III',dll,offset+i*28+12)[0],struct.unpack_from('<I',dll,offset+i*28+16)[0],struct.unpack_from('<I',dll,offset+i*28+24)[0]
    need(pointer+length<=len(dll));debug.append(kind)
  # This release's supported package validator excludes standalone PDB assets.
  # Never call a present embedded PDB/SourceLink inspected without a parser.
  need(17 not in debug)
  print(json.dumps({'zipCRC':'verified','inventory':inventory,'nuspec':{'id':name,'version':version,
   'repository':'https://github.com/SolusQuest/solus-agent','commit':source,'dependencies':deps},
   'pdb':{'embedded':False,'packagePdbMembers':0,'externalCodeViewReference':2 in debug},
   'sourceLink':{'status':'unavailable-no-pdb-in-original-package','verified':False}},sort_keys=True))
except Exception:
 print('APR_SOLUS_REGISTRY_ARCHIVE_INVALID',file=sys.stderr);sys.exit(1)
`;

export async function runRegistryCommand(label, command, args, cwd, childEnv, context) {
  const { interrupted, until, processes } = context;
  requireThat(!interrupted.signal.aborted, 'APR_SOLUS_REGISTRY_INTERRUPTED');
  const timeout = Math.min(300_000, until - performance.now() - 20_000);
  requireThat(timeout > 0, 'APR_SOLUS_REGISTRY_DEADLINE');
  const child = spawn(command, args, {
    cwd,
    env: childEnv,
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const chunks = [];
  let bytes = 0,
    failed = false,
    category;
  const kill = (reason) => {
    failed = true;
    category ??= reason;
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {}
  };
  context.activeKill = kill;
  const timer = setTimeout(() => kill('timeout'), timeout);
  const started = performance.now();
  child.stdout.on('data', (chunk) => {
    bytes += chunk.length;
    if (bytes <= 16 * 1024 * 1024) chunks.push(chunk);
    else kill('output-limit');
  });
  child.stderr.on('data', (chunk) => {
    bytes += chunk.length;
    if (bytes > 16 * 1024 * 1024) kill('output-limit');
  });
  child.stdout.once('error', () => kill('stream'));
  child.stderr.once('error', () => kill('stream'));
  let code, signal;
  try {
    [code, signal] = await new Promise((done) => {
      child.once('error', () => {
        failed = true;
        category ??= 'spawn';
      });
      child.once('close', (c, s) => done([c, s]));
    });
  } finally {
    clearTimeout(timer);
  }
  let closed = child.pid === undefined;
  for (let i = 0; !closed && i < 200; i++) {
    try {
      process.kill(-child.pid, 0);
      kill('process-group');
    } catch (error) {
      if (error.code === 'ESRCH') {
        closed = true;
        break;
      }
      failed = true;
      break;
    }
    await new Promise((done) => setTimeout(done, 10));
  }
  processes.push({
    stage: label,
    pid: child.pid ?? null,
    exit: code,
    groupClosed: closed,
    elapsedMs: Math.ceil(performance.now() - started),
  });
  context.activeKill = undefined;
  if (!closed || failed || code !== 0 || signal !== null) {
    const stages = [
      'source-git',
      'foundation-evaluate',
      'foundation-restore',
      'archive-inspect',
      'upstream-archive-validator',
      'foundation-build',
      'foundation-entry',
    ];
    const categories = [
      'spawn',
      'timeout',
      'output-limit',
      'stream',
      'interrupted',
      'process-group',
      'exit',
    ];
    const reason =
      category ??
      (!closed
        ? 'process-group'
        : interrupted.signal.aborted
          ? 'interrupted'
          : code !== 0
            ? 'exit'
            : 'unknown');
    console.error(
      'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=' +
        (stages.includes(label) ? label : 'unknown') +
        ' category=' +
        (categories.includes(reason) ? reason : 'unknown'),
    );
    throw new Error('APR_SOLUS_REGISTRY_COMMAND');
  }
  return Buffer.concat(chunks).toString('utf8');
}

async function readRegistry(output) {
  const fixed = registryReadback;
  requireThat(process.platform === 'linux' && process.arch === 'x64', 'APR_SOLUS_RUNNER');
  const os = await readFile('/etc/os-release', 'utf8');
  requireThat(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'APR_SOLUS_RUNNER');
  requireThat(token && actor, 'APR_SOLUS_AUTH_UNAVAILABLE');
  const parent = await realpath(process.env.RUNNER_TEMP);
  output = resolve(output);
  requireThat(
    resolve(parent) === resolve(process.env.RUNNER_TEMP) &&
      output.startsWith(parent + sep) &&
      relative(parent, output).split(sep).length === 1,
    'APR_SOLUS_OUTPUT',
  );
  const custody = await mkdtemp(join(parent, 'apr-solus-private-'));
  await chmod(custody, 0o700);
  const owned = await lstat(custody);
  const until = performance.now() + 900_000;
  const processes = [];
  const interrupted = new AbortController();
  let execution;
  const interrupt = () => {
    interrupted.abort();
    execution?.activeKill?.('interrupted');
  };
  try {
    process.on('SIGTERM', interrupt);
    process.on('SIGINT', interrupt);
    let stage = 'source-admission',
      manifest;
    const env = {
      ...commandEnv,
      HOME: join(custody, 'home'),
      DOTNET_CLI_HOME: join(custody, 'home'),
      TMPDIR: join(custody, 'tmp'),
      NUGET_PACKAGES: join(custody, 'packages'),
      NUGET_HTTP_CACHE_PATH: join(custody, 'http'),
      NUGET_PLUGINS_CACHE_PATH: join(custody, 'plugins'),
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: '1',
      MSBUILDDISABLENODEREUSE: '1',
      GIT_CONFIG_NOSYSTEM: '1',
      GIT_TERMINAL_PROMPT: '0',
    };
    execution = { interrupted, until, processes };
    const run = (label, command, args, cwd, childEnv = env) =>
      runRegistryCommand(label, command, args, cwd, childEnv, execution);
    const git = (args, cwd = custody) =>
      run(
        'source-git',
        'git',
        ['-c', 'credential.helper=', '-c', 'core.hooksPath=/dev/null', ...args],
        cwd,
      );
    async function bounded(path, maximum) {
      requireThat(
        path.startsWith(custody + sep) && (await realpath(path)) === resolve(path),
        'APR_SOLUS_REGISTRY_FILE',
      );
      const handle = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
      try {
        const stat = await handle.stat();
        requireThat(
          stat.isFile() && stat.uid === process.getuid() && stat.size > 0 && stat.size <= maximum,
          'APR_SOLUS_REGISTRY_FILE',
        );
        const bytes = Buffer.alloc(stat.size + 1);
        let size = 0;
        while (size < bytes.length) {
          const part = await handle.read(bytes, size, bytes.length - size, size);
          if (!part.bytesRead) break;
          size += part.bytesRead;
        }
        requireThat(size === stat.size, 'APR_SOLUS_REGISTRY_FILE');
        return bytes.subarray(0, size);
      } finally {
        await handle.close();
      }
    }
    try {
      for (const directory of ['home', 'tmp', 'packages', 'http', 'plugins'])
        await mkdir(join(custody, directory), { mode: 0o700 });
      const checkout = process.cwd();
      const head = (await git(['rev-parse', 'HEAD'], checkout)).trim();
      requireThat(
        /^[a-f0-9]{40}$/.test(head) &&
          head === process.env.EXPECTED_HEAD &&
          head === process.env.WORKFLOW_HEAD &&
          process.env.GITHUB_REPOSITORY === 'SolusQuest/agentic-pr-review' &&
          process.env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
          process.env.GITHUB_RUN_ATTEMPT === '1' &&
          /^[1-9][0-9]{0,18}$/.test(process.env.GITHUB_RUN_ID ?? ''),
        'APR_SOLUS_HEAD',
      );
      requireThat((await git(['status', '--porcelain'], checkout)) === '', 'APR_SOLUS_DIRTY');
      stage = 'public-source';
      const upstream = join(custody, 'upstream');
      await mkdir(upstream, { mode: 0o700 });
      const url = 'https://github.com/' + fixed.repository;
      requireThat(
        (await git(['ls-remote', url, 'refs/tags/v' + fixed.version])).trim() ===
          fixed.source + '\trefs/tags/v' + fixed.version,
        'APR_SOLUS_REGISTRY_TAG',
      );
      await git(['init', '--quiet'], upstream);
      await git(['fetch', '--depth=1', url, fixed.source], upstream);
      await git(['checkout', '--quiet', '--detach', 'FETCH_HEAD'], upstream);
      requireThat(
        (await git(['rev-parse', 'HEAD'], upstream)).trim() === fixed.source &&
          (await git(['rev-parse', 'HEAD^{tree}'], upstream)).trim() === fixed.tree &&
          (await git(['status', '--porcelain'], upstream)) === '',
        'APR_SOLUS_REGISTRY_SOURCE',
      );
      const { packages: graph } = await import(
        pathToFileURL(join(upstream, 'scripts/packages/common.ts')).href
      );
      const { selectScenarios } = await import(
        pathToFileURL(join(upstream, 'scripts/packages/selection.ts')).href
      );
      requireThat(
        JSON.stringify(Object.keys(graph).sort()) === JSON.stringify(Object.keys(fixed.ids).sort()),
        'APR_SOLUS_REGISTRY_IDS',
      );
      stage = 'registry-identity';
      const registry = [];
      for (const [id, registryId] of Object.entries(fixed.ids)) {
        const api = 'https://api.github.com/orgs/SolusQuest/packages/nuget/' + id;
        const values = [];
        for (const suffix of ['', '/versions/' + registryId]) {
          const response = await fetch(api + suffix, {
            redirect: 'error',
            signal: AbortSignal.any([
              interrupted.signal,
              AbortSignal.timeout(
                Math.min(30_000, Math.max(1, until - performance.now() - 20_000)),
              ),
            ]),
            headers: {
              Authorization: 'Bearer ' + token,
              Accept: 'application/vnd.github+json',
              'X-GitHub-Api-Version': '2022-11-28',
              Connection: 'close',
            },
          });
          try {
            requireThat(response.status === 200, 'APR_SOLUS_REGISTRY_IDENTITY');
            const chunks = [];
            let size = 0;
            for await (const chunk of response.body) {
              size += chunk.length;
              requireThat(size <= 65536, 'APR_SOLUS_REGISTRY_IDENTITY');
              chunks.push(chunk);
            }
            values.push(JSON.parse(Buffer.concat(chunks).toString('utf8')));
          } finally {
            await response.body?.cancel().catch(() => {});
          }
        }
        requireThat(
          values[0].name === id &&
            values[0].visibility === 'private' &&
            values[0].package_type === 'nuget' &&
            values[0].repository?.full_name === fixed.repository &&
            values[1].id === registryId &&
            values[1].name === fixed.version,
          'APR_SOLUS_REGISTRY_IDENTITY',
        );
        registry.push({ id, registryId, private: true });
      }
      stage = 'foundation';
      const [selection] = selectScenarios(join(upstream, 'tests/PackageConsumers'), {
        scenarios: ['foundation'],
      });
      requireThat(selection && selection.arguments.length === 0, 'APR_SOLUS_REGISTRY_SCENARIO');
      const scenario = join(custody, 'foundation');
      await cp(selection.path, scenario, {
        recursive: true,
        filter: (path) =>
          !['bin', 'obj', '.local', '.git', 'TestResults'].includes(path.split(/[\\/]/).at(-1)),
      });
      await writeFile(join(custody, 'global.json'), await readFile(join(upstream, 'global.json')), {
        flag: 'wx',
        mode: 0o600,
      });
      const inventory = JSON.parse(await readFile(join(scenario, 'scenario.json'), 'utf8'));
      requireThat(
        Array.isArray(inventory.projects) &&
          inventory.projects.length > 0 &&
          inventory.projects.length <= 8 &&
          new Set(inventory.projects).size === inventory.projects.length &&
          Array.isArray(inventory.entrypoints) &&
          inventory.entrypoints.length > 0 &&
          inventory.entrypoints.length <= 4 &&
          inventory.entrypoints.every((p) => inventory.projects.includes(p)),
        'APR_SOLUS_REGISTRY_SCENARIO',
      );
      for (const project of inventory.projects) {
        requireThat(
          typeof project === 'string' &&
            /^[A-Za-z0-9_./-]+\.csproj$/.test(project) &&
            !project.split('/').includes('..') &&
            !isAbsolute(project),
          'APR_SOLUS_REGISTRY_SCENARIO',
        );
        const path = join(scenario, project);
        requireThat(
          (await realpath(path)).startsWith(scenario + sep) &&
            !(await lstat(path)).isSymbolicLink(),
          'APR_SOLUS_REGISTRY_SCENARIO',
        );
      }
      const config = join(custody, 'NuGet.Config');
      await writeFile(
        config,
        '<configuration><packageSources><clear/><add key="selected" value="' +
          fixed.feed +
          '"/><add key="tooling" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="selected"><package pattern="SolusAgent.*"/></packageSource><packageSource key="tooling"><package pattern="*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><disabledPackageSources><clear/></disabledPackageSources></configuration>',
        { flag: 'wx', mode: 0o600 },
      );
      const properties = [
        '-p:SolusAgentVersion=' + fixed.version,
        '-p:Configuration=Release',
        '-p:ImportDirectoryBuildProps=false',
        '-p:ImportDirectoryBuildTargets=false',
        '-p:ManagePackageVersionsCentrally=false',
        '-p:NuGetAudit=false',
        '-p:RestoreFallbackFolders=',
        '-p:RestorePackagesPath=' + env.NUGET_PACKAGES,
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false',
      ];
      const assemblies = new Map(),
        assets = [];
      for (const project of inventory.projects) {
        const evaluated = await run(
          'foundation-evaluate',
          'dotnet',
          [
            'msbuild',
            project,
            '-nologo',
            ...properties,
            '-getProperty:TargetFramework,PublishAot,PublishTrimmed,IsPackable,AssemblyName,OutputType',
            '-getItem:ProjectReference,PackageReference,Reference,Compile',
          ],
          scenario,
        );
        const evaluation = JSON.parse(evaluated.slice(evaluated.indexOf('{')));
        requireThat(
          evaluation.Properties.TargetFramework === 'net10.0' &&
            evaluation.Properties.PublishAot !== 'true' &&
            evaluation.Properties.PublishTrimmed !== 'true' &&
            evaluation.Properties.IsPackable === 'false' &&
            !evaluation.Items.Reference?.length,
          'APR_SOLUS_REGISTRY_SCENARIO',
        );
        const name = evaluation.Properties.AssemblyName;
        requireThat(
          /^[A-Za-z][A-Za-z0-9_.-]{0,150}$/.test(name) &&
            name !== 'SolusAgent.ContractTests' &&
            !Object.keys(fixed.ids).some((id) => id.toLowerCase() === name.toLowerCase()),
          'APR_SOLUS_REGISTRY_SCENARIO',
        );
        assemblies.set(project, name);
        for (const item of evaluation.Items.Compile ?? [])
          requireThat(
            (
              await realpath(item.FullPath || resolve(scenario, project, '..', item.Identity))
            ).startsWith(scenario + sep),
            'APR_SOLUS_REGISTRY_SCENARIO',
          );
        requireThat(
          new Set([...assemblies.values()].map((name) => name.toLowerCase())).size ===
            assemblies.size &&
            (!inventory.entrypoints.includes(project) ||
              evaluation.Properties.OutputType === 'Exe'),
          'APR_SOLUS_REGISTRY_SCENARIO',
        );
        for (const reference of evaluation.Items.ProjectReference ?? [])
          requireThat(
            inventory.projects.some(
              (p) => resolve(scenario, p) === resolve(scenario, project, '..', reference.Identity),
            ),
            'APR_SOLUS_REGISTRY_SCENARIO',
          );
        for (const item of evaluation.Items.PackageReference ?? [])
          if (item.Identity.toLowerCase().startsWith('solusagent.'))
            requireThat(
              Object.keys(fixed.ids).some(
                (id) => id.toLowerCase() === item.Identity.toLowerCase(),
              ) && item.Version === '[' + fixed.version + ']',
              'APR_SOLUS_REGISTRY_PIN',
            );
        await run(
          'foundation-restore',
          'dotnet',
          ['restore', project, '--configfile', config, '--no-http-cache', ...properties],
          scenario,
          registryRestoreEnvironment(env),
        );
        const actual = JSON.parse(
          await readFile(join(scenario, project, '../obj/project.assets.json'), 'utf8'),
        );
        requireThat(
          Object.keys(actual.packageFolders).length === 1 &&
            resolve(Object.keys(actual.packageFolders)[0]) === env.NUGET_PACKAGES,
          'APR_SOLUS_REGISTRY_CACHE',
        );
        assets.push(actual);
        for (const [identity, library] of Object.entries(actual.libraries))
          if (identity.toLowerCase().startsWith('solusagent.'))
            requireThat(
              library.type === 'package' &&
                library.path === identity.toLowerCase() &&
                Object.keys(fixed.ids).some(
                  (id) => identity.toLowerCase() === id.toLowerCase() + '/' + fixed.version,
                ),
              'APR_SOLUS_REGISTRY_PIN',
            );
      }
      stage = 'original-archives';
      const archiveCheck = join(custody, 'archive-check');
      await mkdir(archiveCheck, { mode: 0o700 });
      const packages = [];
      for (const observed of registry) {
        const lower = observed.id.toLowerCase(),
          root = join(env.NUGET_PACKAGES, lower, fixed.version);
        const original = join(root, lower + '.' + fixed.version + '.nupkg');
        const bytes = await bounded(original, 16 * 1024 * 1024);
        const metadata = JSON.parse(
          (await bounded(join(root, '.nupkg.metadata'), 65536)).toString('utf8'),
        );
        const rawHash = (await bounded(original + '.sha512', 256)).toString('utf8').trim();
        const libraries = assets
          .flatMap((a) => Object.entries(a.libraries))
          .filter(([id]) => id.toLowerCase() === lower + '/' + fixed.version)
          .map(([, v]) => v);
        const library = libraries[0];
        requireThat(
          metadata.source?.replace(/\/$/, '') === fixed.feed &&
            library &&
            libraries.every((v) => v.sha512 === metadata.contentHash),
          'APR_SOLUS_REGISTRY_SOURCE',
        );
        const hashes = packageHashes(bytes, metadata.contentHash, library.sha512, rawHash);
        const inspected = JSON.parse(
          await run(
            'archive-inspect',
            'python3',
            ['-c', registryArchiveProgram, original, observed.id, fixed.version, fixed.source],
            custody,
          ),
        );
        await writeFile(join(archiveCheck, observed.id + '.' + fixed.version + '.nupkg'), bytes, {
          flag: 'wx',
          mode: 0o400,
        });
        packages.push({
          id: observed.id,
          version: fixed.version,
          registryId: observed.registryId,
          private: true,
          source: fixed.feed,
          bytes: bytes.length,
          sha256: hashes.sha256,
          sha512: createHash('sha512').update(bytes).digest('hex'),
          rawSha512Base64: rawHash,
          nugetLogicalContentHash: hashes.contentHash,
          logicalHashEvidence:
            'observed in cold NuGet metadata and matched assets, not raw-hash substitution',
          ...inspected,
        });
      }
      await run(
        'upstream-archive-validator',
        process.execPath,
        [
          '--input-type=module',
          '-e',
          'const m=await import(process.argv[1]); m.inspectPackages(process.argv[2],process.argv[3]);',
          pathToFileURL(join(upstream, 'scripts/packages/common.ts')).href,
          archiveCheck,
          fixed.version,
        ],
        upstream,
      );
      stage = 'foundation-execution';
      for (const project of inventory.projects)
        await run(
          'foundation-build',
          'dotnet',
          ['build', project, '--configuration', 'Release', '--no-restore', ...properties],
          scenario,
        );
      for (const project of inventory.entrypoints)
        await run(
          'foundation-entry',
          'dotnet',
          [join(scenario, project, '../bin/Release/net10.0', assemblies.get(project) + '.dll')],
          scenario,
        );
      manifest = {
        qualification: false,
        producerProof: false,
        mode: 'registry-readback',
        aprSource: head,
        upstreamSource: fixed.source,
        upstreamTree: fixed.tree,
        tag: 'v' + fixed.version,
        tagMatched: true,
        runId: process.env.GITHUB_RUN_ID,
        attempt: 1,
        feed: fixed.feed,
        version: fixed.version,
        coldCache: true,
        selectedSourceMapping: true,
        productionProfileChanged: false,
        foundation: {
          projects: inventory.projects.length,
          entrypoints: inventory.entrypoints.length,
        },
        packages,
        processes,
        privateBytesExported: false,
        nugetInternalWireCount: 'UNKNOWN',
        providerCalls: 0,
      };
    } catch (error) {
      console.error('APR_SOLUS_REGISTRY_FAILURE stage=' + stage);
      throw error;
    } finally {
      requireThat(
        processes.every((p) => p.groupClosed),
        'APR_SOLUS_REGISTRY_CLEANUP',
      );
      requireThat((await realpath(custody)) === custody, 'APR_SOLUS_REGISTRY_CLEANUP');
      const current = await lstat(custody);
      requireThat(
        current.isDirectory() &&
          !current.isSymbolicLink() &&
          current.uid === process.getuid() &&
          (current.mode & 0o077) === 0 &&
          current.dev === owned.dev &&
          current.ino === owned.ino,
        'APR_SOLUS_REGISTRY_CLEANUP',
      );
      await rm(custody, { recursive: true, force: false });
    }
    requireThat(
      processes.every((p) => p.groupClosed),
      'APR_SOLUS_REGISTRY_CLEANUP',
    );
    requireThat(
      (await git(['rev-parse', 'HEAD'], process.cwd())).trim() === manifest.aprSource &&
        (await git(['status', '--porcelain'], process.cwd())) === '',
      'APR_SOLUS_DIRTY',
    );
    manifest.cleanupConfirmed = true;
    requireThat(!interrupted.signal.aborted, 'APR_SOLUS_REGISTRY_INTERRUPTED');
    const encoded = JSON.stringify(manifest, null, 2) + '\n';
    requireThat(Buffer.byteLength(encoded) <= 65536, 'APR_SOLUS_REGISTRY_MANIFEST');
    await mkdir(output, { mode: 0o700 });
    await writeFile(join(output, 'metadata.json'), encoded, { flag: 'wx', mode: 0o400 });
    console.log(
      JSON.stringify({
        mode: 'registry-readback',
        qualification: false,
        packages: 5,
        cleanupConfirmed: true,
        privateBytesExported: false,
      }),
    );
  } finally {
    process.off('SIGTERM', interrupt);
    process.off('SIGINT', interrupt);
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url)
  try {
    requireThat(
      ['collect', 'verify', 'registry-readback'].includes(process.argv[2]) &&
        process.argv.length === 4,
      'APR_SOLUS_USAGE',
    );
    if (process.argv[2] === 'registry-readback') await readRegistry(process.argv[3]);
    else await collect(process.argv[3], process.argv[2] === 'verify');
  } catch (error) {
    // Fixed code only: never echo credential-bearing subprocess output or arbitrary exceptions.
    console.error(
      /^APR_SOLUS_[A-Z_]+$/.test(error.message) ? error.message : 'APR_SOLUS_PROOF_FAILED',
    );
    process.exitCode = 1;
  }
