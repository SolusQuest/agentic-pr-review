using System.Diagnostics;
using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Regenerates the ledger fixture corpus in a temporary directory using the
/// checked-in <c>ledger-fixture-gen</c> tool and byte-compares each file
/// against the committed fixture. Guards against silent drift between the
/// generator and the golden bytes shipped in the repo.
///
/// The test locates the fixture-gen project by walking upward from the test
/// binary directory. It is skipped when the source project cannot be found
/// (e.g. binary-only test packaging), which keeps hosted CI runs happy while
/// still gating local + repo CI.
/// </summary>
public sealed class LedgerFixtureGeneratorTests
{
    [Fact]
    public void GeneratorReproducesCheckedInFixturesByteForByte()
    {
        var projectRoot = FindRepositoryRoot();
        if (projectRoot is null)
        {
            // Cannot regenerate outside the repo checkout.
            return;
        }
        var genProject = Path.Combine(projectRoot, "runtime", "tools", "ledger-fixture-gen", "LedgerFixtureGen.csproj");
        if (!File.Exists(genProject))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "ledger-fixture-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(genProject);
            psi.ArgumentList.Add("--configuration");
            psi.ArgumentList.Add("Debug");
            psi.ArgumentList.Add("--no-restore");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(tempDir);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(180_000);
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.HasExited, "fixture generator did not exit within 180s: " + stderr);
            Assert.Equal(0, proc.ExitCode);

            var goldenDir = Path.Combine(projectRoot, "protocol", "fixtures", "v1", "provider-session-ledger");
            var goldenFiles = Directory.GetFiles(goldenDir).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var driftedFiles = new List<string>();
            foreach (var golden in goldenFiles)
            {
                var name = Path.GetFileName(golden);
                var regenerated = Path.Combine(tempDir, name);
                if (!File.Exists(regenerated))
                {
                    driftedFiles.Add($"{name} (missing from regenerated corpus)");
                    continue;
                }
                var goldenSha = HashFile(golden);
                var regeneratedSha = HashFile(regenerated);
                if (goldenSha != regeneratedSha)
                {
                    driftedFiles.Add($"{name} (golden={goldenSha} regenerated={regeneratedSha})");
                }
            }
            // Also flag any regenerated file not present in the golden corpus.
            foreach (var regenerated in Directory.GetFiles(tempDir))
            {
                var name = Path.GetFileName(regenerated);
                if (!File.Exists(Path.Combine(goldenDir, name)))
                {
                    driftedFiles.Add($"{name} (present in regenerated but not committed)");
                }
            }

            Assert.True(driftedFiles.Count == 0,
                "Generator output drifted from committed fixtures:\n  " + string.Join("\n  ", driftedFiles));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GeneratorRunIsIdempotent()
    {
        var projectRoot = FindRepositoryRoot();
        if (projectRoot is null) return;
        var genProject = Path.Combine(projectRoot, "runtime", "tools", "ledger-fixture-gen", "LedgerFixtureGen.csproj");
        if (!File.Exists(genProject)) return;

        var a = Path.Combine(Path.GetTempPath(), "ledger-fixture-gen-a-" + Guid.NewGuid().ToString("N"));
        var b = Path.Combine(Path.GetTempPath(), "ledger-fixture-gen-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        try
        {
            Run(genProject, projectRoot, a);
            Run(genProject, projectRoot, b);
            var namesA = Directory.GetFiles(a).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var namesB = Directory.GetFiles(b).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(namesA, namesB);
            foreach (var name in namesA)
            {
                var ah = HashFile(Path.Combine(a, name!));
                var bh = HashFile(Path.Combine(b, name!));
                Assert.Equal(ah, bh);
            }
        }
        finally
        {
            try { Directory.Delete(a, recursive: true); } catch { }
            try { Directory.Delete(b, recursive: true); } catch { }
        }
    }

    private static void Run(string genProject, string workingDir, string outDir)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(genProject);
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--no-restore");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(outDir);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(180_000);
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.HasExited, "generator did not exit: " + stderr);
        Assert.Equal(0, proc.ExitCode);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "runtime", "tools", "ledger-fixture-gen"))
                && File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
