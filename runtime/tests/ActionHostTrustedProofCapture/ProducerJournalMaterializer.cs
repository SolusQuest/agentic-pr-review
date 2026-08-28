using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class ProducerJournalMaterializer
{
    private static readonly string[] Commands =
    [
        "producer-journal-create",
        "producer-journal-execute",
        "producer-journal-reconcile",
        "producer-journal-seal",
    ];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.Ordinal);

    public static int Run(string[] args)
    {
        CredentialFileRepresentations? token = null;
        try
        {
            var command = args[0];
            var options = Parse(args.Skip(1).ToArray(), command);
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var authorization = ReadAuthorization(
                root,
                options["--execution-authorization"],
                options["--execution-authorization-sha256"],
                options["--destination-identity"]);
            var materializerBuildSha256 = AssemblySha256();
            if (authorization.MaterializerBuildSha256 != materializerBuildSha256)
            {
                throw new InvalidDataException("producer_materializer_build_invalid");
            }

            if (command == "producer-journal-create")
            {
                _ = ProducerOutcomeJournal.CreateNew(
                    root,
                    options["--journal-directory"],
                    options["--execution-authorization-sha256"],
                    authorization.MaterializerSourceSha256,
                    authorization.MaterializerBuildSha256,
                    authorization.Repository,
                    authorization.OperationIds,
                    authorization.Commands.Select(item => item.Target).ToArray(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Console.Out.WriteLine("APR_R4_E3_PRODUCER_JOURNAL_CREATED");
                return 0;
            }

            var journal = ProducerOutcomeJournal.Open(
                root,
                options["--journal-directory"],
                options["--execution-authorization-sha256"]);
            token = ReadAuthorizedToken(root, options, authorization, materializerBuildSha256);
            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            if (command == "producer-journal-seal")
            {
                var sources = CaptureDiscovery(
                    root,
                    journal.Authority,
                    options["--journal-directory"],
                    "final",
                    client);
                var seal = journal.SealCreateNew(sources);
                Console.Out.WriteLine(
                    $"APR_R4_E3_PRODUCER_JOURNAL_SEALED {seal.Sha256} " +
                    $"{seal.PhysicalIdentitySha256} {seal.Document.Disposition}");
                return 0;
            }

            var producer = authorization.Commands.SingleOrDefault(item =>
                item.Target.AuthorityId == options["--authority-id"]) ??
                throw new InvalidDataException("producer_materializer_authority_invalid");
            return command == "producer-journal-reconcile"
                ? Reconcile(root, options, journal, producer, client)
                : Execute(journal, producer, client);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            ArgumentException or InvalidOperationException or OverflowException or
            HttpRequestException or OperationCanceledException or KeyNotFoundException or
            FormatException)
        {
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_JOURNAL_INVALID");
            return 1;
        }
        finally
        {
            token?.Dispose();
        }
    }

    private static int Execute(
        ProducerOutcomeJournal journal,
        ProducerCommand producer,
        TrustedProofCaptureClient client)
    {
        var prior = journal.Entries.LastOrDefault(item => item.AuthorityId == producer.Target.AuthorityId);
        var attempt = prior is null ? 1 : checked(prior.Attempt + 1);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        journal.AppendCreateNew(
            producer.Target.AuthorityId,
            attempt,
            "before-dispatch",
            before,
            before,
            []);
        try
        {
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var response = client.SendProducerAsync(
                producer.Method,
                producer.Route,
                producer.Body,
                producer.ExpectedStatus,
                timeout.Token).GetAwaiter().GetResult();
            try
            {
                var captureBytes = CanonicalEvidence.Encode(response.Capture, EvidenceJson.Options);
                try
                {
                    journal.AppendCreateNew(
                        producer.Target.AuthorityId,
                        attempt,
                        "committed",
                        response.Capture.RequestStartedUnixMilliseconds,
                        response.Capture.ResponseReceivedUnixMilliseconds,
                        [CanonicalEvidence.Sha256(captureBytes)]);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(captureBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response.Body);
            }
            Console.Out.WriteLine("APR_R4_E3_PRODUCER_COMMITTED");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException or
            OperationCanceledException or IOException)
        {
            var observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            journal.AppendCreateNew(
                producer.Target.AuthorityId,
                attempt,
                "outcome-unknown",
                before,
                observed,
                []);
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_UNKNOWN");
            return 2;
        }
        finally
        {
            if (producer.Body is not null) CryptographicOperations.ZeroMemory(producer.Body);
        }
    }

    private static int Reconcile(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerOutcomeJournal journal,
        ProducerCommand producer,
        TrustedProofCaptureClient client)
    {
        var unknown = journal.Entries.LastOrDefault(item =>
            item.AuthorityId == producer.Target.AuthorityId && item.Outcome == "outcome-unknown");
        if (unknown is null || journal.Entries.Last(item =>
                item.AuthorityId == producer.Target.AuthorityId) != unknown)
        {
            throw new InvalidDataException("producer_reconciliation_invalid");
        }
        var phase = $"reconcile-{journal.Entries.Count + 1:D4}";
        var sources = CaptureDiscovery(
            root,
            journal.Authority,
            options["--journal-directory"],
            phase,
            client);
        if (!DiscoveryContainsCandidate(root, sources, unknown.Observation.RequestStartedUnixMilliseconds))
        {
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_STILL_UNKNOWN");
            return 2;
        }
        journal.AppendCreateNew(
            producer.Target.AuthorityId,
            unknown.Attempt,
            "reconciled-committed",
            sources[0].RequestStartedUnixMilliseconds,
            sources[^1].ResponseReceivedUnixMilliseconds,
            sources.Select(item => item.BodySha256).ToArray());
        Console.Out.WriteLine("APR_R4_E3_PRODUCER_RECONCILED");
        return 0;
    }

    private static ProducerDiscoverySource[] CaptureDiscovery(
        RestrictedEvidenceRoot root,
        ProducerAuthorityDocument authority,
        string journalDirectory,
        string phase,
        TrustedProofCaptureClient client)
    {
        var endpoint = $"/repos/{authority.Repository}/actions/workflows/r4-trusted-proof.yml/runs";
        using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
        var pages = client.GetPaginatedAsync(
            $"{endpoint}?per_page=100",
            endpoint,
            timeout.Token).GetAwaiter().GetResult();
        try
        {
            var sources = new ProducerDiscoverySource[pages.Captures.Length];
            for (var index = 0; index < pages.Captures.Length; index++)
            {
                var name = $"discovery-{phase}-page-{index + 1:D4}.json";
                var relativePath = $"{journalDirectory}/{name}";
                var identity = root.WritePinnedFileCreateNew(relativePath, pages.Bodies[index]);
                var capture = pages.Captures[index];
                sources[index] = new ProducerDiscoverySource(
                    $"producer-discovery-{phase}:page:{index + 1}",
                    capture.Route,
                    capture.Page,
                    capture.Status,
                    relativePath,
                    capture.BodySha256,
                    capture.BodySize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    identity,
                    capture.SafeHeadersSha256,
                    capture.RequestStartedUnixMilliseconds,
                    capture.ResponseReceivedUnixMilliseconds,
                    capture.NextRoute);
            }
            return sources;
        }
        finally
        {
            foreach (var body in pages.Bodies) CryptographicOperations.ZeroMemory(body);
        }
    }

    private static bool DiscoveryContainsCandidate(
        RestrictedEvidenceRoot root,
        IReadOnlyList<ProducerDiscoverySource> sources,
        long notBefore)
    {
        foreach (var source in sources)
        {
            using var lease = root.AcquirePinnedFile(source.BodyPath, EvidenceLimits.MaximumDocumentBytes);
            using var document = JsonDocument.Parse(lease.Bytes);
            foreach (var run in document.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                if (!DateTimeOffset.TryParse(
                    run.GetProperty("created_at").GetString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var created))
                {
                    throw new InvalidDataException("producer_reconciliation_invalid");
                }
                if (created.ToUnixTimeMilliseconds() + 999 >= notBefore) return true;
            }
        }
        return false;
    }

    private static CredentialFileRepresentations ReadAuthorizedToken(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerAuthorization authorization,
        string materializerBuildSha256)
    {
        var admission = CredentialAdmissionReceipt.Read(
            root,
            options["--credential-admission-receipt"],
            authorization.OperationIds);
        if (admission.Document.ExecutionAuthorizationSha256 !=
                options["--execution-authorization-sha256"] ||
            admission.Document.Consumers.Single(item =>
                item.Component == "producer-journal-materializer").BuildSha256 !=
                materializerBuildSha256)
        {
            throw new InvalidDataException("producer_materializer_credential_invalid");
        }
        var token = root.ReadCredentialFileRepresentations(
            options["--github-token-file"],
            base64Key: false,
            deleteExactIdentityOnFailure: false);
        if (CredentialAdmissionReceipt.AuthorizedIdentities(admission.Document)["github-token"] !=
            token.PhysicalIdentitySha256)
        {
            token.Dispose();
            throw new InvalidDataException("producer_materializer_credential_invalid");
        }
        return token;
    }

    private static ProducerAuthorization ReadAuthorization(
        RestrictedEvidenceRoot root,
        string relativePath,
        string expectedSha256,
        string destinationIdentitySha256)
    {
        using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        if (CanonicalEvidence.Sha256(lease.Bytes) != expectedSha256)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        using var document = JsonDocument.Parse(lease.Bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
        });
        var execution = document.RootElement;
        if (execution.GetProperty("kind").GetString() != "apr-r4-e3-execution-authorization-v1" ||
            execution.GetProperty("destinations").GetProperty("private")
                .GetProperty("identity_sha256").GetString() != destinationIdentitySha256)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var operationIds = execution.GetProperty("operation_ids").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var repository = execution.GetProperty("coordinates").GetProperty("repository")
            .GetString() ?? string.Empty;
        if (operationIds.Length != 2 || operationIds.Any(item => !Sha256(item)) ||
            repository.Split('/').Length != 2)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var identity = execution.GetProperty("correction_gate")
            .GetProperty("authority_identities").EnumerateArray()
            .Single(item => item.GetProperty("component").GetString() ==
                "producer-journal-materializer");
        var sourceSha256 = identity.GetProperty("source_sha256").GetString() ?? string.Empty;
        var buildSha256 = identity.GetProperty("build_sha256").GetString() ?? string.Empty;
        if (!Sha256(sourceSha256) || !Sha256(buildSha256))
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var roles = new[]
        {
            "normal-bootstrap",
            "normal-continuation",
            "stale-protected",
            "stale-follow-on",
        };
        var triggers = execution.GetProperty("trigger_plan").EnumerateArray().ToArray();
        if (triggers.Length != roles.Length)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var commands = triggers.Select((trigger, index) =>
        {
            var bytes = CanonicalEvidence.Encode(trigger, EvidenceJson.Options);
            try
            {
                var descriptorSha256 = CanonicalEvidence.Sha256(bytes);
                var role = trigger.GetProperty("role").GetString() ?? string.Empty;
                var operationId = trigger.GetProperty("operation_id").GetString() ?? string.Empty;
                var scope = trigger.GetProperty("scope").GetString() ?? string.Empty;
                var producer = trigger.GetProperty("producer").GetString() ?? string.Empty;
                var expectedEvent = trigger.GetProperty("expected_event").GetString() ?? string.Empty;
                var prNumber = trigger.GetProperty("pr_number").GetString() ?? string.Empty;
                var reference = trigger.GetProperty("ref").GetString() ?? string.Empty;
                var source = trigger.GetProperty("source_coordinate");
                if (role != roles[index] || !operationIds.Contains(operationId, StringComparer.Ordinal) ||
                    scope is not ("normal" or "stale") ||
                    expectedEvent is not ("workflow_run" or "workflow_dispatch") ||
                    !PositiveDecimal(prNumber) || !reference.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("producer_materializer_authorization_invalid");
                }
                var target = new ProducerTargetAuthority(
                    descriptorSha256,
                    operationId,
                    role,
                    scope,
                    producer,
                    expectedEvent,
                    descriptorSha256);
                return producer switch
                {
                    "rerun-upstream-ci" => new ProducerCommand(
                        target,
                        HttpMethod.Post,
                        $"/repos/{repository}/actions/runs/{RequiredDecimal(source, "id")}/rerun",
                        null,
                        HttpStatusCode.Created),
                    "dispatch-proof-workflow" => new ProducerCommand(
                        target,
                        HttpMethod.Post,
                        $"/repos/{repository}/actions/workflows/r4-trusted-proof.yml/dispatches",
                        EncodeBody(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["ref"] = RequiredText(source, "value"),
                            ["inputs"] = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["pr-number"] = prNumber,
                            },
                        }),
                        HttpStatusCode.NoContent),
                    "advance-stale-ref" => new ProducerCommand(
                        target,
                        HttpMethod.Patch,
                        $"/repos/{repository}/git/refs/{reference["refs/".Length..]}",
                        EncodeBody(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["sha"] = RequiredHex40(source, "value"),
                            ["force"] = false,
                        }),
                        HttpStatusCode.OK),
                    _ => throw new InvalidDataException("producer_materializer_authorization_invalid"),
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }).ToArray();
        return new ProducerAuthorization(repository, operationIds, sourceSha256, buildSha256, commands);
    }

    private static byte[] EncodeBody(object value) => CanonicalEvidence.Encode(value, EvidenceJson.Options);

    private static string RequiredDecimal(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetString() ?? string.Empty;
        return PositiveDecimal(result)
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static string RequiredText(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetString() ?? string.Empty;
        return result.Length is > 0 and <= EvidenceLimits.MaximumNameBytes
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static string RequiredHex40(JsonElement value, string property)
    {
        var result = RequiredText(value, property);
        return result.Length == 40 && result.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static Dictionary<string, string> Parse(string[] args, string command)
    {
        var common = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256", "--journal-directory",
        };
        var credential = new[] { "--credential-admission-receipt", "--github-token-file" };
        var names = command switch
        {
            "producer-journal-create" => common,
            "producer-journal-seal" => common.Concat(credential).ToArray(),
            _ => common.Concat(credential).Append("--authority-id").ToArray(),
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }
        if (names.Any(name => !result.ContainsKey(name)) ||
            !Sha256(result["--execution-authorization-sha256"]) ||
            (names.Contains("--authority-id", StringComparer.Ordinal) &&
                !Sha256(result["--authority-id"])))
        {
            throw new InvalidDataException("arguments_invalid");
        }
        return result;
    }

    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidDataException("producer_materializer_build_invalid");
        }
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PositiveDecimal(string value) => value.Length > 0 && value != "0" &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0');

    private sealed record ProducerAuthorization(
        string Repository,
        string[] OperationIds,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        ProducerCommand[] Commands);

    private sealed record ProducerCommand(
        ProducerTargetAuthority Target,
        HttpMethod Method,
        string Route,
        byte[]? Body,
        HttpStatusCode ExpectedStatus);
}
