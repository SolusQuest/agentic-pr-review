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

            var snapshotDirectory = RestrictedEvidenceRoot.ResolveChildPath(
                root.Path,
                options["--snapshot-directory"]);
            var intermediateDirectory = RestrictedEvidenceRoot.ResolveChildPath(
                root.Path,
                options["--intermediate-directory"]);
            var outputDirectory = RestrictedEvidenceRoot.ResolveChildPath(
                root.Path,
                options["--output-directory"]);
            using var snapshot = AuthorizedGitSnapshot.Materialize(
                git,
                sourceRoot,
                expectedCommit,
                expectedTree,
                snapshotDirectory);
            CreateFreshBuildDirectory(intermediateDirectory);
            CreateFreshBuildDirectory(outputDirectory);
            RunBuild(
                dotnet,
                snapshot.Root,
                intermediateDirectory,
                outputDirectory,
                expectedCommit,
                expectedTree);
            snapshot.Validate();

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

    private static void RunBuild(
        string dotnet,
        string sourceRoot,
        string intermediateDirectory,
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
        var toolHome = System.IO.Path.Join(intermediateDirectory, "tool-home");
        var packages = System.IO.Path.Join(intermediateDirectory, "nuget-packages");
        var temporary = System.IO.Path.Join(intermediateDirectory, "temporary");
        foreach (var directory in new[] { toolHome, packages, temporary })
        {
            Directory.CreateDirectory(directory);
            RestrictDirectory(directory);
        }
        var nugetConfig = System.IO.Path.Join(intermediateDirectory, "NuGet.Config");
        var nugetConfigBytes = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" /></packageSources></configuration>\n");
        using (var config = new FileStream(
            nugetConfig,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read))
        {
            config.Write(nugetConfigBytes);
            config.Flush(flushToDisk: true);
        }
        var arguments = new[]
        {
            "publish",
            project,
            "--configuration",
            "Release",
            "--output",
            outputDirectory,
            "--artifacts-path",
            intermediateDirectory,
            "--disable-build-servers",
            "--force",
            "--no-cache",
            "--configfile",
            nugetConfig,
            $"-p:TrustedProofOracleSourceSha={sourceCommit}",
            $"-p:TrustedProofOracleSourceTree={sourceTree}",
            "-p:ActionHostVerifierFrameworkReference=true",
            "-p:ContinuousIntegrationBuild=true",
            "-p:Deterministic=true",
            "-p:UseSharedCompilation=false",
            $"-p:PathMap={sourceRoot}=/_/apr-r4-e3-authorized-source",
            "-p:ImportDirectoryBuildProps=false",
            "-p:ImportDirectoryBuildTargets=false",
            "--nologo",
        };
        _ = Run(
            dotnet,
            sourceRoot,
            arguments,
            toolHome,
            packages,
            temporary,
            TimeSpan.FromMinutes(10));
    }

    private static string Run(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string toolHome,
        string packages,
        string temporary,
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
        start.Environment.Clear();
        start.Environment["DOTNET_CLI_HOME"] = toolHome;
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_XMLDOC_MODE"] = "skip";
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            var commonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var roaming = System.IO.Path.Join(toolHome, "appdata", "roaming");
            var local = System.IO.Path.Join(toolHome, "appdata", "local");
            Directory.CreateDirectory(roaming);
            Directory.CreateDirectory(local);
            start.Environment["APPDATA"] = roaming;
            start.Environment["LOCALAPPDATA"] = local;
            start.Environment["USERPROFILE"] = toolHome;
            start.Environment["ProgramData"] = programData;
            start.Environment["ProgramFiles"] = programFiles;
            start.Environment["ProgramFiles(x86)"] = programFilesX86;
            start.Environment["CommonProgramFiles"] = commonProgramFiles;
            start.Environment["CommonProgramFiles(x86)"] = commonProgramFilesX86;
            start.Environment["SystemRoot"] = windows;
            start.Environment["WINDIR"] = windows;
            start.Environment["TEMP"] = temporary;
            start.Environment["TMP"] = temporary;
        }
        else
        {
            start.Environment["TMPDIR"] = temporary;
        }
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
            "--snapshot-directory",
            "--intermediate-directory",
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
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--snapshot-directory"]) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--intermediate-directory"]) ||
            new[] {
                result["--snapshot-directory"],
                result["--intermediate-directory"],
                result["--output-directory"],
                result["--build-receipt-output"],
            }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4 ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--build-receipt-output"]))
        {
            throw new InvalidDataException("oracle_build_arguments_invalid");
        }
        return result;
    }

    internal static void CreateFreshBuildDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new InvalidDataException("oracle_build_output_invalid");
        }
        Directory.CreateDirectory(path);
        RestrictDirectory(path);
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
