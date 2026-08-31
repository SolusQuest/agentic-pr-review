using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record CredentialLeaseSpec(string RelativePath, bool Base64Key);

public sealed record CredentialLeaseDescriptorFile(
    string RelativePath,
    bool Base64Key,
    string PhysicalIdentitySha256);

public sealed record CredentialLeaseDescriptor(
    string Kind,
    string PipeName,
    int ProcessId,
    CredentialLeaseDescriptorFile[] Files);

public sealed record CredentialLeaseAuthorityTimeouts(
    TimeSpan Handoff,
    TimeSpan ConnectedSession)
{
    public static CredentialLeaseAuthorityTimeouts CaptureSuccessor { get; } = new(
        EvidenceLimits.CaptureCredentialHandoffTimeout,
        EvidenceLimits.CredentialConnectedSessionTimeout);

    public static CredentialLeaseAuthorityTimeouts OracleSuccessor { get; } = new(
        EvidenceLimits.OracleCredentialHandoffTimeout,
        EvidenceLimits.CredentialConnectedSessionTimeout);

    public static CredentialLeaseAuthorityTimeouts OperationSuccessor { get; } = new(
        EvidenceLimits.OperationCredentialHandoffTimeout,
        EvidenceLimits.CredentialConnectedSessionTimeout);
}

public sealed class CredentialLeaseValue : IDisposable
{
    internal CredentialLeaseValue(
        string relativePath,
        string physicalIdentitySha256,
        byte[] fileBytes,
        byte[]? decodedKey)
    {
        RelativePath = relativePath;
        PhysicalIdentitySha256 = physicalIdentitySha256;
        FileBytes = fileBytes;
        DecodedKey = decodedKey;
    }

    public string RelativePath { get; }
    public string PhysicalIdentitySha256 { get; }
    public byte[] FileBytes { get; }
    public byte[]? DecodedKey { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(FileBytes);
        if (DecodedKey is not null)
        {
            CryptographicOperations.ZeroMemory(DecodedKey);
        }
    }
}

public sealed class CredentialLeaseAuthorityClient : IDisposable
{
    public const string GitHubDescriptorName = "github-token-credential-lease.json";
    public const string StateKeyDescriptorName = "state-key-credential-lease.json";
    private const string DescriptorKind = "apr-r4-e3-credential-lease-v1";
    private readonly NamedPipeClientStream pipe;
    private bool valuesRead;
    private bool deleted;
    private bool disposed;

    public bool CredentialsDeleted => deleted;

    private CredentialLeaseAuthorityClient(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
    }

    public static CredentialLeaseAuthorityClient Open(
        RestrictedEvidenceRoot root,
        string descriptorName,
        IReadOnlyList<CredentialLeaseSpec> expected)
    {
        using var lease = root.AcquirePinnedFile(descriptorName, EvidenceLimits.MaximumDocumentBytes);
        var descriptor = JsonSerializer.Deserialize<CredentialLeaseDescriptor>(
            lease.Bytes,
            EvidenceJson.Options) ?? throw new InvalidDataException("credential_lease_descriptor_invalid");
        var canonical = CanonicalEvidence.Encode(descriptor, EvidenceJson.Options);
        try
        {
            if (!lease.Bytes.AsSpan().SequenceEqual(canonical) ||
                descriptor.Kind != DescriptorKind ||
                descriptor.ProcessId <= 0 ||
                descriptor.PipeName.Length is < 32 or > 128 ||
                descriptor.Files.Length != expected.Count ||
                descriptor.Files.Select((file, index) =>
                    file.RelativePath != expected[index].RelativePath ||
                    file.Base64Key != expected[index].Base64Key).Any(value => value) ||
                descriptor.Files.Any(file =>
                    file.PhysicalIdentitySha256.Length != 64 ||
                    file.PhysicalIdentitySha256.Any(character =>
                        character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) ||
                descriptor.PipeName != CreatePipeName(
                    root.DestinationIdentitySha256,
                    descriptorName,
                    descriptor.Files))
            {
                throw new InvalidDataException("credential_lease_descriptor_invalid");
            }
            lease.ValidateExactBytes();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }

        var pipe = new NamedPipeClientStream(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)TimeSpan.FromSeconds(20).TotalMilliseconds);
            return new CredentialLeaseAuthorityClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public CredentialLeaseValue[] ReadValues()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (valuesRead)
        {
            throw new InvalidDataException("credential_lease_protocol_invalid");
        }
        valuesRead = true;
        WriteCommand(pipe, "SCAN");
        var count = ReadInt32(pipe);
        if (count is < 1 or > 3)
        {
            throw new InvalidDataException("credential_lease_protocol_invalid");
        }
        var values = new List<CredentialLeaseValue>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var name = ReadText(pipe, EvidenceLimits.MaximumNameBytes);
                var identity = ReadText(pipe, 64);
                var base64Key = ReadByte(pipe) == 1;
                var fileBytes = ReadBytes(pipe, EvidenceLimits.MaximumCredentialBytes);
                byte[]? decoded = null;
                try
                {
                    decoded = base64Key ? DecodeCanonicalKey(fileBytes) : null;
                    values.Add(new CredentialLeaseValue(name, identity, fileBytes, decoded));
                    fileBytes = [];
                    decoded = null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fileBytes);
                    if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
                }
            }
            return [.. values];
        }
        catch
        {
            foreach (var value in values) value.Dispose();
            throw;
        }
    }

    public void DeleteCredentialFiles()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!valuesRead || deleted)
        {
            throw new InvalidDataException("credential_lease_protocol_invalid");
        }
        WriteCommand(pipe, "DELETE");
        if (ReadByte(pipe) != 1)
        {
            throw new InvalidDataException("credential_lease_deletion_invalid");
        }
        deleted = true;
    }

    public static void DeleteAbandoned(
        RestrictedEvidenceRoot root,
        string descriptorName,
        IReadOnlyList<CredentialLeaseSpec> expected)
    {
        using var client = Open(root, descriptorName, expected);
        var values = client.ReadValues();
        try
        {
            client.DeleteCredentialFiles();
        }
        finally
        {
            foreach (var value in values) value.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Dispose();
    }

    public static int LaunchCurrentProcess(
        RestrictedEvidenceRoot root,
        string destinationIdentity,
        IReadOnlyList<string> excludedRoots,
        string descriptorName,
        IReadOnlyList<CredentialLeaseSpec> specs,
        IReadOnlyList<CredentialFileRepresentations> values,
        string? entryAssemblyOverride = null,
        string? executableOverride = null,
        CredentialLeaseAuthorityTimeouts? timeouts = null)
    {
        if (specs.Count != values.Count || specs.Count is < 1 or > 3)
        {
            throw new InvalidDataException("credential_lease_launch_invalid");
        }
        var executable = executableOverride ?? (entryAssemblyOverride is null
            ? Environment.ProcessPath ?? throw new InvalidDataException("credential_lease_launch_invalid")
            : "dotnet");
        var entryAssembly = entryAssemblyOverride ?? Assembly.GetEntryAssembly()?.Location ?? "";
        timeouts ??= CredentialLeaseAuthorityTimeouts.CaptureSuccessor;
        int handoffTimeoutMilliseconds;
        int connectedSessionTimeoutMilliseconds;
        try
        {
            handoffTimeoutMilliseconds = TimeoutMilliseconds(timeouts.Handoff);
            connectedSessionTimeoutMilliseconds = TimeoutMilliseconds(timeouts.ConnectedSession);
        }
        catch
        {
            DeleteInputsIfRetained(values);
            throw;
        }
        var info = CreateGuardianStartInfo(executable);
        if (entryAssemblyOverride is not null ||
            Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (entryAssembly.Length == 0) throw new InvalidDataException("credential_lease_launch_invalid");
            info.ArgumentList.Add(entryAssembly);
        }
        info.ArgumentList.Add("credential-guardian");
        info.ArgumentList.Add(root.Path);
        info.ArgumentList.Add(destinationIdentity);
        info.ArgumentList.Add(descriptorName);
        info.ArgumentList.Add(handoffTimeoutMilliseconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(connectedSessionTimeoutMilliseconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(excludedRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var excluded in excludedRoots) info.ArgumentList.Add(excluded);
        info.ArgumentList.Add(specs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var spec in specs)
        {
            info.ArgumentList.Add(spec.RelativePath);
            info.ArgumentList.Add(spec.Base64Key ? "1" : "0");
        }
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidDataException("credential_lease_launch_invalid");
            WriteCommand(process.StandardInput.BaseStream, "INIT");
            WriteInt32(process.StandardInput.BaseStream, values.Count);
            foreach (var value in values) WriteBytes(process.StandardInput.BaseStream, value.FileBytes);
            process.StandardInput.Close();
            var ready = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(20))
                .GetAwaiter()
                .GetResult();
            if (ready != "APR_R4_E3_CREDENTIAL_GUARDIAN_READY")
            {
                throw new InvalidDataException("credential_lease_launch_invalid");
            }
            foreach (var value in values)
            {
                value.ReleaseRetainedIdentity();
            }
            return process.Id;
        }
        catch (TimeoutException exception)
        {
            StopFailedGuardian(process);
            DeleteInputsIfRetained(values);
            throw new InvalidDataException("credential_lease_launch_invalid", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            StopFailedGuardian(process);
            DeleteInputsIfRetained(values);
            throw new InvalidDataException("credential_lease_launch_invalid", exception);
        }
        catch
        {
            StopFailedGuardian(process);
            DeleteInputsIfRetained(values);
            throw;
        }
    }

    internal static ProcessStartInfo CreateGuardianStartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        info.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length != 0)
            {
                info.Environment["SystemRoot"] = windows;
                info.Environment["WINDIR"] = windows;
            }
        }
        return info;
    }

    public static bool IsGuardianCommand(string[] args) =>
        args.Length > 0 && args[0] == "credential-guardian";

    public static async Task<int> RunGuardianAsync(string[] args)
    {
        RestrictedEvidenceRoot? root = null;
        var leases = new List<PinnedEvidenceLease>();
        PinnedEvidenceLease? descriptorLease = null;
        CredentialLeaseSpec[] specs = [];
        string? descriptorName = null;
        var removed = false;
        try
        {
            var index = 1;
            var rootPath = args[index++];
            var destinationIdentity = args[index++];
            descriptorName = args[index++];
            var handoffTimeout = ParseTimeout(args[index++]);
            var connectedSessionTimeout = ParseTimeout(args[index++]);
            var excludedCount = ParseCount(args[index++], 1, 4);
            var excluded = args.Skip(index).Take(excludedCount).ToArray();
            index += excludedCount;
            var credentialCount = ParseCount(args[index++], 1, 3);
            specs = new CredentialLeaseSpec[credentialCount];
            for (var item = 0; item < credentialCount; item++)
            {
                specs[item] = new CredentialLeaseSpec(args[index++], args[index++] switch
                {
                    "0" => false,
                    "1" => true,
                    _ => throw new InvalidDataException("credential_lease_arguments_invalid"),
                });
            }
            if (index != args.Length || !RestrictedEvidenceRoot.IsSinglePathSegment(descriptorName))
            {
                throw new InvalidDataException("credential_lease_arguments_invalid");
            }
            root = RestrictedEvidenceRoot.Open(rootPath, destinationIdentity, excluded);
            using var standardInput = Console.OpenStandardInput();
            if (ReadCommand(standardInput) != "INIT" ||
                ReadInt32(standardInput) != credentialCount)
            {
                throw new InvalidDataException("credential_lease_protocol_invalid");
            }
            for (var item = 0; item < credentialCount; item++)
            {
                var expected = ReadBytes(standardInput, EvidenceLimits.MaximumCredentialBytes);
                try
                {
                    var lease = root.AcquirePinnedFile(specs[item].RelativePath, EvidenceLimits.MaximumCredentialBytes);
                    if (!lease.Bytes.AsSpan().SequenceEqual(expected))
                    {
                        lease.Dispose();
                        throw new InvalidDataException("credential_lease_value_invalid");
                    }
                    lease.ValidateExactBytes();
                    leases.Add(lease);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expected);
                }
            }
            var descriptorFiles = specs.Select((spec, item) => new CredentialLeaseDescriptorFile(
                spec.RelativePath,
                spec.Base64Key,
                leases[item].Identity)).ToArray();
            var pipeName = CreatePipeName(root.DestinationIdentitySha256, descriptorName, descriptorFiles);
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var descriptor = new CredentialLeaseDescriptor(
                DescriptorKind,
                pipeName,
                Environment.ProcessId,
                descriptorFiles);
            var descriptorBytes = CanonicalEvidence.Encode(descriptor, EvidenceJson.Options);
            try
            {
                root.WritePinnedFileCreateNew(descriptorName, descriptorBytes);
                descriptorLease = root.AcquirePinnedFile(
                    descriptorName,
                    EvidenceLimits.MaximumDocumentBytes);
                if (!descriptorLease.Bytes.AsSpan().SequenceEqual(descriptorBytes))
                {
                    throw new InvalidDataException("credential_lease_descriptor_invalid");
                }
                descriptorLease.ValidateExactBytes();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(descriptorBytes);
            }
            Console.Out.WriteLine("APR_R4_E3_CREDENTIAL_GUARDIAN_READY");
            Console.Out.Flush();
            using (var handoff = new CancellationTokenSource(handoffTimeout))
            using (handoff.Token.Register(
                static state => ((NamedPipeServerStream)state!).Dispose(),
                server))
            {
                await server.WaitForConnectionAsync(handoff.Token).ConfigureAwait(false);
            }
            using var connectedSession = new CancellationTokenSource(connectedSessionTimeout);
            using var abortConnectedSession = connectedSession.Token.Register(
                static state => ((NamedPipeServerStream)state!).Dispose(),
                server);
            if (ReadCommand(server) != "SCAN") throw new InvalidDataException("credential_lease_protocol_invalid");
            WriteInt32(server, leases.Count);
            for (var item = 0; item < leases.Count; item++)
            {
                WriteText(server, specs[item].RelativePath);
                WriteText(server, leases[item].Identity);
                server.WriteByte(specs[item].Base64Key ? (byte)1 : (byte)0);
                WriteBytes(server, leases[item].Bytes);
            }
            server.Flush();
            if (ReadCommand(server) != "DELETE") throw new InvalidDataException("credential_lease_protocol_invalid");
            try
            {
                for (var item = 0; item < leases.Count; item++)
                {
                    leases[item].DeleteExactIdentity();
                }
                if (specs.Any(spec => EvidenceFileHandle.PathEntryExists(
                        RestrictedEvidenceRoot.ResolveChildPath(root.Path, spec.RelativePath))))
                {
                    throw new InvalidDataException("credential_lease_deletion_invalid");
                }
            }
            catch
            {
                server.WriteByte(0);
                server.Flush();
                throw;
            }
            descriptorLease.DeleteExactIdentity();
            if (EvidenceFileHandle.PathEntryExists(
                    RestrictedEvidenceRoot.ResolveChildPath(root.Path, descriptorName)))
            {
                throw new InvalidDataException("credential_lease_deletion_invalid");
            }
            descriptorLease.Dispose();
            descriptorLease = null;
            descriptorName = null;
            removed = true;
            server.WriteByte(1);
            server.Flush();
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or OperationCanceledException or CryptographicException)
        {
            return 1;
        }
        finally
        {
            if (!removed && root is not null)
            {
                for (var index = 0; index < leases.Count && index < specs.Length; index++)
                {
                    try
                    {
                        leases[index].DeleteExactIdentity();
                    }
                    catch
                    {
                        // Never delete a path after identity or byte drift.
                    }
                }
            }
            foreach (var lease in leases) lease.Dispose();
            if (descriptorName is not null && root is not null && descriptorLease is not null)
            {
                try
                {
                    descriptorLease.DeleteExactIdentity();
                }
                catch
                {
                    // Never delete a replacement descriptor pathname.
                }
            }
            descriptorLease?.Dispose();
        }
    }

    private static string CreatePipeName(
        string destinationIdentity,
        string descriptorName,
        IReadOnlyList<CredentialLeaseDescriptorFile> files)
    {
        var binding = string.Join(
            "\n",
            new[] { DescriptorKind, destinationIdentity, descriptorName }
                .Concat(files.Select(file =>
                    $"{file.RelativePath}\0{(file.Base64Key ? 1 : 0)}\0{file.PhysicalIdentitySha256}")));
        return "apr-r4-e3-" + CanonicalEvidence.Sha256(Encoding.UTF8.GetBytes(binding));
    }

    private static void StopFailedGuardian(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)TimeSpan.FromSeconds(20).TotalMilliseconds);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The launch failure remains authoritative.
        }
    }

    private static void DeleteInputsIfRetained(
        IReadOnlyList<CredentialFileRepresentations> values)
    {
        foreach (var value in values)
        {
            try
            {
                value.DeleteExactIdentity();
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException or
                ObjectDisposedException)
            {
                // A failed launch has no successor authority. Preserve any replacement pathname.
            }
        }
    }

    private static byte[] DecodeCanonicalKey(byte[] bytes)
    {
        if (bytes.Length != 44) throw new InvalidDataException("credential_lease_value_invalid");
        var decoded = new byte[32];
        var canonical = new byte[44];
        try
        {
            if (Base64.DecodeFromUtf8(bytes, decoded, out var consumed, out var written) != OperationStatus.Done ||
                consumed != bytes.Length || written != decoded.Length ||
                Base64.EncodeToUtf8(decoded, canonical, out _, out var encoded) != OperationStatus.Done ||
                encoded != canonical.Length || !canonical.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("credential_lease_value_invalid");
            }
            return decoded;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static int ParseCount(string value, int minimum, int maximum) =>
        int.TryParse(value, out var count) && count >= minimum && count <= maximum
            ? count
            : throw new InvalidDataException("credential_lease_arguments_invalid");

    private static TimeSpan ParseTimeout(string value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var milliseconds) &&
            milliseconds >= 1 &&
            milliseconds <= MaximumGuardianTimeoutMilliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw new InvalidDataException("credential_lease_arguments_invalid");

    private static int TimeoutMilliseconds(TimeSpan value)
    {
        var milliseconds = value.TotalMilliseconds;
        if (milliseconds < 1 ||
            milliseconds > MaximumGuardianTimeoutMilliseconds ||
            milliseconds != Math.Truncate(milliseconds))
        {
            throw new InvalidDataException("credential_lease_launch_invalid");
        }
        return checked((int)milliseconds);
    }

    private static int MaximumGuardianTimeoutMilliseconds => checked(
        (int)EvidenceLimits.OperationCredentialHandoffTimeout.TotalMilliseconds);

    private static void WriteCommand(Stream stream, string command) => WriteText(stream, command);
    private static string ReadCommand(Stream stream) => ReadText(stream, 16);
    private static void WriteText(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));
    private static string ReadText(Stream stream, int maximum) => Encoding.UTF8.GetString(ReadBytes(stream, maximum));
    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
        stream.Flush();
    }
    private static byte[] ReadBytes(Stream stream, int maximum)
    {
        var length = ReadInt32(stream);
        if (length < 0 || length > maximum) throw new InvalidDataException("credential_lease_protocol_invalid");
        var value = new byte[length];
        stream.ReadExactly(value);
        return value;
    }
    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
        stream.Flush();
    }
    private static int ReadInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
    private static int ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        return value;
    }
}
