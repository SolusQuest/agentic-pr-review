using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class ManagedArchitectureAudit
{
    private const string Kind = "apr-r4-e2-managed-architecture-v1";
    private const long MaximumAssemblyBytes = 64 * 1024 * 1024;

    private static readonly string[] RequiredFixtureTypes =
    [
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.AotProof",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkCanaryCapture",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkGitHubHandler",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkHost",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkIndentedJsonContext",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkJsonContext",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkProviderHandler",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkStateDependencies",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkSupervisor",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.ManagedArchitectureAudit",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.SyntheticOfficialPlatform",
    ];

    private static readonly string[] RequiredRuntimeTypes =
    [
        "AgenticPrReview.Runtime.ActionHost.ActionHostComposition",
        "AgenticPrReview.Runtime.ActionHost.ActionHostCoordinator",
        "AgenticPrReview.Runtime.ActionHost.Authorization.ActionHostEventJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubAuthorizationTransportFactory",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitObjectJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostReviewedSnapshotJsonContext",
        "AgenticPrReview.Runtime.ActionHost.Policy.ActionHostTrustedPolicyJsonContext",
        "AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory",
        "AgenticPrReview.Runtime.ActionHost.Serialization.ActionHostJsonContext",
        "AgenticPrReview.Runtime.Agent.Session.AgentSessionJsonContext",
        "AgenticPrReview.Runtime.Agent.Tools.AgentToolJsonContext",
        "AgenticPrReview.Runtime.LiveAgentFreshProcessJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherTransportFactory",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline.InlineCommentJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky.StickyCommentJsonContext",
        "AgenticPrReview.Runtime.Host.State.GitHubArtifacts.ArtifactBridgeJsonContext",
        "AgenticPrReview.Runtime.Host.State.GitHubArtifacts.GitHubArtifactRestrictedStateStore",
        "AgenticPrReview.Runtime.Host.State.Restore.AcceptedStateProductionDependencies",
        "AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions.RestrictedStateTransactionIndexJsonContext",
        "AgenticPrReview.Runtime.RuntimeJsonContext",
    ];

    private static readonly string[] ForbiddenAssemblies =
    [
        "AgenticPrReview.CanonicalOracle",
        "AgenticPrReview.SharedUtilities",
        "AgenticPrReview.TypeScriptHost",
    ];

    private static readonly string[] ForbiddenTypes =
    [
        "AgenticPrReview.Runtime.CanonicalOracle",
        "AgenticPrReview.Runtime.RootReviewDto",
        "AgenticPrReview.Runtime.SharedUtilities",
        "AgenticPrReview.Runtime.TypeScriptHost",
    ];

    private static readonly string[] RequiredFixtureConstructorRoutes =
    [
        "AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory::.ctor",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubAuthorizationTransportFactory::.ctor",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherTransportFactory::.ctor",
        "AgenticPrReview.Runtime.Host.State.Restore.AcceptedStateProductionDependencies::.ctor",
    ];

    internal static bool TryAudit(
        string fixtureAssembly,
        string runtimeAssembly,
        out string digest)
    {
        digest = string.Empty;
        try
        {
            var fixture = Read(fixtureAssembly);
            var runtime = Read(runtimeAssembly);
            if (fixture.AssemblyName !=
                    "AgenticPrReview.Runtime.ActionHostVerifierFixture" ||
                runtime.AssemblyName != "AgenticPrReview.Runtime" ||
                !fixture.AssemblyReferences.Contains(
                    "AgenticPrReview.Runtime") ||
                !RequiredFixtureTypes.All(fixture.TypeDefinitions.Contains) ||
                !RequiredRuntimeTypes.All(runtime.TypeDefinitions.Contains) ||
                RequiredFixtureConstructorRoutes.Any(route =>
                    fixture.MemberReferences.GetValueOrDefault(route) != 1) ||
                ForbiddenAssemblies.Any(name =>
                    fixture.AssemblyReferences.Contains(name) ||
                    runtime.AssemblyReferences.Contains(name)) ||
                ForbiddenTypes.Any(name =>
                    fixture.AllTypes.Contains(name) ||
                    runtime.AllTypes.Contains(name)))
            {
                return false;
            }

            var framing = new StringBuilder(Kind).Append('\n')
                .Append("fixture-assembly:").Append(fixture.AssemblyName)
                .Append('\n')
                .Append("runtime-assembly:").Append(runtime.AssemblyName)
                .Append('\n')
                .Append("fixture-reference:AgenticPrReview.Runtime\n");
            foreach (var type in RequiredFixtureTypes)
            {
                framing.Append("fixture-type:").Append(type).Append('\n');
            }
            foreach (var type in RequiredRuntimeTypes)
            {
                framing.Append("runtime-type:").Append(type).Append('\n');
            }
            foreach (var route in RequiredFixtureConstructorRoutes)
            {
                framing.Append("fixture-constructor:").Append(route)
                    .Append(":1\n");
            }
            foreach (var assembly in ForbiddenAssemblies)
            {
                framing.Append("forbidden-assembly:").Append(assembly)
                    .Append('\n');
            }
            foreach (var type in ForbiddenTypes)
            {
                framing.Append("forbidden-type:").Append(type).Append('\n');
            }

            digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(framing.ToString())))
                .ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or BadImageFormatException or
            InvalidOperationException)
        {
            return false;
        }
    }

    private static ManagedAssemblyMetadata Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is < 2 or > MaximumAssemblyBytes)
        {
            throw new BadImageFormatException();
        }

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream,
            PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException();
        }

        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly)
        {
            throw new BadImageFormatException();
        }

        var definitions = metadata.TypeDefinitions
            .Select(handle => FullName(metadata,
                metadata.GetTypeDefinition(handle).Namespace,
                metadata.GetTypeDefinition(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
        var references = metadata.TypeReferences
            .Select(handle => FullName(metadata,
                metadata.GetTypeReference(handle).Namespace,
                metadata.GetTypeReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
        var assemblyReferences = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(
                metadata.GetAssemblyReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
        var memberReferences = metadata.MemberReferences
            .Select(handle => metadata.GetMemberReference(handle))
            .Select(member => (Parent: ParentName(metadata, member.Parent),
                Name: metadata.GetString(member.Name)))
            .Where(member => member.Parent is not null)
            .GroupBy(member => member.Parent + "::" + member.Name,
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);
        return new ManagedAssemblyMetadata(
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            definitions,
            definitions.Concat(references).ToHashSet(StringComparer.Ordinal),
            assemblyReferences,
            memberReferences);
    }

    private static string? ParentName(
        MetadataReader metadata,
        EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => DefinitionName(metadata,
                (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ReferenceName(metadata,
                (TypeReferenceHandle)handle),
            _ => null,
        };

    private static string DefinitionName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        return FullName(metadata, definition.Namespace, definition.Name);
    }

    private static string ReferenceName(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        return FullName(metadata, reference.Namespace, reference.Name);
    }

    private static string FullName(
        MetadataReader metadata,
        StringHandle namespaceHandle,
        StringHandle nameHandle)
    {
        var @namespace = metadata.GetString(namespaceHandle);
        var name = metadata.GetString(nameHandle);
        return @namespace.Length == 0 ? name : @namespace + "." + name;
    }

    private sealed record ManagedAssemblyMetadata(
        string AssemblyName,
        HashSet<string> TypeDefinitions,
        HashSet<string> AllTypes,
        HashSet<string> AssemblyReferences,
        Dictionary<string, int> MemberReferences);
}
