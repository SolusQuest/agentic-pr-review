using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed partial class DeepSeekTransportArchitectureTests
{
    private const string DeepSeekNamespace =
        "AgenticPrReview.Runtime.Execution.DeepSeek";
    private const string AgentNamespace = "AgenticPrReview.Runtime.Agent";
    private const string StateNamespace =
        "AgenticPrReview.Runtime.Host.State";

    [Fact]
    public void DeepSeekTransportNamespaceHasOnlyReviewedCapabilities()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var types = assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                DeepSeekNamespace,
                StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(types);

        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var referenced in ReferencedTypes(type)
                         .SelectMany(ExpandTypeGraph))
            {
                if (ForbiddenType(type, referenced))
                {
                    violations.Add(
                        $"signature:{type.FullName}->{TypeName(referenced)}");
                }
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                var body = method.GetMethodBody();
                if (body is not null)
                {
                    foreach (var local in body.LocalVariables)
                    {
                        foreach (var localType in
                                 ExpandTypeGraph(local.LocalType))
                        {
                            if (ForbiddenType(type, localType))
                            {
                                violations.Add(
                                    $"local:{type.FullName}.{method.Name}->" +
                                    TypeName(localType));
                            }
                        }
                    }

                    foreach (var clause in body.ExceptionHandlingClauses)
                    {
                        if (clause.Flags ==
                                ExceptionHandlingClauseOptions.Clause &&
                            clause.CatchType is { } catchType &&
                            ForbiddenType(type, catchType))
                        {
                            violations.Add(
                                $"catch:{type.FullName}.{method.Name}->" +
                                TypeName(catchType));
                        }
                    }
                }

                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    if (ForbiddenMember(type, member))
                    {
                        violations.Add(
                            $"body:{type.FullName}.{method.Name}->" +
                            FormatMember(member));
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void AgentSessionToolsAndStateDoNotAcquireDeepSeekCapabilities()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var protectedTypes = assembly.GetTypes()
            .Where(type =>
                type.Namespace?.StartsWith(
                    AgentNamespace,
                    StringComparison.Ordinal) == true ||
                type.Namespace?.StartsWith(
                    StateNamespace,
                    StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(protectedTypes);

        var violations = new List<string>();
        foreach (var type in protectedTypes)
        {
            foreach (var referenced in ReferencedTypes(type)
                         .SelectMany(ExpandTypeGraph))
            {
                if (IsDeepSeekCapability(referenced))
                {
                    violations.Add(
                        $"signature:{type.FullName}->{TypeName(referenced)}");
                }
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    var declaring = member as Type ?? member.DeclaringType;
                    if (declaring is not null &&
                        IsDeepSeekCapability(declaring))
                    {
                        violations.Add(
                            $"body:{type.FullName}.{method.Name}->" +
                            FormatMember(member));
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TestConstructionSeamsHaveNoProductionCallers()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var seamNames = new HashSet<string>(
            [
                nameof(DeepSeekTransport.CreateForTesting),
                nameof(DeepSeekTransport.CreateHandler),
            ],
            StringComparer.Ordinal);
        var callers = new List<string>();
        foreach (var type in assembly.GetTypes()
                     .Where(type => !IsConcreteTransport(type)))
        {
            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    if (member.DeclaringType == typeof(DeepSeekTransport) &&
                        seamNames.Contains(member.Name))
                    {
                        callers.Add(
                            $"{type.FullName}.{method.Name}->{member.Name}");
                    }
                }
            }
        }

        Assert.Empty(callers);
    }

    [Fact]
    public void RawCredentialValueIsReadOnlyByTheConcreteTransport()
    {
        var readers = new List<string>();
        var violations = new List<string>();
        foreach (var type in typeof(RuntimeApplication).Assembly.GetTypes())
        {
            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    if (member.DeclaringType != typeof(DeepSeekCredential) ||
                        !StringComparer.Ordinal.Equals(
                            member.Name,
                            "get_Value"))
                    {
                        continue;
                    }

                    var reader = $"{type.FullName}.{method.Name}";
                    readers.Add(reader);
                    if (!IsConcreteTransport(type))
                    {
                        violations.Add(reader);
                    }
                }
            }
        }

        Assert.NotEmpty(readers);
        Assert.Empty(violations);
    }

    [Fact]
    public void RequestWriterHasNoCredentialOrTransportCapability()
    {
        var types = new[]
        {
            typeof(DeepSeekRequestWriter),
            typeof(DeepSeekRequestWriteResult),
        };
        var forbidden = new[]
        {
            typeof(DeepSeekCredential),
            typeof(DeepSeekTransport),
            typeof(IDeepSeekTransport),
            typeof(HttpClient),
            typeof(HttpMessageHandler),
            typeof(AgentToolRegistry),
            typeof(DeepSeekProviderContract),
            typeof(DeepSeekLiveProviderExecutor),
        };
        var references = types
            .SelectMany(ReferencedTypes)
            .SelectMany(ExpandTypeGraph)
            .ToArray();

        Assert.DoesNotContain(references, forbidden.Contains);
    }

    [Fact]
    public void TransportSurfaceContainsNoLaterLeafContracts()
    {
        var forbiddenFragments = new[]
        {
            "IProjectChatClient",
            "MinimalChatClient",
            "ProjectChatResponse",
            "ProviderUsage",
            "Parser",
            "JsonDocument",
            "JsonSerializer",
            "AgentRunOutcome",
            "Agent.Session",
            "Host.State",
        };
        var references = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                DeepSeekNamespace,
                StringComparison.Ordinal) == true)
            .SelectMany(ReferencedTypes)
            .SelectMany(ExpandTypeGraph)
            .Select(TypeName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(
            references,
            name => forbiddenFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ProductionDeepSeekNamespaceHasNoNativeImports()
    {
        var imports = ReadNativeImports(
            typeof(RuntimeApplication).Assembly,
            typeName => typeName.StartsWith(
                DeepSeekNamespace,
                StringComparison.Ordinal));

        Assert.Empty(imports);
    }

    [Fact]
    public void ScannerDetectsForbiddenConstructorParameters()
    {
        var references = ReferencedTypes(
                typeof(ForbiddenConstructorParameterFixture))
            .SelectMany(ExpandTypeGraph)
            .ToArray();

        Assert.Contains(references, type => type == typeof(HttpClient));
    }

    [Fact]
    public void ScannerDetectsMethodBodyOnlyEnvironmentAndDynamicLoading()
    {
        var method = typeof(DeepSeekTransportArchitectureTests).GetMethod(
            nameof(MethodBodyOnlyForbiddenFixture),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var members = ResolveMethodBodyMembers(method).ToArray();

        Assert.Contains(
            members,
            member => member.DeclaringType == typeof(Environment));
        Assert.Contains(
            members,
            member => member.DeclaringType == typeof(NativeLibrary));
    }

    [Fact]
    public void ScannerChecksUnusedBodyFreeNativeDeclarations()
    {
        var testType = typeof(DeepSeekTransportArchitectureTests).FullName!;
        var imports = ReadNativeImports(
            typeof(DeepSeekTransportArchitectureTests).Assembly,
            typeName => StringComparer.Ordinal.Equals(typeName, testType));
        var allowed =
            testType +
            "|libc|fstat|System.Int32(System.Int32,System.IntPtr)";
        var forbidden =
            testType +
            "|libc|execve|System.Int32(System.IntPtr,System.IntPtr,System.IntPtr)";

        Assert.Contains(allowed, imports);
        Assert.Contains(forbidden, imports);
        Assert.Empty(imports.Except([allowed, forbidden]));
        Assert.Single(imports.Except([allowed]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint MethodBodyOnlyForbiddenFixture()
    {
        _ = Environment.GetEnvironmentVariable("APR_DEEPSEEK_ARCH_FIXTURE");
        return NativeLibrary.Load("apr-deepseek-architecture-fixture");
    }

    private sealed class ForbiddenConstructorParameterFixture(
        HttpClient client)
    {
        private readonly HttpClient _client = client;
    }

    [LibraryImport("libc", EntryPoint = "fstat")]
    private static partial int AllowedIdentityImportFixture(
        int fileDescriptor,
        nint information);

    [LibraryImport("libc", EntryPoint = "execve")]
    private static partial int ForbiddenNativeImportFixture(
        nint path,
        nint arguments,
        nint environment);

    private static bool ForbiddenType(Type source, Type referenced)
    {
        var name = TypeName(referenced);
        if (GlobalForbidden(name))
        {
            return true;
        }

        return !IsConcreteTransport(source) &&
            name.StartsWith("System.Net.", StringComparison.Ordinal);
    }

    private static bool ForbiddenMember(Type source, MemberInfo member)
    {
        if (member is Type referencedType)
        {
            return ForbiddenType(source, referencedType);
        }

        var declaring = member.DeclaringType;
        if (declaring is null)
        {
            return false;
        }

        var name = TypeName(declaring);
        if (GlobalForbidden(name))
        {
            return true;
        }

        if (declaring == typeof(NativeLibrary) ||
            declaring == typeof(Marshal) &&
            member.Name.Contains(
                "GetDelegateForFunctionPointer",
                StringComparison.Ordinal))
        {
            return true;
        }

        return !IsConcreteTransport(source) &&
            name.StartsWith("System.Net.", StringComparison.Ordinal);
    }

    private static bool GlobalForbidden(string name)
    {
        var fragments = new[]
        {
            "Microsoft.Extensions.AI",
            "System.Diagnostics.Process",
            "System.Environment",
            "System.IServiceProvider",
            "System.Runtime.InteropServices.NativeLibrary",
            "Octokit",
            "GitHub",
            "Actions",
            "Publisher",
            "Shell",
            "HostSecret",
            "Configuration",
            "ServiceProvider",
        };
        return fragments.Any(fragment =>
            name.Contains(fragment, StringComparison.Ordinal));
    }

    private static bool IsConcreteTransport(Type type)
    {
        for (var current = type; current is not null;
             current = current.DeclaringType)
        {
            if (current == typeof(DeepSeekTransport))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDeepSeekCapability(Type type) =>
        type.Namespace?.StartsWith(
            DeepSeekNamespace,
            StringComparison.Ordinal) == true ||
        TypeName(type).Contains("DeepSeekCredential", StringComparison.Ordinal);

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        yield return type;
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var implemented in type.GetInterfaces())
        {
            yield return implemented;
        }

        foreach (var parameter in type.GetGenericArguments())
        {
            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                yield return constraint;
            }
        }

        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
        }

        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return field.FieldType;
        }

        foreach (var @event in type.GetEvents(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            if (@event.EventHandlerType is { } handlerType)
            {
                yield return handlerType;
            }
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            foreach (var genericParameter in method.GetGenericArguments())
            {
                foreach (var constraint in
                         genericParameter.GetGenericParameterConstraints())
                {
                    yield return constraint;
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandTypeGraph(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in ExpandTypeGraph(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in ExpandTypeGraph(argument))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<MethodBase> DeclaredExecutableMembers(Type type)
    {
        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return method;
        }

        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return constructor;
        }

        if (type.TypeInitializer is { } typeInitializer)
        {
            yield return typeInitializer;
        }
    }

    private static IEnumerable<MemberInfo> ResolveMethodBodyMembers(
        MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null)
        {
            yield break;
        }

        var il = body.GetILAsByteArray()!;
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadOpCode(il, ref offset);
            if (opCode == OpCodes.Calli)
            {
                throw new Xunit.Sdk.XunitException(
                    "Unmanaged or indirect calli is forbidden: " +
                    $"{method.DeclaringType?.FullName}.{method.Name}");
            }

            var tokenOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (opCode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok or
                OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(il, tokenOffset);
                MemberInfo resolved;
                try
                {
                    resolved = method.Module.ResolveMember(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo info
                            ? info.GetGenericArguments()
                            : null)!;
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        "IL token resolution failed for " +
                        $"{method.DeclaringType?.FullName}.{method.Name}: " +
                        exception.GetType().Name);
                }

                yield return resolved;
            }

            offset += operandSize;
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        var value = first == 0xFE
            ? (ushort)(0xFE00 | il[offset++])
            : first;
        if (!OpCodesByValue.TryGetValue(value, out var opCode))
        {
            throw new Xunit.Sdk.XunitException(
                $"Unknown IL opcode 0x{value:x4}.");
        }

        return opCode;
    }

    private static int OperandSize(
        OperandType operandType,
        byte[] il,
        int offset) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or
            OperandType.ShortInlineI or
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or
            OperandType.InlineField or
            OperandType.InlineI or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType or
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch =>
                4 + checked(BitConverter.ToInt32(il, offset) * 4),
            _ => throw new Xunit.Sdk.XunitException(
                $"Unsupported IL operand type {operandType}."),
        };

    private static IReadOnlyCollection<string> ReadNativeImports(
        Assembly assembly,
        Func<string, bool> includeType)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var imports = new List<string>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = FullTypeName(reader, type);
            if (!includeType(typeName))
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                {
                    continue;
                }

                var import = method.GetImport();
                var module = reader.GetString(
                    reader.GetModuleReference(import.Module).Name);
                var entryPoint = reader.GetString(import.Name);
                var token =
                    System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(
                        methodHandle);
                var reflectionMethod =
                    assembly.ManifestModule.ResolveMethod(token)!;
                imports.Add(string.Concat(
                    typeName,
                    "|",
                    module,
                    "|",
                    entryPoint,
                    "|",
                    FormatSignature(reflectionMethod)));
            }
        }

        return imports;
    }

    private static string FullTypeName(
        MetadataReader reader,
        TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullTypeName(reader, reader.GetTypeDefinition(declaring)) +
                "+" +
                name;
        }

        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace)
            ? name
            : @namespace + "." + name;
    }

    private static string FormatSignature(MethodBase method)
    {
        var returnType = method is MethodInfo info
            ? TypeName(info.ReturnType)
            : "System.Void";
        return string.Concat(
            returnType,
            "(",
            string.Join(
                ",",
                method.GetParameters().Select(parameter =>
                    TypeName(parameter.ParameterType))),
            ")");
    }

    private static string TypeName(Type type) =>
        type.FullName ?? type.ToString();

    private static string FormatMember(MemberInfo member) =>
        string.Concat(
            member.DeclaringType?.FullName,
            ".",
            member.Name);

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                opCode => unchecked((ushort)opCode.Value),
                opCode => opCode);
}
