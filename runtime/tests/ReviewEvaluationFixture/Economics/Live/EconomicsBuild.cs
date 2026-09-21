using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsBuild
{
    private static readonly string[] Dependencies =
    ["AgenticPrReview.Runtime.dll", "Humanizer.dll", "Json.More.dll", "JsonPointer.Net.dll", "JsonSchema.Net.dll"];

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Native AOT hashes its executable; only framework execution uses assembly locations.")]
    internal static string Current()
    {
        var assembly = typeof(Program).Assembly.Location;
        var members = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > 256 * 1024 * 1024 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new EconomicsRejected("r6_economics_build_invalid");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            members.Add(name, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        if (string.IsNullOrEmpty(assembly)) Add("native", Environment.ProcessPath ?? throw new IOException());
        else
        {
            var folder = Path.GetDirectoryName(assembly)!;
            Add("evaluator", assembly);
            foreach (var dependency in Dependencies) Add(dependency, Path.Combine(folder, dependency));
            var launch = ReplayProcess.StartInfo(Path.GetTempPath());
            Add("host", launch.FileName);
            Add("runtimeconfig", launch.ArgumentList[2]);
            var deps = Path.ChangeExtension(assembly, ".deps.json");
            if (File.Exists(deps)) Add("deps", deps);
            else members.Add("deps", "absent");
            Add("corelib", typeof(object).Assembly.Location);
        }
        return AgentCanonical.HashDomain("apr.r6.economics.build",
            Encoding.UTF8.GetBytes(string.Join('\n', members.Select(pair => pair.Key + ":" + pair.Value))));
    }
}
