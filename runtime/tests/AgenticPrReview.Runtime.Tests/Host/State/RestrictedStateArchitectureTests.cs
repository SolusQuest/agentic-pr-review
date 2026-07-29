using System.Reflection;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Host.State;

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
        Assert.All(
            typeof(IRestrictedStateStore).GetMethods(),
            method => Assert.Equal(
                typeof(AuthorizedStateAccess),
                method.GetParameters()[0].ParameterType));
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
                ("Close", "libc", "close"),
                ("CreateFile", "kernel32.dll", "CreateFileW"),
                ("FStat", "libc", "fstat"),
                ("FSync", "libc", "fsync"),
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
    }

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
