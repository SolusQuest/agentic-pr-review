using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class R4StickyPublicationByteVectorTests
{
    [Theory]
    [MemberData(nameof(R4StickyPublicationByteVectors.Names),
        MemberType = typeof(R4StickyPublicationByteVectors))]
    public void RealP1RenderToP2BytesAndHashesAreFrozen(string name)
    {
        var vector = R4StickyPublicationByteVectors.Get(name);
        var rendered = vector.Rendered;
        var inspection = R4StickyMarker.Inspect(rendered.Comment);

        Assert.Equal(R4StickyInspectionKind.ValidR4, inspection.Kind);
        Assert.Equal(rendered.Body, inspection.Body);
        Assert.Equal(rendered.Identity, inspection.Identity);
        Assert.True(StickyCommentSerializer.TrySerialize(rendered.Comment,
            out var first));
        Assert.True(StickyCommentSerializer.TrySerialize(rendered.Comment,
            out var second));
        var independent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            body = rendered.Comment,
        });

        Assert.Equal(independent, first);
        Assert.Equal(first, second);
        Assert.Equal(vector.CommentSha256, Hash(rendered.Comment));
        Assert.Equal(vector.RequestSha256, Hash(first!));
        Assert.InRange(first!.Length, 1,
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes);
        var scalars = rendered.Comment.EnumerateRunes().Count();
        var bytes = Encoding.UTF8.GetByteCount(rendered.Comment);
        Assert.True(
            scalars >= R4PublicationBudget.MaximumScalars - 1_024 ||
            bytes >= R4PublicationBudget.MaximumUtf8Bytes - 1_024,
            $"Vector {name} is not near a P1 boundary: " +
            $"scalars={scalars}, bytes={bytes}.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

internal sealed record R4StickyPublicationByteVector(
    string Name,
    R4RenderedStickyComment Rendered,
    string CommentSha256,
    string RequestSha256);

internal static class R4StickyPublicationByteVectors
{
    private static readonly ImmutableArray<R4StickyPublicationByteVector>
        Vectors =
        [
            Build("ascii", "a",
                "09d91315885b1a3c9fd7a6e4f8e991c3d77143018292a4c7d6f63af2980cc548",
                "8e1b4d4710abc3f94514ea4760e9327f34aa67f581739bd71f743c3bc85ac548"),
            Build("quotes-backslashes", "\"\\",
                "d98301148823d3a8f89fd526bc19fe9bdbde8531e6119c888b8c6f9bdfe8810b",
                "a351062dcfc63103ed1e4a35a84f46de5f1c9aff5404ec528353e1b90d2d70c3"),
            Build("html-sensitive", "<>&",
                "cf620d74fdd343d28bbc67eea0c4844ae5edebfab64ccccd7f83c71b083229df",
                "8bcd66cf3e3ef0615999ad978a165bb699e518f4de0c333f91f6b42377ac0c80"),
            Build("bmp", "é",
                "2b222ed21ea09788fbec3d818fcbe6cb7b862b801d322aca42838685d977b188",
                "c8f8dfc8536148ca86bf5374fd7aa94a6c9bc726f73c000a1ae850ad232f815e"),
            Build("supplementary", "😀",
                "0869667ae78b54b4b631bd891db2b0f67dcad105d0680dcd9aa9cfeb65dc488c",
                "c1e9e7a1f1510fa639b754e84aa48b2e8b34514b91fa7454cfd4aa3296957246"),
        ];

    public static TheoryData<string> Names => new(
        Vectors.Select(static vector => vector.Name));

    internal static ImmutableArray<R4StickyPublicationByteVector> All =>
        Vectors;

    internal static R4StickyPublicationByteVector Get(string name) =>
        Vectors.Single(vector => StringComparer.Ordinal.Equals(
            vector.Name, name));

    private static R4StickyPublicationByteVector Build(string name,
        string unit, string commentSha256, string requestSha256)
    {
        var summary = RepeatWithinUtf8(unit, AgentLimits.SummaryBytes);
        var findings = ImmutableArray.CreateBuilder<AgentFinding>();
        R4RenderedStickyComment? lastAccepted = null;
        for (var index = 0; index < AgentLimits.Findings; index++)
        {
            findings.Add(Finding(unit, index,
                AgentLimits.FindingMessageBytes));
            var rendered = R4PublicationTestData.Render(summary,
                findings.ToImmutable());
            if (rendered.RenderedFindingCount != findings.Count)
            {
                findings.RemoveAt(findings.Count - 1);
                break;
            }
            lastAccepted = rendered;
        }

        if (findings.Count < AgentLimits.Findings)
        {
            var low = 1;
            var high = AgentLimits.FindingMessageBytes;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = findings.ToImmutable().Add(
                    Finding(unit, findings.Count, middle));
                var rendered = R4PublicationTestData.Render(summary,
                    candidate);
                if (rendered.RenderedFindingCount == candidate.Length)
                {
                    lastAccepted = rendered;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
        }

        if (lastAccepted is null)
            throw new InvalidOperationException(
                $"Unable to build P1 byte vector {name}.");
        return new(name, lastAccepted, commentSha256, requestSha256);
    }

    private static AgentFinding Finding(string unit, int index,
        int messageBytes) => R4PublicationTestData.Finding(
            title: RepeatWithinUtf8(unit, AgentLimits.FindingTitleBytes),
            message: RepeatWithinUtf8(unit, messageBytes),
            evidence:
            [
                R4PublicationTestData.Evidence(
                    observation: "0123456789abcdef0123"[index],
                    path: $"src/vector-{index}.cs"),
            ]);

    private static string RepeatWithinUtf8(string unit, int maximumBytes)
    {
        var unitBytes = Encoding.UTF8.GetByteCount(unit);
        var repetitions = maximumBytes / unitBytes;
        return string.Concat(Enumerable.Repeat(unit, repetitions));
    }
}
