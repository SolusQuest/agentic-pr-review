using System.IO;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

internal static partial class Program
{
    private static string root = default!;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: LedgerFixtureGen <fixture-root>");
            return 2;
        }
        root = args[0];
        Directory.CreateDirectory(root);

        // Valid ledger fixtures (canonical bytes on disk).
        var bootstrap = BuildBootstrap();
        Write("bootstrap-minimal.json", bootstrap);

        var continuation = BuildContinuation(bootstrap);
        Write("continuation-one-append.json", continuation);

        var resetCache = BuildResetCacheContract(bootstrap);
        Write("reset-cache-contract-change.json", resetCache);

        var resetBase = BuildResetBase(bootstrap);
        Write("reset-base-change.json", resetBase);

        var recoveryRoot = BuildRecoveryRoot();
        Write("recovery-root-unavailable-accepted-artifact.json", recoveryRoot);

        var maxInter = BuildMaxInteractions();
        Write("continuation-max-interactions.json", maxInter);

        // TODO: negative fixtures (raw-transport, schema, semantic, transition)
        // are regenerated in the follow-up rewrite of Mutations.cs against the
        // new frozen contract. This entry point currently emits only the
        // valid-fixture baseline; the negative matrix is added incrementally
        // as tests are rewritten.

        return 0;
    }

    private static void Write(string name, ValidatedLedger v)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, v.ToCanonicalByteArray());
        Console.WriteLine($"{name}: {v.ContentSha256}");
    }

    internal static void WriteRaw(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"{name}: (raw, {bytes.Length} bytes)");
    }
}
