using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    private static readonly HashSet<Type> FreshProcessFileSystemCapabilities =
    [
        typeof(Directory),
        typeof(DirectoryInfo),
        typeof(File),
        typeof(FileAccess),
        typeof(FileAttributes),
        typeof(FileMode),
        typeof(FileOptions),
        typeof(FileShare),
        typeof(FileStream),
        typeof(FileSystemInfo),
        typeof(Path),
        typeof(RandomAccess),
        typeof(SafeFileHandle),
        typeof(Stream),
    ];

    [Fact]
    public void FreshProcessProductionRootInventoryIsExact()
    {
        var roots = FreshProcessRootTypes()
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "ILiveAgentFreshProcessFileSystem",
                "LiveAgentFreshProcessAdmittedLineage",
                "LiveAgentFreshProcessAtomicWriteReceipt",
                "LiveAgentFreshProcessAuthorizationDocument",
                "LiveAgentFreshProcessAuthorizedInput",
                "LiveAgentFreshProcessAuthorizedRoot",
                "LiveAgentFreshProcessChangedFileDocument",
                "LiveAgentFreshProcessCodec",
                "LiveAgentFreshProcessCodes",
                "LiveAgentFreshProcessCommand",
                "LiveAgentFreshProcessCommandResult",
                "LiveAgentFreshProcessDeterministicTransport",
                "LiveAgentFreshProcessDeterministicTransportFactory",
                "LiveAgentFreshProcessDiffHunkDocument",
                "LiveAgentFreshProcessDiffLineDocument",
                "LiveAgentFreshProcessDiffSourceDocument",
                "LiveAgentFreshProcessDomain",
                "LiveAgentFreshProcessFileDocument",
                "LiveAgentFreshProcessFileSystem",
                "LiveAgentFreshProcessFileVersion",
                "LiveAgentFreshProcessJsonContext",
                "LiveAgentFreshProcessLineageAdmission",
                "LiveAgentFreshProcessLineageDocument",
                "LiveAgentFreshProcessLineagePublicationReceipt",
                "LiveAgentFreshProcessLineageSink",
                "LiveAgentFreshProcessManifest",
                "LiveAgentFreshProcessManifestFile",
                "LiveAgentFreshProcessManifestFileAccess",
                "LiveAgentFreshProcessManifestFileAccessFactory",
                "LiveAgentFreshProcessRead",
                "LiveAgentFreshProcessResultDocument",
                "LiveAgentFreshProcessReviewedIdentityDocument",
                "LiveAgentFreshProcessReviewedInputDocument",
                "LiveAgentFreshProcessScopeDocument",
                "LiveAgentFreshProcessSnapshotInput",
                "LiveAgentFreshProcessSnapshotManifestDocument",
                "LiveAgentFreshProcessStableAuthority",
                "LiveAgentFreshProcessTransitionDocument",
                "LiveAgentFreshProcessTransportProof",
            ],
            roots);
    }

    [Fact]
    public void FreshProcessTypesHaveOnlyClosedCapabilities()
    {
        var violations = FindFreshProcessCapabilityViolations(
            FreshProcessTypes());

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void FreshProcessCommandHasOneExternalRuntimeDispatch()
    {
        var family = FreshProcessTypes().ToHashSet();
        var calls = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => !family.Contains(type))
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Where(member => member.DeclaringType ==
                        typeof(LiveAgentFreshProcessCommand))
                    .Select(member => (Caller: type, Member: member))))
            .ToArray();

        var call = Assert.Single(calls);
        var callerRoot = call.Caller;
        while (callerRoot.DeclaringType is not null)
        {
            callerRoot = callerRoot.DeclaringType;
        }

        Assert.Equal(typeof(RuntimeApplication), callerRoot);
        Assert.Equal("RunAsync", call.Member.Name);
    }

    [Fact]
    public void FreshProcessScannerTraversesHiddenCapabilityShapes()
    {
        var fixtureTypes = new[]
        {
            typeof(FreshProcessAsyncNetworkFixture),
            typeof(FreshProcessEnvironmentFixture),
            typeof(FreshProcessProcessFixture),
            typeof(FreshProcessGenericFileFixture),
            typeof(FreshProcessNativeFixture),
        }.SelectMany(IncludeTypeAndNestedTypesRecursively);

        var violations = FindFreshProcessCapabilityViolations(fixtureTypes);

        Assert.Contains(violations, value => value.Contains(
            "System.Net.Sockets.TcpClient",
            StringComparison.Ordinal));
        Assert.Contains(violations, value => value.Contains(
            "System.Environment",
            StringComparison.Ordinal));
        Assert.Contains(violations, value => value.Contains(
            "System.Diagnostics.Process",
            StringComparison.Ordinal));
        Assert.Contains(violations, value => value.Contains(
            "System.IO.FileInfo",
            StringComparison.Ordinal));
        Assert.Contains(violations, value => value.StartsWith(
            "native:",
            StringComparison.Ordinal));
    }

    private static IEnumerable<Type> FreshProcessRootTypes() =>
        typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type =>
                type.Namespace == "AgenticPrReview.Runtime" &&
                type.DeclaringType is null &&
                (type.Name.StartsWith(
                        "LiveAgentFreshProcess",
                        StringComparison.Ordinal) ||
                    type.Name.StartsWith(
                        "ILiveAgentFreshProcess",
                        StringComparison.Ordinal)));

    private static IEnumerable<Type> FreshProcessTypes() =>
        FreshProcessRootTypes()
            .SelectMany(IncludeTypeAndNestedTypesRecursively)
            .Distinct();

    private static List<string> FindFreshProcessCapabilityViolations(
        IEnumerable<Type> types)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var referenced in ReferencedTypes(type)
                .SelectMany(ExpandTypeGraph))
            {
                AddFreshProcessViolation(
                    violations,
                    type,
                    referenced,
                    $"signature:{type.FullName}");
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                if (method.GetCustomAttribute<DllImportAttribute>() is not null ||
                    method.GetCustomAttribute<LibraryImportAttribute>() is not null)
                {
                    violations.Add($"native:{type.FullName}.{method.Name}");
                }

                var body = method.GetMethodBody();
                if (body is not null)
                {
                    foreach (var local in body.LocalVariables
                        .SelectMany(value => ExpandTypeGraph(value.LocalType)))
                    {
                        AddFreshProcessViolation(
                            violations,
                            type,
                            local,
                            $"local:{type.FullName}.{method.Name}");
                    }

                    foreach (var clause in body.ExceptionHandlingClauses)
                    {
                        if (clause.Flags ==
                                ExceptionHandlingClauseOptions.Clause &&
                            clause.CatchType is { } catchType)
                        {
                            AddFreshProcessViolation(
                                violations,
                                type,
                                catchType,
                                $"catch:{type.FullName}.{method.Name}");
                        }
                    }
                }

                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    var referenced = member as Type ?? member.DeclaringType;
                    if (referenced is not null)
                    {
                        foreach (var expanded in ExpandTypeGraph(referenced))
                        {
                            AddFreshProcessViolation(
                                violations,
                                type,
                                expanded,
                                $"body:{type.FullName}.{method.Name}",
                                member);
                        }
                    }

                    if (member is MethodInfo generic &&
                        generic.IsGenericMethod)
                    {
                        foreach (var argument in generic.GetGenericArguments()
                            .SelectMany(ExpandTypeGraph))
                        {
                            AddFreshProcessViolation(
                                violations,
                                type,
                                argument,
                                $"generic:{type.FullName}.{method.Name}");
                        }
                    }

                    if (member is MethodInfo called &&
                        called.DeclaringType == typeof(DeepSeekTransport) &&
                        called.Name is "CreateForTesting" or "CreateHandler")
                    {
                        violations.Add(
                            $"test-seam:{type.FullName}.{method.Name}->" +
                            FormatMember(member));
                    }
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToList();
    }

    private static void AddFreshProcessViolation(
        ISet<string> violations,
        Type owner,
        Type referenced,
        string location,
        MemberInfo? member = null)
    {
        if (IsFreshProcessForbiddenCapability(owner, referenced))
        {
            violations.Add(
                $"{location}->{(member is null ?
                    TypeName(referenced) :
                    FormatMember(member))}");
        }
    }

    private static bool IsFreshProcessForbiddenCapability(
        Type owner,
        Type referenced)
    {
        if (typeof(IOException).IsAssignableFrom(referenced))
        {
            return false;
        }

        var root = owner;
        while (root.DeclaringType is not null)
        {
            root = root.DeclaringType;
        }

        var name = TypeName(referenced);
        if (root == typeof(LiveAgentFreshProcessFileSystem) &&
            FreshProcessFileSystemCapabilities.Contains(referenced))
        {
            return false;
        }

        if (root == typeof(LiveAgentFreshProcessDeterministicTransport) &&
            referenced == typeof(MemoryStream))
        {
            return false;
        }

        return referenced == typeof(Environment) ||
            name.StartsWith("System.Net.", StringComparison.Ordinal) ||
            name.StartsWith(
                "System.Diagnostics.Process",
                StringComparison.Ordinal) ||
            name.StartsWith("System.IO.", StringComparison.Ordinal) ||
            name.StartsWith(
                "Microsoft.Win32.SafeHandles.",
                StringComparison.Ordinal) ||
            name.Contains("System.IServiceProvider", StringComparison.Ordinal) ||
            name.Contains("System.Management.Automation", StringComparison.Ordinal) ||
            name.Contains("IRuntimeFileSystem", StringComparison.Ordinal) ||
            name.Contains("Octokit", StringComparison.Ordinal) ||
            name.Contains("GitHub", StringComparison.Ordinal) ||
            name.Contains("Actions", StringComparison.Ordinal) ||
            name.Contains("Publisher", StringComparison.Ordinal) ||
            name.Contains("Shell", StringComparison.Ordinal);
    }

    private static class FreshProcessAsyncNetworkFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static async Task OpenAsync()
        {
            await Task.Yield();
            using var client = new System.Net.Sockets.TcpClient();
        }
    }

    private static class FreshProcessEnvironmentFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string? Read() =>
            Environment.GetEnvironmentVariable("APR_TEST");
    }

    private static class FreshProcessProcessFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Read() =>
            System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    private static class FreshProcessGenericFileFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static T Echo<T>(T value) => value;

        internal static object Hide() =>
            Echo(new List<System.IO.FileInfo>());
    }

    private static class FreshProcessNativeFixture
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();
    }
}
