using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.GitHub;

public sealed partial class ActionHostGitObjectTransportTests
{
    [Fact]
    public async Task HeadArchiveUsesOnlyPinnedHttpsCodeloadRedirectWithoutBearer()
    {
        var sha = new string('a', 40);
        var archive = GzipTar("agentic-pr-review-" + sha + "/file", "ok"u8.ToArray());
        var handler = new CapturingHandler(request =>
        {
            if (StringComparer.Ordinal.Equals(request.RequestUri!.Host, "api.github.com"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + sha);
                return redirect;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-gzip");
            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        using var reader = Assert.IsType<GitHubCodeloadArchiveReader>(result.Value);
        var entry = Assert.IsType<ActionHostGitArchiveEntry>(
            await reader.GetNextEntryAsync(CancellationToken.None));
        Assert.Equal("agentic-pr-review-" + sha + "/file", entry.Name);
        Assert.Equal(ActionHostGitArchiveEntryType.RegularFile, entry.EntryType);
        using var copied = new MemoryStream();
        Assert.NotNull(entry.DataStream);
        await entry.DataStream!.CopyToAsync(copied);
        Assert.Equal("ok"u8.ToArray(), copied.ToArray());
        Assert.Null(await reader.GetNextEntryAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer token-canary", handler.Requests[0].Header("Authorization"));
        Assert.DoesNotContain("Authorization", handler.Requests[1].Headers.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("https://codeload.github.com", new Uri(handler.Requests[1].Uri).GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public async Task HeadArchiveAcceptsSuccessfulResponsesWithValidRateHeaders()
    {
        var sha = new string('e', 40);
        var archive = GzipTar("agentic-pr-review-" + sha + "/file",
            "ok"u8.ToArray());
        var handler = new CapturingHandler(request =>
        {
            var response = StringComparer.Ordinal.Equals(
                request.RequestUri!.Host, "api.github.com")
                ? RedirectResponse(sha)
                : ArchiveResponse(archive);
            AddValidRateHeaders(response);
            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        using var reader = Assert.IsType<GitHubCodeloadArchiveReader>(result.Value);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadArchiveRejectsRetryAfterOnSuccessfulResponses(
        bool redirectResponse)
    {
        var sha = new string('f', 40);
        var handler = new CapturingHandler(request =>
        {
            var response = StringComparer.Ordinal.Equals(
                request.RequestUri!.Host, "api.github.com")
                ? RedirectResponse(sha)
                : ArchiveResponse(GzipTar("fixture/file", "ok"u8.ToArray()));
            if (StringComparer.Ordinal.Equals(request.RequestUri.Host,
                    redirectResponse ? "api.github.com" : "codeload.github.com"))
            {
                response.Headers.TryAddWithoutValidation("retry-after", "1");
            }

            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(redirectResponse ? 1 : 2, handler.Requests.Count);
    }

    [Fact]
    public async Task HeadArchiveDisposesCodeloadResponseWhenSuccessfulRateHeadersAreRejected()
    {
        var sha = new string('1', 40);
        var content = new FaultingArchiveContent();
        var handler = new CapturingHandler(request =>
        {
            if (StringComparer.Ordinal.Equals(request.RequestUri!.Host,
                    "api.github.com"))
            {
                return RedirectResponse(sha);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
            response.Headers.TryAddWithoutValidation("retry-after", "1");
            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.True(content.Disposed);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task HeadArchiveRejectsMalformedOrContradictoryRateHeadersOnSuccessfulResponses(
        bool redirectResponse,
        bool contradictory)
    {
        var sha = new string('0', 40);
        var handler = new CapturingHandler(request =>
        {
            var response = StringComparer.Ordinal.Equals(
                request.RequestUri!.Host, "api.github.com")
                ? RedirectResponse(sha)
                : ArchiveResponse(GzipTar("fixture/file", "ok"u8.ToArray()));
            if (StringComparer.Ordinal.Equals(request.RequestUri.Host,
                    redirectResponse ? "api.github.com" : "codeload.github.com"))
            {
                if (contradictory)
                {
                    response.Headers.TryAddWithoutValidation(
                        "x-ratelimit-remaining", "2");
                    response.Headers.TryAddWithoutValidation(
                        "x-ratelimit-limit", "1");
                }
                else
                {
                    response.Headers.TryAddWithoutValidation(
                        "x-ratelimit-remaining", "not-a-number");
                }
            }

            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(redirectResponse ? 1 : 2, handler.Requests.Count);
    }

    [Fact]
    public async Task HeadArchiveRejectsUntrustedOrUnpinnedRedirectAndMapsExhausted403ToRateLimit()
    {
        var sha = new string('b', 40);
        var rejectedLocations = new[]
        {
            "https://example.test/archive",
            "https://codeload.github.com/Other/agentic-pr-review/legacy.tar.gz/" + sha,
            "https://codeload.github.com/SolusQuest/other/legacy.tar.gz/" + sha,
            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + new string('c', 40),
            "https://codeload.github.com/SolusQuest/agentic-pr-review/tar.gz/" + sha,
            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + sha + "/extra",
            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + sha + "?x=1",
            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + sha + "#fragment",
            "https://user@codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" + sha,
        };
        foreach (var location in rejectedLocations)
        {
            var rejected = new CapturingHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(location);
                return response;
            });
            using var transport = ActionHostGitObjectTransport.CreateForTesting("token-canary", rejected);
            var result = await transport.GetHeadArchiveAsync("SolusQuest/agentic-pr-review", sha, CancellationToken.None);
            Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
            Assert.Single(rejected.Requests);
        }

        var limited = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
            return response;
        });
        using var limitedTransport = ActionHostGitObjectTransport.CreateForTesting("token-canary", limited);
        var rate = await limitedTransport.GetCommitObjectAsync("SolusQuest/agentic-pr-review", sha, CancellationToken.None);
        Assert.Equal(ActionHostGitObjectFailure.RateLimited, rate.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "0", "1900000000",
        "RateLimited")]
    [InlineData(HttpStatusCode.Forbidden, "not-a-number", null,
        "InvalidResponse")]
    [InlineData(HttpStatusCode.TooManyRequests, null, null,
        "InvalidResponse")]
    [InlineData(HttpStatusCode.TooManyRequests, "not-a-number", null,
        "InvalidResponse")]
    public async Task ObjectStatusDecisionUsesTheSharedRateClassification(
        HttpStatusCode status,
        string? remaining,
        string? reset,
        string expected)
    {
        var sha = new string('d', 40);
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (remaining is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-remaining", remaining);
            }
            if (reset is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-reset", reset);
            }

            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Equal(
            Enum.Parse<ActionHostGitObjectFailure>(expected),
            result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HeadArchiveDisposesCodeloadResponseWhenStreamAcquisitionFails()
    {
        var sha = new string('c', 40);
        var content = new FaultingArchiveContent();
        var handler = new CapturingHandler(request =>
        {
            if (StringComparer.Ordinal.Equals(
                    request.RequestUri!.Host, "api.github.com"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" +
                    sha);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.TransportFailure, result.Failure);
        Assert.True(content.Disposed);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HeadArchiveRejectsACompressedResponseOverTheSixteenMiBCap()
    {
        var sha = new string('d', 40);
        var handler = new CapturingHandler(request =>
        {
            if (StringComparer.Ordinal.Equals(request.RequestUri!.Host,
                    "api.github.com"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" +
                    sha);
                return redirect;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[
                    ReviewedContentLimits.GitObjectResponseBytes + 1]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/x-gzip");
            return response;
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary", handler);

        var result = await transport.GetHeadArchiveAsync(
            "SolusQuest/agentic-pr-review", sha, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge,
            result.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CodeloadReaderEnforcesCompressedAndDecodedCapsAtTheExactBoundary()
    {
        var raw = Tar("fixture-root/file", new byte[513]);
        var compressed = Gzip(raw);

        int decodedAtCap;
        using (var probe = CreateArchiveReader(compressed,
                   maximumCompressedBytes: compressed.Length,
                   maximumDecodedBytes: raw.Length))
        {
            await DrainArchiveAsync(probe);
            decodedAtCap = probe.CapturedDecodedBytes;
            Assert.InRange(decodedAtCap, 1, raw.Length);
            Assert.Equal(compressed.Length, probe.CapturedResponseBytes);
        }

        using (var atCap = CreateArchiveReader(compressed,
                   maximumCompressedBytes: compressed.Length,
                   maximumDecodedBytes: decodedAtCap))
        {
            await DrainArchiveAsync(atCap);
            Assert.Equal(decodedAtCap, atCap.CapturedDecodedBytes);
            Assert.Equal(compressed.Length, atCap.CapturedResponseBytes);
        }

        using (var decodedCapPlusOne = CreateArchiveReader(compressed,
                   maximumCompressedBytes: compressed.Length,
                   maximumDecodedBytes: decodedAtCap - 1))
        {
            var exception = await Assert.ThrowsAsync<ActionHostGitArchiveReadException>(async () =>
                await DrainArchiveAsync(decodedCapPlusOne));
            Assert.Equal(ActionHostGitArchiveReadFailure.DecodedLimitExceeded,
                exception.Failure);
        }

        using var compressedCapPlusOne = CreateArchiveReader(compressed,
            maximumCompressedBytes: compressed.Length - 1,
            maximumDecodedBytes: raw.Length);
        var compressedException = await Assert.ThrowsAsync<ActionHostGitArchiveReadException>(async () =>
            await DrainArchiveAsync(compressedCapPlusOne));
        Assert.Equal(ActionHostGitArchiveReadFailure.CompressedLimitExceeded,
            compressedException.Failure);
    }

    [Fact]
    public async Task CodeloadReaderAcceptsZeroTarPaddingAfterEndMarkers()
    {
        var tar = Tar("fixture-root/file", "ok"u8.ToArray());
        var padded = new byte[tar.Length + 1024];
        tar.CopyTo(padded, 0);
        var compressed = Gzip(padded);
        using var reader = CreateArchiveReader(compressed,
            maximumCompressedBytes: compressed.Length,
            maximumDecodedBytes: padded.Length);

        await DrainArchiveAsync(reader);

        Assert.Equal(padded.Length, reader.CapturedDecodedBytes);
        Assert.Equal(compressed.Length, reader.CapturedResponseBytes);
    }

    [Fact]
    public async Task CodeloadReaderRejectsNonZeroDecodedTrailingData()
    {
        var tar = Tar("fixture-root/file", "ok"u8.ToArray());
        var trailing = new byte[tar.Length + 1];
        tar.CopyTo(trailing, 0);
        trailing[^1] = 0x7f;
        var compressed = Gzip(trailing);
        using var reader = CreateArchiveReader(compressed,
            maximumCompressedBytes: compressed.Length,
            maximumDecodedBytes: trailing.Length);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await DrainArchiveAsync(reader));
    }

    [Fact]
    public async Task CodeloadReaderRejectsTrailingCompressedBytes()
    {
        var raw = Tar("fixture-root/file", "ok"u8.ToArray());
        var gzip = Gzip(raw);
        var trailing = new byte[gzip.Length + 1];
        gzip.CopyTo(trailing, 0);
        trailing[^1] = 0x7f;
        using var reader = CreateArchiveReader(trailing,
            maximumCompressedBytes: trailing.Length,
            maximumDecodedBytes: raw.Length);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await DrainArchiveAsync(reader));
    }

    [Fact]
    public async Task CodeloadReaderRejectsASecondGzipMember()
    {
        var raw = Tar("fixture-root/file", "ok"u8.ToArray());
        var first = Gzip(raw);
        var second = Gzip([0]);
        var concatenated = new byte[first.Length + second.Length];
        first.CopyTo(concatenated, 0);
        second.CopyTo(concatenated, first.Length);
        using var reader = CreateArchiveReader(concatenated,
            maximumCompressedBytes: concatenated.Length,
            maximumDecodedBytes: raw.Length + 1);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await DrainArchiveAsync(reader));
    }

    [Fact]
    public async Task ExactObjectEndpointsAreGetOnlyAndTreeIsNonRecursive()
    {
        var commitSha = new string('a', 40);
        var treeSha = new string('b', 40);
        var parentSha = new string('c', 40);
        var blobBytes = Encoding.UTF8.GetBytes("policy\n");
        var blobSha = GitBlobSha(blobBytes);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                $"{{\"sha\":\"{commitSha}\",\"tree\":{{\"sha\":\"{treeSha}\"}}," +
                $"\"parents\":[{{\"sha\":\"{parentSha}\"}}]}}"),
            JsonResponse(
                $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[" +
                $"{{\"path\":\"config.json\",\"mode\":\"100644\"," +
                $"\"type\":\"blob\",\"sha\":\"{blobSha}\"," +
                $"\"size\":{blobBytes.Length}}}]}}"),
            JsonResponse(BlobResponse(blobBytes, blobSha)),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var commit = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review",
            commitSha,
            CancellationToken.None);
        var tree = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);
        var blob = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            blobSha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.NotNull(commit.Value);
        Assert.Equal([parentSha], commit.Value.ParentShas);
        Assert.NotNull(tree.Value);
        Assert.Equal(blobBytes.Length,
            Assert.Single(tree.Value!.Entries).Size);
        Assert.Equal(blobBytes, blob.Value!.Bytes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer token-canary",
                request.Header("Authorization"));
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.UserAgent,
                request.Header("User-Agent"));
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.Accept,
                request.Header("Accept"));
        });
        Assert.EndsWith("/git/commits/" + commitSha,
            handler.Requests[0].Uri, StringComparison.Ordinal);
        Assert.EndsWith("/git/trees/" + treeSha,
            handler.Requests[1].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("recursive", handler.Requests[1].Uri,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/git/blobs/" + blobSha,
            handler.Requests[2].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("token-canary", transport.ToString(),
            StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MissingRequiredMembers => new()
    {
        { "commit", "{\"tree\":{\"sha\":\"{sha}\"}}" },
        { "commit", "{\"sha\":\"{sha}\"}" },
        { "commit", "{\"sha\":\"{sha}\",\"tree\":{}}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"type\":\"blob\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"mode\":\"100644\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"mode\":\"100644\",\"type\":\"blob\"}]}" },
        { "blob", "{\"size\":0,\"encoding\":\"base64\",\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"encoding\":\"base64\",\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"size\":0,\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"size\":0,\"encoding\":\"base64\"}" },
    };

    public static TheoryData<string> InvalidCommitParents => new()
    {
        "[null]",
        "[{}]",
        "[{\"sha\":\"bad\"}]",
        "[{\"sha\":\"{sha}\"}]",
        "[{\"sha\":\"{other}\"},{\"sha\":\"{other}\"}]",
    };

    [Theory]
    [MemberData(nameof(InvalidCommitParents))]
    public async Task CommitParentsRejectMalformedDuplicateAndSelfDescriptors(
        string parentsTemplate)
    {
        var sha = new string('a', 40);
        var tree = new string('b', 40);
        var other = new string('c', 40);
        var parents = parentsTemplate
            .Replace("{sha}", sha, StringComparison.Ordinal)
            .Replace("{other}", other, StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(
            $"{{\"sha\":\"{sha}\",\"tree\":{{\"sha\":\"{tree}\"}}," +
            $"\"parents\":{parents}}}"));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            CancellationToken.None);

        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Null(result.Value);
    }

    [Theory]
    [MemberData(nameof(MissingRequiredMembers))]
    public async Task ExactObjectDtosRequireEveryExternalMember(
        string kind,
        string template)
    {
        var sha = kind == "blob"
            ? GitBlobSha([])
            : new string('a', 40);
        var body = template.Replace("{sha}", sha,
            StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var failure = kind switch
        {
            "commit" => (await transport.GetCommitObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                CancellationToken.None)).Failure,
            "tree" => (await transport.GetTreeObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                CancellationToken.None)).Failure,
            "blob" => (await transport.GetBlobObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                ActionHostGitBlobReadBudget.TrustedConfig,
                CancellationToken.None)).Failure,
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, failure);
    }

    public static TheoryData<string> IncompleteTrees => new()
    {
        "{\"sha\":\"{sha}\",\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":null,\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":\"false\",\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":true,\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":null}",
        "{\"truncated\":false,\"tree\":[]}",
    };

    [Theory]
    [MemberData(nameof(IncompleteTrees))]
    public async Task TreeCompletenessRequiresExplicitPositiveEvidence(
        string template)
    {
        var treeSha = new string('a', 40);
        var body = template.Replace("{sha}", treeSha,
            StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
    }

    [Fact]
    public async Task ExplicitCompleteEmptyTreeIsAccepted()
    {
        var treeSha = new string('a', 40);
        var handler = new CapturingHandler(_ => JsonResponse(
            $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[]}}"));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Entries);
    }

    [Fact]
    public async Task TreeRejectsRootMismatchAndDuplicateEntryNames()
    {
        var treeSha = new string('a', 40);
        var entrySha = new string('b', 40);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                $"{{\"sha\":\"{entrySha}\",\"truncated\":false,\"tree\":[]}}"),
            JsonResponse(
                $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[" +
                $"{{\"path\":\"same\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{entrySha}\"}}," +
                $"{{\"path\":\"same\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{entrySha}\"}}]}}"),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var mismatched = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);
        var duplicate = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            mismatched.Failure);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            duplicate.Failure);
    }

    public static TheoryData<string> InvalidBase64 => new()
    {
        "YWJj ZA==",
        "YWJj\tZA==",
        "YWJj_ZA=",
        "YWJjZA",
        "YW\rJjZA==",
        "\nYWJjZA==",
        "\r\nYWJjZA==",
    };

    [Theory]
    [MemberData(nameof(InvalidBase64))]
    public async Task BlobRejectsNonCanonicalBase64(string content)
    {
        var sha = new string('a', 40);
        var handler = new CapturingHandler(_ => JsonResponse($$"""
            {"sha":"{{sha}}","size":4,"encoding":"base64","content":{{Json(content)}}}
            """));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task BlobAcceptsCanonicalWrappedBase64(string newline)
    {
        var bytes = Encoding.UTF8.GetBytes("wrapped-policy-bytes");
        var sha = GitBlobSha(bytes);
        var encoded = Convert.ToBase64String(bytes);
        var wrapped = string.Join(newline,
            Enumerable.Range(0, encoded.Length / 4)
                .Select(index => encoded.Substring(index * 4, 4)));
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha, wrapped)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Equal(bytes, result.Value!.Bytes);
    }

    [Fact]
    public async Task BlobDecodedBudgetIsInclusiveAndRejectsPlusOne()
    {
        var acceptedBytes = new byte[16 * 1024];
        Array.Fill<byte>(acceptedBytes, 0x61);
        var rejectedBytes = new byte[acceptedBytes.Length + 1];
        Array.Fill<byte>(rejectedBytes, 0x62);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(BlobResponse(
                acceptedBytes,
                GitBlobSha(acceptedBytes))),
            JsonResponse(BlobResponse(
                rejectedBytes,
                GitBlobSha(rejectedBytes))),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var accepted = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(acceptedBytes),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);
        var rejected = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(rejectedBytes),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Equal(acceptedBytes, accepted.Value!.Bytes);
        Assert.Null(rejected.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            rejected.Failure);
    }

    [Fact]
    public async Task InstructionsDecodedBudgetIsInclusiveAndRejectsPlusOne()
    {
        var acceptedBytes = Enumerable.Repeat((byte)0x63, 64 * 1024)
            .ToArray();
        var rejectedBytes = Enumerable.Repeat(
            (byte)0x64,
            acceptedBytes.Length + 1).ToArray();
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(BlobResponse(
                acceptedBytes,
                GitBlobSha(acceptedBytes))),
            JsonResponse(BlobResponse(
                rejectedBytes,
                GitBlobSha(rejectedBytes))),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var accepted = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(acceptedBytes),
            ActionHostGitBlobReadBudget.TrustedInstructions,
            CancellationToken.None);
        var rejected = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(rejectedBytes),
            ActionHostGitBlobReadBudget.TrustedInstructions,
            CancellationToken.None);

        Assert.Equal(acceptedBytes, accepted.Value!.Bytes);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            rejected.Failure);
    }

    [Fact]
    public async Task SharedEnvelopeCarriesMaximumDecodedHeadBlob()
    {
        Assert.Equal(
            checked((int)ReviewedContentLimits.GitObjectResponseBytes),
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumResponseBytes);
        Assert.Equal(
            checked((int)ReviewedContentLimits.HeadBlobBytes),
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumDecodedBytes);
        Assert.Equal(
            4 * ((checked((int)ReviewedContentLimits.HeadBlobBytes) + 2) / 3),
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumEncodedCharacters);
        Assert.Equal(
            (ActionHostGitBlobReadBudget.MaximumSupported.MaximumResponseBytes -
                1_024 -
                ActionHostGitBlobReadBudget.MaximumSupported
                    .MaximumEncodedCharacters) / 6,
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumWhitespaceCharacters);
        var bytes = new byte[checked((int)ReviewedContentLimits.HeadBlobBytes)];
        Array.Fill<byte>(bytes, 0x5a);
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Equal(bytes.Length, result.Value!.Bytes.Length);
        Assert.InRange(result.CapturedResponseBytes,
            bytes.Length,
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumResponseBytes);
    }

    [Fact]
    public async Task SharedEnvelopeRejectsDecodedHeadBlobCapPlusOne()
    {
        var bytes = new byte[checked(
            (int)ReviewedContentLimits.HeadBlobBytes + 1)];
        Array.Fill<byte>(bytes, 0x5c);
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
    }

    [Fact]
    public async Task MaximumDecodedBlobAcceptsBoundedCanonicalWrapping()
    {
        var bytes = new byte[checked((int)ReviewedContentLimits.HeadBlobBytes)];
        Array.Fill<byte>(bytes, 0x5b);
        var sha = GitBlobSha(bytes);
        var encoded = Convert.ToBase64String(bytes);
        var wrapped = WrapEveryLine(encoded, 76, "\r\n");
        var whitespaceCharacters = wrapped.Length - encoded.Length;
        Assert.True(whitespaceCharacters > 0);
        Assert.True(whitespaceCharacters <=
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumWhitespaceCharacters);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha, wrapped)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Equal(bytes, result.Value!.Bytes);
        Assert.InRange(result.CapturedResponseBytes,
            bytes.Length,
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumResponseBytes);
    }

    [Fact]
    public void Base64WhitespaceBeyondTheResponseDerivedMarginIsRejected()
    {
        var budget = ActionHostGitBlobReadBudget.MaximumSupported;
        var bytes = new byte[checked(3 * (budget.MaximumWhitespaceCharacters + 2))];
        Array.Fill<byte>(bytes, 0x5d);
        var encoded = Convert.ToBase64String(bytes);
        var wrapped = WrapEveryQuantum(encoded);

        Assert.Equal(budget.MaximumWhitespaceCharacters + 1,
            wrapped.Length - encoded.Length);
        Assert.False(ActionHostGitHubBase64.TryDecode(
            wrapped,
            budget.MaximumEncodedCharacters,
            budget.MaximumWhitespaceCharacters,
            budget.MaximumDecodedBytes,
            out _));
    }

    [Fact]
    public async Task ResponseCapFailsBeforeCompleteRetention()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            new string(' ',
                ActionHostGitBlobReadBudget.TrustedConfig
                    .MaximumResponseBytes + 1)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            new string('a', 40),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge,
            result.Failure);
    }

    [Fact]
    public async Task MalformedJsonPreservesEveryCapturedResponseByte()
    {
        const string body = "{\"malformed\":";
        var sha = new string('a', 40);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body),
            result.CapturedResponseBytes);
    }

    [Fact]
    public async Task UnknownLengthJsonCapPlusOneReportsConsumedBytes()
    {
        var maximumBytes = ActionHostGitBlobReadBudget.TrustedConfig
            .MaximumResponseBytes;
        var body = new byte[maximumBytes + 1];
        Array.Fill<byte>(body, 0x20);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new UnknownLengthJsonContent(body),
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            new string('a', 40),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(body.Length, result.CapturedResponseBytes);
    }

    [Fact]
    public async Task MaximumEnvelopeUnknownLengthResponseCapPlusOneIsBounded()
    {
        var maximumBytes = ActionHostGitBlobReadBudget.MaximumSupported
            .MaximumResponseBytes;
        var body = new byte[maximumBytes + 1];
        Array.Fill<byte>(body, 0x20);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new UnknownLengthJsonContent(body),
        });
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            new string('a', 40),
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(body.Length, result.CapturedResponseBytes);
    }

    [Fact]
    public async Task MapperRejectionKeepsCapturedBodyChargedOnce()
    {
        var requestedSha = new string('a', 40);
        var returnedSha = new string('b', 40);
        var body =
            $"{{\"sha\":\"{returnedSha}\",\"tree\":{{\"sha\":\"{returnedSha}\"}}}}";
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review",
            requestedSha,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body),
            result.CapturedResponseBytes);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage RedirectResponse(string sha)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(
            "https://codeload.github.com/SolusQuest/agentic-pr-review/legacy.tar.gz/" +
            sha);
        return response;
    }

    private static HttpResponseMessage ArchiveResponse(byte[] archive)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-gzip");
        return response;
    }

    private static void AddValidRateHeaders(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "1");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "5000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
    }

    private static string BlobResponse(
        byte[] bytes,
        string sha,
        string? content = null) => $$"""
        {"sha":"{{sha}}","size":{{bytes.Length}},"encoding":"base64","content":{{Json(content ?? Convert.ToBase64String(bytes))}}}
        """;

    private static byte[] GzipTar(string name, byte[] bytes)
    {
        return Gzip(Tar(name, bytes));
    }

    private static byte[] Tar(string name, byte[] bytes)
    {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes, writable: false),
            });
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

    private static GitHubCodeloadArchiveReader CreateArchiveReader(
        byte[] compressed,
        int maximumCompressedBytes,
        int maximumDecodedBytes) => new(
            new HttpResponseMessage(HttpStatusCode.OK),
            new MemoryStream(compressed, writable: false),
            maximumCompressedBytes,
            maximumDecodedBytes);

    private static async Task DrainArchiveAsync(
        GitHubCodeloadArchiveReader reader)
    {
        ActionHostGitArchiveEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(
            CancellationToken.None)) is not null)
        {
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(Stream.Null);
            }
        }
    }

    private static string Json(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string WrapEveryQuantum(string encoded)
    {
        var wrapped = new StringBuilder(
            encoded.Length + encoded.Length / 4);
        for (var index = 0; index < encoded.Length; index += 4)
        {
            if (index > 0)
            {
                wrapped.Append('\n');
            }

            wrapped.Append(encoded, index, 4);
        }

        return wrapped.ToString();
    }

    private static string WrapEveryLine(string encoded, int lineLength, string newline)
    {
        var wrapped = new StringBuilder(
            encoded.Length + (encoded.Length / lineLength + 1) * newline.Length);
        for (var index = 0; index < encoded.Length; index += lineLength)
        {
            if (index > 0)
            {
                wrapped.Append(newline);
            }

            wrapped.Append(encoded, index, Math.Min(lineLength, encoded.Length - index));
        }

        return wrapped.ToString();
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexStringLower(
            SHA1.HashData([.. header, .. bytes]));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _reply = reply;
        }

        internal List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));
            return Task.FromResult(_reply(request));
        }
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal UnknownLengthJsonContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }

    private sealed class FaultingArchiveContent : HttpContent
    {
        internal FaultingArchiveContent()
        {
            Headers.ContentType = new MediaTypeHeaderValue(
                "application/x-gzip");
        }

        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromException(new IOException("archive stream unavailable"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(
                new IOException("archive stream unavailable"));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) =>
            new(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(' ', header.Value),
                    StringComparer.OrdinalIgnoreCase));
    }
}
