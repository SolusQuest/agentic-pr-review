using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class CleanupAuthorizationMaterializer
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] == "cleanup-authorization-readback";

    public static int Run(string[] args)
    {
        CredentialFileRepresentations? token = null;
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            using var authorizationLease = root.AcquirePinnedFile(
                options["--cleanup-authorization"],
                EvidenceLimits.MaximumDocumentBytes);
            using var cleanupPlanLease = root.AcquirePinnedFile(
                options["--cleanup-plan"],
                EvidenceLimits.MaximumDocumentBytes);
            using var authorizationDocument = JsonDocument.Parse(authorizationLease.Bytes);
            using var cleanupPlanDocument = JsonDocument.Parse(cleanupPlanLease.Bytes);
            var authorization = authorizationDocument.RootElement;
            var source = authorization.GetProperty("source");
            var repository = source.GetProperty("repository").GetString() ?? string.Empty;
            var issueNumber = source.GetProperty("issue_number").GetString() ?? string.Empty;
            var commentId = source.GetProperty("comment_id").GetString() ?? string.Empty;
            var authorId = source.GetProperty("author_id").GetString() ?? string.Empty;
            var expectedPermission = source.GetProperty("author_permission").GetString() ?? string.Empty;
            var planSha256 = CanonicalEvidence.Sha256(cleanupPlanLease.Bytes);
            var operationIds = cleanupPlanDocument.RootElement.GetProperty("operation_ids")
                .EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            if (authorization.GetProperty("kind").GetString() !=
                    "apr-r4-e3-cleanup-authorization-v1" ||
                authorization.GetProperty("phase").GetString() != "cleanup" ||
                authorization.GetProperty("plan_sha256").GetString() != planSha256 ||
                repository.Split('/').Length != 2 || !PositiveDecimal(issueNumber) ||
                !PositiveDecimal(commentId) || !PositiveDecimal(authorId) ||
                expectedPermission is not ("admin" or "write") ||
                operationIds.Length != 2 || operationIds.Any(item => !Sha256(item)))
            {
                throw new InvalidDataException("cleanup_authorization_invalid");
            }

            var admission = CredentialAdmissionReceipt.Read(
                root,
                options["--credential-admission-receipt"],
                operationIds);
            var assemblySha256 = AssemblySha256();
            if (admission.Document.Consumers.Single(item => item.Component == "capture").BuildSha256 !=
                assemblySha256)
            {
                throw new InvalidDataException("cleanup_authorization_credential_invalid");
            }
            token = root.ReadCredentialFileRepresentations(
                options["--github-token-file"],
                base64Key: false,
                deleteExactIdentityOnFailure: false);
            if (CredentialAdmissionReceipt.AuthorizedIdentities(admission.Document)["github-token"] !=
                token.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("cleanup_authorization_credential_invalid");
            }

            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var comment = client.GetExpectedAsync(
                $"/repos/{repository}/issues/comments/{commentId}",
                System.Net.HttpStatusCode.OK,
                timeout.Token).GetAwaiter().GetResult();
            CapturePageSet? permission = null;
            try
            {
                if (comment.Bodies.Length != 1)
                {
                    throw new InvalidDataException("cleanup_authorization_readback_invalid");
                }
                using var commentJson = JsonDocument.Parse(comment.Bodies[0]);
                var commentRoot = commentJson.RootElement;
                if (commentRoot.GetProperty("id").GetRawText() != commentId ||
                    commentRoot.GetProperty("user").GetProperty("id").GetRawText() != authorId)
                {
                    throw new InvalidDataException("cleanup_authorization_readback_invalid");
                }
                var login = commentRoot.GetProperty("user").GetProperty("login").GetString() ?? string.Empty;
                if (login.Length == 0)
                {
                    throw new InvalidDataException("cleanup_authorization_readback_invalid");
                }
                ValidateMarker(authorization, commentRoot.GetProperty("body").GetString() ?? string.Empty,
                    repository, issueNumber);
                permission = client.GetExpectedAsync(
                    $"/repos/{repository}/collaborators/{login}/permission",
                    System.Net.HttpStatusCode.OK,
                    timeout.Token).GetAwaiter().GetResult();
                if (permission.Bodies.Length != 1)
                {
                    throw new InvalidDataException("cleanup_authorization_readback_invalid");
                }
                using var permissionJson = JsonDocument.Parse(permission.Bodies[0]);
                var permissionRoot = permissionJson.RootElement;
                if (permissionRoot.GetProperty("permission").GetString() != expectedPermission ||
                    permissionRoot.GetProperty("user").GetProperty("id").GetRawText() != authorId ||
                    permissionRoot.GetProperty("user").GetProperty("login").GetString() != login)
                {
                    throw new InvalidDataException("cleanup_authorization_readback_invalid");
                }
                var readback = new CleanupAuthorizationReadback(
                    "apr-r4-e3-cleanup-authorization-readback-v1",
                    commentRoot.Clone(),
                    permissionRoot.Clone(),
                    new CleanupAuthorizationObservation(
                        comment.Captures[0].RequestStartedUnixMilliseconds,
                        permission.Captures[0].ResponseReceivedUnixMilliseconds));
                var bytes = CanonicalEvidence.Encode(readback, EvidenceJson.Options);
                try
                {
                    var identity = root.WritePinnedFileCreateNew(options["--output"], bytes);
                    Console.Out.WriteLine(
                        $"APR_R4_E3_CLEANUP_AUTHORIZATION_OK {CanonicalEvidence.Sha256(bytes)} {identity}");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
                return 0;
            }
            finally
            {
                foreach (var body in comment.Bodies.Concat(permission?.Bodies ?? []))
                {
                    CryptographicOperations.ZeroMemory(body);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            HttpRequestException or OperationCanceledException or ArgumentException or
            KeyNotFoundException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine("APR_R4_E3_CLEANUP_AUTHORIZATION_INVALID");
            return 1;
        }
        finally
        {
            token?.Dispose();
        }
    }

    internal static void ValidateMarker(
        JsonElement authorization,
        string body,
        string repository,
        string issueNumber)
    {
        const string prefix = "<!-- apr-r4-e3-authorization ";
        const string suffix = " -->";
        if (!body.StartsWith(prefix, StringComparison.Ordinal) ||
            !body.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("cleanup_authorization_marker_invalid");
        }
        using var marker = JsonDocument.Parse(body[prefix.Length..^suffix.Length]);
        var root = marker.RootElement;
        if (root.GetProperty("contract").GetString() != "apr-r4-e3-maintainer-authorization-v1" ||
            root.GetProperty("phase").GetString() != "cleanup" ||
            root.GetProperty("repository").GetString() != repository ||
            root.GetProperty("issue_number").GetRawText() != issueNumber)
        {
            throw new InvalidDataException("cleanup_authorization_marker_invalid");
        }
        using var projected = ProjectWithoutSource(authorization);
        if (!JsonElement.DeepEquals(root.GetProperty("authorization"), projected.RootElement))
        {
            throw new InvalidDataException("cleanup_authorization_marker_invalid");
        }
    }

    private static JsonDocument ProjectWithoutSource(JsonElement authorization)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in authorization.EnumerateObject())
            {
                if (property.NameEquals("source")) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--cleanup-authorization", "--cleanup-plan", "--credential-admission-receipt",
            "--github-token-file", "--output",
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !options.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }
        if (names.Any(name => !options.ContainsKey(name)))
        {
            throw new InvalidDataException("arguments_invalid");
        }
        return options;
    }

    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool PositiveDecimal(string value) => value.Length > 0 && value != "0" &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0');

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record CleanupAuthorizationReadback(
        string Kind,
        JsonElement Comment,
        JsonElement Permission,
        CleanupAuthorizationObservation Observation);

    private sealed record CleanupAuthorizationObservation(
        long RequestStarted,
        long ResponseReceived);
}
