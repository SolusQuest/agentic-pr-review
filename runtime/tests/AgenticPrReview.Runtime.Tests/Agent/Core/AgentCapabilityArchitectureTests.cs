using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    private const string AgentNamespace = "AgenticPrReview.Runtime.Agent.";
    private const string ToolsNamespace = "AgenticPrReview.Runtime.Agent.Tools";

    private static readonly HashSet<string> AllowedNativeImports =
    [
        "AgenticPrReview.Runtime.Agent.Tools.NativeReviewedFileIdentity|kernel32.dll|GetFileInformationByHandle|System.Int32(System.IntPtr,AgenticPrReview.Runtime.Agent.Tools.NativeReviewedFileIdentity+ByHandleFileInformation*)",
        "AgenticPrReview.Runtime.Agent.Tools.NativeReviewedFileIdentity|libc|fstat|System.Int32(System.Int32,AgenticPrReview.Runtime.Agent.Tools.NativeReviewedFileIdentity+LinuxFileInformation*)",
    ];

    [Fact]
    public void ProductionAgentSignaturesAndMethodBodiesAreCapabilityMinimal()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var types = AgentTypes(assembly).ToArray();
        Assert.NotEmpty(types);

        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var referenced in ReferencedTypes(type).SelectMany(ExpandTypeGraph))
            {
                if (IsForbiddenType(type, referenced))
                {
                    violations.Add(
                        $"signature:{type.FullName}->{referenced.FullName ?? referenced.Name}");
                }
            }

            foreach (var method in type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            {
                var body = method.GetMethodBody();
                if (body is not null)
                {
                    foreach (var local in body.LocalVariables)
                    {
                        if (IsForbiddenType(type, local.LocalType))
                        {
                            violations.Add(
                                $"local:{type.FullName}.{method.Name}->{local.LocalType.FullName}");
                        }
                    }

                    foreach (var clause in body.ExceptionHandlingClauses)
                    {
                        if (clause.Flags ==
                                ExceptionHandlingClauseOptions.Clause &&
                            clause.CatchType is { } catchType &&
                            IsForbiddenType(type, catchType))
                        {
                            violations.Add(
                                $"catch:{type.FullName}.{method.Name}->{catchType.FullName}");
                        }
                    }
                }

                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    if (IsForbiddenMember(type, member))
                    {
                        violations.Add(
                            $"body:{type.FullName}.{method.Name}->{FormatMember(member)}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionNativeImportsUseOnlyTheExactReviewedFileIdentitySet()
    {
        var imports = ReadNativeImports(
            typeof(RuntimeApplication).Assembly,
            typeName => typeName.StartsWith(AgentNamespace, StringComparison.Ordinal));

        Assert.Equal(
            AllowedNativeImports.Order(StringComparer.Ordinal),
            imports.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ScannerDetectsMethodBodyOnlyForbiddenCapabilities()
    {
        var method = typeof(AgentCapabilityArchitectureTests).GetMethod(
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
        var imports = ReadNativeImports(
            typeof(AgentCapabilityArchitectureTests).Assembly,
            typeName => StringComparer.Ordinal.Equals(
                typeName,
                typeof(AgentCapabilityArchitectureTests).FullName));
        var allowedFixture =
            typeof(AgentCapabilityArchitectureTests).FullName +
            "|libc|fstat|System.Int32(System.Int32,System.IntPtr)";
        var forbiddenFixture =
            typeof(AgentCapabilityArchitectureTests).FullName +
            "|libc|execve|System.Int32(System.IntPtr,System.IntPtr,System.IntPtr)";

        Assert.Contains(allowedFixture, imports);
        Assert.Contains(forbiddenFixture, imports);
        Assert.Empty(imports.Except([allowedFixture, forbiddenFixture]));
        Assert.Single(imports.Except([allowedFixture]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint MethodBodyOnlyForbiddenFixture()
    {
        _ = Environment.GetEnvironmentVariable("APR_ARCHITECTURE_FIXTURE");
        return NativeLibrary.Load("apr-architecture-fixture");
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

    private static IEnumerable<Type> AgentTypes(Assembly assembly) =>
        assembly.GetTypes().Where(type =>
            type.Namespace?.StartsWith(AgentNamespace, StringComparison.Ordinal) == true);

    private static bool IsForbiddenType(Type source, Type referenced)
    {
        var name = referenced.FullName ?? referenced.Name;
        if (GlobalForbidden(name))
        {
            return true;
        }

        if (source.Namespace?.StartsWith(ToolsNamespace, StringComparison.Ordinal) == true)
        {
            return name.Contains(
                "AgenticPrReview.Runtime.Host.IRuntimeFileSystem",
                StringComparison.Ordinal);
        }

        return name.StartsWith("System.IO.", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.Win32.SafeHandles.", StringComparison.Ordinal) ||
            name.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal);
    }

    private static bool IsForbiddenMember(Type source, MemberInfo member)
    {
        if (member is Type referencedType)
        {
            return IsForbiddenType(source, referencedType);
        }

        var declaring = member.DeclaringType;
        if (declaring is null)
        {
            return false;
        }

        var name = declaring.FullName ?? declaring.Name;
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

        var tools = source.Namespace?.StartsWith(
            ToolsNamespace,
            StringComparison.Ordinal) == true;
        if (!tools)
        {
            return name.StartsWith("System.IO.", StringComparison.Ordinal) ||
                name.StartsWith(
                    "Microsoft.Win32.SafeHandles.",
                    StringComparison.Ordinal) ||
                name.StartsWith(
                    "System.Runtime.InteropServices.",
                    StringComparison.Ordinal);
        }

        if (name.Contains(
                "AgenticPrReview.Runtime.Host.IRuntimeFileSystem",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!name.StartsWith("System.IO.", StringComparison.Ordinal) &&
            !name.StartsWith("Microsoft.Win32.SafeHandles.", StringComparison.Ordinal))
        {
            return false;
        }

        return !AllowedToolsFileMember(member);
    }

    private static bool AllowedToolsFileMember(MemberInfo member)
    {
        var type = member.DeclaringType?.FullName;
        return type switch
        {
            "System.IO.Path" => member.Name is
                "GetFullPath" or
                "IsPathFullyQualified" or
                "TrimEndingDirectorySeparator" or
                "Combine" or
                "get_DirectorySeparatorChar" or
                "DirectorySeparatorChar",
            "System.IO.Directory" => member.Name is "Exists",
            "System.IO.File" => member.Name is "OpenHandle" or "GetAttributes",
            "System.IO.RandomAccess" => member.Name is "GetLength" or "ReadAsync",
            "System.IO.FileInfo" or "System.IO.DirectoryInfo" => member.Name is
                ".ctor" or
                "get_Exists" or
                "get_Attributes" or
                "get_LinkTarget" or
                "get_Length",
            "Microsoft.Win32.SafeHandles.SafeFileHandle" => member.Name is
                "DangerousGetHandle" or
                "Dispose",
            "System.IO.FileSystemInfo" => member.Name is
                "get_Exists" or
                "get_Attributes" or
                "get_LinkTarget",
            _ => member.DeclaringType?.IsEnum == true ||
                type is "System.IO.IOException",
        };
    }

    private static bool GlobalForbidden(string name)
    {
        var fragments = new[]
        {
            "Microsoft.Extensions.AI",
            "System.Net.",
            "System.Diagnostics.Process",
            "System.Environment",
            "System.IServiceProvider",
            "Octokit",
            "GitHub",
            "Actions",
            "Publisher",
            "Shell",
            "HostSecret",
            "Credential",
            "AuthorizationHeader",
            "HttpClient",
        };
        return fragments.Any(fragment =>
            name.Contains(fragment, StringComparison.Ordinal));
    }

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

        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic))
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

    private static IEnumerable<MemberInfo> ResolveMethodBodyMembers(MethodInfo method)
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
                    $"Unmanaged or indirect calli is forbidden: {method.DeclaringType?.FullName}.{method.Name}");
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
                        method.GetGenericArguments())!;
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"IL token resolution failed for {method.DeclaringType?.FullName}.{method.Name}: {exception.GetType().Name}");
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
            throw new Xunit.Sdk.XunitException($"Unknown IL opcode 0x{value:x4}.");
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
                var token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(
                    methodHandle);
                var reflectionMethod = assembly.ManifestModule.ResolveMethod(token)!;
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
            return FullTypeName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        }

        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
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
