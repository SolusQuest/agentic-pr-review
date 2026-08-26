using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild;

internal sealed record OracleBuildReceipt(
    string Kind,
    string SourceCommit,
    string SourceTree,
    string OracleAssemblyPath,
    string OracleAssemblySha256,
    string ProductionAssemblyPath,
    string ProductionAssemblySha256,
    string Result);

internal static class Program
{
    private const string OracleAssemblyName =
        "AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle.dll";
    private const string ProductionAssemblyName = "AgenticPrReview.Runtime.dll";

    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            var sourceRoot = ExactRoot(options["--source-root"]);
            var repositoryRoot = ExactRoot(options["--repository-root"]);
            var worktreeRoot = ExactRoot(options["--worktree-root"]);
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [sourceRoot, repositoryRoot, worktreeRoot]);
            var expectedCommit = ExactSha(options["--source-commit"], 40);
            var expectedTree = ExactSha(options["--source-tree"], 40);
            var git = ExactExecutable(options["--git-executable"]);
            var dotnet = ExactExecutable(options["--dotnet-executable"]);

            AssertSourceIdentity(git, sourceRoot, expectedCommit, expectedTree);
            var outputDirectory = RestrictedEvidenceRoot.ResolveChildPath(
                root.Path,
                options["--output-directory"]);
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            {
                throw new InvalidDataException("oracle_build_output_invalid");
            }
            Directory.CreateDirectory(outputDirectory);
            RestrictDirectory(outputDirectory);
            RunBuild(dotnet, sourceRoot, outputDirectory, expectedCommit, expectedTree);
            AssertSourceIdentity(git, sourceRoot, expectedCommit, expectedTree);

            var oraclePath = System.IO.Path.Join(outputDirectory, OracleAssemblyName);
            var productionPath = System.IO.Path.Join(outputDirectory, ProductionAssemblyName);
            var oracle = CanonicalEvidence.ReadPinnedAbsolute(
                oraclePath,
                EvidenceLimits.MaximumArchiveBytes);
            var production = CanonicalEvidence.ReadPinnedAbsolute(
                productionPath,
                EvidenceLimits.MaximumArchiveBytes);
            try
            {
                var receipt = new OracleBuildReceipt(
                    "apr-r4-e3-independent-oracle-build-receipt-v2",
                    expectedCommit,
                    expectedTree,
                    System.IO.Path.GetRelativePath(root.Path, oraclePath),
                    CanonicalEvidence.Sha256(oracle.Bytes),
                    System.IO.Path.GetRelativePath(root.Path, productionPath),
                    CanonicalEvidence.Sha256(production.Bytes),
                    "passed");
                root.WritePinnedFileCreateNew(
                    options["--build-receipt-output"],
                    CanonicalEvidence.Encode(receipt, EvidenceJson.Options));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oracle.Bytes);
                CryptographicOperations.ZeroMemory(production.Bytes);
            }

            Console.Out.WriteLine("APR_R4_E3_ORACLE_BUILD_OK");
            return 0;
        }
        catch (InvalidDataException)
        {
            return Invalid();
        }
        catch (IOException)
        {
            return Invalid();
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid();
        }
        catch (CryptographicException)
        {
            return Invalid();
        }
        catch (Win32Exception)
        {
            return Invalid();
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static void AssertSourceIdentity(
        string git,
        string sourceRoot,
        string expectedCommit,
        string expectedTree)
    {
        var commit = Run(git, sourceRoot, ["rev-parse", "--verify", "HEAD"]);
        var tree = Run(git, sourceRoot, ["rev-parse", "--verify", "HEAD^{tree}"]);
        var status = Run(
            git,
            sourceRoot,
            ["status", "--porcelain=v1", "--untracked-files=all"]);
        if (!StringComparer.Ordinal.Equals(commit, expectedCommit) ||
            !StringComparer.Ordinal.Equals(tree, expectedTree) ||
            status.Length != 0)
        {
            throw new InvalidDataException("oracle_build_source_invalid");
        }
    }

    private static void RunBuild(
        string dotnet,
        string sourceRoot,
        string outputDirectory,
        string sourceCommit,
        string sourceTree)
    {
        var project = System.IO.Path.Join(
            sourceRoot,
            "runtime",
            "tests",
            "ActionHostTrustedProofEvidenceOracle",
            "AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle.csproj");
        if (!File.Exists(project))
        {
            throw new InvalidDataException("oracle_build_project_invalid");
        }
        var arguments = new[]
        {
            "publish",
            project,
            "--configuration",
            "Release",
            "--output",
            outputDirectory,
            $"-p:TrustedProofOracleSourceSha={sourceCommit}",
            $"-p:TrustedProofOracleSourceTree={sourceTree}",
            "--nologo",
        };
        _ = Run(dotnet, sourceRoot, arguments, TimeSpan.FromMinutes(10));
    }

    private static string Run(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("DEEPSEEK_API_KEY");
        start.Environment.Remove("AGENTIC_PR_REVIEW_STATE_KEY");
        start.Environment.Remove("AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY");
        using var process = Process.Start(start) ??
            throw new InvalidDataException("oracle_build_process_invalid");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(1));
        try
        {
            process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidDataException("oracle_build_process_timeout");
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || output.Length > 64 * 1024 || error.Length > 64 * 1024)
        {
            throw new InvalidDataException("oracle_build_process_failed");
        }
        return output.TrimEnd('\r', '\n');
    }

    private static string ExactRoot(string value)
    {
        if (!System.IO.Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("oracle_build_root_invalid");
        }
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
        var directory = new DirectoryInfo(full);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("oracle_build_root_invalid");
        }
        return full;
    }

    private static string ExactExecutable(string value)
    {
        if (!System.IO.Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("oracle_build_executable_invalid");
        }
        var full = System.IO.Path.GetFullPath(value);
        var file = new FileInfo(full);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("oracle_build_executable_invalid");
        }
        return full;
    }

    private static string ExactSha(string value, int length)
    {
        if (value.Length != length ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("oracle_build_source_invalid");
        }
        return value;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--source-root",
            "--source-commit",
            "--source-tree",
            "--restricted-root",
            "--destination-identity",
            "--repository-root",
            "--worktree-root",
            "--git-executable",
            "--dotnet-executable",
            "--output-directory",
            "--build-receipt-output",
        };
        if (args.Length != names.Length * 2)
        {
            throw new InvalidDataException("oracle_build_arguments_invalid");
        }
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!allowed.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("oracle_build_arguments_invalid");
            }
        }
        if (names.Any(name => !result.ContainsKey(name)) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--output-directory"]) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--build-receipt-output"]))
        {
            throw new InvalidDataException("oracle_build_arguments_invalid");
        }
        return result;
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_ORACLE_BUILD_INVALID");
        return 1;
    }
}
