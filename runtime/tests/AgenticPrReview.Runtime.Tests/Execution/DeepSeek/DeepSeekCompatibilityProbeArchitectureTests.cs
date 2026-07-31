using System.Reflection;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.DeepSeekCompatibilityProbe;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed partial class DeepSeekTransportArchitectureTests
{
    [Fact]
    public void ProbeAssemblyUsesOnlyTheReviewedRuntimeSurface()
    {
        var runtimeAssembly = typeof(RuntimeApplication).Assembly;
        var allowed = new HashSet<Type>
        {
            typeof(MinimalChatRequest),
            typeof(MinimalChatMessage),
            typeof(MinimalChatMessage[]),
            typeof(MinimalChatContent),
            typeof(MinimalChatTool),
            typeof(MinimalChatContinuation),
            typeof(MinimalChatContinuationItem),
            typeof(DeepSeekRequestWriter),
            typeof(DeepSeekRequestWriteResult),
            typeof(DeepSeekRequestWriteOutcome),
            typeof(DeepSeekCredential),
            typeof(IDeepSeekTransport),
            typeof(DeepSeekTransport),
            typeof(DeepSeekTransportResult),
            typeof(DeepSeekTransportOutcome),
        };
        var observed = new HashSet<Type>();
        foreach (var type in typeof(DeepSeekCompatibilityProbeRunner)
                     .Assembly.GetTypes())
        {
            foreach (var referenced in ReferencedTypes(type)
                         .SelectMany(ExpandTypeGraph))
            {
                if (referenced.Assembly == runtimeAssembly)
                {
                    observed.Add(referenced);
                }
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    var referenced = member as Type ?? member.DeclaringType;
                    if (referenced?.Assembly == runtimeAssembly)
                    {
                        observed.Add(referenced);
                    }
                }
            }
        }

        Assert.NotEmpty(observed);
        Assert.Empty(observed.Except(allowed));
    }

    [Fact]
    public void ProbeAssemblyHasNoLaterOrAmbientCapabilities()
    {
        var forbiddenFragments = new[]
        {
            "DeepSeekLiveProviderExecutor",
            "Agent.Loop",
            "Agent.Session",
            "Host.State",
            "IProjectChatClient",
            "ProjectChatResponse",
            "Parser",
            "System.Net.",
            "HttpClient",
            "HttpMessageHandler",
            "GitHub",
            "Actions",
        };
        var violations = new List<string>();
        foreach (var type in typeof(DeepSeekCompatibilityProbeRunner)
                     .Assembly.GetTypes())
        {
            foreach (var referenced in ReferencedTypes(type)
                         .SelectMany(ExpandTypeGraph))
            {
                AddIfForbidden(
                    violations,
                    $"signature:{type.FullName}->{TypeName(referenced)}",
                    TypeName(referenced),
                    forbiddenFragments);
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    AddIfForbidden(
                        violations,
                        $"body:{type.FullName}.{method.Name}->{member}",
                        TypeName(member as Type ?? member.DeclaringType!),
                        forbiddenFragments);
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ProbeAssemblyReadsOnlyTheNamedProviderSecretOnce()
    {
        var environmentCalls = new List<(Type Type, MethodBase Method, MemberInfo Member)>();
        foreach (var type in typeof(DeepSeekCompatibilityProbeRunner)
                     .Assembly.GetTypes())
        {
            foreach (var method in DeclaredExecutableMembers(type))
            {
                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    if (member.DeclaringType == typeof(Environment))
                    {
                        environmentCalls.Add((type, method, member));
                    }
                }
            }
        }

        var call = Assert.Single(environmentCalls);
        Assert.Equal("GetEnvironmentVariable", call.Member.Name);
        Assert.Contains("Program", call.Type.FullName, StringComparison.Ordinal);
    }

    private static void AddIfForbidden(
        List<string> violations,
        string description,
        string candidate,
        IEnumerable<string> forbiddenFragments)
    {
        if (forbiddenFragments.Any(fragment => candidate.Contains(
                fragment,
                StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(description);
        }
    }
}
