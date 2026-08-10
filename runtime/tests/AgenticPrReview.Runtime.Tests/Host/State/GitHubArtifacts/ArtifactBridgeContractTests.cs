using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class ArtifactBridgeContractTests
{
    [Fact]
    public void FreezesExactPrivateBounds()
    {
        Assert.Equal(256, ArtifactBridgeLimits.MaximumNameBytes);
        Assert.Equal(256, ArtifactBridgeLimits.MaximumCorrelationBytes);
        Assert.Equal(1_024, ArtifactBridgeLimits.MaximumRelativePathBytes);
        Assert.Equal(2 * 1024 * 1024,
            ArtifactBridgeLimits.MaximumEncryptedObjectBytes);
        Assert.Equal(4 * 1024 * 1024,
            ArtifactBridgeLimits.MaximumStagingFileBytes);
        Assert.Equal(256 * 1024,
            ArtifactBridgeLimits.MaximumDocumentBytes);
        Assert.Equal(100, ArtifactBridgeLimits.RecordsPerPage);
        Assert.Equal(3, ArtifactBridgeLimits.MaximumPages);
        Assert.Equal(256, ArtifactBridgeLimits.MaximumRecords);
        Assert.Equal(TimeSpan.FromSeconds(30),
            ArtifactBridgeLimits.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120),
            ArtifactBridgeLimits.LogicalOperationTimeout);
    }

    [Fact]
    public void UsesStrictSourceGeneratedPrivateSerialization()
    {
        var context = ArtifactBridgeJsonContext.Default;
        var options = context.Options;
        Assert.False(options.PropertyNameCaseInsensitive);
        Assert.False(options.AllowDuplicateProperties);
        Assert.Equal(
            JsonUnmappedMemberHandling.Disallow,
            options.UnmappedMemberHandling);
        Assert.Equal(
            JsonIgnoreCondition.Never,
            options.DefaultIgnoreCondition);
        Assert.Same(JsonNamingPolicy.SnakeCaseLower,
            options.PropertyNamingPolicy);

        AssertGenerated<ArtifactBridgeListExactCommandDocument>(context);
        AssertGenerated<ArtifactBridgeMetadataCommandDocument>(context);
        AssertGenerated<ArtifactBridgeDownloadCommandDocument>(context);
        AssertGenerated<ArtifactBridgeUploadCommandDocument>(context);
        AssertGenerated<ArtifactBridgeReadBackCommandDocument>(context);
        AssertGenerated<ArtifactBridgeDeleteCommandDocument>(context);
        Assert.NotNull(context.GetTypeInfo(typeof(
            ActionHostPrivateCommandResultEnvelope<
                ArtifactBridgeResultDocument>)));
    }

    [Fact]
    public void H1CodecRejectsUnknownDuplicateAndBuildMismatch()
    {
        const string build = "build-152";
        var command = new ArtifactBridgeListExactCommandDocument(
            "list_exact",
            "correlation",
            "opaque-state",
            "8");
        var envelope = new ActionHostPrivateCommandEnvelope<
            ArtifactBridgeListExactCommandDocument>(build, command);
        var type = RequireTypeInfo<ActionHostPrivateCommandEnvelope<
            ArtifactBridgeListExactCommandDocument>>();
        Assert.True(ActionHostPrivateCommandCodec.TryWriteCommand(
            envelope,
            build,
            type,
            out var bytes));
        Assert.Contains("\"operation\":\"list_exact\"",
            Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-state", envelope.ToString());
        Assert.DoesNotContain("opaque-state", command.ToString());

        var json = Encoding.UTF8.GetString(bytes);
        var unknown = Encoding.UTF8.GetBytes(
            json.Replace(
                "\"maximum_objects\":\"8\"",
                "\"maximum_objects\":\"8\",\"unknown\":true",
                StringComparison.Ordinal));
        var duplicate = Encoding.UTF8.GetBytes(
            json.Replace(
                "\"build_discriminator\":\"build-152\"",
                "\"build_discriminator\":\"build-152\",\"build_discriminator\":\"build-152\"",
                StringComparison.Ordinal));
        Assert.False(ActionHostPrivateCommandCodec.TryReadCommand(
            unknown,
            build,
            type,
            out _));
        Assert.False(ActionHostPrivateCommandCodec.TryReadCommand(
            duplicate,
            build,
            type,
            out _));
        Assert.False(ActionHostPrivateCommandCodec.TryReadCommand(
            bytes,
            "other-build",
            type,
            out _));
    }

    [Fact]
    public void GitHubArtifactAdapterTypesStayInternalAndCapabilityNarrow()
    {
        var rootType = typeof(GitHubArtifactRestrictedStateStore);
        var types = rootType.Assembly.GetTypes()
            .Where(type => type.Namespace is not null &&
                type.Namespace.StartsWith(
                    "AgenticPrReview.Runtime.Host.State.GitHubArtifacts",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(types);
        Assert.All(types, type =>
        {
            Assert.False(type.IsPublic);
            Assert.False(type.IsNestedPublic);
        });

        var sourceRoot = FindSourceRoot();
        var combined = string.Join(
            "\n",
            Directory.GetFiles(sourceRoot, "*.cs")
                .Select(File.ReadAllText));
        foreach (var forbidden in new[]
        {
            "RestrictedStateSnapshot",
            "SESSION",
            "StateKey",
            "Lineage",
            "Provider",
            "Publication",
            "HttpClient",
            "System.Diagnostics.Process",
            "IServiceProvider",
            "Octokit",
        })
        {
            Assert.DoesNotContain(
                forbidden,
                combined,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CSharpStagingEnforcesTheEncryptedObjectCap()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-artifact-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var staging = new ArtifactBridgeStaging(root);
            var maximum = new byte[2 * 1024 * 1024];
            maximum[0] = 1;
            maximum[^1] = 2;
            var scope = await staging.StageUploadAsync(
                maximum,
                CancellationToken.None);
            Assert.Equal(maximum.Length,
                new FileInfo(scope.FullPath).Length);
            await scope.DisposeAsync();
            Assert.True(scope.CleanupSucceeded);

            await Assert.ThrowsAsync<IOException>(() =>
                staging.StageUploadAsync(
                    new byte[(2 * 1024 * 1024) + 1],
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CSharpStagingRefusesReparseCleanup()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-artifact-cleanup-{Guid.NewGuid():N}");
        var outside = Path.Join(
            Path.GetTempPath(),
            $"apr-artifact-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var marker = Path.Join(outside, "keep");
        await File.WriteAllTextAsync(marker, "keep");
        string? operation = null;
        try
        {
            var staging = new ArtifactBridgeStaging(root);
            var scope = await staging.StageUploadAsync(
                new byte[] { 1 },
                CancellationToken.None);
            operation = Path.GetDirectoryName(scope.FullPath)!;
            File.Delete(scope.FullPath);
            Directory.Delete(operation);
            try
            {
                Directory.CreateSymbolicLink(operation, outside);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                await scope.DisposeAsync();
                return;
            }

            await scope.DisposeAsync();

            Assert.False(scope.CleanupSucceeded);
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (operation is not null &&
                Directory.Exists(operation) &&
                (File.GetAttributes(operation) &
                    FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(operation);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static void AssertGenerated<TCommand>(
        ArtifactBridgeJsonContext context)
        where TCommand : class, IActionHostPrivateCommandDocument =>
        Assert.NotNull(context.GetTypeInfo(typeof(
            ActionHostPrivateCommandEnvelope<TCommand>)));

    private static JsonTypeInfo<T> RequireTypeInfo<T>() =>
        ArtifactBridgeJsonContext.Default.GetTypeInfo(typeof(T)) as
            JsonTypeInfo<T> ??
        throw new InvalidOperationException();

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(
                directory.FullName,
                "runtime",
                "src",
                "AgenticPrReview.Runtime",
                "Host",
                "State",
                "GitHubArtifacts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
