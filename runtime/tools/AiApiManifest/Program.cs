using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AgenticPrReview.Runtime.AiApiManifest;

internal static class Program
{
    private const string PackageAssembly = "Microsoft.Extensions.AI.Abstractions";
    private const string PackageOwner = "Microsoft.Extensions.AI.Abstractions/10.8.1";

    private static readonly IReadOnlyDictionary<short, OpCode> Opcodes =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => opcode.Value);

    public static int Main(string[] args)
    {
        if (args.Length != 4 ||
            (args[3] != "project" && args[3] != "package"))
        {
            Console.Error.WriteLine(
                "APR_AI_API_MANIFEST_USAGE <assembly> <pdb> <candidate> <project|package>");
            return 2;
        }

        try
        {
            var lines = Generate(args[0], args[1], args[2], args[3]);
            Console.Out.Write(string.Join('\n', lines));
            Console.Out.Write('\n');
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("APR_AI_API_MANIFEST_GENERATION_FAILED");
            return 1;
        }
    }

    private static IReadOnlyList<string> Generate(
        string assemblyPath,
        string pdbPath,
        string candidate,
        string mode)
    {
        using var assemblyStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(assemblyStream);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException();
        }

        using var pdbStream = File.OpenRead(pdbPath);
        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(
            pdbStream);
        var metadata = peReader.GetMetadataReader();
        var pdb = pdbProvider.GetMetadataReader();
        var adapterFragment = $"/Candidates/{candidate}/Adapter/";
        var sourceMethods = metadata.MethodDefinitions
            .Where(handle => HasAdapterSequencePoint(
                handle,
                pdb,
                adapterFragment))
            .ToHashSet();
        foreach (var handle in sourceMethods.ToArray())
        {
            var row = MetadataTokens.GetRowNumber(handle);
            var debug = pdb.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(row));
            var kickoff = debug.GetStateMachineKickoffMethod();
            if (!kickoff.IsNil)
            {
                sourceMethods.Add(kickoff);
            }
        }

        if (sourceMethods.Count == 0)
        {
            throw new InvalidDataException();
        }

        return mode == "project"
            ? GenerateProjectManifest(metadata, sourceMethods, candidate)
            : GeneratePackageManifest(
                peReader,
                metadata,
                sourceMethods,
                candidate);
    }

    private static IReadOnlyList<string> GenerateProjectManifest(
        MetadataReader metadata,
        IReadOnlySet<MethodDefinitionHandle> sourceMethods,
        string candidate)
    {
        var sourceTypes = sourceMethods
            .Select(handle => metadata.GetMethodDefinition(handle)
                .GetDeclaringType())
            .Where(handle => !IsGeneratedType(metadata, handle))
            .Distinct()
            .ToArray();
        var rows = new List<string>
        {
            "candidate\towner\tkind\tdeclaring_type\tmember\tsignature_hex",
        };

        foreach (var typeHandle in sourceTypes.OrderBy(
                     handle => GetTypeName(metadata, handle),
                     StringComparer.Ordinal))
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = GetTypeName(metadata, typeHandle);
            rows.Add($"{candidate}\tproject\ttype\t{typeName}\t-\t-");

            foreach (var methodHandle in type.GetMethods())
            {
                if (!sourceMethods.Contains(methodHandle))
                {
                    continue;
                }

                var method = metadata.GetMethodDefinition(methodHandle);
                var name = metadata.GetString(method.Name);
                if ((method.Attributes & MethodAttributes.SpecialName) != 0 &&
                    name != ".ctor" &&
                    name != ".cctor")
                {
                    continue;
                }

                rows.Add(
                    $"{candidate}\tproject\tmethod\t{typeName}\t{name}\t" +
                    GetBlobHex(metadata, method.Signature));
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                rows.Add(
                    $"{candidate}\tproject\tproperty\t{typeName}\t" +
                    $"{metadata.GetString(property.Name)}\t" +
                    GetBlobHex(metadata, property.Signature));
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                var name = metadata.GetString(field.Name);
                if (name.Contains('<', StringComparison.Ordinal))
                {
                    continue;
                }

                rows.Add(
                    $"{candidate}\tproject\tfield\t{typeName}\t{name}\t" +
                    GetBlobHex(metadata, field.Signature));
            }
        }

        return new[]
            {
                rows[0],
            }
            .Concat(rows.Skip(1).Order(StringComparer.Ordinal))
            .ToArray();
    }

    private static IReadOnlyList<string> GeneratePackageManifest(
        PEReader peReader,
        MetadataReader metadata,
        IReadOnlySet<MethodDefinitionHandle> sourceMethods,
        string candidate)
    {
        var memberReferences = new HashSet<MemberReferenceHandle>();
        foreach (var methodHandle in sourceMethods)
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            if (il is null)
            {
                throw new BadImageFormatException();
            }
            CollectMemberReferences(
                il,
                metadata,
                memberReferences);
        }

        var rows = new List<string>
        {
            "candidate\towner\tkind\tdeclaring_type\tmember\tsignature_hex",
        };
        foreach (var handle in memberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (!TryGetPackageTypeName(
                    metadata,
                    member.Parent,
                    out var declaringType))
            {
                continue;
            }

            rows.Add(
                $"{candidate}\t{PackageOwner}\tmember_reference\t" +
                $"{declaringType}\t{metadata.GetString(member.Name)}\t" +
                GetBlobHex(metadata, member.Signature));
        }

        return new[]
            {
                rows[0],
            }
            .Concat(rows.Skip(1).Order(StringComparer.Ordinal))
            .ToArray();
    }

    private static bool HasAdapterSequencePoint(
        MethodDefinitionHandle methodHandle,
        MetadataReader pdb,
        string adapterFragment)
    {
        var row = MetadataTokens.GetRowNumber(methodHandle);
        var debugHandle = MetadataTokens.MethodDebugInformationHandle(row);
        if (debugHandle.IsNil)
        {
            return false;
        }

        var debug = pdb.GetMethodDebugInformation(debugHandle);
        var defaultDocument = debug.Document;
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.IsHidden)
            {
                continue;
            }

            var documentHandle = point.Document.IsNil
                ? defaultDocument
                : point.Document;
            if (documentHandle.IsNil)
            {
                continue;
            }

            var document = pdb.GetDocument(documentHandle);
            var path = pdb.GetString(document.Name).Replace('\\', '/');
            if (path.Contains(adapterFragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedType(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var name = metadata.GetString(metadata.GetTypeDefinition(handle).Name);
        return name.Contains('<', StringComparison.Ordinal);
    }

    private static string GetTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);
        var parent = type.GetDeclaringType();
        if (!parent.IsNil)
        {
            return $"{GetTypeName(metadata, parent)}+{name}";
        }

        var typeNamespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace)
            ? name
            : $"{typeNamespace}.{name}";
    }

    private static string GetTypeName(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return
                $"{GetTypeName(metadata, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        var typeNamespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace)
            ? name
            : $"{typeNamespace}.{name}";
    }

    private static bool TryGetPackageTypeName(
        MetadataReader metadata,
        EntityHandle parent,
        out string typeName)
    {
        typeName = string.Empty;
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var handle = (TypeReferenceHandle)parent;
        var scope = metadata.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = metadata.GetTypeReference(
                (TypeReferenceHandle)scope).ResolutionScope;
        }

        if (scope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assembly = metadata.GetAssemblyReference(
            (AssemblyReferenceHandle)scope);
        if (!string.Equals(
                metadata.GetString(assembly.Name),
                PackageAssembly,
                StringComparison.Ordinal))
        {
            return false;
        }

        typeName = GetTypeName(metadata, handle);
        return true;
    }

    private static void CollectMemberReferences(
        byte[] il,
        MetadataReader metadata,
        ISet<MemberReferenceHandle> output)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            short value = il[offset++];
            if (value == 0xfe)
            {
                if (offset >= il.Length)
                {
                    throw new BadImageFormatException();
                }

                value = unchecked((short)(0xfe00 | il[offset++]));
            }

            if (!Opcodes.TryGetValue(value, out var opcode))
            {
                throw new BadImageFormatException();
            }

            var operandOffset = offset;
            var operandSize = GetOperandSize(opcode.OperandType, il, offset);
            if (operandOffset + operandSize > il.Length)
            {
                throw new BadImageFormatException();
            }

            if (opcode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok)
            {
                AddMemberReference(
                    BitConverter.ToInt32(il, operandOffset),
                    metadata,
                    output);
            }

            offset += operandSize;
        }
    }

    private static int GetOperandSize(
        OperandType operandType,
        byte[] il,
        int offset) => operandType switch
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
            OperandType.InlineI8 or
            OperandType.InlineR => 8,
            OperandType.InlineSwitch => GetSwitchOperandSize(il, offset),
            _ => throw new BadImageFormatException(),
        };

    private static int GetSwitchOperandSize(byte[] il, int offset)
    {
        if (offset + sizeof(int) > il.Length)
        {
            throw new BadImageFormatException();
        }

        var count = BitConverter.ToInt32(il, offset);
        if (count < 0)
        {
            throw new BadImageFormatException();
        }

        return checked(sizeof(int) + (count * sizeof(int)));
    }

    private static void AddMemberReference(
        int token,
        MetadataReader metadata,
        ISet<MemberReferenceHandle> output)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MemberReference)
        {
            output.Add((MemberReferenceHandle)handle);
            return;
        }

        if (handle.Kind != HandleKind.MethodSpecification)
        {
            return;
        }

        var method = metadata.GetMethodSpecification(
            (MethodSpecificationHandle)handle).Method;
        if (method.Kind == HandleKind.MemberReference)
        {
            output.Add((MemberReferenceHandle)method);
        }
    }

    private static string GetBlobHex(
        MetadataReader metadata,
        BlobHandle handle) =>
        Convert.ToHexString(metadata.GetBlobBytes(handle))
            .ToLower(CultureInfo.InvariantCulture);
}
