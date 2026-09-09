using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5ReplayAdmissionTests
{
    private const string SourcePath = "src/Counter.cs";
    private const string Canary = "EXPECTATION_ONLY_CANARY";
    private static string Seed => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay-seed", "valid");

    [Fact]
    public async Task CompleteSeedProducesOwnedInputsForActualToolsAndPolicy()
    {
        var result = ReplayAdmission.Load(Seed);
        Assert.Equal(ReplayAdmissionCode.Admitted, result.Code);
        var fixture = Assert.IsType<AdmittedReplayFixture>(result.Fixture);
        Assert.Equal("3dc214862fc636bdca23bd0acb94ff0f15e632910d6e0557f3034b0be4df8d0c", fixture.CorpusSha256);
        Assert.Equal(6, fixture.Files.Length);
        var run = Assert.Single(fixture.Runs);
        Assert.Equal("db6a077771c3c87335e6726e0934ab4b50c849d125b222671f875cd3ce2a67da", run.Expected.Sha256);
        Assert.Equal("4506af9bdefc3dd8b6eaa2993415236b8989c471f2630c578c43dd6f706a8041", run.ConfigurationSha256);
        Assert.Equal("seed-safe-control", run.Expected.Input.Id);
        Assert.Equal(fixture.CorpusSha256, run.Expected.Input.CorpusSha256);
        Assert.Equal(EvaluationCode.Scored, run.ExpectedCode);
        Assert.Equal(["read0", "finish0"], run.Script.Turns.SelectMany(turn => turn.ToolCalls).Select(call => call.Id));
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(run.CreateTrustedRequest("test-build"), null, out var stable));
        Assert.Equal(AgentCanonical.LimitsSha256(), stable!.StablePlan.LimitsSha256);
        var snapshot = run.CreateSnapshot(Path.GetTempPath());
        Assert.Equal(SourcePath, Assert.Single(snapshot.OrderedTrackedFiles));
        var tools = new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot));
        Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"src/Counter.cs\",\"start_line\":1,\"line_count\":1}", out var read));
        var file = await tools.ExecuteAsync(new PreparedReadFileCall("actual-read", read!), CancellationToken.None);
        Assert.True(file.Succeeded);
        Assert.Contains("a + b", file.ResultJson);
        Assert.True(AgentToolArguments.TryReadDiff("{\"path\":\"src/Counter.cs\",\"start_hunk\":1,\"hunk_count\":1}", out var diff));
        Assert.True((await tools.ExecuteAsync(new PreparedReadDiffCall("actual-diff", diff!), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AssertionsAreNotContextPolicyOrToolContentAndAdmittedBytesOutliveBundle()
    {
        using var bundle = new Bundle();
        var original = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(bundle.Root).Fixture);
        bundle.Edit("expected.json", document => document["defects"] = new JsonArray(JsonSerializer.SerializeToNode(
            new ExpectedDefect(Canary, "high", new string('a', 64), SourcePath, 1, 1), EvaluationJsonContext.Default.ExpectedDefect)));
        var admitted = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(bundle.Root).Fixture);
        Assert.NotEqual(original.CorpusSha256, admitted.CorpusSha256);
        Assert.NotEqual(original.Runs[0].Expected.Sha256, admitted.Runs[0].Expected.Sha256);
        Assert.Equal(original.Runs[0].ConfigurationSha256, admitted.Runs[0].ConfigurationSha256);
        var run = admitted.Runs[0];
        Assert.DoesNotContain(Canary, run.InitialContext);
        var trusted = run.CreateTrustedRequest("test");
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(trusted.TrustedPolicyBytes));
        trusted.TrustedPolicyBytes[0] ^= 1;
        Assert.NotEqual(trusted.TrustedPolicyBytes, run.CreateTrustedRequest("test").TrustedPolicyBytes);
        var snapshot = run.CreateSnapshot(Path.GetTempPath());
        var access = run.CreateFileAccess(snapshot);
        var before = await access.ReadAsync(snapshot, SourcePath, access.Probe(snapshot, SourcePath), CancellationToken.None);
        var originalBytes = before.Bytes!.ToArray();
        before.Bytes![0] ^= 1;
        Directory.Delete(bundle.Root, recursive: true);
        var after = await access.ReadAsync(snapshot, SourcePath, access.Probe(snapshot, SourcePath), CancellationToken.None);
        Assert.Equal(originalBytes, after.Bytes);
        Assert.Equal(ReviewedFileAccessStatus.Unsafe, access.Probe(snapshot, "expected.json").Status);
        Assert.Equal(ReviewedFileAccessStatus.Unsafe, access.Probe(snapshot, "provider.json").Status);
        Assert.DoesNotContain(Canary, admitted.ToString() + run + run.Script);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("empty-directory")]
    [InlineData("file-as-directory")]
    [InlineData("same-length-change")]
    [InlineData("append")]
    [InlineData("truncate")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("invalid-utf8")]
    public void DirectoryAndContentFaultsNeverReturnPartialFixture(string fault)
    {
        using var bundle = new Bundle();
        var source = Path.Combine(bundle.Root, "source.txt");
        switch (fault)
        {
            case "missing": File.Delete(source); break;
            case "extra": File.WriteAllText(Path.Combine(bundle.Root, "unexpected.txt"), Canary); break;
            case "empty-directory": Directory.CreateDirectory(Path.Combine(bundle.Root, "extra")); break;
            case "file-as-directory": File.Delete(source); Directory.CreateDirectory(source); break;
            case "same-length-change": var bytes = File.ReadAllBytes(source); bytes[0] ^= 1; File.WriteAllBytes(source, bytes); break;
            case "append": File.AppendAllText(source, "x"); break;
            case "truncate": File.WriteAllBytes(source, []); break;
            case "length": bundle.Manifest(document => FileRow(document, "source.txt")["length"] = 0); break;
            case "hash": bundle.Manifest(document => FileRow(document, "source.txt")["sha256"] = new string('0', 64)); break;
            case "invalid-utf8": bundle.Rewrite("source.txt", [0xc3, 0x28]); break;
        }
        Reject(bundle.Root);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("https://invalid.example/file")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("NUL.txt")]
    [InlineData("a/COM1")]
    [InlineData("a/ending.")]
    [InlineData("a/ending ")]
    public void UntrustedMemberPathsReject(string path)
    {
        using var bundle = new Bundle();
        bundle.Manifest(document => FileRow(document, "source.txt")["path"] = path);
        Reject(bundle.Root);
    }

    [Theory]
    [InlineData("duplicate-file")]
    [InlineData("case-alias")]
    [InlineData("duplicate-repository-path")]
    [InlineData("role-swap")]
    [InlineData("dangling-reference")]
    [InlineData("unused-file")]
    [InlineData("unsupported-format")]
    [InlineData("production-source")]
    [InlineData("bad-first-transition")]
    public void ClosedReferencesRolesAndIdentityReject(string fault)
    {
        using var bundle = new Bundle();
        if (fault == "unused-file") bundle.AddFile("unused.txt", "repository", [], null);
        else bundle.Manifest(document =>
        {
            var files = document["files"]!.AsArray();
            var run = document["runs"]![0]!;
            switch (fault)
            {
                case "duplicate-file": files.Add(files[0]!.DeepClone()); break;
                case "case-alias": var alias = files[0]!.DeepClone(); alias["path"] = alias["path"]!.GetValue<string>().ToUpperInvariant(); files.Add(alias); break;
                case "duplicate-repository-path": run["repository"]!.AsArray().Add(run["repository"]![0]!.DeepClone()); break;
                case "role-swap": run["context"] = "expected.json"; break;
                case "dangling-reference": run["policy"] = "absent.txt"; break;
                case "unsupported-format": document["format"] = "unsupported"; break;
                case "production-source": document["source_kind"] = "captured-session"; break;
                case "bad-first-transition": run["transition"] = "verified_ahead"; break;
            }
        });
        Reject(bundle.Root);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("diff.json")]
    [InlineData("provider.json")]
    [InlineData("expected.json")]
    public void EveryObjectHasClosedRequiredFields(string file)
    {
        using var pristine = new Bundle();
        var template = JsonNode.Parse(File.ReadAllBytes(Path.Combine(pristine.Root, file)))!;
        var pointers = ObjectPaths(template).ToArray();
        foreach (var pointer in pointers)
        {
            var original = At(template, pointer).AsObject();
            foreach (var property in original.Select(pair => pair.Key).Append("unknown-field"))
            {
                using var bundle = new Bundle();
                var modified = template.DeepClone();
                var target = At(modified, pointer).AsObject();
                if (property == "unknown-field") target[property] = 1;
                else target.Remove(property);
                bundle.Rewrite(file, Encoding.UTF8.GetBytes(modified.ToJsonString()));
                Reject(bundle.Root);
            }
        }
        using var duplicate = new Bundle();
        var text = File.ReadAllText(Path.Combine(duplicate.Root, file));
        var first = template.AsObject().First();
        duplicate.Rewrite(file, Encoding.UTF8.GetBytes("{" + JsonSerializer.Serialize(first.Key) + ":" + first.Value!.ToJsonString() + "," + text[1..]));
        Reject(duplicate.Root);
    }

    [Fact]
    public void CountByteAndDepthBoundsAreEnforcedAtActualBoundaries()
    {
        using var bundle = new Bundle();
        var bytes = File.ReadAllBytes(Path.Combine(bundle.Root, "manifest.json"));
        File.WriteAllBytes(Path.Combine(bundle.Root, "manifest.json"), [.. bytes, .. Enumerable.Repeat((byte)' ', ReplayLimits.ManifestBytes - bytes.Length)]);
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(bundle.Root).Code);
        File.AppendAllText(Path.Combine(bundle.Root, "manifest.json"), " ");
        Reject(bundle.Root);
        using var count = new Bundle();
        for (var index = 6; index < ReplayLimits.Files; index++) count.AddFile($"z{index}.txt", "repository", [], $"src/Z{index}.txt");
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(count.Root).Code);
        count.AddFile("zoverflow.txt", "repository", [], "src/Overflow.txt");
        Reject(count.Root);
        using var aggregate = new Bundle();
        for (var index = 0; index < 7; index++) aggregate.AddFile($"z{index}.txt", "repository", Encoding.UTF8.GetBytes(new string('x', ReplayLimits.FileBytes)), $"src/Z{index}.txt");
        var size = JsonNode.Parse(File.ReadAllBytes(Path.Combine(aggregate.Root, "manifest.json")))!["files"]!.AsArray().Sum(row => row!["length"]!.GetValue<int>());
        var remainder = ReplayLimits.TotalBytes - size;
        Assert.InRange(remainder, 1, ReplayLimits.FileBytes - 1);
        aggregate.AddFile("zlast.txt", "repository", Encoding.UTF8.GetBytes(new string('x', remainder)), "src/Last.txt");
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(aggregate.Root).Code);
        aggregate.Rewrite("zlast.txt", Encoding.UTF8.GetBytes(new string('x', remainder + 1)));
        Reject(aggregate.Root);
        using var member = new Bundle();
        member.AddFile("zbig.txt", "repository", Encoding.UTF8.GetBytes(new string('x', ReplayLimits.FileBytes)), "src/Big.txt");
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(member.Root).Code);
        member.Rewrite("zbig.txt", Encoding.UTF8.GetBytes(new string('x', ReplayLimits.FileBytes + 1)));
        Reject(member.Root);
        using var deep = new Bundle();
        deep.Rewrite("expected.json", Encoding.UTF8.GetBytes(new string('[', ReplayLimits.Depth + 1) + "0" + new string(']', ReplayLimits.Depth + 1)));
        Reject(deep.Root);
    }

    [Theory]
    [InlineData("head-content")]
    [InlineData("hunk-progression")]
    [InlineData("unknown-status")]
    [InlineData("duplicate-change")]
    [InlineData("numeric-outcome")]
    public void SnapshotAndExpectationSemanticsRejectAfterValidFileHashes(string fault)
    {
        using var bundle = new Bundle();
        if (fault == "numeric-outcome") bundle.Edit("expected.json", document => document["expected_code"] = "0");
        else bundle.Edit("diff.json", document =>
        {
            var change = document["changes"]![0]!;
            switch (fault)
            {
                case "head-content": change["hunks"]![0]!["lines"]![1]!["text"] = "wrong source"; break;
                case "hunk-progression": change["hunks"]![0]!["new_count"] = 2; break;
                case "unknown-status": change["status"] = "unknown"; break;
                case "duplicate-change": document["changes"]!.AsArray().Add(change.DeepClone()); break;
            }
        });
        Reject(bundle.Root);
    }

    [Fact]
    public void BomCrLfPreserveRawBytesAndIntentionallyBadScriptsRemainInputs()
    {
        using var bundle = new Bundle();
        var original = ReplayAdmission.Load(bundle.Root).Fixture!;
        var source = File.ReadAllText(Path.Combine(bundle.Root, "source.txt"));
        bundle.Rewrite("source.txt", [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(source.Replace("\n", "\r\n"))]);
        bundle.Edit("provider.json", document => document["turns"]![0]!["tool_calls"]![0]!["arguments_json"] = "intentionally malformed");
        var changed = ReplayAdmission.Load(bundle.Root).Fixture;
        Assert.NotNull(changed);
        Assert.NotEqual(original.CorpusSha256, changed.CorpusSha256);
        Assert.Equal(original.Runs[0].ConfigurationSha256, changed.Runs[0].ConfigurationSha256);
        Assert.Equal("intentionally malformed", changed.Runs[0].Script.Turns[0].ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void CancellationAndUnsupportedRootsAreSafeFailures()
    {
        Assert.Equal(ReplayAdmissionCode.Cancelled, ReplayAdmission.Load(Seed, new CancellationToken(true)).Code);
        Reject("\\\\invalid-server\\share");
        Reject("https://invalid.example/bundle");
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("diff.json")]
    [InlineData("provider.json")]
    [InlineData("expected.json")]
    public void NestedDuplicatesNullsAndWrongTypesReject(string file)
    {
        using var pristine = new Bundle();
        var template = JsonNode.Parse(File.ReadAllBytes(Path.Combine(pristine.Root, file)))!;
        foreach (var pointer in ObjectPaths(template))
        {
            foreach (var pair in At(template, pointer).AsObject())
            {
                if (pair.Value is not null)
                {
                    using var nullBundle = new Bundle();
                    var nullDocument = template.DeepClone();
                    At(nullDocument, pointer)[pair.Key] = null;
                    nullBundle.Rewrite(file, Encoding.UTF8.GetBytes(nullDocument.ToJsonString()));
                    Reject(nullBundle.Root);
                }
                using var wrong = new Bundle();
                var wrongDocument = template.DeepClone();
                At(wrongDocument, pointer)[pair.Key] = new JsonObject { ["wrong-type"] = true };
                wrong.Rewrite(file, Encoding.UTF8.GetBytes(wrongDocument.ToJsonString()));
                Reject(wrong.Root);
            }
            using var duplicate = new Bundle();
            var document = template.DeepClone();
            var target = At(document, pointer).AsObject();
            var first = target.First();
            // Replace this exact object's serialization, preserving the rest of the valid document.
            var original = target.ToJsonString();
            var repeated = "{" + JsonSerializer.Serialize(first.Key) + ":" + (first.Value?.ToJsonString() ?? "null") + "," + original[1..];
            duplicate.Rewrite(file, Encoding.UTF8.GetBytes(document.ToJsonString().Replace(original, repeated, StringComparison.Ordinal)));
            Reject(duplicate.Root);
        }
    }

    [Fact]
    public void RunOrderBoundsAndExplicitTransitionsAreBoundToOneTarget()
    {
        using var bundle = new Bundle();
        bundle.Manifest(document =>
        {
            var runs = document["runs"]!.AsArray();
            for (var index = 1; index < ReplayLimits.Runs; index++)
            {
                var run = runs[index - 1]!.DeepClone();
                run["id"] = "run-" + index;
                run["case_id"] = "case-" + index;
                run["previous_run_id"] = runs[index - 1]!["id"]!.DeepClone();
                run["transition"] = index == 1 ? "verified_ahead" : "same_head";
                run["reviewed_identity"]!["head_sha"] = new string('3', 40);
                runs.Add(run);
            }
        });
        var admitted = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(bundle.Root).Fixture);
        Assert.Equal(ReplayLimits.Runs, admitted.Runs.Length);
        Assert.Equal("run-15", admitted.Runs[^1].Input.Id);
        var valid = File.ReadAllBytes(Path.Combine(bundle.Root, "manifest.json"));
        foreach (var fault in new[] { "count", "id", "case", "previous", "head", "base", "repository", "target", "transition", "ahead" })
        {
            bundle.Rewrite("manifest.json", valid);
            bundle.Manifest(document =>
            {
                var runs = document["runs"]!.AsArray();
                var run = runs[2]!;
                switch (fault)
                {
                    case "count": var extra = run.DeepClone(); extra["id"] = "overflow"; extra["case_id"] = "overflow"; extra["previous_run_id"] = "run-15"; runs.Add(extra); break;
                    case "id": run["id"] = "run-1"; break;
                    case "case": run["case_id"] = "case-1"; break;
                    case "previous": run["previous_run_id"] = "seed-run"; break;
                    case "head": run["reviewed_identity"]!["head_sha"] = new string('4', 40); break;
                    case "base": run["reviewed_identity"]!["base_sha"] = new string('4', 40); break;
                    case "repository": run["reviewed_identity"]!["repository_id"] = "other/repository"; break;
                    case "target": run["reviewed_identity"]!["review_target"] = 246; break;
                    case "transition": run["transition"] = "initial"; break;
                    case "ahead": run["transition"] = "verified_ahead"; break;
                }
            });
            Reject(bundle.Root);
        }
    }

    [Fact]
    public void PortableNestedPathsAcceptAtLimitsAndRejectAliases()
    {
        using var bundle = new Bundle();
        var path = "a/b/c/d/e/f/g/" + new string('z', ReplayLimits.PathBytes - 14);
        bundle.AddFile(path, "repository", [], "extra.txt");
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(bundle.Root).Code);
        Assert.True(ReplayLimits.Path(path));
        Assert.False(ReplayLimits.Path(path + "z"));
        Assert.False(ReplayLimits.Path("a/b/c/d/e/f/g/h/i"));
        bundle.AddFile("A/other.txt", "repository", [], "other.txt");
        Reject(bundle.Root);
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("changed")]
    [InlineData("renamed")]
    [InlineData("copied")]
    public void EveryAdmittedLifecycleUsesRuntimeValidationAndHeadContent(string status)
    {
        using var bundle = new Bundle();
        if (status == "removed")
        {
            bundle.Manifest(document => document["runs"]![0]!["repository"]![0]!["path"] = "unchanged.txt");
            bundle.Edit("expected.json", document => document["prohibited_findings"] = new JsonArray());
        }
        if (status == "copied") bundle.Manifest(document => document["runs"]![0]!["repository"]!.AsArray().Add(
            new JsonObject { ["path"] = "src/Previous.cs", ["file"] = "source.txt" }));
        bundle.Edit("diff.json", document =>
        {
            var change = document["changes"]![0]!;
            change["status"] = status;
            if (status is "renamed" or "copied") change["previous_path"] = "src/Previous.cs";
            var hunk = change["hunks"]![0]!;
            if (status == "added") { hunk["old_start"] = 0; hunk["old_count"] = 0; hunk["lines"]!.AsArray().RemoveAt(0); }
            if (status == "removed") { hunk["new_start"] = 0; hunk["new_count"] = 0; hunk["lines"]!.AsArray().RemoveAt(1); }
        });
        var admitted = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(bundle.Root).Fixture);
        Assert.Equal(status, Assert.Single(admitted.Runs[0].CreateSnapshot(Path.GetTempPath()).OrderedChangedFiles).Status);
    }

    [Theory]
    [InlineData("source.txt")]
    [InlineData("manifest.json")]
    public void HardLinkedMembersRejectOnBothPlatforms(string member)
    {
        using var bundle = new Bundle();
        using var other = new Bundle();
        var link = Path.Combine(other.Root, "hard-link");
        var target = Path.Combine(bundle.Root, member);
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(link, target, IntPtr.Zero));
        else Assert.Equal(0, Link(target, link));
        Reject(bundle.Root);
    }

    [Theory]
    [InlineData("file-link")]
    [InlineData("manifest-link")]
    [InlineData("directory-link")]
    [InlineData("root-link")]
    [InlineData("ancestor-link")]
    [InlineData("fifo")]
    [InlineData("socket")]
    public async Task LinuxLinksAndSpecialFilesRejectWithoutBlocking(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var bundle = new Bundle();
        using var other = new Bundle();
        string root = bundle.Root;
        Socket? socket = null;
        try
        {
            if (fault is "file-link" or "manifest-link")
            {
                var member = fault == "file-link" ? "source.txt" : "manifest.json";
                File.Delete(Path.Combine(root, member));
                File.CreateSymbolicLink(Path.Combine(root, member), Path.Combine(other.Root, member));
            }
            else if (fault == "directory-link")
            {
                bundle.AddFile("nested/source.txt", "repository", [], "nested.txt");
                Directory.Delete(Path.Combine(root, "nested"), true);
                Directory.CreateSymbolicLink(Path.Combine(root, "nested"), other.Root);
            }
            else if (fault is "root-link" or "ancestor-link")
            {
                var link = Path.Combine(other.Root, "linked");
                Directory.CreateSymbolicLink(link, fault == "root-link" ? root : Path.GetDirectoryName(root)!);
                root = fault == "root-link" ? link : Path.Combine(link, Path.GetFileName(root));
            }
            else
            {
                var path = Path.Combine(root, "source.txt");
                File.Delete(path);
                if (fault == "fifo") Assert.Equal(0, MkFifo(path, 384));
                else { socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); socket.Bind(new UnixDomainSocketEndPoint(path)); }
            }
            var result = await Task.Run(() => ReplayAdmission.Load(root)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(ReplayAdmissionCode.Admitted, result.Code);
            Assert.Null(result.Fixture);
        }
        finally { socket?.Dispose(); }
    }

    [Fact]
    public void PinnedDirectoryRejectsReplacementOrKeepsTheOriginalAnchor()
    {
        using var bundle = new Bundle();
        var moved = bundle.Root + "-moved";
        using (var guard = ReplayDirectoryGuard.Open(bundle.Root))
        {
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => Directory.Move(bundle.Root, moved));
            else
            {
                Directory.Move(bundle.Root, moved);
                try
                {
                    Directory.CreateDirectory(bundle.Root);
                    Assert.False(guard.StillMatches());
                    Assert.True(File.Exists(Path.Combine(guard.AnchoredPath, "manifest.json")));
                    Reject(bundle.Root);
                }
                finally { Directory.Delete(bundle.Root); Directory.Move(moved, bundle.Root); }
            }
        }
        Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(bundle.Root).Code);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ancestor")]
    [InlineData("member")]
    public async Task WindowsDirectoryReparsePointsReject(string position)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var bundle = new Bundle();
        using var other = new Bundle();
        var link = Path.Combine(other.Root, "junction");
        var target = position == "ancestor" ? Path.GetDirectoryName(bundle.Root)! : bundle.Root;
        if (position == "member")
        {
            bundle.AddFile("nested/file.txt", "repository", [], "nested.txt");
            Directory.Delete(Path.Combine(bundle.Root, "nested"), true);
            link = Path.Combine(bundle.Root, "nested");
            target = other.Root;
        }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        try { Reject(position == "root" ? link : position == "ancestor" ? Path.Combine(link, Path.GetFileName(bundle.Root)) : bundle.Root); }
        finally { Directory.Delete(link); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string target, IntPtr attributes);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string target, string link);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private static void Reject(string root)
    {
        var result = ReplayAdmission.Load(root);
        Assert.NotEqual(ReplayAdmissionCode.Admitted, result.Code);
        Assert.Null(result.Fixture);
        Assert.DoesNotContain(Canary, result.ToString());
    }

    private static JsonObject FileRow(JsonNode document, string path) => document["files"]!.AsArray().Single(row => row!["path"]!.GetValue<string>() == path)!.AsObject();
    private static IEnumerable<string[]> ObjectPaths(JsonNode node, string[]? prefix = null)
    {
        prefix ??= [];
        if (node is JsonObject obj)
        {
            yield return prefix;
            foreach (var pair in obj)
                if (pair.Value is JsonObject or JsonArray)
                    foreach (var path in ObjectPaths(pair.Value, [.. prefix, pair.Key])) yield return path;
        }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonObject or JsonArray)
                    foreach (var path in ObjectPaths(array[index]!, [.. prefix, index.ToString()])) yield return path;
    }
    private static JsonNode At(JsonNode node, string[] path)
    {
        foreach (var component in path) node = node is JsonArray ? node[int.Parse(component)]! : node[component]!;
        return node;
    }

    private sealed class Bundle : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "r5-replay-" + Guid.NewGuid().ToString("N"));
        internal Bundle()
        {
            Directory.CreateDirectory(Root);
            foreach (var file in Directory.EnumerateFiles(Seed)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }
        internal void Manifest(Action<JsonNode> edit)
        {
            var document = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Root, "manifest.json")))!;
            edit(document);
            File.WriteAllText(Path.Combine(Root, "manifest.json"), document.ToJsonString());
        }
        internal void Edit(string file, Action<JsonNode> edit)
        {
            var document = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Root, file)))!;
            edit(document);
            Rewrite(file, Encoding.UTF8.GetBytes(document.ToJsonString()));
        }
        internal void Rewrite(string file, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(Root, file), bytes);
            if (file != "manifest.json") Manifest(document =>
            { var row = FileRow(document, file); row["length"] = bytes.Length; row["sha256"] = AgentCanonical.HashRaw(bytes); });
        }
        internal void AddFile(string file, string role, byte[] bytes, string? repositoryPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, file))!);
            File.WriteAllBytes(Path.Combine(Root, file), bytes);
            Manifest(document =>
            {
                var files = document["files"]!.AsArray();
                files.Add(new JsonObject { ["path"] = file, ["role"] = role, ["length"] = bytes.Length, ["sha256"] = AgentCanonical.HashRaw(bytes) });
                document["files"] = new JsonArray(files.OrderBy(row => row!["path"]!.GetValue<string>(), StringComparer.Ordinal).Select(row => row!.DeepClone()).ToArray());
                if (repositoryPath is not null) document["runs"]![0]!["repository"]!.AsArray().Add(new JsonObject { ["path"] = repositoryPath, ["file"] = file });
            });
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
