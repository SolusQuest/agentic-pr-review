using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkCanaryCapture
{
    private sealed record Definition(string Name, string Value);

    private static readonly object Gate = new();

    private static readonly Definition[] Definitions =
    [
        new("provider-key", FrameworkCanaries.ProviderKey),
        new("github-token", FrameworkCanaries.GitHubToken),
        new("state-key-current", FrameworkCanaries.StateKey),
        new("state-key-previous", FrameworkCanaries.PreviousStateKey),
        new("actions-runtime-jwt", FrameworkSupervisor.RuntimeToken),
        new("signed-url-sig", FrameworkCanaries.SignedUrl),
        new("repository", FrameworkCanaries.Repository),
        new("reviewed-path", FrameworkCanaries.ReviewedPath),
        new("workflow-source", FrameworkCanaries.Workflow),
        new("prompt", FrameworkCanaries.Prompt),
        new("tool-data", FrameworkCanaries.ToolData),
        new("session-plaintext", FrameworkCanaries.ContinuationMarker),
        new("public-result", FrameworkCanaries.PublicResult),
    ];

    internal static bool Observe(
        string evidenceRoot,
        string canaryClass,
        string sink,
        params object?[] captures)
    {
        var definition = Definitions.SingleOrDefault(value =>
            value.Name == canaryClass);
        if (definition is null || !captures.Any(capture =>
                CaptureContains(capture, definition.Value)))
        {
            Violation(evidenceRoot, canaryClass, sink, "expected_missing");
            return false;
        }

        Record(evidenceRoot, canaryClass, sink);
        return true;
    }

    internal static int CaptureAll(
        string evidenceRoot,
        string sink,
        params object?[] captures)
    {
        var count = 0;
        foreach (var definition in Definitions)
        {
            if (!captures.Any(capture =>
                    CaptureContains(capture, definition.Value)))
            {
                continue;
            }

            Record(evidenceRoot, definition.Name, sink);
            count++;
        }

        return count;
    }

    internal static bool AssertPublicSafe(
        string evidenceRoot,
        string sink,
        params object?[] captures)
    {
        string[] forbidden =
        [
            "provider-key", "github-token", "state-key-current",
            "state-key-previous", "actions-runtime-jwt", "signed-url-sig",
            "prompt", "session-plaintext", "public-result",
        ];
        var passed = true;
        foreach (var definition in Definitions.Where(value =>
                     forbidden.Contains(value.Name, StringComparer.Ordinal)))
        {
            if (!captures.Any(capture =>
                    CaptureContains(capture, definition.Value)))
            {
                continue;
            }

            Violation(evidenceRoot, definition.Name, sink, "public_leak");
            passed = false;
        }

        return passed;
    }

    internal static bool AssertAbsent(
        string evidenceRoot,
        string sink,
        params object?[] captures)
    {
        var passed = true;
        foreach (var definition in Definitions)
        {
            passed &= AssertCanaryAbsent(evidenceRoot, definition.Name,
                definition.Value, sink, captures);
        }

        return passed;
    }

    internal static bool AssertCanaryAbsent(
        string evidenceRoot,
        string canaryClass,
        string canary,
        string sink,
        params object?[] captures)
    {
        if (!captures.Any(capture => CaptureContains(capture, canary)))
        {
            return true;
        }

        Violation(evidenceRoot, canaryClass, sink, "forbidden_present");
        return false;
    }

    internal static string RequiredCanaryValue(string canaryClass) =>
        Definitions.SingleOrDefault(value => value.Name == canaryClass)?.Value ??
        throw new InvalidOperationException("unknown framework canary class");

    internal static bool ObserveCiphertextArchive(
        string evidenceRoot,
        byte[] archive,
        out string envelopeDigest)
    {
        envelopeDigest = "";
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count != 1 ||
                zip.Entries[0].FullName != "artifact-envelope.json")
            {
                Violation(evidenceRoot, "artifact-ciphertext",
                    "artifact.archive", "zip_shape_invalid");
                return false;
            }

            using var entry = zip.Entries[0].Open();
            using var document = JsonDocument.Parse(entry);
            var root = document.RootElement;
            string[] keys =
            [
                "discriminator", "producing_run_id",
                "producing_run_attempt", "encrypted_object_digest",
                "encrypted_object_size", "encrypted_object_base64",
            ];
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Select(value => value.Name)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(keys.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal) is false ||
                !root.TryGetProperty("encrypted_object_digest", out var digest) ||
                digest.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("encrypted_object_size", out var size) ||
                size.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("encrypted_object_base64", out var encoded) ||
                encoded.ValueKind != JsonValueKind.String ||
                !int.TryParse(size.GetString(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var expectedSize) || expectedSize < 1)
            {
                Violation(evidenceRoot, "artifact-ciphertext",
                    "artifact.archive", "envelope_shape_invalid");
                return false;
            }

            var encrypted = Convert.FromBase64String(encoded.GetString()!);
            var actualDigest = Convert.ToHexString(SHA256.HashData(encrypted))
                .ToLowerInvariant();
            if (encrypted.Length != expectedSize ||
                digest.GetString() != actualDigest ||
                !AssertAbsent(evidenceRoot, "artifact.ciphertext", encrypted))
            {
                Violation(evidenceRoot, "artifact-ciphertext",
                    "artifact.archive", "ciphertext_binding_invalid");
                return false;
            }

            envelopeDigest = Convert.ToHexString(SHA256.HashData(
                    FrameworkJson.SerializeToUtf8Bytes(
                        FrameworkJson.Element(root)!)))
                .ToLowerInvariant();
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Join(evidenceRoot, "artifact-ciphertext-proof.tsv"),
                    actualDigest + "\t" + envelopeDigest + "\t" +
                    encrypted.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    "\n");
            }
            Record(evidenceRoot, "artifact-ciphertext", "artifact.archive");
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or
            JsonException or FormatException or IOException)
        {
            Violation(evidenceRoot, "artifact-ciphertext",
                "artifact.archive", "archive_decode_invalid");
            return false;
        }
    }

    internal static bool ArchiveHasNoPrivateCanary(
        string evidenceRoot,
        byte[] archive,
        string sink)
    {
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var passed = AssertAbsent(evidenceRoot, sink, archive);
            foreach (var entryStream in zip.Entries
                         .Select(entry => entry.Open()))
            {
                using (entryStream)
                {
                    using var content = new MemoryStream();
                    entryStream.CopyTo(content);
                    passed &= AssertAbsent(evidenceRoot, sink, content.ToArray());
                }
            }

            return passed;
        }
        catch (InvalidDataException)
        {
            Violation(evidenceRoot, "archive", sink, "zip_decode_invalid");
            return false;
        }
    }

    internal static bool ArchiveHasNoCanary(
        string evidenceRoot,
        string canaryClass,
        string canary,
        byte[] archive,
        string sink)
    {
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var passed = AssertCanaryAbsent(evidenceRoot, canaryClass, canary,
                sink, archive);
            foreach (var entryStream in zip.Entries
                         .Select(entry => entry.Open()))
            {
                using (entryStream)
                {
                    using var content = new MemoryStream();
                    entryStream.CopyTo(content);
                    passed &= AssertCanaryAbsent(evidenceRoot, canaryClass,
                        canary, sink, content.ToArray());
                }
            }

            return passed;
        }
        catch (InvalidDataException)
        {
            Violation(evidenceRoot, canaryClass, sink, "zip_decode_invalid");
            return false;
        }
    }

    private static bool CaptureContains(object? capture, string canary)
    {
        if (capture is null) return false;
        var bytes = capture switch
        {
            byte[] value => value,
            ReadOnlyMemory<byte> value => value.ToArray(),
            _ => Encoding.UTF8.GetBytes(capture.ToString() ?? ""),
        };
        return DecodedTexts(bytes, 0, new HashSet<string>(StringComparer.Ordinal))
            .Any(value => Representations(canary).Any(candidate =>
                value.Contains(candidate, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> DecodedTexts(
        byte[] bytes,
        int depth,
        HashSet<string> seen)
    {
        if (depth > 4 || bytes.Length > 4 * 1024 * 1024) yield break;
        var text = Encoding.UTF8.GetString(bytes);
        if (!seen.Add(text)) yield break;
        yield return text;

        string[] jsonValues;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            jsonValues = JsonStrings(document.RootElement).ToArray();
        }
        catch (JsonException)
        {
            jsonValues = [];
        }

        foreach (var value in jsonValues)
        {
            foreach (var decoded in DecodedStrings(value, depth, seen))
            {
                yield return decoded;
            }
        }
    }

    private static IEnumerable<string> DecodedStrings(
        string value,
        int depth,
        HashSet<string> seen)
    {
        if (seen.Add(value)) yield return value;
        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            unescaped = value;
        }
        if (seen.Add(unescaped)) yield return unescaped;

        if (value.Length % 4 != 0 || value.Length == 0 ||
            value.Any(character => !char.IsLetterOrDigit(character) &&
                character is not '+' and not '/' and not '='))
        {
            yield break;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            yield break;
        }

        foreach (var text in DecodedTexts(decoded, depth + 1, seen))
        {
            yield return text;
        }
    }

    private static IEnumerable<string> JsonStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            yield return value.GetString() ?? "";
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var text in JsonStrings(item)) yield return text;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in value.EnumerateObject())
        foreach (var text in JsonStrings(property.Value)) yield return text;
    }

    private static IEnumerable<string> Representations(string value)
    {
        yield return value;
        yield return Uri.EscapeDataString(value);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static void Record(
        string evidenceRoot,
        string canaryClass,
        string sink)
    {
        lock (Gate)
        {
            File.AppendAllText(
                Path.Join(evidenceRoot, "canary-observations.tsv"),
                canaryClass + "\t" + sink + "\n");
        }
    }

    private static void Violation(
        string evidenceRoot,
        string canaryClass,
        string sink,
        string reason)
    {
        lock (Gate)
        {
            File.AppendAllText(
                Path.Join(evidenceRoot, "canary-route-violation"),
                canaryClass + "\t" + sink + "\t" + reason + "\n");
        }
    }
}
