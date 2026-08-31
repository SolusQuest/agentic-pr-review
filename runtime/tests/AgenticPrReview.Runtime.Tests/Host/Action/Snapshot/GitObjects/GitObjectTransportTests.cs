using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.GitObjects;

public sealed partial class GitObjectTransportTests
{
    [Fact]
    public async Task ExactCommitAndNonRecursiveTreeRequestsUseFrozenAuthority()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('1', 40);
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith(
                "/git/commits/" + ActionHostAuthorizationScenario.HeadSha,
                StringComparison.Ordinal)
                ? JsonResponse($$"""
                    {
                      "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
                      "tree": { "sha": "{{treeSha}}" },
                      "forward_compatible": true
                    }
                    """)
                : JsonResponse($$"""
                    {
                      "sha": "{{treeSha}}",
                      "truncated": false,
                      "tree": []
                    }
                    """);
        });
        var budget = ProductionBudget();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);

        var commit = await transport.GetCommitAsync(CancellationToken.None);
        var tree = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(treeSha, commit.Value!.TreeSha);
        Assert.Empty(tree.Value!.Entries);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com", request.Origin);
            Assert.Equal(string.Empty, request.Query);
            Assert.Equal("Bearer token-canary",
                request.Header("Authorization"));
            Assert.Equal("2026-03-10",
                request.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(
            "recursive",
            handler.Requests[1].Uri,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedJsonConsumesItsExactCapturedAggregateBytes()
    {
        const string body = "{\"malformed\":";
        var invocation = await AuthorizedInvocation();
        var budget = ProductionBudget();
        Assert.True(budget.TryGetRemaining(out var before));
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            new CapturingHandler(_ => JsonResponse(body)));

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ReviewedGitObjectFailure.InvalidResponse, result.Failure);
        Assert.True(budget.TryGetRemaining(out var after));
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body),
            before!.ResponseBytes - after!.ResponseBytes);
    }

    [Fact]
    public async Task SharedBlobEnvelopeStagesExactDecodedBytes()
    {
        var invocation = await AuthorizedInvocation();
        var bytes = "text\0and-binary"u8.ToArray();
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes)));
        var budget = ProductionBudget();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);
        var parent = CreateTemporaryDirectory();
        try
        {
            var staging = ReviewedSnapshotTestAccess.Staging(parent, budget);
            var result = await transport.StageBlobAsync(
                sha,
                bytes.Length,
                staging,
                CancellationToken.None);

            var staged = Assert.IsType<ReviewedStagedBlob>(result.Value);
            using var copied = new MemoryStream();
            Assert.True(await staged.CopyVerifiedToAsync(
                copied,
                CancellationToken.None));
            Assert.Equal(bytes, copied.ToArray());
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.Accept,
                Assert.Single(handler.Requests).Header("Accept"));
            Assert.True(staging.Cleanup());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task HeadArchiveStagesOnlyRegularFilesAndVerifiesGitArchiveMetadata()
    {
        var regular = "regular"u8.ToArray();
        var executable = "#!/bin/sh\necho trusted\n"u8.ToArray();
        var symlinkTarget = "README.md"u8.ToArray();
        var entries = new[]
        {
            ArchiveEntry("README.md", "100644", regular),
            ArchiveEntry("COPY.md", "100644", regular),
            ArchiveEntry("bin/run", "100755", executable),
            ArchiveEntry("readme-link", "120000", symlinkTarget),
        };
        var archive = GzipTar(
            DirectoryEntry("fixture-root/"),
            FileEntry("fixture-root/README.md", regular, 0x1b4),
            FileEntry("fixture-root/COPY.md", regular, 0x1b4),
            DirectoryEntry("fixture-root/bin/"),
            FileEntry("fixture-root/bin/run", executable, 0x1fd),
            SymlinkEntry("fixture-root/readme-link", "README.md", 0x1ff));
        var attempt = await StageHeadArchive(entries, archive);
        try
        {
            var batch = Assert.IsType<ReviewedHeadArchiveBatch>(
                attempt.Result.Value);
            Assert.Equal(2, batch.StagedBySha.Count);
            Assert.DoesNotContain(GitBlobSha(symlinkTarget),
                batch.StagedBySha.Keys);
            await AssertStagedBytes(batch.StagedBySha[GitBlobSha(regular)], regular);
            await AssertStagedBytes(batch.StagedBySha[GitBlobSha(executable)], executable);

            Assert.Equal(2, attempt.Handler.Requests.Count);
            Assert.Contains("/tarball/", attempt.Handler.Requests[0].Uri,
                StringComparison.Ordinal);
            Assert.Equal("https://codeload.github.com",
                attempt.Handler.Requests[1].Origin);
            Assert.DoesNotContain("Authorization",
                attempt.Handler.Requests[1].Headers.Keys,
                StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(attempt.Handler.Requests, request =>
                request.Uri.Contains("/git/blobs/", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(attempt);
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsMissingExtraDuplicateTraversalAndNonRegularMembers()
    {
        var bytes = "trusted"u8.ToArray();
        var expected = new[] { ArchiveEntry("README.md", "100644", bytes) };
        var cases = new[]
        {
            GzipTar(DirectoryEntry("fixture-root/")),
            GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/README.md", bytes, 0x1b4),
                FileEntry("fixture-root/extra.md", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/README.md", bytes, 0x1b4),
                FileEntry("fixture-root/README.md", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/../README.md", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                SymlinkEntry("fixture-root/README.md", "README.md", 0x1ff)),
        };

        foreach (var archive in cases)
        {
            var attempt = await StageHeadArchive(expected, archive);
            try
            {
                Assert.Null(attempt.Result.Value);
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                    attempt.Result.Failure);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsWrongModeSizeAndGitBlobIdentity()
    {
        var bytes = "trusted"u8.ToArray();
        var expected = new[] { ArchiveEntry("README.md", "100644", bytes) };
        var wrongSha = new ReviewedHeadArchiveEntry(
            "README.md", "100644", new string('e', 40), bytes.Length);
        var cases = new[]
        {
            (expected, GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/README.md", bytes, 0x1a4))),
            (expected, GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/README.md", "longer"u8.ToArray(), 0x1b4))),
            (new[] { wrongSha }, GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/README.md", bytes, 0x1b4))),
        };

        foreach (var testCase in cases)
        {
            var attempt = await StageHeadArchive(testCase.Item1, testCase.Item2);
            try
            {
                Assert.Null(attempt.Result.Value);
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                    attempt.Result.Failure);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsSymlinkTargetOrMetadataMismatch()
    {
        var target = "README.md"u8.ToArray();
        var expected = new[] { ArchiveEntry("readme-link", "120000", target) };
        var cases = new[]
        {
            GzipTar(DirectoryEntry("fixture-root/"),
                SymlinkEntry("fixture-root/readme-link", "other.md", 0x1ff)),
            GzipTar(DirectoryEntry("fixture-root/"),
                SymlinkEntry("fixture-root/readme-link", "README.md", 0x1fd)),
        };

        foreach (var archive in cases)
        {
            var attempt = await StageHeadArchive(expected, archive);
            try
            {
                Assert.Null(attempt.Result.Value);
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                    attempt.Result.Failure);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsDirectoryPayloadBeforeItCanConsumeInflatedBudget()
    {
        var bytes = "trusted"u8.ToArray();
        var raw = RawTarDirectoryWithPayload("fixture-root/", "x"u8.ToArray());
        var attempt = await StageHeadArchive(
            [ArchiveEntry("README.md", "100644", bytes)], Gzip(raw));
        try
        {
            Assert.Null(attempt.Result.Value);
            Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                attempt.Result.Failure);
        }
        finally
        {
            Cleanup(attempt);
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsDeclaredDirectoryBombBeforeReadingItsPayload()
    {
        var bytes = "trusted"u8.ToArray();
        var raw = RawTarDirectoryWithDeclaredLength(
            "fixture-root/",
            checked((int)ReviewedContentLimits.HeadArchiveDecodedBytes));
        var attempt = await StageHeadArchive(
            [ArchiveEntry("README.md", "100644", bytes)], Gzip(raw));
        try
        {
            Assert.Null(attempt.Result.Value);
            Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                attempt.Result.Failure);
            Assert.Equal(2, attempt.Handler.Requests.Count);
        }
        finally
        {
            Cleanup(attempt);
        }
    }

    [Fact]
    public async Task HeadArchiveTransportMemberCapAcceptsItsBoundaryAndRejectsOneExtraDirectory()
    {
        var atCap = BuildTransportCapArchive(extraDirectory: false);
        var accepted = await StageHeadArchive(atCap.Entries, atCap.Archive);
        try
        {
            Assert.NotNull(accepted.Result.Value);
            Assert.True(accepted.Budget.TryGetRemaining(out var remaining));
            Assert.Equal(
                ReviewedContentLimits.GitObjectRequests - 2,
                remaining!.Requests);
            Assert.Equal(
                ReviewedContentLimits.AggregateResponseBytes -
                    atCap.Archive.Length,
                remaining.ResponseBytes);
            Assert.Equal("Bearer token-canary",
                accepted.Handler.Requests[0].Header("Authorization"));
            Assert.DoesNotContain("Authorization",
                accepted.Handler.Requests[1].Headers.Keys,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(accepted);
        }

        var capPlusOne = BuildTransportCapArchive(extraDirectory: true);
        var rejected = await StageHeadArchive(
            capPlusOne.Entries, capPlusOne.Archive);
        try
        {
            Assert.Null(rejected.Result.Value);
            Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
                rejected.Result.Failure);
            Assert.Equal(2, rejected.Handler.Requests.Count);
        }
        finally
        {
            Cleanup(rejected);
        }
    }

    [Fact]
    public async Task HeadArchiveRequiresEveryCanonicalDirectoryExactlyOnce()
    {
        var bytes = "trusted"u8.ToArray();
        var expected = new[] { ArchiveEntry("bin/run", "100644", bytes) };
        var cases = new[]
        {
            // Root and each logical parent are part of the physical archive
            // inventory; neither may be silently inferred from a file entry.
            GzipTar(DirectoryEntry("fixture-root/bin/"),
                FileEntry("fixture-root/bin/run", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/bin/run", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                DirectoryEntry("fixture-root/"),
                DirectoryEntry("fixture-root/bin/"),
                FileEntry("fixture-root/bin/run", bytes, 0x1b4)),
            Gzip(RawTarDirectoryWithPayload("fixture-root", Array.Empty<byte>())),
            Gzip(RawTarDirectoryWithPayload("fixture-root//", Array.Empty<byte>())),
            GzipTar(DirectoryEntry("fixture-root/"),
                DirectoryEntry("fixture-root/bin/"),
                FileEntry("fixture-root/bin/run/", bytes, 0x1b4)),
        };

        foreach (var archive in cases)
        {
            var attempt = await StageHeadArchive(expected, archive);
            try
            {
                Assert.Null(attempt.Result.Value);
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch,
                    attempt.Result.Failure);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    [Fact]
    public async Task DecodedArchiveLimitMapsToUnsupportedSizeInSnapshotTransport()
    {
        var invocation = await AuthorizedInvocation();
        var parent = CreateTemporaryDirectory();
        var budget = ProductionBudget();
        var staging = ReviewedSnapshotTestAccess.Staging(parent, budget);
        try
        {
            using var transport = ReviewedSnapshotTestAccess.Transport(
                invocation, budget, new ArchiveReadFailureTransport(
                    ActionHostGitArchiveReadFailure.DecodedLimitExceeded));
            var result = await transport.StageHeadRegularBlobsAsync(
                [ArchiveEntry("README.md", "100644", "trusted"u8.ToArray())],
                staging,
                CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
                result.Failure);
            Assert.True(budget.TryGetRemaining(out var remaining));
            Assert.Equal(ReviewedContentLimits.GitObjectRequests - 2,
                remaining!.Requests);
        }
        finally
        {
            staging.Cleanup();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsSizeAndRequestLimitWithoutBlobFallback()
    {
        var bytes = "trusted"u8.ToArray();
        var archive = GzipTar(DirectoryEntry("fixture-root/"),
            FileEntry("fixture-root/README.md", bytes, 0x1b4));
        var oversize = new ReviewedHeadArchiveEntry("README.md", "100644",
            GitBlobSha(bytes), ReviewedContentLimits.HeadBlobBytes + 1);
        var beforeSend = await StageHeadArchive([oversize], archive);
        try
        {
            Assert.Equal(ReviewedGitObjectFailure.InvalidRequest,
                beforeSend.Result.Failure);
            Assert.Empty(beforeSend.Handler.Requests);
        }
        finally
        {
            Cleanup(beforeSend);
        }

        var requestLimited = await StageHeadArchive(
            [ArchiveEntry("README.md", "100644", bytes)], archive,
            maximumRequests: 1);
        try
        {
            Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
                requestLimited.Result.Failure);
            Assert.Empty(requestLimited.Handler.Requests);
        }
        finally
        {
            Cleanup(requestLimited);
        }
    }

    [Fact]
    public async Task SameSizePostStageTamperProducesNoBytes()
    {
        var staged = await Stage("trusted"u8.ToArray());
        try
        {
            var stream = ReviewedSnapshotTestAccess.StagedStream(staged.Blob);
            await RandomAccess.WriteAsync(
                stream.SafeFileHandle,
                "altered"u8.ToArray(),
                0,
                CancellationToken.None);
            using var destination = new MemoryStream();

            Assert.False(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Empty(destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
        }
        finally
        {
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredSharedDeadlinePreventsStagedCopy()
    {
        var time = new ManualTimeProvider();
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            time);
        var staged = await Stage("trusted"u8.ToArray(), budget);
        try
        {
            time.Advance(
                ReviewedContentLimits.AcquisitionAndMaterializationTimeout);
            using var destination = new MemoryStream();

            Assert.False(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Empty(destination.ToArray());
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxSymlinkPathCannotRedirectHandleBackedBlob()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var bytes = "trusted"u8.ToArray();
        var staged = await Stage(bytes);
        try
        {
            var path = ReviewedSnapshotTestAccess.StagedPath(staged.Blob);
            var target = Path.Join(staged.Parent, "symlink-target");
            await File.WriteAllBytesAsync(target, bytes);
            Assert.False(File.Exists(path));
            File.CreateSymbolicLink(path, target);

            using var destination = new MemoryStream();
            Assert.True(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Equal(bytes, destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
            Assert.True(File.Exists(path));
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxFifoPathCannotBlockOrRedirectHandleBackedBlob()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var staged = await Stage("trusted"u8.ToArray());
        try
        {
            var path = ReviewedSnapshotTestAccess.StagedPath(staged.Blob);
            Assert.False(File.Exists(path));
            Assert.Equal(0, MakeFifo(path, Convert.ToUInt32("600", 8)));
            using var destination = new MemoryStream();

            var copied = await staged.Blob.CopyVerifiedToAsync(
                    destination,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(copied);
            Assert.Equal("trusted"u8.ToArray(), destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
            Assert.True(File.Exists(path));
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task MissingTruncatedAndDuplicatePropertiesFailClosed()
    {
        var invocation = await AuthorizedInvocation();
        var sha = new string('2', 40);
        foreach (var body in new[]
        {
            $$"""{"sha":"{{sha}}","tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":false,"truncated":false,"tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":true,"tree":[]}""",
        })
        {
        using var transport = ReviewedSnapshotTestAccess.Transport(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse(body)));
            var result = await transport.GetTreeAsync(
                sha,
                CancellationToken.None);
            Assert.Null(result.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                result.Failure);
        }
    }

    [Fact]
    public async Task CommitAndTreeResponsesMustMatchTheRequestedObjectSha()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('4', 40);
        using (var commitTransport =
               ReviewedSnapshotTestAccess.Transport(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonResponse($$"""
                       {
                         "sha": "{{new string('5', 40)}}",
                         "tree": { "sha": "{{treeSha}}" }
                       }
                       """))))
        {
            var commit = await commitTransport.GetCommitAsync(
                CancellationToken.None);
            Assert.Null(commit.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                commit.Failure);
        }

        using var treeTransport =
            ReviewedSnapshotTestAccess.Transport(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse($$"""
                    {
                      "sha": "{{new string('6', 40)}}",
                      "truncated": false,
                      "tree": []
                    }
                    """)));
        var tree = await treeTransport.GetTreeAsync(
            treeSha,
            CancellationToken.None);
        Assert.Null(tree.Value);
        Assert.Equal(ReviewedGitObjectFailure.InvalidResponse, tree.Failure);
    }

    [Fact]
    public async Task ObjectResponseByteBoundaryAcceptsCapAndRejectsCapPlusOne()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('7', 40);
        var json = Encoding.UTF8.GetBytes($$$"""
            {"sha":"{{{ActionHostAuthorizationScenario.HeadSha}}}","tree":{"sha":"{{{treeSha}}}"}}
            """);
        var atCap = new byte[ReviewedContentLimits.GitObjectResponseBytes];
        json.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', json.Length,
            atCap.Length - json.Length);
        using (var accepted = ReviewedSnapshotTestAccess.Transport(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonBytesResponse(atCap))))
        {
            var result = await accepted.GetCommitAsync(
                CancellationToken.None);
            Assert.NotNull(result.Value);
        }

        var overCap = new byte[ReviewedContentLimits.GitObjectResponseBytes + 1];
        using var rejected = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ProductionBudget(),
            new CapturingHandler(_ => JsonBytesResponse(overCap)));
        var overflow = await rejected.GetCommitAsync(CancellationToken.None);
        Assert.Null(overflow.Value);
        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
    }

    [Fact]
    public async Task RequestOverflowIsRejectedBeforeASecondSend()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('3', 40);
        var handler = new CapturingHandler(_ => JsonResponse($$"""
            {
              "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
              "tree": { "sha": "{{treeSha}}" }
            }
            """));
        var budget = ReviewedSnapshotTestAccess.Budget(
            1,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            TimeSpan.FromMinutes(5),
            TimeProvider.System);
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);

        Assert.NotNull((await transport.GetCommitAsync(
            CancellationToken.None)).Value);
        var overflow = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BudgetDeadlineCancelsStalledResponseHeaders()
    {
        var invocation = await AuthorizedInvocation();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ShortDeadlineBudget(),
            new StallingHeadersHandler());
        var stopwatch = Stopwatch.StartNew();

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize, result.Failure);
        Assert.Null(result.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BudgetDeadlineCancelsStalledResponseBody()
    {
        var invocation = await AuthorizedInvocation();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ShortDeadlineBudget(),
            new CapturingHandler(_ => StallingJsonResponse()));
        var stopwatch = Stopwatch.StartNew();

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize, result.Failure);
        Assert.Null(result.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IncompleteRateEvidenceIsNotRetriedAndReturnsNoValue()
    {
        var invocation = await AuthorizedInvocation();
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)429));
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ProductionBudget(),
            handler);

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ReviewedGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SourceGeneratedJsonRootsExistWithoutReflectionFallback()
    {
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitCommitDocument);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitTreeDocument);
        Assert.False(ActionHostGitObjectJsonContext.Default.Options
            .PropertyNameCaseInsensitive);
        Assert.False(ActionHostGitObjectJsonContext.Default.Options
            .AllowDuplicateProperties);
    }

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizedInvocation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private static ActionHostGitHubToken Token()
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            "token-canary",
            out var token));
        return token!;
    }

    private static ReviewedContentBudget ProductionBudget() =>
        ReviewedSnapshotTestAccess.ProductionBudget();

    private static ReviewedContentBudget ShortDeadlineBudget() =>
        ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System);

    private static ReviewedHeadArchiveEntry ArchiveEntry(
        string path,
        string mode,
        byte[] bytes) => new(path, mode, GitBlobSha(bytes), bytes.Length);

    private static async Task<ArchiveAttempt> StageHeadArchive(
        IReadOnlyList<ReviewedHeadArchiveEntry> entries,
        byte[] archive,
        int maximumRequests = ReviewedContentLimits.GitObjectRequests)
    {
        var invocation = await AuthorizedInvocation();
        var parent = CreateTemporaryDirectory();
        var budget = ReviewedSnapshotTestAccess.Budget(
            maximumRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            TimeProvider.System);
        var staging = ReviewedSnapshotTestAccess.Staging(parent, budget);
        var handler = new CapturingHandler(request =>
        {
            if (StringComparer.Ordinal.Equals(
                    request.RequestUri!.Host, "api.github.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = new Uri(
                            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" +
                            ActionHostAuthorizationScenario.HeadSha),
                    },
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/x-gzip");
            return response;
        });
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation, Token(), budget, handler);
        var result = await transport.StageHeadRegularBlobsAsync(
            entries, staging, CancellationToken.None);
        return new ArchiveAttempt(result, handler, staging, budget, parent);
    }

    private static async Task AssertStagedBytes(
        ReviewedStagedBlob staged,
        byte[] expected)
    {
        using var copied = new MemoryStream();
        Assert.True(await staged.CopyVerifiedToAsync(copied,
            CancellationToken.None));
        Assert.Equal(expected, copied.ToArray());
    }

    private static void Cleanup(ArchiveAttempt attempt)
    {
        attempt.Staging.Cleanup();
        Directory.Delete(attempt.Parent, recursive: true);
    }

    private static TarEntry DirectoryEntry(string name) =>
        new PaxTarEntry(TarEntryType.Directory, name)
        {
            Mode = (UnixFileMode)0x1fd,
        };

    private static TarEntry FileEntry(
        string name,
        byte[] bytes,
        int mode) => new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes, writable: false),
            Mode = (UnixFileMode)mode,
        };

    private static TarEntry SymlinkEntry(
        string name,
        string target,
        int mode) => new PaxTarEntry(TarEntryType.SymbolicLink, name)
        {
            LinkName = target,
            Mode = (UnixFileMode)mode,
        };

    private static byte[] GzipTar(params TarEntry[] entries) =>
        GzipTar((IEnumerable<TarEntry>)entries);

    private static byte[] GzipTar(IEnumerable<TarEntry> entries)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize,
                   leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }

        return archive.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return archive.ToArray();
    }

    private static byte[] RawTarDirectoryWithPayload(string name, byte[] payload)
    {
        var header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, 0x1fd);
        WriteOctal(header, 124, 12, payload.Length);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'5';
        WriteAscii(header, 257, 6, "ustar");
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        var checksum = header.Sum(static value => value);
        var checksumText = checksum.ToString("D6", CultureInfo.InvariantCulture) + "\0 ";
        WriteAscii(header, 148, 8, checksumText);

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(payload);
        var padding = (512 - payload.Length % 512) % 512;
        if (padding > 0)
        {
            stream.Write(new byte[padding]);
        }

        stream.Write(new byte[1024]);
        return stream.ToArray();
    }

    private static byte[] RawTarDirectoryWithDeclaredLength(
        string name,
        int declaredLength)
    {
        var header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, 0x1fd);
        WriteOctal(header, 124, 12, declaredLength);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'5';
        WriteAscii(header, 257, 6, "ustar");
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        var checksum = header.Sum(static value => value);
        WriteAscii(header, 148, 8,
            checksum.ToString("D6", CultureInfo.InvariantCulture) + "\0 ");

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(new byte[1024]);
        return stream.ToArray();
    }

    private static (IReadOnlyList<ReviewedHeadArchiveEntry> Entries,
        byte[] Archive) BuildTransportCapArchive(bool extraDirectory)
    {
        var bytes = "x"u8.ToArray();
        // This is an independent archive transport ceiling, not a derivation
        // from the Git graph's unique-object budget.  More than 4,000 logical
        // directories are intentionally admitted here.
        var directories = 5_000;
        var files = ReviewedContentLimits.HeadArchiveMembers - 1 - directories;
        var entries = new List<ReviewedHeadArchiveEntry>(
            files);
        var members = new List<TarEntry>(
            ReviewedContentLimits.HeadArchiveMembers +
            (extraDirectory ? 1 : 0))
        {
            DirectoryEntry("fixture-root/"),
        };
        for (var directory = 0; directory < directories; directory++)
        {
            members.Add(DirectoryEntry(
                "fixture-root/d" + directory.ToString("D4", CultureInfo.InvariantCulture) +
                "/"));
        }

        for (var file = 0; file < files; file++)
        {
            var directory = file % directories;
            var path = "d" + directory.ToString("D4", CultureInfo.InvariantCulture) +
                "/f" + file.ToString("D5", CultureInfo.InvariantCulture);
            entries.Add(ArchiveEntry(path, "100644", bytes));
            members.Add(FileEntry("fixture-root/" + path, bytes, 0x1b4));
        }

        if (extraDirectory)
        {
            members.Add(DirectoryEntry("fixture-root/excess/"));
        }

        return (entries, GzipTar(members));
    }

    private static void WriteAscii(
        byte[] destination,
        int offset,
        int length,
        string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Assert.True(bytes.Length <= length);
        bytes.CopyTo(destination, offset);
    }

    private static void WriteOctal(
        byte[] destination,
        int offset,
        int length,
        int value)
    {
        var text = Convert.ToString(value, 8)!.PadLeft(length - 1, '0') + "\0";
        WriteAscii(destination, offset, length, text);
    }

    private static async Task<(
        ReviewedStagedBlob Blob,
        ReviewedBlobStagingLease Lease,
        string Parent)> Stage(
            byte[] bytes,
            ReviewedContentBudget? suppliedBudget = null)
    {
        var invocation = await AuthorizedInvocation();
        var parent = CreateTemporaryDirectory();
        var budget = suppliedBudget ?? ProductionBudget();
        var lease = ReviewedSnapshotTestAccess.Staging(parent, budget);
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            new CapturingHandler(_ => JsonResponse(BlobResponse(bytes))));
        var result = await transport.StageBlobAsync(
            GitBlobSha(bytes),
            bytes.Length,
            lease,
            CancellationToken.None);
        return (Assert.IsType<ReviewedStagedBlob>(result.Value), lease, parent);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage JsonBytesResponse(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json")
        {
            CharSet = "utf-8",
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static HttpResponseMessage StallingJsonResponse()
    {
        var content = new StreamContent(new StallingReadStream());
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static string BlobResponse(byte[] bytes) => $$"""
        {"sha":"{{GitBlobSha(bytes)}}","size":{{bytes.Length}},"encoding":"base64","content":"{{Convert.ToBase64String(bytes)}}"}
        """;

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "apr-h4-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ArchiveAttempt(
        ReviewedGitObjectResult<ReviewedHeadArchiveBatch> Result,
        CapturingHandler Handler,
        ReviewedBlobStagingLease Staging,
        ReviewedContentBudget Budget,
        string Parent);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        internal List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));
            return Task.FromResult(_response(request));
        }
    }

    private sealed class ArchiveReadFailureTransport :
        IActionHostGitObjectTransport
    {
        private readonly ActionHostGitArchiveReadFailure _failure;

        internal ArchiveReadFailureTransport(ActionHostGitArchiveReadFailure failure)
        {
            _failure = failure;
        }

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<ActionHostGitCommitObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest));

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<ActionHostGitTreeObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest));

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<ActionHostGitBlobObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest));

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<ActionHostGitArchiveReader>
                .Success(new ArchiveReadFailureReader(_failure), 0));

        public void Dispose()
        {
        }
    }

    private sealed class ArchiveReadFailureReader : ActionHostGitArchiveReader
    {
        private readonly ActionHostGitArchiveReadFailure _failure;

        internal ArchiveReadFailureReader(ActionHostGitArchiveReadFailure failure)
        {
            _failure = failure;
        }

        internal override int CapturedResponseBytes => 0;

        internal override Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(
            CancellationToken cancellationToken) =>
            Task.FromException<ActionHostGitArchiveEntry?>(
                new ActionHostGitArchiveReadException(_failure));

        public override void Dispose()
        {
        }
    }

    private sealed class StallingHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan time) =>
            _timestamp = checked(_timestamp + time.Ticks);
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [LibraryImport(
        "libc",
        EntryPoint = "mkfifo",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeFifo(string path, uint mode);

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string Origin,
        string Query,
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.RequestUri.GetLeftPart(UriPartial.Authority),
            request.RequestUri.Query,
            request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(' ', header.Value),
                StringComparer.OrdinalIgnoreCase));
    }
}
