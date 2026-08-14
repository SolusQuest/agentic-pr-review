using AgenticPrReview.Runtime.Host.Publishing.Recovery;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;

public sealed class PublicationRecoveryArchitectureTests
{
    [Fact]
    public void P5OwnsDecisionsButNotProviderRenderingEncryptionOrMutation()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(
            root,
            "runtime",
            "src",
            "AgenticPrReview.Runtime",
            "Host",
            "Publishing",
            "Recovery");
        var production = string.Join(
            '\n',
            Directory.GetFiles(directory, "*.cs")
                .Select(File.ReadAllText));

        Assert.DoesNotContain("R4StickyRenderer.Render", production,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PublishAsync(", production,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MutateStickyCommentAsync", production,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RestrictedStateEnvelope", production,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IRestrictedStateStore", production,
            StringComparison.Ordinal);
        Assert.Contains(".DiscoverAsync(", production,
            StringComparison.Ordinal);
        Assert.Contains("ValidatedPublicationPayloadV1", production,
            StringComparison.Ordinal);
        Assert.Contains("BoundedGitHubPublisherOutcome", production,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionSurfaceIsClosedAndDefaultsToConflict()
    {
        Assert.Equal(
            PublicationRecoveryAction.Conflict,
            PublicationRecoveryClassifier.Classify(
                null,
                PublicationMarkerObservation.Incomplete).Action);
        Assert.Equal(14, Enum.GetValues<PublicationRecoveryAction>().Length);
        Assert.Equal(
            6,
            Enum.GetValues<PublicationRecoveryLifecycleState>().Length);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}
