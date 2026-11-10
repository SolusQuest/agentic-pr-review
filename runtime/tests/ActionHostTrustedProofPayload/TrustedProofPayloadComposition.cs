using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

#if !APR_TEST_ORIGINAL_RUNTIME_PREVIEW
internal static class TrustedProofPayloadComposition
{
    internal static ActionHostCompositionDependencies CreateProductionLike(
        TrustedProofGitHubRequestBudget githubBudget,
        TrustedProofPayloadRuntimePorts ports,
        ITrustedProofStaleSignal? staleSignal = null,
        TrustedProofRequestBudgetProfile? requestBudgetProfile = null)
    {
        ArgumentNullException.ThrowIfNull(githubBudget);
        ArgumentNullException.ThrowIfNull(ports);
        var github = new ActionHostGitHubAuthorizationTransportFactory(
            githubBudget.CreateOtherGitHubHandler);
        // This capability is passed only to ReviewedTreeReader.  It labels
        // the authenticated commit/tree/tarball acquisition of the frozen
        // reviewed head; policy, base-diff, revalidation, and snapshot outer
        // routes remain on the ordinary GitHub capability above.
        var reviewedHeadSource = new ActionHostGitHubAuthorizationTransportFactory(
            githubBudget.CreateHeadSourceHandler);
        var publisher = new BoundedGitHubPublisherTransportFactory(
            githubBudget.CreateHandler);
        var provider = new CurrentHostAuthoredRunnerFactory(ports, staleSignal);
        var artifactPolicy = ArtifactRestBudgetPolicy.Protected(
            (requestBudgetProfile ?? TrustedProofRequestBudgetProfile.Measurement).MeasurementOnly);
        var stateDependencies = ports.CreateStateDependencies(github);
        if (stateDependencies is AcceptedStateProductionDependencies production)
            stateDependencies = production.WithArtifactPolicy(artifactPolicy);
        return new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            stateDependencies,
            publisher,
            provider,
            ports.TimeProvider,
            ports.StagingParentFactory,
            new PostAcceptanceInlinePublisherHook(publisher),
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance,
            reviewedHeadSourceFactory: reviewedHeadSource,
            changedFileSourceFactory:
                TrustedProofChangedFileSourceFactory.Instance,
            stateReconciliationDiagnosticSink:
                StateReconciliationStderrSink.Instance,
            artifactBudgetPolicy: artifactPolicy);
    }
}
#endif

#if APR_TEST_ORIGINAL_RUNTIME_PREVIEW
// Read-only metadata stage of the isolated friend reference. This is explicitly
// not the genuine-seal loader or a Host/provider execution capability.
internal static class OriginalRuntimePreviewReferenceProbe
{
    internal static int Inspect()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64) return 2;
        var root = OriginalRuntimeReferenceBuildIdentity.Root;
        if (Path.GetFullPath(root) != root || !Directory.Exists(root) ||
            new DirectoryInfo(root).LinkTarget is not null ||
            (File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) return 2;
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) return 2;
        var file = new FileInfo(Path.Combine(root, "AgenticPrReview.Runtime.dll"));
        if (file.LinkTarget is not null || !file.Exists || file.Length is <= 0 or > 16 * 1024 * 1024) return 2;
        using var stream = file.OpenRead();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        if (hash != OriginalRuntimeReferenceBuildIdentity.AssemblySHA256) return 2;
        stream.Position = 0;
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!reader.HasMetadata) return 2;
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
        if (metadata.GetString(metadata.GetAssemblyDefinition().Name) != "AgenticPrReview.Runtime") return 2;
        Console.WriteLine("APR_TEST_ORIGINAL_REFERENCE_METADATA sha256=" + hash +
            " assembly_loaded=false qualification=false provider_calls=0");
        return 0;
    }
}
#endif

#if APR_TEST_ORIGINAL_RUNTIME_PREVIEW
// BCL-only until every original member has been independently opened and hashed.
// The parent supplies fd4 only from its live VerifiedManagedTree lease, inside the
// genuine once-consumed seal. Embedded expected bytes never recreate that seal.
internal sealed class OriginalRuntimePreviewLoader : IDisposable
{
    private readonly Dictionary<string, OriginalFile> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OriginalFile> assemblies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Reflection.Assembly> loaded = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private System.Runtime.Loader.AssemblyLoadContext? resolver;
    internal string WorkflowCommit { get; private set; } = "";
    internal string TemplateSha256 { get; private set; } = "";
    internal string ReviewBaseSha { get; private set; } = "";
    internal string ArchiveSha256 { get; private set; } = "";
    internal string ReceiptSha256 { get; private set; } = "";
    internal long RunId { get; private set; }
    internal bool RuntimeLoaded => loaded.ContainsKey("AgenticPrReview.Runtime");
    private sealed record OriginalFile(string Path, long Size, string Sha256,
        Microsoft.Win32.SafeHandles.SafeFileHandle Handle, NativeStat Snapshot,
        System.Reflection.AssemblyName? AssemblyName);

    internal static OriginalRuntimePreviewLoader Open()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64) throw Rejected();
        var owner = new OriginalRuntimePreviewLoader();
        try { owner.Initialize(); return owner; }
        catch { owner.Dispose(); throw; }
    }

    private void Initialize()
    {
        using var resource = typeof(OriginalRuntimePreviewLoader).Assembly.GetManifestResourceStream(
            "APR.TestOnly.OriginalRuntime.Context") ?? throw Rejected();
        if (resource.Length is < 1 or > 131072 || !Hex(OriginalRuntimeReferenceBuildIdentity.ContextSHA256, 64)) throw Rejected();
        var bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        if (Sha(bytes) != OriginalRuntimeReferenceBuildIdentity.ContextSHA256) throw Rejected();
        using var document = System.Text.Json.JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        Unique(document.RootElement);
        var context = document.RootElement;
        Keys(context, "workflowCommit", "workflowTemplateSha256", "runId", "reviewBaseSha", "receiptBase64");
        WorkflowCommit = Text(context, "workflowCommit", 40);
        TemplateSha256 = Text(context, "workflowTemplateSha256", 64);
        ReviewBaseSha = Text(context, "reviewBaseSha", 40);
        RunId = context.GetProperty("runId").GetInt64();
        if (RunId <= 0 || WorkflowCommit == TrustedProofPayloadBuildIdentity.SourceCommit) throw Rejected();
        var receiptBytes = context.GetProperty("receiptBase64").GetBytesFromBase64();
        if (receiptBytes.Length is < 1 or > 65536) throw Rejected();
        ReceiptSha256 = Sha(receiptBytes);
        using var receiptDocument = System.Text.Json.JsonDocument.Parse(receiptBytes, new() { MaxDepth = 16 });
        Unique(receiptDocument.RootElement);
        var receipt = receiptDocument.RootElement;
        Keys(receipt, "formatVersion", "archiveName", "archiveSize", "archiveSha256", "identity", "members");
        if (receipt.GetProperty("formatVersion").GetString() != "managed-v1") throw Rejected();
        ArchiveSha256 = Text(receipt, "archiveSha256", 64);
        var identity = receipt.GetProperty("identity");
        if (Text(identity, "sourceCommit", 40) != TrustedProofPayloadBuildIdentity.SourceCommit ||
            Text(identity, "sourceTree", 40) != TrustedProofPayloadBuildIdentity.SourceTree) throw Rejected();
        // Descriptor inheritance is explicit. Do not leak these loading capabilities to SDK children.
        if (Native.Fcntl(4, 2, 1) != 0 || Native.Fcntl(5, 2, 1) != 0) throw Rejected();
        var root = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)Native.Fcntl(4, 1030, 10), true);
        if (root.IsInvalid) throw Rejected();
        directories.Add("", root);
        Owned(Stat(root), true);
        var fixture = StatFd(5);
        Owned(fixture, true);
        var members = receipt.GetProperty("members");
        if (members.ValueKind != System.Text.Json.JsonValueKind.Array || members.GetArrayLength() is < 3 or > 512) throw Rejected();
        long totalBytes = 0;
        foreach (var member in members.EnumerateArray())
        {
            Keys(member, "path", "mode", "size", "sha256");
            var name = member.GetProperty("path").GetString() ?? throw Rejected();
            var parts = name.Split('/');
            if (name.Length > 100 || parts.Length is < 2 or > 8 || parts[0] != "agentic-pr-review" ||
                parts.Any(p => p.Length is < 1 or > 128 || p is "." or ".." ||
                    p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))) throw Rejected();
            if (member.GetProperty("mode").GetInt32() != 420) throw Rejected();
            var size = member.GetProperty("size").GetInt64();
            if (size is < 1 or > 33554432 || files.ContainsKey(name) ||
                (totalBytes = checked(totalBytes + size)) > 134217728) throw Rejected();
            var parent = DirectoryFor(string.Join('/', parts[..^1]));
            var fd = Native.OpenAt(parent.DangerousGetHandle().ToInt32(), parts[^1], 0xA0800);
            if (fd < 0) throw Rejected(); // O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true);
            try
            {
                var stat = Stat(handle);
                Owned(stat, false);
                if (stat.Size != (ulong)size) throw Rejected();
                var content = Read(handle, size);
                var hash = Text(member, "sha256", 64);
                if (Sha(content) != hash || !Same(stat, Stat(handle))) throw Rejected();
                System.Reflection.AssemblyName? assemblyName = null;
                if (name.EndsWith(".dll", StringComparison.Ordinal))
                {
                    using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(content, false));
                    if (!pe.HasMetadata) throw Rejected();
                    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    var definition = metadata.GetAssemblyDefinition();
                    assemblyName = new(metadata.GetString(definition.Name)) { Version = definition.Version,
                        CultureName = definition.Culture.IsNil ? "" : metadata.GetString(definition.Culture) };
                    if (!definition.PublicKey.IsNil) assemblyName.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
                    else assemblyName.SetPublicKeyToken([]); // Explicit unsigned identity, matching CLR reference names.
                }
                else if (content.AsSpan().StartsWith(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }))
                    throw Rejected(); // No unreviewed native dependency search in this fixed managed route.
                var file = new OriginalFile(name, size, hash, handle, stat, assemblyName);
                files.Add(name, file);
                if (assemblyName?.Name is { } simple && !assemblies.TryAdd(simple, file)) throw Rejected();
            }
            catch { handle.Dispose(); throw; }
        }
        if (!assemblies.TryGetValue("AgenticPrReview.Runtime", out var runtime) ||
            runtime.Path != "agentic-pr-review/AgenticPrReview.Runtime.dll" ||
            runtime.Sha256 != OriginalRuntimeReferenceBuildIdentity.AssemblySHA256) throw Rejected();
        Validate();
        var frameworkNames = Directory.EnumerateFiles(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        if (assemblies.Keys.Any(frameworkNames.Contains)) throw Rejected();
        // A preload from another path is not an acceptable implementation of this reference.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (!FrameworkOrFixture(assembly)) throw Rejected();
        resolver = System.Runtime.Loader.AssemblyLoadContext.Default;
        resolver.Resolving += Resolve;
        _ = Resolve(resolver, runtime.AssemblyName!);
    }

    private Microsoft.Win32.SafeHandles.SafeFileHandle DirectoryFor(string relative)
    {
        if (directories.TryGetValue(relative, out var retained)) return retained;
        var slash = relative.LastIndexOf('/');
        var parent = DirectoryFor(slash < 0 ? "" : relative[..slash]);
        var name = relative[(slash + 1)..];
        var fd = Native.OpenAt(parent.DangerousGetHandle().ToInt32(), name, 0xB0000);
        if (fd < 0) throw Rejected(); // O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true);
        try { Owned(Stat(handle), true); directories.Add(relative, handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal void Validate()
    {
        foreach (var (relative, handle) in directories)
        {
            Owned(Stat(handle), true);
            if (relative != "")
            {
                var slash = relative.LastIndexOf('/');
                using var named = OpenNamed(directories[slash < 0 ? "" : relative[..slash]], relative[(slash + 1)..], true);
                if (!Same(Stat(handle), Stat(named))) throw Rejected();
            }
            var expected = directories.Keys.Concat(files.Keys).Where(p => p.Length > relative.Length &&
                (relative == "" || p.StartsWith(relative + "/", StringComparison.Ordinal)))
                .Select(p => p[(relative == "" ? 0 : relative.Length + 1)..].Split('/')[0]).Distinct().Order().ToArray();
            var actual = Directory.EnumerateFileSystemEntries("/proc/self/fd/" + handle.DangerousGetHandle().ToInt32())
                .Take(513).Select(Path.GetFileName).Order().ToArray();
            if (!actual.SequenceEqual(expected)) throw Rejected();
        }
        foreach (var file in files.Values)
        {
            var slash = file.Path.LastIndexOf('/');
            using var named = OpenNamed(directories[file.Path[..slash]], file.Path[(slash + 1)..], false);
            if (!Same(file.Snapshot, Stat(file.Handle)) || !Same(file.Snapshot, Stat(named)) ||
                Sha(Read(file.Handle, file.Size)) != file.Sha256 || !Same(file.Snapshot, Stat(file.Handle))) throw Rejected();
        }
    }

    private System.Reflection.Assembly Resolve(System.Runtime.Loader.AssemblyLoadContext context,
        System.Reflection.AssemblyName requested)
    {
        lock (sync)
        {
            if (requested.Name is not { } name || !assemblies.TryGetValue(name, out var file) ||
                requested.FullName != file.AssemblyName!.FullName) throw Rejected();
            if (loaded.TryGetValue(name, out var prior)) return prior;
            if (!Same(file.Snapshot, Stat(file.Handle))) throw Rejected();
            var bytes = Read(file.Handle, file.Size);
            if (Sha(bytes) != file.Sha256 || !Same(file.Snapshot, Stat(file.Handle))) throw Rejected();
            using var stream = new MemoryStream(bytes, false);
            var assembly = context.LoadFromStream(stream);
            if (assembly.GetName().FullName != requested.FullName) throw Rejected();
            loaded.Add(name, assembly);
            return assembly;
        }
    }

    private static bool FrameworkOrFixture(System.Reflection.Assembly assembly) =>
        ReferenceEquals(assembly, typeof(OriginalRuntimePreviewLoader).Assembly) ||
        (!string.IsNullOrEmpty(assembly.Location) &&
         Path.GetDirectoryName(assembly.Location) == Path.TrimEndingDirectorySeparator(
             System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()));
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenNamed(
        Microsoft.Win32.SafeHandles.SafeFileHandle parent, string name, bool directory)
    {
        var fd = Native.OpenAt(parent.DangerousGetHandle().ToInt32(), name, directory ? 0xB0000 : 0xA0800);
        if (fd < 0) throw Rejected();
        return new((IntPtr)fd, true);
    }
    private static byte[] Read(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long length)
    {
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw Rejected();
            offset += count;
        }
        return bytes;
    }
    private static string Text(System.Text.Json.JsonElement value, string name, int size)
    {
        var text = value.GetProperty(name).GetString() ?? throw Rejected();
        return Hex(text, size) ? text : throw Rejected();
    }
    private static bool Hex(string value, int size) => value.Length == size && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    private static void Keys(System.Text.Json.JsonElement value, params string[] names)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Order())) throw Rejected();
    }
    private static void Unique(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!seen.Add(property.Name)) throw Rejected(); Unique(property.Value); }
        }
        else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static NativeStat Stat(Microsoft.Win32.SafeHandles.SafeFileHandle handle) => StatFd(handle.DangerousGetHandle().ToInt32());
    private static NativeStat StatFd(int fd) => Native.Statx(fd, "", 0x1100, 0x7ff, out var stat) == 0 ? stat : throw Rejected();
    private static void Owned(NativeStat value, bool directory)
    {
        if ((value.Mask & 0x7ff) != 0x7ff || value.Uid != Native.GetUid() ||
            value.Mode != (directory ? 0x4140 : 0x8100) || (!directory && value.Links != 1)) throw Rejected();
    }
    private static bool Same(NativeStat a, NativeStat b) => a.Inode == b.Inode && a.DeviceMajor == b.DeviceMajor &&
        a.DeviceMinor == b.DeviceMinor && a.Uid == b.Uid && a.Mode == b.Mode && a.Links == b.Links && a.Size == b.Size &&
        a.ModificationSeconds == b.ModificationSeconds && a.ModificationNanoseconds == b.ModificationNanoseconds &&
        a.ChangeSeconds == b.ChangeSeconds && a.ChangeNanoseconds == b.ChangeNanoseconds;
    private static InvalidOperationException Rejected() => new("APR_TEST_ORIGINAL_LOADER_REJECTED");
    public void Dispose()
    {
        if (resolver is not null) resolver.Resolving -= Resolve;
        foreach (var file in files.Values) file.Handle.Dispose();
        foreach (var directory in directories.Values) directory.Dispose();
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
    private struct NativeStat
    {
        [System.Runtime.InteropServices.FieldOffset(0)] internal uint Mask;
        [System.Runtime.InteropServices.FieldOffset(16)] internal uint Links;
        [System.Runtime.InteropServices.FieldOffset(20)] internal uint Uid;
        [System.Runtime.InteropServices.FieldOffset(28)] internal ushort Mode;
        [System.Runtime.InteropServices.FieldOffset(32)] internal ulong Inode;
        [System.Runtime.InteropServices.FieldOffset(40)] internal ulong Size;
        [System.Runtime.InteropServices.FieldOffset(96)] internal long ChangeSeconds;
        [System.Runtime.InteropServices.FieldOffset(104)] internal uint ChangeNanoseconds;
        [System.Runtime.InteropServices.FieldOffset(112)] internal long ModificationSeconds;
        [System.Runtime.InteropServices.FieldOffset(120)] internal uint ModificationNanoseconds;
        [System.Runtime.InteropServices.FieldOffset(136)] internal uint DeviceMajor;
        [System.Runtime.InteropServices.FieldOffset(140)] internal uint DeviceMinor;
    }
    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        internal static extern int OpenAt(int fd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name, int flags);
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(int fd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name, int flags, uint mask, out NativeStat stat);
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Fcntl(int fd, int command, int value);
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")] internal static extern uint GetUid();
    }
}

internal static class OriginalRuntimePreviewHost
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static async Task<int> RunAsync(OriginalRuntimePreviewLoader original, bool bootstrap)
    {
        var role = OriginalRuntimeReferenceBuildIdentity.SourceRole switch {
            "pr" => OriginalRuntimePreviewSourceRole.PullRequest,
            "main" => OriginalRuntimePreviewSourceRole.ActualMain,
            _ => throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_ROLE"),
        };
        var phase = bootstrap ? OriginalRuntimePreviewPhase.Bootstrap : OriginalRuntimePreviewPhase.Continuation;
        var expected = new OriginalRuntimePreviewExpected(TrustedProofPayloadBuildIdentity.SourceCommit,
            TrustedProofPayloadBuildIdentity.SourceTree, original.WorkflowCommit, original.TemplateSha256,
            original.RunId, 1, original.ArchiveSha256, original.ReceiptSha256,
            OriginalRuntimeReferenceBuildIdentity.AssemblySHA256, original.ReviewBaseSha,
            OriginalRuntimePreviewWorkflowAdmission.TargetHead(role, phase), role, phase);
        var admission = new OriginalRuntimePreviewWorkflowAdmission(expected);
        var production = ActionHostCompositionDependencies.Production();
        var dependencies = new ActionHostCompositionDependencies(production.EventReader,
            production.AuthorizationFactory, production.GitObjectFactory, production.SnapshotFactory,
            production.StateDependencies, production.PublisherFactory, production.ProviderFactory,
            production.TimeProvider, production.StagingParentFactory, production.InlineHook, admission,
            production.ReviewedHeadSourceFactory, production.ChangedFileSourceFactory,
            production.StateReconciliationDiagnosticSink, production.ArtifactBudgetPolicy);
        var host = new ActionHostComposition(dependencies);
        // Fixed original managed type only. A nonmanaged reference can compile the metadata
        // fixture but cannot supply a fallback review implementation at runtime.
        var type = typeof(ActionHostComposition).Assembly.GetType(
            "AgenticPrReview.Runtime.ActionHost.SolusManagedActionComposition", throwOnError: true)!;
        var constructor = type.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(ActionHostComposition), typeof(Func<HttpMessageHandler>)], null) ?? throw new InvalidOperationException();
        var composition = constructor.Invoke([host, null]); // ordinary fixed HTTPS provider transport
        var run = type.GetMethod("RunAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostLaunchContract), typeof(CancellationToken)], null)
            ?? throw new InvalidOperationException();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1020));
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
            signal => { signal.Cancel = true; stop.Cancel(); });
        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT,
            signal => { signal.Cancel = true; stop.Cancel(); });
        original.Validate();
        byte[] observationBytes = "{\"classification\":\"Unavailable\"}"u8.ToArray();
        var result = await AgenticPrReview.Runtime.RuntimeApplication.RunActionHostAsync(
            Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, stop.Token,
            async (launch, cancellation) => {
                await RequireActualSourceRoleAsync(role, launch.Inputs.GitHubToken, cancellation).ConfigureAwait(false);
                var completion = await ((Task<AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostCompletion>)
                    (run.Invoke(composition, [launch, cancellation]) ?? throw new InvalidOperationException())).ConfigureAwait(false);
                // Fixed read of the exact newly built original's completed invocation.
                // Observation failure never replaces normal completion/accounting.
                try
                {
                    var getter = type.GetMethod("TakeCompletedObservation",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, [launch.GetType(), completion.GetType()], null);
                    var observed = getter?.Invoke(composition, [launch, completion]);
                    if (observed is not null)
                    {
                        var facts = new Dictionary<string, object?>();
                        foreach (var name in new[] { "Classification", "RunId", "RunAttempt", "ActionSourceSha", "WorkflowSha",
                            "ReviewedHead", "FamilySha256", "LogicalGenerationIdentity", "EnvelopeSha256", "CheckpointSha256",
                            "PredecessorLogicalGenerationIdentity", "PredecessorEnvelopeSha256", "SourceCheckpointSha256" })
                        {
                            var value = observed.GetType().GetProperty(name)?.GetValue(observed);
                            if (value is not null && value is not (string or long)) throw new InvalidOperationException();
                            facts[char.ToLowerInvariant(name[0]) + name[1..]] = value;
                        }
                        observationBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(facts);
                    }
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
                return completion;
            }).ConfigureAwait(false);
        original.Validate();
        try
        {
            if (observationBytes.Length > 4096) throw new InvalidOperationException();
            using var output = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)6, ownsHandle: true), FileAccess.Write);
            var frame = new byte[4 + observationBytes.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, observationBytes.Length);
            observationBytes.CopyTo(frame, 4);
            await output.WriteAsync(frame).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
        return result;
    }

    // One fixed normal authenticated source-ref read per review. The role label
    // alone cannot make a premerge source into actual APR main or a merged PR into PR source.
    private static async Task RequireActualSourceRoleAsync(OriginalRuntimePreviewSourceRole role,
        AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostGitHubToken? token, CancellationToken cancellation)
    {
        if (token is null) throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_FACT");
        var path = role switch {
            OriginalRuntimePreviewSourceRole.PullRequest => "/repos/SolusQuest/agentic-pr-review/pulls/494",
            OriginalRuntimePreviewSourceRole.ActualMain => "/repos/SolusQuest/agentic-pr-review/git/ref/heads/main",
            _ => throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_ROLE"),
        };
        using var handler = ActionHostGitHubAuthorizationTransport.CreateHandler(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.ExportForPrivateLaunch());
        request.Headers.TryAddWithoutValidation("User-Agent", "AgenticPrReview-TEST-ONLY-original-source");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await ActionHostGitHubAuthorizationTransport.SendWithConnectionCancellationAsync(
            client, request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength is > 262144)
            throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_FACT");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count == 0) break;
            if (body.Length + count > 262144) throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_FACT");
            body.Write(buffer, 0, count);
        }
        if (!OriginalRuntimePreviewWorkflowAdmission.MatchesActualSourceFact(role,
            TrustedProofPayloadBuildIdentity.SourceCommit, body.ToArray()))
            throw new InvalidOperationException("APR_TEST_ORIGINAL_SOURCE_FACT");
    }
}
#endif
