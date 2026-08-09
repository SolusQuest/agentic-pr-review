using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateArchitectureTests
{
    private const string StateNamespace =
        "AgenticPrReview.Runtime.Host.State";

    [Fact]
    public void StateSurfaceIsInternalAndHostOwned()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace == StateNamespace)
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic));
        Assert.DoesNotContain(
            types.SelectMany(ReferencedTypes),
            type =>
            {
                var name = type.FullName ?? type.Name;
                return name.Contains("Octokit", StringComparison.Ordinal) ||
                    name.Contains("System.Net.Http", StringComparison.Ordinal) ||
                    name.Contains("IServiceProvider", StringComparison.Ordinal) ||
                    name.Contains("System.Diagnostics.Process", StringComparison.Ordinal) ||
                    name.Contains("ProviderSessionLedgerV1", StringComparison.Ordinal) ||
                    name.Contains("StateManifestV2", StringComparison.Ordinal);
            });
    }

    [Fact]
    public void AuthorizationCapabilityCannotBeConstructedByCallers()
    {
        var constructors = typeof(AuthorizedStateAccess)
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

        var constructor = Assert.Single(constructors);
        Assert.True(constructor.IsPrivate);
        Assert.DoesNotContain(
            typeof(IRestrictedStateStore).GetMethods()
                .SelectMany(method => method.GetParameters()),
            parameter => ReferencedTypes(parameter.ParameterType)
                .Contains(typeof(AuthorizedStateAccess)));
        Assert.All(
            typeof(IRestrictedStateKeyResolver).GetMethods(),
            method => Assert.Equal(
                typeof(AuthorizedStateAccess),
                method.GetParameters()[0].ParameterType));
        Assert.All(
            typeof(IRestrictedStateSessionAdmission).GetMethods(),
            method => Assert.Equal(
                typeof(AuthorizedStateAccess),
                method.GetParameters()[0].ParameterType));
        var operations = typeof(RestrictedStateService)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(method => new[]
            {
                "EnumerateAsync",
                "PrepareAsync",
                "RestoreAsync",
                "AcceptAsync",
                "ReconcileAsync",
                "ResetAsync",
                "CleanupExpiredAsync",
                "PrepareHandoffAsync",
            }.Contains(method.Name, StringComparer.Ordinal))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(8, operations.Length);
        Assert.All(
            operations,
            method => Assert.Equal(
                typeof(AuthorizedStateAccess),
                method.GetParameters()[0].ParameterType));
    }

    [Fact]
    public void OpaqueStoreBoundaryIsInternalAndExactlySixAsyncOperations()
    {
        var store = typeof(IRestrictedStateStore);
        Assert.True(store.IsNotPublic);
        Assert.Equal(
            AgentLimits.StateEnvelopeBytes,
            OpaqueStoreLimits.MaximumObjectBytes);
        var methods = store.GetMethods()
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "DeleteExactAsync",
                "DownloadAsync",
                "ListExactAsync",
                "ReadBackExactAsync",
                "ReadMetadataAsync",
                "UploadImmutableAsync",
            },
            methods.Select(method => method.Name));
        Assert.All(methods, method =>
        {
            Assert.Equal(2, method.GetParameters().Length);
            Assert.Equal(
                typeof(CancellationToken),
                method.GetParameters()[1].ParameterType);
            Assert.True(method.ReturnType.IsGenericType);
            Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
            Assert.EndsWith("Request", method.GetParameters()[0].ParameterType.Name);
            Assert.EndsWith(
                "Result",
                method.ReturnType.GetGenericArguments()[0].Name);
        });

        var contractTypes = store.Assembly.GetTypes()
            .Where(type => type.Namespace ==
                "AgenticPrReview.Runtime.Host.State.OpaqueStore")
            .ToArray();
        Assert.NotEmpty(contractTypes);
        Assert.All(contractTypes, type => Assert.False(type.IsPublic));
        Assert.DoesNotContain(
            contractTypes.SelectMany(ReferencedTypes),
            IsForbiddenAdapterType);
    }

    [Fact]
    public void LocalAdapterSignaturesAndAsyncBodiesRemainTransportOnly()
    {
        var roots = ExpandNestedTypes(
            typeof(IRestrictedStateStore).Assembly.GetTypes()
                .Where(type => type.Namespace ==
                    "AgenticPrReview.Runtime.Host.State.OpaqueStore")
                .Concat(new[]
                {
                    typeof(LocalRestrictedStateStore),
                    typeof(LocalOpaqueStoreOperation),
                    typeof(LocalOpaqueStoreRead),
                    typeof(LocalOpaqueStoreRecordCodec),
                    typeof(NativeRestrictedStateFiles),
                    typeof(RestrictedStateFileIdentity),
                    typeof(RestrictedStateRootEntry),
                    typeof(RestrictedStateOpenResult),
                })).ToArray();

        var referenced = roots
            .SelectMany(ReferencedTypes)
            .Concat(roots.SelectMany(ReferencedBodyTypes))
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(referenced, IsForbiddenAdapterType);
    }

    [Fact]
    public async Task IlScannerFindsForbiddenReferenceInsideAsyncMoveNext()
    {
        await ForbiddenAsyncFixture.RunAsync();
        var roots = ExpandNestedTypes(new[]
        {
            typeof(ForbiddenAsyncFixture),
        });
        Assert.Contains(
            roots.SelectMany(ReferencedBodyTypes),
            type => type == typeof(RestrictedStateSnapshot));
    }

    [Fact]
    public void ActionAndCodeTaxonomyIsExactlyFrozen()
    {
        Assert.Equal(
            new[]
            {
                "Authorized",
                "Enumerated",
                "Prepared",
                "Restored",
                "Bootstrap",
                "Reset",
                "Accepted",
                "Idempotent",
                "HandoffReady",
                "Denied",
                "Failed",
            },
            Enum.GetNames<StateAction>());
        Assert.Equal(
            new[]
            {
                "state_absent",
                "state_accepted",
                "state_access_denied",
                "state_authentication_failed",
                "state_authorized",
                "state_cancelled",
                "state_cleanup_failed",
                "state_conflict",
                "state_current_missing",
                "state_enumerated",
                "state_enumeration_invalid",
                "state_envelope_invalid",
                "state_expired",
                "state_explicit_missing",
                "state_handoff_ready",
                "state_idempotent",
                "state_io_failed",
                "state_key_unavailable",
                "state_lineage_mismatch",
                "state_prepared",
                "state_replay_rejected",
                "state_reset",
                "state_restored",
            },
            RestrictedStateCodes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DiagnosticsContractsContainNoSecretOrContentFields()
    {
        var diagnosticTypes = new[]
        {
            typeof(StateResult),
            typeof(PreparedStateReceipt),
            typeof(RestrictedStateHandoffReceipt),
        };
        var prohibited = new[]
        {
            "Plaintext",
            "Ciphertext",
            "Nonce",
            "Tag",
            "Aad",
            "Key",
            "Path",
            "Authorization",
            "Continuation",
            "Tool",
        };

        Assert.DoesNotContain(
            diagnosticTypes.SelectMany(type => type.GetProperties()),
            property => prohibited.Any(value =>
                property.Name.Contains(
                    value,
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NativeFileImportsAreNarrowAndPinned()
    {
        var methods = typeof(NativeRestrictedStateFiles)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.NonPublic)
            .Select(method => (
                Method: method,
                Import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(item => item.Import is not null)
            .Select(item => (
                item.Method.Name,
                Library: item.Import!.LibraryName,
                item.Import.EntryPoint))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new (string, string, string?)[]
            {
                ("CreateFile", "kernel32.dll", "CreateFileW"),
                ("FStat", "libc", "fstat"),
                ("FSync", "libc", "fsync"),
                ("Fcntl", "libc", "fcntl"),
                (
                    "GetFileInformationByHandle",
                    "kernel32.dll",
                    "GetFileInformationByHandle"),
                ("Open", "libc", "open"),
            },
            methods);
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        yield return type;
        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var nested in Expand(field.FieldType))
            {
                yield return nested;
            }
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var nested in Expand(property.PropertyType))
            {
                yield return nested;
            }
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var nested in Expand(method.ReturnType))
            {
                yield return nested;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var nested in Expand(parameter.ParameterType))
                {
                    yield return nested;
                }
            }
        }

        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var nested in Expand(parameter.ParameterType))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandNestedTypes(
        IEnumerable<Type> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var nested in ExpandNestedTypes(root.GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic)))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<Type> ReferencedBodyTypes(Type type)
    {
        var methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly));
        foreach (var method in methods)
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                foreach (var expanded in Expand(local.LocalType))
                {
                    yield return expanded;
                }
            }

            foreach (var clause in body.ExceptionHandlingClauses)
            {
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause &&
                    clause.CatchType is { } catchType)
                {
                    yield return catchType;
                }
            }

            foreach (var member in ResolveMethodBodyMembers(method))
            {
                foreach (var referenced in MemberTypes(member))
                {
                    foreach (var expanded in Expand(referenced))
                    {
                        yield return expanded;
                    }
                }
            }
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
            var tokenOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (opCode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok or
                OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(il, tokenOffset);
                yield return method.Module.ResolveMember(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo info
                        ? info.GetGenericArguments()
                        : null)!;
            }

            offset += operandSize;
        }
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member)
    {
        if (member.DeclaringType is { } declaring)
        {
            yield return declaring;
        }

        switch (member)
        {
            case Type type:
                yield return type;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
                break;
            case ConstructorInfo constructor:
                foreach (var parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
                break;
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        var value = first == 0xfe
            ? (ushort)(0xfe00 | il[offset++])
            : first;
        return OpCodesByValue[value];
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
            _ => throw new InvalidOperationException(
                $"Unsupported IL operand type {operandType}."),
        };

    private static bool IsForbiddenAdapterType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return new[]
        {
            "RestrictedStateSnapshot",
            "RestrictedStateCandidate",
            "RestrictedStateBinding",
            "AcceptedLineage",
            "AgentSession",
            "RestrictedStateKey",
            "StateResult",
            "AgenticPrReview.Runtime.Agent",
            "Octokit",
            "System.Net.Http",
            "IServiceProvider",
            "System.Diagnostics.Process",
            "ProviderSession",
            "Publication",
            "GitHub",
        }.Any(value => name.Contains(value, StringComparison.Ordinal));
    }

    private sealed class ForbiddenAsyncFixture
    {
        internal static async Task RunAsync()
        {
            await Task.Yield();
            GC.KeepAlive(RestrictedStateSnapshot.Empty);
        }
    }

    private static readonly IReadOnlyDictionary<ushort, OpCode>
        OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Expand(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument))
            {
                yield return nested;
            }
        }
    }
}
