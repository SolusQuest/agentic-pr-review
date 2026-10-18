using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

// Authored immutable source/script/oracle input only. This loader has no state,
// provider, GitHub effect or tool-observation authority.
internal sealed class Corpus : IDisposable
{
    internal static readonly string[] Cases = ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule",
        "sticky-only", "no-required-tool", "irrelevant-tool", "wrong-evidence", "wrong-location",
        "safe-invention", "duplicate-proposal", "pathless-proposal"];
    private readonly JsonDocument manifest;
    private readonly Dictionary<string, byte[]> members = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    internal readonly JsonDocument Diff;
    internal string ManifestSha256 { get; }
    internal string Root { get; }
    internal JsonElement Runs => manifest.RootElement.GetProperty("runs");
    internal Corpus(string root)
    {
        Root = Path.GetFullPath(root);
        var bytes = File.ReadAllBytes(Path.Join(Root, "manifest.json"));
        Proof.Require(bytes.Length <= 65536, "manifest_bound");
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Proof.Require(ManifestSha256 == "55a4fa8cca7f081169c4f1f09503c8a5c8f23197c1960ca09fb6266bcb7375b3",
            "authored_oracle_identity");
        manifest = JsonDocument.Parse(bytes);
        var value = manifest.RootElement;
        Proof.Require(value.GetProperty("format").GetString() == "apr.r5.synthetic-replay.v1" &&
            value.GetProperty("source_kind").GetString() == "authored-synthetic", "manifest_kind");
        var inventory = value.GetProperty("files");
        Proof.Require(inventory.GetArrayLength() == 50 && Runs.GetArrayLength() == Cases.Length &&
            Runs.EnumerateArray().Select(r => r.GetProperty("id").GetString()).SequenceEqual(Cases), "fixed_inventory");
        long total = 0;
        foreach (var entry in inventory.EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString()!;
            Proof.Require(relative.Length is > 0 and <= 256 && !Path.IsPathRooted(relative) &&
                !relative.Contains('\\') && relative.Split('/').All(p => p is not ("" or "." or "..")), "member_path");
            var file = new FileInfo(Path.Join(Root, relative));
            Proof.Require(file.LinkTarget is null && file.Length == entry.GetProperty("length").GetInt64() &&
                file.Length <= 65536, "member_bound");
            var member = File.ReadAllBytes(file.FullName); total += member.Length;
            Proof.Require(total <= 524288 && Convert.ToHexStringLower(SHA256.HashData(member)) ==
                entry.GetProperty("sha256").GetString() && members.TryAdd(relative, member), "member_integrity");
        }
        foreach (var entry in Runs[0].GetProperty("repository").EnumerateArray())
            Files.Add(entry.GetProperty("path").GetString()!, Member(entry.GetProperty("file").GetString()!));
        Proof.Require(Files.Count == 9, "source_inventory");
        Diff = JsonDocument.Parse(Member("diff.json"));
    }
    internal byte[] Member(string name) => members[name].ToArray();
    internal JsonDocument Script(string id) => JsonDocument.Parse(Member($"cases/{id}/provider.json"));
    internal JsonDocument Expected(string id) => JsonDocument.Parse(Member($"cases/{id}/expected.json"));
    public void Dispose() { manifest.Dispose(); Diff.Dispose(); }
}

// S4-local adaptation: an authored successor fixes Caller and changes the
// client fallback. Only this synthetic graph supplies the explicit parent edge.
// Real APR validators consume these synthetic GitHub ports. All tree/blob and
// archive data come from the fixed source corpus, never from scripted findings.
internal sealed class CorpusGitHub : IActionHostGitObjectTransportFactory, IActionHostReviewedSnapshotTransportFactory
{
    private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionHostGitTreeObject> trees = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, byte[]> files;
    private readonly ActionHostPullRequestFileObject[] changes;
    private readonly string workflowSha;
    private readonly string workflowRoot;
    private readonly string headRoot;
    private readonly string baseRoot;
    private readonly bool verifiedAhead;
    internal ActionHostGitHubPullRequestFact PullRequest { get; }
    internal ActionHostGitHubPullRequestFact? CurrentOverride { get; set; }
    internal ActionHostGitObjectFailure? CurrentFailure { get; set; }
    internal Action? OnCurrent { get; set; }
    internal int CurrentCalls, BlobReads, ArchiveReads;

    internal CorpusGitHub(Corpus corpus, ActionHostGitHubPullRequestFact pullRequest,
        string workflowSha, byte[]? instructionOverride = null, string? reviewJson = null, bool inline = false, bool ahead = false, bool verifiedAhead = false)
    {
        PullRequest = pullRequest; this.workflowSha = workflowSha; this.verifiedAhead = verifiedAhead;
        files = corpus.Files.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        if (ahead) {
            var changed = (Dictionary<string, byte[]>)files;
            changed["src/Caller.cs"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed["src/Caller.cs"])
                .Replace("Lookup.Find(key).Trim()", "Lookup.Find(key)?.Trim() ?? string.Empty", StringComparison.Ordinal));
            changed["src/client.ts"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(changed["src/client.ts"])
                .Replace("|| 3000", "|| 5000", StringComparison.Ordinal));
        }
        foreach (var bytes in files.Values) AddBlob(bytes);
        var config = Encoding.UTF8.GetBytes("{\"schema\":\"agentic-pr-review.config.v1\",\"instructionsPath\":" +
            "\".github/agentic-pr-review/instructions.md\",\"publication\":{\"mode\":\"" +
            (inline ? "sticky_and_inline" : "sticky") + "\"}" +
            (reviewJson is null ? "" : ",\"review\":" + reviewJson) + "}");
        var policy = new Dictionary<string, byte[]> {
            [".github/agentic-pr-review.json"] = config,
            [".github/agentic-pr-review/instructions.md"] = instructionOverride ?? corpus.Member("policy.txt"),
        };
        foreach (var bytes in policy.Values) AddBlob(bytes);
        workflowRoot = BuildTree(policy, ""); headRoot = BuildTree(files, "");
        var rows = new List<ActionHostPullRequestFileObject>();
        foreach (var diff in corpus.Diff.RootElement.GetProperty("changes").EnumerateArray())
        {
            var path = diff.GetProperty("path").GetString()!;
            var patch = new StringBuilder(); var additions = 0; var deletions = 0;
            foreach (var hunk in diff.GetProperty("hunks").EnumerateArray())
            {
                if (patch.Length != 0) patch.Append('\n');
                patch.Append($"@@ -{hunk.GetProperty("old_start").GetInt32()},{hunk.GetProperty("old_count").GetInt32()} " +
                    $"+{hunk.GetProperty("new_start").GetInt32()},{hunk.GetProperty("new_count").GetInt32()} @@");
                foreach (var line in hunk.GetProperty("lines").EnumerateArray())
                {
                    var kind = line.GetProperty("kind").GetString();
                    var prefix = kind == "addition" ? '+' : kind == "deletion" ? '-' : ' ';
                    if (prefix == '+') additions++; if (prefix == '-') deletions++;
                    var text = line.GetProperty("text").GetString()!;
                    if (ahead) text = text.Replace("Lookup.Find(key).Trim()", "Lookup.Find(key)?.Trim() ?? string.Empty", StringComparison.Ordinal)
                        .Replace("|| 3000", "|| 5000", StringComparison.Ordinal);
                    patch.Append('\n').Append(prefix).Append(text);
                }
            }
            rows.Add(new(BlobSha(files[path]), path, null, diff.GetProperty("status").GetString()!,
                additions, deletions, additions + deletions, patch.ToString()));
        }
        changes = rows.ToArray();
        baseRoot = BuildTree(files.Where(p => changes.All(c => c.Path != p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), "");
    }
    internal static string BlobSha(byte[] bytes) => GitSha("blob", bytes);
    private static string GitSha(string kind, byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(
        Encoding.ASCII.GetBytes($"{kind} {bytes.Length}\0").Concat(bytes).ToArray()));
    private void AddBlob(byte[] bytes) => blobs.TryAdd(BlobSha(bytes), bytes);
    private string BuildTree(IReadOnlyDictionary<string, byte[]> input, string prefix)
    {
        var entries = new List<ActionHostGitTreeEntryObject>();
        foreach (var name in input.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..].Split('/')[0]).Distinct().Order(StringComparer.Ordinal))
        {
            var full = prefix + name;
            if (input.TryGetValue(full, out var bytes)) entries.Add(new(name, "100644", "blob", BlobSha(bytes), bytes.Length));
            else entries.Add(new(name, "040000", "tree", BuildTree(input, full + "/")));
        }
        using var framed = new MemoryStream();
        foreach (var entry in entries)
        {
            framed.Write(Encoding.UTF8.GetBytes((entry.Type == "tree" ? "40000" : "100644") + " " + entry.Path + "\0"));
            framed.Write(Convert.FromHexString(entry.Sha));
        }
        var sha = GitSha("tree", framed.ToArray()); trees.TryAdd(sha, new(sha, entries)); return sha;
    }
    public IActionHostGitObjectTransport CreateExactObjectTransport(ActionHostGitHubToken token) => new Port(this);
    public IActionHostReviewedSnapshotTransport CreateReviewedSnapshotTransport(ActionHostGitHubToken token) => new Port(this);
    private sealed class Port(CorpusGitHub owner) : IActionHostGitObjectTransport, IActionHostReviewedSnapshotTransport
    {
        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>> GetCommitObjectAsync(string repositoryName,
            string commitSha, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ActionHostGitObjectResult<ActionHostGitCommitObject>.Success(new(commitSha,
                commitSha == owner.workflowSha ? owner.workflowRoot : commitSha == owner.PullRequest.BaseSha ? owner.baseRoot : owner.headRoot,
                owner.verifiedAhead && commitSha == new string('3', 40) ? [new string('2', 40)] : []), 64));
        }
        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>> GetTreeObjectAsync(string repositoryName,
            string treeSha, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ActionHostGitObjectResult<ActionHostGitTreeObject>.Success(owner.trees[treeSha], 64));
        }
        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>> GetBlobObjectAsync(string repositoryName,
            string blobSha, ActionHostGitBlobReadBudget budget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); owner.BlobReads++;
            var bytes = owner.blobs[blobSha].ToArray();
            return Task.FromResult(ActionHostGitObjectResult<ActionHostGitBlobObject>.Success(new(blobSha, bytes), bytes.Length));
        }
        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>> GetHeadArchiveAsync(string repositoryName,
            string headSha, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); owner.ArchiveReads++;
            Proof.Require(headSha == owner.PullRequest.HeadSha, "archive_head");
            return Task.FromResult(ActionHostGitObjectResult<ActionHostGitArchiveReader>.Success(new Archive(owner.files),
                owner.files.Values.Sum(b => b.Length)));
        }
        public async Task<ActionHostGitObjectResult<ActionHostStreamedBlobObject>> CopyBlobObjectAsync(string repositoryName,
            string blobSha, long declaredSize, Stream destination, CancellationToken cancellationToken)
        {
            var bytes = owner.blobs[blobSha]; Proof.Require(bytes.Length == declaredSize, "blob_size");
            await destination.WriteAsync(bytes, cancellationToken);
            return ActionHostGitObjectResult<ActionHostStreamedBlobObject>.Success(new(blobSha, bytes.Length), bytes.Length);
        }
        public Task<ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>> GetCurrentPullRequestAsync(string repositoryName,
            long pullRequestNumber, CancellationToken cancellationToken)
        {
            owner.CurrentCalls++; owner.OnCurrent?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (owner.CurrentFailure is { } failure)
                return Task.FromResult(ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>.Failed(failure));
            return Task.FromResult(ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>.Success(owner.CurrentOverride ?? owner.PullRequest, 64));
        }
        public Task<ActionHostGitObjectResult<ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(string repositoryName,
            long pullRequestNumber, int page, int perPage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Proof.Require(page == 1, "changed_page");
            return Task.FromResult(ActionHostGitObjectResult<ActionHostPullRequestFilePageObject>.Success(new(owner.changes, true), 64));
        }
        public void Dispose() { }
    }
    private sealed class Archive : ActionHostGitArchiveReader
    {
        private readonly Queue<ActionHostGitArchiveEntry> entries = new();
        private readonly List<MemoryStream> streams = [];
        private readonly int bytes;
        internal Archive(IReadOnlyDictionary<string, byte[]> files)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal) { "corpus/" };
            foreach (var path in files.Keys)
            {
                var parts = path.Split('/'); var prefix = "corpus/";
                foreach (var part in parts[..^1]) { prefix += part + "/"; directories.Add(prefix); }
            }
            foreach (var directory in directories.Order(StringComparer.Ordinal))
                entries.Enqueue(new(directory, ActionHostGitArchiveEntryType.Directory, 0, 0, null, null));
            foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var stream = new MemoryStream(file.Value, false); streams.Add(stream); bytes += file.Value.Length;
                entries.Enqueue(new("corpus/" + file.Key, ActionHostGitArchiveEntryType.RegularFile, 0x1b4,
                    file.Value.Length, null, stream));
            }
        }
        internal override int CapturedResponseBytes => bytes;
        internal override Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(entries.TryDequeue(out var entry) ? entry : null); }
        public override void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }
}
