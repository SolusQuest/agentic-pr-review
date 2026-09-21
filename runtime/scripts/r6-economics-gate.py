"""Private, bounded execution supervisor for the same-source R6 oracle."""

import json
import os
from pathlib import Path
import platform
import re
import resource
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import traceback
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parents[2]
ASSEMBLY = "AgenticPrReview.Runtime.ReviewEvaluationFixture"
PROJECT = REPO / "runtime/tests/ReviewEvaluationFixture" / (ASSEMBLY + ".csproj")
TESTS = REPO / "runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
FIXTURES = REPO / "runtime/tests/fixtures/agent/r5"
MAX_CAPTURE = 64 * 1024 * 1024
MAX_TRACE = 128 * 1024 * 1024
MUTATIONS = (
    "missing-case duplicate-case renamed-case reordered-case substituted-case "
    "wrong-source wrong-tree wrong-clean wrong-build wrong-mode wrong-corpus "
    "invalid-binding wrong-artifact price-tamper chain-tamper live cleanup unreaped "
    "unknown-field duplicate-field raw-canary escaped-canary extra-record stderr-canary"
).split()
NETWORK = re.compile(rb"\b(?:socket|connect|sendto|sendmsg|sendmmsg)\(.*\bAF_INET6?\b")


def require(condition):
    if not condition:
        raise RuntimeError("gate rejected")


def owned_processes(root, group=None):
    """Only this supervisor's process group or private environment namespace."""
    result = []
    prefix = os.fsencode(str(root) + "/")
    for entry in Path("/proc").iterdir():
        if not entry.name.isdecimal() or int(entry.name) == os.getpid():
            continue
        try:
            fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
            environ = (entry / "environ").read_bytes().split(b"\0")
            owns = any(item.startswith(key + prefix) for item in environ
                       for key in (b"TMPDIR=", b"TMP=", b"HOME="))
            if owns or group is not None and int(fields[2]) == group:
                result.append(int(entry.name))
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    return result


def bounded(path, maximum=MAX_CAPTURE):
    require(path.is_file() and path.stat().st_size <= maximum)
    with path.open("rb") as stream:
        result = stream.read(maximum + 1)
    require(len(result) <= maximum)
    return result


class Gate:
    def __init__(self):
        self.root = Path(tempfile.mkdtemp(prefix="apr-r6-gate-", dir="/tmp"))
        self.stage = "initialization"
        self.sequence = 0
        self.groups = []
        self.env = dict(os.environ)
        for key in ("GIT_DIR", "GIT_WORK_TREE"):
            self.env.pop(key, None)
        self.env.update(MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        self.dotnet = shutil.which("dotnet")
        require(self.dotnet and shutil.which("strace"))

    def mark(self, stage):
        self.stage = stage
        print("r6_economics_gate stage=" + stage, flush=True)

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=REPO, env=self.env,
                                       stderr=subprocess.DEVNULL, timeout=30).decode().strip()

    def run(self, command, *, audit=True, seconds=600, code=0, retained=False, network=False):
        self.sequence += 1
        run = self.root / ("run-" + str(self.sequence))
        run.mkdir(mode=0o700)
        private = run / "private"
        private.mkdir(mode=0o700)
        for name in ("home", "tmp"):
            (private / name).mkdir(mode=0o700)
        env = self.env
        if audit:
            # No inherited provider secrets, credentials, proxies or endpoint overrides.
            env = {key: self.env[key] for key in ("PATH", "DOTNET_ROOT") if key in self.env}
            env.update(HOME=str(private / "home"), DOTNET_CLI_HOME=str(private / "home"),
                       TMP=str(private / "tmp"), TEMP=str(private / "tmp"), TMPDIR=str(private / "tmp"),
                       DOTNET_EnableDiagnostics="0", LANG="C.UTF-8", LC_ALL="C.UTF-8")
            command = ["strace", "-ff", "-qq", "-s", "256", "-e", "trace=network,process",
                       "-o", str(run / "trace"), *map(str, command)]
        output, error = run / "stdout", run / "stderr"

        def limits():
            # Scenario files are bounded too. Compiler object/debug artifacts
            # can exceed the capture limit; supervise their logs separately.
            if audit:
                resource.setrlimit(resource.RLIMIT_FSIZE, (MAX_CAPTURE, MAX_CAPTURE))
            resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

        with output.open("wb") as stdout, error.open("wb") as stderr:
            child = subprocess.Popen(command, cwd=REPO, env=env, stdout=stdout, stderr=stderr,
                                     start_new_session=True, preexec_fn=limits)
            self.groups.append(child.pid)
            try:
                deadline = time.monotonic() + seconds
                while child.poll() is None:
                    # RLIMIT_FSIZE bounds each capture; also bound aggregate tracing.
                    traces = list(run.glob("trace.*"))
                    excessive = (len(traces) > 4096 or sum(file.stat().st_size for file in traces) > MAX_TRACE or
                                 output.stat().st_size > MAX_CAPTURE or error.stat().st_size > MAX_CAPTURE)
                    require(not excessive and time.monotonic() < deadline)
                    time.sleep(.1)
            except BaseException:
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                child.wait(timeout=10)
                raise
        require(not owned_processes(private, child.pid))
        require(child.returncode == code)
        bounded(output)
        bounded(error)
        if audit:
            traces = list(run.glob("trace.*"))
            require(traces and len(traces) <= 4096)
            traffic = False
            for trace in traces:
                for line in bounded(trace).splitlines():
                    traffic = traffic or bool(NETWORK.search(line))
            require(traffic == network)
            roots = list(private.rglob("apr-*"))
            if retained:
                # The sole deliberate retained-root probe has no live descendant.
                require(len(roots) == 1 and roots[0].parent == private / "tmp" and
                        (roots[0] / "synthetic-probe").read_text() == "owned-negative")
                shutil.rmtree(roots[0])
            else:
                require(not roots)
        return output, error

    def focused(self):
        self.mark("focused-tests")
        trx = self.root / "focused.trx"
        self.run([self.dotnet, "test", str(TESTS), "-c", "Release", "--nologo", "-m:1",
                  "-p:UseSharedCompilation=false", "--filter", "FullyQualifiedName~R6VerifierCoverageTests",
                  "--logger", "trx;LogFileName=" + str(trx)], audit=False, seconds=900)
        parsed = ET.fromstring(bounded(trx))
        results = parsed.findall(".//{*}UnitTestResult")
        require(results and all(result.get("outcome") == "Passed" for result in results))
        required = {
            "ComparisonOracleRetainsConditionalQualityAndRejectsConflictingInputs": 1,
            "EconomicsOracleChecksActualWorkerAndFailureSemantics": 19,
            "HostProbeReadsActualEncryptedResetGenerationAndFreshContinuation": 1,
            "HistoryOwnersProduceFreshWorkersAndTheProductionHostResetChain": 1,
            "TokenAndPrefixOwnersProduceTheIndependentlyRequiredSemantics": 1,
            "CoverageAndStrictEnvelopeRemainOutsideTheProducer": 1,
            "ReboundWrongArtifactsCannotSatisfyTheTokenOracle": 1,
        }
        for method, count in required.items():
            require(sum(method in result.get("testName", "") for result in results) == count)

    def mode(self, mode, commit, tree):
        self.mark(mode + "-build")
        build = self.root / mode
        command = [self.dotnet, "build" if mode == "framework" else "publish", str(PROJECT),
                   "-c", "Release", "--nologo", "-m:1", "-p:UseSharedCompilation=false", "-o", str(build)]
        if mode == "aot":
            command += ["-r", "linux-x64", "--self-contained", "true", "-p:PublishAot=true",
                        "-p:JsonSerializerIsReflectionEnabledByDefault=false"]
        self.run(command, audit=False, seconds=1200)
        runner = [self.dotnet, str(build / (ASSEMBLY + ".dll"))] if mode == "framework" else [str(build / ASSEMBLY)]
        self.mark(mode + "-network-audit-probe")
        self.run([*runner, "r6-gate", "network-probe"], network=True, seconds=30)
        selected, _ = self.run([*runner, "r6-gate", "select", "--fixtures", FIXTURES,
                                "--commit", commit, "--tree", tree, "--mode", mode], seconds=30)
        self.mark(mode + "-produce")
        report, stderr = self.run([*runner, "r6-gate", "produce", "--fixtures", FIXTURES], seconds=900)
        projection = build / "projection.json"

        def verify(candidate, errors, code=0):
            return self.run([*runner, "r6-gate", "verify", "--fixtures", FIXTURES, "--selection", selected,
                             "--report", candidate, "--stderr", errors, "--projection", projection], code=code, seconds=90)

        self.mark(mode + "-verify")
        verdict_path, _ = verify(report, stderr)
        verdict = json.loads(bounded(verdict_path))
        require(verdict["code"] == "r6_gate_verified" and verdict["cases"] == 82)
        stable = bounded(projection)
        self.mark(mode + "-hostile-producers")
        for mutation in MUTATIONS:
            candidate, errors = self.run([*runner, "r6-gate", "mutate", "--report", report,
                                          "--mutation", mutation], seconds=30)
            verify(candidate, errors, 1)
        self.run([*runner, "r6-gate", "mutate", "--report", report,
                  "--mutation", "retained-root"], retained=True, seconds=30)
        print("r6_economics_gate mode=" + mode + " cases=82 adversarial=26 result=verified", flush=True)
        return stable

    def execute(self, selected_mode):
        require(platform.system() == "Linux" and platform.machine() == "x86_64")
        require(selected_mode in ("framework", "aot", "all"))
        commit, tree = self.git("rev-parse", "HEAD"), self.git("rev-parse", "HEAD^{tree}")
        require(not self.git("status", "--porcelain", "--untracked-files=normal"))
        projections = []
        for mode in (("framework", "aot") if selected_mode == "all" else (selected_mode,)):
            if mode == "framework":
                self.focused()
            projections.append(self.mode(mode, commit, tree))
        self.mark("final-parity-and-cleanup")
        require(len(projections) == 1 or projections[0] == projections[1])
        require(self.git("rev-parse", "HEAD") == commit and self.git("rev-parse", "HEAD^{tree}") == tree and
                not self.git("status", "--porcelain", "--untracked-files=normal"))
        require(not owned_processes(self.root) and not any(self.root.rglob("apr-r5-replay-*")))
        shutil.rmtree(self.root)
        require(not self.root.exists())
        print("r6_economics_gate result=verified mode=" + selected_mode + " source_commit=" + commit +
              " source_tree=" + tree + " private_state=absent", flush=True)


if __name__ == "__main__":
    gate = None
    try:
        gate = Gate()
        gate.execute(sys.argv[1] if len(sys.argv) == 2 else "all")
    except BaseException:
        # No candidate text, exception message, private path or captured log is
        # printed/uploaded. Failed owned evidence is retained, never erased to
        # manufacture a cleanup pass. Timeout kills only the launched group.
        if gate is not None:
            (gate.root / "supervisor-error").write_text(traceback.format_exc()[-16384:])
        print("r6_economics_gate result=rejected stage=" + (gate.stage if gate else "initialization"), file=sys.stderr)
        sys.exit(1)
