using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
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

    private static readonly HashSet<string> FreshProcessFileSystemMembers =
    [
        "System.IO.Directory.EnumerateFileSystemEntries(System.String)->" +
            "System.Collections.Generic.IEnumerable`1[[System.String, " +
            "System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, " +
            "PublicKeyToken=7cec85d7bea7798e]]",
        "System.IO.DirectoryInfo..ctor(System.String)->ctor",
        "System.IO.DirectoryInfo.get_Parent()->System.IO.DirectoryInfo",
        "System.IO.File.Delete(System.String)->System.Void",
        "System.IO.File.GetAttributes(" +
            "Microsoft.Win32.SafeHandles.SafeFileHandle)->" +
            "System.IO.FileAttributes",
        "System.IO.File.Move(System.String,System.String,System.Boolean)->" +
            "System.Void",
        "System.IO.FileStream..ctor(System.String,System.IO.FileMode," +
            "System.IO.FileAccess,System.IO.FileShare,System.Int32," +
            "System.IO.FileOptions)->ctor",
        "System.IO.FileStream.Flush(System.Boolean)->System.Void",
        "System.IO.FileSystemInfo.get_FullName()->System.String",
        "System.IO.Path.GetDirectoryName(System.String)->System.String",
        "System.IO.Path.GetFileName(System.String)->System.String",
        "System.IO.Path.GetFullPath(System.String)->System.String",
        "System.IO.Path.IsPathFullyQualified(System.String)->System.Boolean",
        "System.IO.Path.Join(System.String,System.String)->System.String",
        "System.IO.Path.Join(System.String,System.String,System.String)->" +
            "System.String",
        "System.IO.Path.TrimEndingDirectorySeparator(System.String)->" +
            "System.String",
        "System.IO.RandomAccess.GetLength(" +
            "Microsoft.Win32.SafeHandles.SafeFileHandle)->System.Int64",
        "System.IO.RandomAccess.Read(" +
            "Microsoft.Win32.SafeHandles.SafeFileHandle," +
            "System.Span`1[[System.Byte, System.Private.CoreLib, " +
            "Version=10.0.0.0, Culture=neutral, " +
            "PublicKeyToken=7cec85d7bea7798e]],System.Int64)->System.Int32",
        "System.IO.Stream.Write(System.ReadOnlySpan`1[[System.Byte, " +
            "System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, " +
            "PublicKeyToken=7cec85d7bea7798e]])->System.Void",
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
                "LiveAgentFreshProcessAuthorizationRead",
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
    public void FreshProcessCommandHasOnePreauthorizationRead()
    {
        var calls = IncludeTypeAndNestedTypesRecursively(
                typeof(LiveAgentFreshProcessCommand))
            .SelectMany(DeclaredExecutableMembers)
            .SelectMany(ResolveMethodBodyMembers)
            .OfType<MethodInfo>()
            .Where(member => member.DeclaringType ==
                    typeof(ILiveAgentFreshProcessFileSystem) &&
                member.Name == "ReadAuthorization")
            .ToArray();

        Assert.Single(calls);
    }

    [Fact]
    public void FreshProcessFamilyHasNoOtherRuntimeReverseDependencies()
    {
        var family = FreshProcessTypes().ToHashSet();
        var violations = new List<string>();
        foreach (var type in typeof(RuntimeApplication).Assembly.GetTypes())
        {
            var root = type;
            while (root.DeclaringType is not null)
            {
                root = root.DeclaringType;
            }

            if (family.Contains(type) || root == typeof(RuntimeApplication))
            {
                continue;
            }

            if (ReferencedTypes(type)
                    .SelectMany(ExpandTypeGraph)
                    .Any(family.Contains) ||
                DeclaredExecutableMembers(type)
                    .SelectMany(ResolveMethodBodyMembers)
                    .Any(member => member.DeclaringType is { } declaring &&
                        family.Contains(declaring)))
            {
                violations.Add(type.FullName ?? type.Name);
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DeterministicTransportStoresNoCredential()
    {
        var fields = new[]
        {
            typeof(LiveAgentFreshProcessDeterministicTransportFactory),
            typeof(LiveAgentFreshProcessDeterministicTransport),
        }.SelectMany(type => type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly));

        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(DeepSeekCredential) ||
            field.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
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
            typeof(FreshProcessDynamicNativeFixture),
            typeof(FreshProcessDirectStateFixture),
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
        Assert.Contains(violations, value => value.Contains(
            "System.Runtime.InteropServices.NativeLibrary",
            StringComparison.Ordinal));
        Assert.Contains(violations, value => value.Contains(
            "RestrictedStateService",
            StringComparison.Ordinal));
    }

    [Fact]
    public void FreshProcessExactAllowlistsRejectPrivilegedAdditions()
    {
        var openRead = typeof(File).GetMethod(
            nameof(File.OpenRead),
            [typeof(string)]);
        Assert.NotNull(openRead);
        Assert.True(IsFreshProcessForbiddenCapability(
            typeof(LiveAgentFreshProcessFileSystem),
            typeof(File),
            openRead));
        Assert.True(IsFreshProcessForbiddenCapability(
            typeof(LiveAgentFreshProcessDeterministicTransport),
            typeof(System.Net.Http.HttpClient),
            member: null));
        Assert.True(IsFreshProcessForbiddenCapability(
            typeof(LiveAgentFreshProcessCommand),
            typeof(NativeLibrary),
            typeof(NativeLibrary).GetMethod(
                nameof(NativeLibrary.Load),
                [typeof(string)])));
        Assert.True(IsFreshProcessForbiddenCapability(
            typeof(LiveAgentFreshProcessCommand),
            typeof(RestrictedStateService),
            member: null));
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
        if (IsFreshProcessForbiddenCapability(owner, referenced, member))
        {
            violations.Add(
                $"{location}->{(member is null ?
                    TypeName(referenced) :
                    FreshProcessMemberKey(member))}");
        }
    }

    private static bool IsFreshProcessForbiddenCapability(
        Type owner,
        Type referenced,
        MemberInfo? member)
    {
        if (referenced.HasElementType)
        {
            return false;
        }

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
        if (root == typeof(LiveAgentFreshProcessFileSystem))
        {
            if (member is not null &&
                IsFileSystemCapability(member.DeclaringType))
            {
                return !FreshProcessFileSystemMembers.Contains(
                    FreshProcessMemberKey(member));
            }

            if (FreshProcessFileSystemCapabilities.Contains(referenced))
            {
                return false;
            }
        }

        if (root == typeof(LiveAgentFreshProcessDeterministicTransport) &&
            referenced == typeof(MemoryStream))
        {
            return false;
        }

        return referenced == typeof(Environment) ||
            referenced == typeof(NativeLibrary) ||
            referenced == typeof(RestrictedStateService) ||
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

    private static bool IsFileSystemCapability(Type? type) =>
        type is not null &&
        (type.Namespace == "System.IO" ||
            type.Namespace == "Microsoft.Win32.SafeHandles");

    private static string FreshProcessMemberKey(MemberInfo member)
    {
        var parameters = member is MethodBase method
            ? string.Join(",", method.GetParameters()
                .Select(parameter => TypeName(parameter.ParameterType)))
            : string.Empty;
        var result = member is MethodInfo function
            ? TypeName(function.ReturnType)
            : member is ConstructorInfo
                ? "ctor"
                : member.MemberType.ToString();
        return string.Concat(
            member.DeclaringType?.FullName,
            ".",
            member.Name,
            "(",
            parameters,
            ")->",
            result);
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

    private static class FreshProcessDynamicNativeFixture
    {
        internal static nint Load() => NativeLibrary.Load("forbidden");
    }

    private static class FreshProcessDirectStateFixture
    {
        internal static object Use(RestrictedStateService service) => service;
    }
}
