# Local Linux runtime validation

The retained Linux x64 verification routes support local Linux environments, including WSL2. A container is optional. Use a clean checkout in the Linux filesystem, Linux-native .NET and Node executables, the SDK selected by `global.json`, Node 24, and the existing clang, zlib and strace dependencies. Do not invoke Windows `dotnet.exe` or `node.exe` through WSL interop. The platform preflight accepts ordinary symlinked ELF tool installations, but not Windows binaries or shell shims; point the tools at their native executables when using a shim manager.

Run local checks before pushing:

```bash
npm run check
npm run dist:check
bash runtime/scripts/verify-runtime.sh test
bash runtime/scripts/verify-action-host.sh framework
APR_D5_REGRESSION_ROLE=first bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh
bash runtime/scripts/verify-live-agent.sh live --dry-run
```

The full-route scripts require a committed, clean source tree. The default trusted-v2 command now retains the complete public FDD Host invariant corpus and emits an explicitly non-qualifying D5 regression receipt. The explicit `managed` route requires the authorized verified build cache and executes two independently built original archives through the generated Action on the selected hosted runner. The former native V2 implementation remains available as `legacy-aot` until D6 retires it. It is not merely an archive-reader replay, and it does not call real GitHub or a real provider. Source, credentials, version, byte-budget, retained-file and network checks remain unchanged. Historical non-v2 proof scripts and their receipts remain historical; this does not qualify WSL1 or establish binary equality across hosts.

The secret-bearing `live` invocation is separate from `live --dry-run` and requires explicit task authorization. Its immediate prepared-supervisor exec path is unchanged; no toolchain helper runs with the provider key.

Local success does not replace the required GitHub Ubuntu CI checks or the real GitHub fixture scenarios. For #181, repeat the accepted local route on merged main and complete the authorized normal/continuation, stale, bounded provider and cleanup steps before claiming the issue complete.
