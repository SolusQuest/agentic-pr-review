import { mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import childProcess, { spawnSync } from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { parse } from 'yaml';
import { describe, expect, it, vi } from 'vitest';

const proof = resolve('scripts/release/solus-package-proof.mjs');
const preload = resolve('tests/distribution/solus-proof-env-preload.mjs');
function exercise(mode: string, scenario: string) {
  const root = mkdtempSync(join(tmpdir(), 'apr-solus-env-'));
  try {
    const audit = join(root, 'audit.json');
    const result = spawnSync(
      process.execPath,
      [
        '--import',
        preload,
        ...(scenario === 'producer'
          ? [
              '--input-type=module',
              '-e',
              "import { admitSource } from './scripts/release/build-payload.mjs'; try { admitSource(process.cwd(), 'a'.repeat(40)); } catch {}",
            ]
          : [proof, mode, join(root, 'proof')]),
      ],
      {
        encoding: 'utf8',
        timeout: 30_000,
        env: {
          ...process.env,
          APR_ENV_SCENARIO: scenario,
          APR_ENV_AUDIT: audit,
          RUNNER_TEMP: root,
          EXPECTED_HEAD: 'a'.repeat(40),
          GITHUB_ACTOR: 'synthetic',
          GITHUB_TOKEN: scenario === 'missing-auth' ? '' : 'synthetic-secret-marker',
          GH_TOKEN: 'synthetic-secret-marker',
          NUGET_AUTH_TOKEN: 'synthetic-secret-marker',
          NuGetPackageSourceCredentials_solus: 'synthetic-secret-marker',
          NuGetPackageSourceCredentials_other: 'synthetic-secret-marker',
        },
      },
    );
    const events: { stage: string; credentials: string[]; token: boolean }[] = JSON.parse(
      readFileSync(audit, 'utf8'),
    );
    return { result, events };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
describe('Solus proof subprocess credential boundary', () => {
  for (const mode of ['collect', 'verify']) {
    it.each(
      mode === 'verify'
        ? [
            'success',
            'missing-auth',
            'early-git',
            'restore',
            'build',
            'unzip',
            'source-commit',
            'late-git',
            'import',
          ]
        : ['success', 'missing-auth', 'early-git', 'restore', 'import'],
    )('%s ' + mode + ' confines credentials to authenticated restore', (scenario) => {
      const { result, events } = exercise(mode, scenario);
      expect(result.status).toBe(scenario === 'success' ? 0 : 1);
      for (const event of events) {
        expect(event.credentials, event.stage).toEqual(
          event.stage === 'authenticated-restore' ? ['NuGetPackageSourceCredentials_solus'] : [],
        );
        expect(event.token, event.stage).toBe(event.stage === 'authenticated-restore');
      }
      const failureStage: Record<string, string> = {
        'missing-auth': 'git:status --porcelain',
        import: 'project-import',
        'early-git': 'git:rev-parse HEAD',
        restore: 'authenticated-restore',
        build: 'build',
        unzip: 'unzip',
        'source-commit': 'unzip',
        'late-git': 'git:status --porcelain',
      };
      if (scenario !== 'success') expect(events.at(-1)?.stage).toBe(failureStage[scenario]);
      if (scenario === 'missing-auth') {
        expect(result.stderr).toContain('APR_SOLUS_AUTH_UNAVAILABLE');
        expect(events.some((e) => e.stage === 'authenticated-restore')).toBe(false);
      }
      if (scenario === 'success') {
        expect(events.some((e) => e.stage === 'authenticated-restore')).toBe(true);
        if (mode === 'verify') {
          expect(events.filter((e) => e.stage === 'unzip')).toHaveLength(10);
          expect(events.filter((e) => e.stage === 'git:status --porcelain')).toHaveLength(2);
          expect(events.some((e) => e.stage === 'startup')).toBe(true);
        }
      }
      expect(result.stdout + result.stderr).not.toContain('synthetic-secret-marker');
    });
  }
  it('keeps direct managed/native producer source admission credential-free', () => {
    const { result, events } = exercise('verify', 'producer');
    expect(result.status).toBe(0);
    expect(events).toHaveLength(4);
    expect(events.every((e) => !e.token && e.credentials.length === 0)).toBe(true);
  });
});

const packageFixture = String.raw`
import sys,json,zipfile,struct
p,kind,source=sys.argv[1:]
dll=bytearray(1024);dll[:2]=b'MZ';struct.pack_into('<I',dll,60,128)
dll[128:132]=b'PE\0\0';struct.pack_into('<H',dll,134,1);struct.pack_into('<H',dll,148,224)
struct.pack_into('<H',dll,152,0x10b)
struct.pack_into('<IIII',dll,384,512,0x2000,512,512)
if kind=='embedded-pdb':
 struct.pack_into('<II',dll,296,0x2000,28);struct.pack_into('<I',dll,524,17)
 struct.pack_into('<I',dll,528,8);struct.pack_into('<I',dll,536,600)
 dll[600:608]=b'MPDB\0\0\0\0'
version='0.1.0-internal.4' if kind=='wrong-version' else '0.1.0-internal.5'
commit='0'*40 if kind=='wrong-source' else source
dep='<dependencies><dependency id="SolusAgent.Runtime" version="[0.1.0-internal.4]"/></dependencies>' if kind=='wrong-dependency' else ''
spec=('<package><metadata><id>SolusAgent.Api</id><version>'+version+'</version>'+
 '<description>PRIVATE_SOURCE_MARKER</description><repository type="git" url="https://github.com/SolusQuest/solus-agent" commit="'+commit+'"/>'+dep+'</metadata></package>').encode()
with zipfile.ZipFile(p,'w') as z:
 z.writestr('SolusAgent.Api.nuspec',spec)
 z.writestr('lib/net10.0/SolusAgent.Api.dll',dll)
 z.writestr('lib/net10.0/SolusAgent.Api.xml','PRIVATE_SOURCE_MARKER')
 if kind=='duplicate':z.writestr('SolusAgent.Api.nuspec',spec)
 if kind=='path-escape':z.writestr('../unexpected','PRIVATE_SOURCE_MARKER')
 if kind=='symlink':
  link=zipfile.ZipInfo('_rels/.rels');link.create_system=3;link.external_attr=0o120777<<16;z.writestr(link,'target')
if kind=='bad-crc':
 b=bytearray(open(p,'rb').read());i=b.index(b'<package>');b[i]^=1;open(p,'wb').write(b)
`;

describe('test-only published registry readback', () => {
  const load = () => import(pathToFileURL(proof).href);
  it.each(['success', 'exit', 'spawn', 'timeout', 'output-limit', 'interrupted', 'unknown-label'])(
    'retains the real command boundary and emits only closed failure facts for %s',
    async (kind) => {
      const { runRegistryCommand } = await load();
      const root = realpathSync(mkdtempSync(join(tmpdir(), 'apr-registry-command-')));
      const marker = 'PRIVATE_SOURCE_MARKER synthetic-secret-marker https://private.invalid/secret';
      const context = {
        interrupted: new AbortController(),
        until: performance.now() + (kind === 'timeout' ? 20_100 : 50_000),
        processes: [] as {
          stage: string;
          pid: number | null;
          exit: number;
          groupClosed: boolean;
        }[],
        activeKill: undefined as undefined | ((reason: string) => void),
      };
      const logged: unknown[][] = [];
      const log = vi.spyOn(console, 'error').mockImplementation((...args) => logged.push(args));
      const interrupt =
        kind === 'interrupted'
          ? setTimeout(() => {
              context.interrupted.abort();
              context.activeKill?.('interrupted');
            }, 100)
          : undefined;
      try {
        const script =
          kind === 'success'
            ? 'process.stdout.write("bounded-success")'
            : kind === 'timeout' || kind === 'interrupted'
              ? 'setTimeout(() => {}, 30000)'
              : kind === 'output-limit'
                ? 'process.stdout.write(Buffer.alloc(17 * 1024 * 1024, "x"))'
                : 'process.stdout.write(process.argv[1]);process.stderr.write(process.argv[1]);process.exitCode=7';
        const result = runRegistryCommand(
          kind === 'unknown-label' ? marker : 'foundation-restore',
          kind === 'spawn' ? join(root, 'missing-executable') : process.execPath,
          ['-e', script, marker],
          root,
          { PATH: process.env.PATH },
          context,
        );
        if (kind === 'success') {
          await expect(result).resolves.toBe('bounded-success');
          expect(logged).toEqual([]);
        } else {
          await expect(result).rejects.toThrow('APR_SOLUS_REGISTRY_COMMAND');
          expect(logged).toEqual([
            [
              'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=' +
                (kind === 'unknown-label' ? 'unknown' : 'foundation-restore') +
                ' category=' +
                (kind === 'unknown-label' ? 'exit' : kind) +
                ' diagnostic=unknown http=unknown parentExit=' +
                (context.processes[0].exit === 0
                  ? 'zero'
                  : Number.isInteger(context.processes[0].exit) && context.processes[0].exit > 0
                    ? 'nonzero'
                    : 'unknown') +
                ' groupClosed=true groupStopRequested=' +
                !['exit', 'spawn', 'unknown-label'].includes(kind),
            ],
          ]);
          expect(JSON.stringify(logged)).not.toContain(marker);
          expect(JSON.stringify(logged)).not.toContain(root);
        }
        expect(context.processes).toHaveLength(1);
        expect(context.processes[0].groupClosed).toBe(true);
        expect(context.activeKill).toBeUndefined();
      } finally {
        if (interrupt) clearTimeout(interrupt);
        log.mockRestore();
        rmSync(root, { recursive: true, force: true });
      }
    },
  );
  it.each([
    ['code-on-stderr', 'private.csproj : error NU1101: PRIVATE_SOURCE_MARKER', 'NU1101', 'unknown'],
    [
      'http-auth',
      'private.csproj : error NU1301: Response status code does not indicate success: 401 (Unauthorized).',
      'NU1301',
      '401',
    ],
    [
      'generic-source',
      'private.csproj : error NU1301: source PRIVATE_SOURCE_MARKER unavailable 401',
      'NU1301',
      'unknown',
    ],
    [
      'warning-and-digits',
      'private.csproj : warning NU1301: nested.csproj : error NU1101: 401\nhttps://private.invalid/401 NU1101',
      'unknown',
      'unknown',
    ],
    [
      'conflicting-codes',
      'private.csproj : error NU1101: hidden\nprivate.csproj : error NU1301: hidden',
      'unknown',
      'unknown',
    ],
    [
      'conflicting-http',
      'private.csproj : error NU1301: Response status code does not indicate success: 401 (Unauthorized).\nprivate.csproj : error NU1301: Response status code does not indicate success: 403 (Forbidden).',
      'NU1301',
      'unknown',
    ],
    [
      'unsupported-code',
      'private.csproj : error NU9999: hidden 401\nprivate.csproj : error : Response status code does not indicate success: 401 (Unauthorized).',
      'unknown',
      'unknown',
    ],
    [
      'bare-http-error',
      'private.csproj : error : Response status code does not indicate success: 401 (Unauthorized).',
      'unknown',
      '401',
    ],
    [
      'sdk-code',
      'private.csproj : error NETSDK1045: PRIVATE_SOURCE_MARKER',
      'NETSDK1045',
      'unknown',
    ],
    [
      'truncated-evidence',
      'x'.repeat(65536) + '\nprivate.csproj : error NU1101: hidden',
      'unknown',
      'unknown',
    ],
  ])('emits only bounded fixed error evidence for %s', async (kind, text, code, http) => {
    const { runRegistryCommand } = await load();
    const context = {
      interrupted: new AbortController(),
      until: performance.now() + 50_000,
      processes: [] as { groupClosed: boolean }[],
    };
    const logged: unknown[][] = [];
    const log = vi.spyOn(console, 'error').mockImplementation((...args) => logged.push(args));
    try {
      await expect(
        runRegistryCommand(
          'foundation-restore',
          process.execPath,
          [
            '-e',
            'const s=process.argv[1];process.stderr.write("synthetic-secret-marker https://private.invalid/secret\\n");const mid=Math.floor(s.length/2);const out=process.argv[2]==="code-on-stderr"?process.stderr:process.stdout;out.write(s.slice(0,mid));setTimeout(()=>{out.write(s.slice(mid));process.exitCode=1},10);',
            text,
            kind,
          ],
          process.cwd(),
          { PATH: process.env.PATH },
          context,
        ),
      ).rejects.toThrow('APR_SOLUS_REGISTRY_COMMAND');
      expect(logged).toEqual([
        [
          'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=foundation-restore category=exit diagnostic=' +
            code +
            ' http=' +
            http +
            ' parentExit=nonzero groupClosed=true groupStopRequested=false',
        ],
      ]);
      expect(JSON.stringify(logged)).not.toMatch(
        /PRIVATE_SOURCE_MARKER|synthetic-secret-marker|private\.csproj|https:/,
      );
      expect(context.processes).toHaveLength(1);
      expect(context.processes[0].groupClosed).toBe(true);
    } finally {
      log.mockRestore();
    }
  });
  it.each(['natural', 'nonzero', 'stubborn'])(
    'waits for bounded natural owned descendants and still rejects %s failure neighbors',
    async (kind) => {
      const { runRegistryCommand } = await load();
      const root = realpathSync(mkdtempSync(join(tmpdir(), 'apr-registry-tail-')));
      const context = {
        interrupted: new AbortController(),
        until: performance.now() + 40_000,
        processes: [] as { pid: number; exit: number; groupClosed: boolean }[],
      };
      const logged: unknown[][] = [];
      const log = vi.spyOn(console, 'error').mockImplementation((...args) => logged.push(args));
      const code = kind === 'nonzero' ? 7 : 0;
      const descendant = 'setTimeout(()=>{},' + (kind === 'stubborn' ? 6000 : 250) + ')';
      const script =
        'const c=require("node:child_process").spawn(process.execPath,["-e",process.argv[1]],{stdio:"ignore",env:{PATH:process.env.PATH}});c.unref();setTimeout(()=>{process.stdout.write("bounded-success");process.exit(' +
        code +
        ')},40);';
      try {
        const result = runRegistryCommand(
          'foundation-restore',
          process.execPath,
          ['-e', script, descendant],
          root,
          { PATH: process.env.PATH },
          context,
        );
        if (kind === 'natural') {
          await expect(result).resolves.toBe('bounded-success');
          expect(logged).toEqual([]);
        } else {
          await expect(result).rejects.toThrow('APR_SOLUS_REGISTRY_COMMAND');
          expect(logged).toEqual([
            [
              'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=foundation-restore category=process-group diagnostic=unknown http=unknown parentExit=' +
                (code === 0 ? 'zero' : 'nonzero') +
                ' groupClosed=true groupStopRequested=true',
            ],
          ]);
        }
        expect(context.processes).toHaveLength(1);
        expect(context.processes[0].exit).toBe(code);
        expect(context.processes[0].groupClosed).toBe(true);
      } finally {
        log.mockRestore();
        if (context.processes[0]?.pid > 0) {
          try {
            process.kill(-context.processes[0].pid, 'SIGKILL');
          } catch {}
        }
        rmSync(root, { recursive: true, force: true });
      }
    },
  );
  it('rejects unknown closure without accepting an already-exited parent as sufficient', async () => {
    const { runRegistryCommand } = await load();
    const originalKill = process.kill;
    const context = {
      interrupted: new AbortController(),
      until: performance.now() + 40_000,
      processes: [] as { pid: number; groupClosed: boolean }[],
    };
    const logged: unknown[][] = [];
    const log = vi.spyOn(console, 'error').mockImplementation((...args) => logged.push(args));
    const probe = vi.spyOn(process, 'kill').mockImplementation((pid, signal) => {
      if (pid < 0 && signal === 0)
        throw Object.assign(new Error('synthetic-secret-marker'), { code: 'EPERM' });
      return originalKill(pid, signal);
    });
    try {
      await expect(
        runRegistryCommand(
          'foundation-restore',
          process.execPath,
          ['-e', ''],
          process.cwd(),
          { PATH: process.env.PATH },
          context,
        ),
      ).rejects.toThrow('APR_SOLUS_REGISTRY_COMMAND');
      expect(context.processes[0].groupClosed).toBe(false);
      expect(logged).toEqual([
        [
          'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=foundation-restore category=process-group diagnostic=unknown http=unknown parentExit=zero groupClosed=false groupStopRequested=true',
        ],
      ]);
    } finally {
      probe.mockRestore();
      log.mockRestore();
    }
    expect(() => originalKill(-context.processes[0].pid, 0)).toThrow();
  });
  it('rejects a controlled stream error on a real owned child without echoing its error', async () => {
    const { runRegistryCommand } = await load();
    const originalSpawn = childProcess.spawn;
    const logged: unknown[][] = [];
    const log = vi.spyOn(console, 'error').mockImplementation((...args) => logged.push(args));
    const context = {
      interrupted: new AbortController(),
      until: performance.now() + 40_000,
      processes: [] as { groupClosed: boolean }[],
    };
    const hook = vi
      .spyOn(childProcess, 'spawn')
      .mockImplementation((...args: Parameters<typeof childProcess.spawn>) => {
        const child = Reflect.apply(originalSpawn, childProcess, args);
        child.once('spawn', () =>
          setTimeout(
            () =>
              child.stderr.emit(
                'error',
                new Error('synthetic-secret-marker https://private.invalid/secret'),
              ),
            25,
          ),
        );
        return child;
      });
    syncBuiltinESMExports();
    try {
      await expect(
        runRegistryCommand(
          'foundation-restore',
          process.execPath,
          ['-e', 'setTimeout(()=>{},30000)'],
          process.cwd(),
          { PATH: process.env.PATH },
          context,
        ),
      ).rejects.toThrow('APR_SOLUS_REGISTRY_COMMAND');
      expect(context.processes[0].groupClosed).toBe(true);
      expect(logged).toEqual([
        [
          'APR_SOLUS_REGISTRY_COMMAND_FAILURE stage=foundation-restore category=stream diagnostic=unknown http=unknown parentExit=unknown groupClosed=true groupStopRequested=true',
        ],
      ]);
    } finally {
      hook.mockRestore();
      syncBuiltinESMExports();
      log.mockRestore();
    }
  });
  it('keeps authority out of the base environment and grants only selected-source restore', async () => {
    const { registryRestoreEnvironment } = await load();
    const base = {
      PATH: '/bin',
      GITHUB_TOKEN: 'synthetic-secret-marker',
      GH_TOKEN: 'other',
      NUGET_AUTH_TOKEN: 'other',
      NuGetPackageSourceCredentials_other: 'other',
    };
    const restored = registryRestoreEnvironment(base, {
      token: 'synthetic-secret-marker',
      actor: 'synthetic',
    });
    expect(Object.keys(restored).sort()).toEqual([
      'NuGetPackageSourceCredentials_selected',
      'PATH',
    ]);
    expect(restored.NuGetPackageSourceCredentials_selected).toContain(
      'ValidAuthenticationTypes=Basic',
    );
    expect(base.GITHUB_TOKEN).toBe('synthetic-secret-marker');
    expect(() => registryRestoreEnvironment(base, { token: '', actor: 'synthetic' })).toThrow(
      'APR_SOLUS_AUTH_UNAVAILABLE',
    );
    expect(() =>
      registryRestoreEnvironment(base, { token: 'synthetic', actor: 'bad\nactor' }),
    ).toThrow();
  });

  it.each([
    'valid',
    'wrong-version',
    'wrong-source',
    'wrong-dependency',
    'duplicate',
    'path-escape',
    'symlink',
    'bad-crc',
    'embedded-pdb',
  ])(
    'reads original ZIP facts or fails closed for %s without exporting member contents',
    async (kind) => {
      const { registryArchiveProgram, registryReadback } = await load();
      const root = mkdtempSync(join(tmpdir(), 'apr-registry-archive-'));
      try {
        const archive = join(root, 'original.nupkg');
        const generated = spawnSync(
          'python3',
          ['-c', packageFixture, archive, kind, registryReadback.source],
          { encoding: 'utf8', timeout: 10_000 },
        );
        expect(generated.status).toBe(0);
        const result = spawnSync(
          'python3',
          [
            '-c',
            registryArchiveProgram,
            archive,
            'SolusAgent.Api',
            registryReadback.version,
            registryReadback.source,
          ],
          { encoding: 'utf8', timeout: 10_000 },
        );
        expect(result.status).toBe(kind === 'valid' ? 0 : 1);
        expect(result.stdout + result.stderr).not.toContain('PRIVATE_SOURCE_MARKER');
        if (kind === 'valid') {
          const fact = JSON.parse(result.stdout);
          expect(Object.keys(fact).sort()).toEqual([
            'inventory',
            'nuspec',
            'pdb',
            'sourceLink',
            'zipCRC',
          ]);
          expect(fact.zipCRC).toBe('verified');
          expect(fact.inventory).toHaveLength(3);
          expect(fact.nuspec.commit).toBe(registryReadback.source);
          expect(fact.pdb.embedded).toBe(false);
          expect(fact.sourceLink).toEqual({
            status: 'unavailable-no-pdb-in-original-package',
            verified: false,
          });
        } else {
          expect(result.stdout).toBe('');
          expect(result.stderr.trim()).toBe('APR_SOLUS_REGISTRY_ARCHIVE_INVALID');
        }
      } finally {
        rmSync(root, { recursive: true, force: true });
      }
    },
  );

  it('routes readback only to its own bounded step and one sanitized metadata artifact', () => {
    const workflow = parse(readFileSync('.github/workflows/solus-agent-package-proof.yml', 'utf8'));
    expect(workflow.on.workflow_dispatch.inputs.mode.options).toContain('registry-readback');
    const selected = (mode: string) =>
      workflow.jobs['install-proof'].steps.filter(
        (step: { if?: string }) =>
          !step.if ||
          Function(
            'inputs',
            'success',
            'always',
            `return (${step.if});`,
          )(
            { mode },
            () => true,
            () => true,
          ),
      );
    const steps = selected('registry-readback');
    expect(
      steps.filter((s: { run?: string }) => s.run).map((s: { name: string }) => s.name),
    ).toEqual([
      'Bind the trusted manual source before package access',
      'Read back only the five published private registry packages',
    ]);
    const uploads = steps.filter((s: { uses?: string }) =>
      s.uses?.startsWith('actions/upload-artifact@'),
    );
    expect(uploads).toHaveLength(1);
    expect(uploads[0].with.path).toBe(
      '${{ runner.temp }}/apr-solus-registry-readback/metadata.json',
    );
    expect(uploads[0].with['retention-days']).toBe(1);
    const collectSteps = selected('collect');
    expect(
      collectSteps.some(
        (s: { name: string }) =>
          s.name === 'Cold-cache actual APR managed construction and pin proof',
      ),
    ).toBe(true);
    expect(
      collectSteps.some(
        (s: { name: string }) =>
          s.name === 'Read back only the five published private registry packages',
      ),
    ).toBe(false);
  });

  it.each(['head-rejection', 'interrupted-child'])(
    '%s removes private custody and emits no manifest or credential without a network call',
    (scenario) => {
      const root = realpathSync(mkdtempSync(join(tmpdir(), 'apr-registry-lifecycle-')));
      try {
        const audit = join(root, 'audit.json');
        const output = join(root, 'output');
        const harness = String.raw`
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import cp from 'node:child_process';
import {syncBuiltinESMExports} from 'node:module';
import {pathToFileURL} from 'node:url';
const [proof,output,audit,scenario]=process.argv.slice(1);
const facts={networkCalls:0,children:[]};const save=()=>fs.writeFileSync(audit,JSON.stringify(facts));
globalThis.fetch=()=>{facts.networkCalls++;save();throw Error('network forbidden');};
Object.defineProperty(process,'platform',{value:'linux'});
Object.defineProperty(process,'arch',{value:'x64'});
const read=fsp.readFile;fsp.readFile=(p,...args)=>String(p)==='/etc/os-release'?Promise.resolve('ID=ubuntu\nVERSION_ID="24.04"\n'):read(p,...args);
const spawn=cp.spawn;
cp.spawn=(command,args,options)=>{
 const item={credentials:Object.keys(options.env).filter(k=>/TOKEN|NuGetPackageSourceCredentials/i.test(k)),closed:false};facts.children.push(item);save();
 const child=scenario==='interrupted-child'?spawn(process.execPath,['-e','setTimeout(()=>{},30000)'],options):spawn(command,args,options);
 child.once('close',()=>{item.closed=true;save();});
 if(scenario==='interrupted-child')setTimeout(()=>process.kill(process.pid,'SIGTERM'),50);
 return child;
};syncBuiltinESMExports();
process.argv=[process.execPath,proof,'registry-readback',output];save();
await import(pathToFileURL(proof).href);
`;
        const result = spawnSync(
          process.execPath,
          ['--input-type=module', '-e', harness, proof, output, audit, scenario],
          {
            encoding: 'utf8',
            timeout: 10_000,
            env: {
              ...process.env,
              RUNNER_TEMP: root,
              EXPECTED_HEAD: '0'.repeat(40),
              WORKFLOW_HEAD: '0'.repeat(40),
              GITHUB_TOKEN: 'synthetic-secret-marker',
              GITHUB_ACTOR: 'synthetic',
              GH_TOKEN: 'synthetic-secret-marker',
              GITHUB_REPOSITORY: 'SolusQuest/agentic-pr-review',
              GITHUB_EVENT_NAME: 'workflow_dispatch',
              GITHUB_RUN_ID: '123',
              GITHUB_RUN_ATTEMPT: '1',
            },
          },
        );
        expect(result.status).toBe(1);
        expect(result.stdout).toBe('');
        expect(result.stderr).not.toContain('synthetic-secret-marker');
        const facts = JSON.parse(readFileSync(audit, 'utf8'));
        expect(facts.networkCalls).toBe(0);
        expect(facts.children).toHaveLength(1);
        expect(facts.children[0]).toEqual({ credentials: [], closed: true });
        expect(readdirSync(root)).toEqual(['audit.json']);
      } finally {
        rmSync(root, { recursive: true, force: true });
      }
    },
  );
});
