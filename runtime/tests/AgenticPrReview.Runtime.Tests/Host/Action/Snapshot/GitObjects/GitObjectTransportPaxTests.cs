using System.Formats.Tar;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.GitObjects;

public sealed partial class GitObjectTransportTests
{
    [Theory]
    [InlineData(false, "not-a-source-identity")]
    [InlineData(true, "not-a-source-identity")]
    [InlineData(true, "ffffffffffffffffffffffffffffffffffffffff")]
    public async Task HeadArchiveGlobalCommentDoesNotReplaceVerifiedMemberIdentity(
        bool globalComment, string comment)
    {
        var bytes = "verified"u8.ToArray();
        var longPath = new string('a', 120) + ".md";
        var target = System.Text.Encoding.UTF8.GetBytes(longPath);
        var members = new List<TarEntry>();
        if (globalComment) members.Add(GlobalComment(comment));
        members.AddRange([
            DirectoryEntry("fixture-root/"),
            FileEntry("fixture-root/" + longPath, bytes, 0x1b4),
            FileEntry("fixture-root/run", bytes, 0x1fd),
            SymlinkEntry("fixture-root/link", longPath, 0x1ff),
        ]);
        var attempt = await StageHeadArchive([
            ArchiveEntry(longPath, "100644", bytes),
            ArchiveEntry("run", "100755", bytes),
            ArchiveEntry("link", "120000", target),
        ], GzipTar(members));
        try
        {
            Assert.Equal(ReviewedGitObjectFailure.None, attempt.Result.Failure);
            var batch = Assert.IsType<ReviewedHeadArchiveBatch>(attempt.Result.Value);
            await AssertStagedBytes(batch.StagedBySha[GitBlobSha(bytes)], bytes);
            Assert.Single(batch.StagedBySha);
            Assert.DoesNotContain(GitBlobSha(target), batch.StagedBySha.Keys);
        }
        finally
        {
            Cleanup(attempt);
        }
    }

    [Fact]
    public async Task HeadArchiveRejectsRepeatedLateAndAuthoritativeGlobalMetadata()
    {
        var bytes = "trusted"u8.ToArray();
        var cases = new[]
        {
            GzipTar(GlobalComment(), GlobalComment(),
                DirectoryEntry("fixture-root/"), FileEntry("fixture-root/file", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"), GlobalComment(),
                FileEntry("fixture-root/file", bytes, 0x1b4)),
            GzipTar(DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", bytes, 0x1b4), GlobalComment()),
            GzipTar(new PaxGlobalExtendedAttributesTarEntry([
                    new("comment", "ignored"), new("path", "fixture-root/file")]),
                DirectoryEntry("fixture-root/"), FileEntry("fixture-root/file", bytes, 0x1b4)),
            GzipTar(new PaxGlobalExtendedAttributesTarEntry([new("mtime", "0")]),
                DirectoryEntry("fixture-root/"), FileEntry("fixture-root/file", bytes, 0x1b4)),
            GzipTar(GlobalComment()),
        };
        foreach (var archive in cases)
        {
            var attempt = await StageHeadArchive([ArchiveEntry("file", "100644", bytes)], archive);
            try
            {
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch, attempt.Result.Failure);
                Assert.Null(attempt.Result.Value);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    [Fact]
    public async Task HeadArchiveGlobalCommentDoesNotAdmitInvalidFollowingMembers()
    {
        var bytes = "trusted"u8.ToArray();
        var cases = new[]
        {
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/../file", bytes, 0x1b4)),
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", bytes, 0x1a4)),
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", "changed"u8.ToArray(), 0x1b4)),
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", "wrong length"u8.ToArray(), 0x1b4)),
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", bytes, 0x1b4),
                FileEntry("fixture-root/extra", bytes, 0x1b4)),
            GzipTar(GlobalComment(), DirectoryEntry("fixture-root/"),
                FileEntry("fixture-root/file", bytes, 0x1b4),
                FileEntry("fixture-root/file", bytes, 0x1b4)),
        };
        foreach (var archive in cases)
        {
            var attempt = await StageHeadArchive([ArchiveEntry("file", "100644", bytes)], archive);
            try
            {
                Assert.Equal(ReviewedGitObjectFailure.IdentityMismatch, attempt.Result.Failure);
                Assert.Null(attempt.Result.Value);
            }
            finally
            {
                Cleanup(attempt);
            }
        }
    }

    private static PaxGlobalExtendedAttributesTarEntry GlobalComment(string value = "ignored") =>
        new([new("comment", value)]);

    [Fact]
    public async Task HeadArchiveAcceptsRealGitArchiveGlobalComment()
    {
        // Produced by git archive --format=tar.gz --prefix=fixture-root/ HEAD
        // from a two-file repository: README.md (100644), bin/run.sh (100755),
        // both containing the exact CRLF bytes asserted below.
        // Keep this real producer sample: TarWriter-only fixtures omitted its
        // global PAX comment and failed to expose the codeload regression.
        var archive = Convert.FromBase64String(
            "H4sIAAAAAAAAA+3VQW+CMBgGYM4m/geWHTyNFdqP4mGHJTM77bLDTktMhSJNFExF488fOC9gnMkCLs73uZSkJC28/b6u1G46XxQztZhmWiXaOt1jlTAM92OlPVaTwvEpEGEQMCnr93xBATnzHvZyZLMula2WtEVR/vTeufn2x10JCty4WC51Xj7RWKfEuIplVElkpBSlJPxEJEHIfS5DNmYRjwZ/vWfoTmp25cbqh/p4P/a0Rl0PUtLp+q+eW/XPSTCHetpPw43XfyP/98nzy9vEWybdrvHd/8Xp/Lls50/Ml85FfuKN5/+hrUmNTtxXU7rKxpnZavdwKLwhWv1/16j/mcn7uAN+0f8F99H/L+Eof7vJvXXW6Rpn8xdH+ZMQhP5/Afd3+9TX2XCwsiYvU3e0PVwJn/kIFwAAAAAAAAAAAAAAAMC1+QIr0xTqACgAAA==");
        var readme = "Verified Git archive fixture.\r\n"u8.ToArray();
        var script = "#!/bin/sh\r\nprintf 'verified\\n'\r\n"u8.ToArray();
        var attempt = await StageHeadArchive(
            [ArchiveEntry("README.md", "100644", readme),
             ArchiveEntry("bin/run.sh", "100755", script)], archive);
        try
        {
            Assert.Equal(ReviewedGitObjectFailure.None, attempt.Result.Failure);
            var batch = Assert.IsType<ReviewedHeadArchiveBatch>(attempt.Result.Value);
            Assert.Equal(2, batch.StagedBySha.Count);
            await AssertStagedBytes(batch.StagedBySha[GitBlobSha(readme)], readme);
            await AssertStagedBytes(batch.StagedBySha[GitBlobSha(script)], script);
            Assert.Equal(2, attempt.Handler.Requests.Count);
        }
        finally
        {
            Cleanup(attempt);
        }
    }
}
