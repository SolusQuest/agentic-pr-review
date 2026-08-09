using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostCompletionTests
{
    private const string Build = "r4-h1";
    private const string ReviewedSha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PublicationUrl =
        "https://github.com/SolusQuest/agentic-pr-review/pull/146#issuecomment-1";

    public static TheoryData<object, object, int, object, object?>
        StatusMatrix => new()
    {
        { ActionHostStatus.Reviewed, ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.Accepted, null },
        { ActionHostStatus.ReviewedWithInlineWarnings,
            ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.Accepted,
            ActionHostAnnotationCode.InlinePublicationIncomplete },
        { ActionHostStatus.SkippedUntrustedEvent,
            ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.NotAccessed, null },
        { ActionHostStatus.SkippedFork, ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.NotAccessed, null },
        { ActionHostStatus.SkippedDraft, ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.NotAccessed, null },
        { ActionHostStatus.SkippedClosed, ActionHostExitClass.Success, 0,
            ActionHostStateDisposition.NotAccessed, null },
        { ActionHostStatus.ConfigurationInvalid, ActionHostExitClass.Contract, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.ConfigurationInvalid },
        { ActionHostStatus.AuthorizationFailed,
            ActionHostExitClass.Authorization, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.AuthorizationFailed },
        { ActionHostStatus.CredentialsMissing,
            ActionHostExitClass.Authorization, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.CredentialsMissing },
        { ActionHostStatus.SnapshotIncomplete, ActionHostExitClass.Snapshot, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.SnapshotIncomplete },
        { ActionHostStatus.ProviderFailed, ActionHostExitClass.Provider, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.ProviderFailed },
        { ActionHostStatus.AgentResultInvalid, ActionHostExitClass.Agent, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.AgentResultInvalid },
        { ActionHostStatus.StaleHead, ActionHostExitClass.Publication, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.StaleHead },
        { ActionHostStatus.StateConflict, ActionHostExitClass.State, 1,
            ActionHostStateDisposition.Conflict,
            ActionHostAnnotationCode.StateConflict },
        { ActionHostStatus.StickyPublicationFailed,
            ActionHostExitClass.Publication, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.StickyPublicationFailed },
        { ActionHostStatus.OutcomeAmbiguous,
            ActionHostExitClass.Reconciliation, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.OutcomeAmbiguous },
        { ActionHostStatus.Cancelled, ActionHostExitClass.Cancellation, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.Cancelled },
        { ActionHostStatus.InternalFailure, ActionHostExitClass.Internal, 1,
            ActionHostStateDisposition.NotCommitted,
            ActionHostAnnotationCode.InternalFailure },
    };

    [Theory]
    [MemberData(nameof(StatusMatrix))]
    public void EveryStatusHasOneExactCompletionRow(
        object statusValue,
        object expectedExitClassValue,
        int expectedProcessExit,
        object dispositionValue,
        object? annotationCodeValue)
    {
        var status = (ActionHostStatus)statusValue;
        var expectedExitClass = (ActionHostExitClass)expectedExitClassValue;
        var disposition = (ActionHostStateDisposition)dispositionValue;
        var annotationCode = annotationCodeValue is { } value
            ? (ActionHostAnnotationCode?)value
            : null;
        var reviewed = status is ActionHostStatus.Reviewed or
            ActionHostStatus.ReviewedWithInlineWarnings;
        Assert.True(ActionHostStepSummary.TryCreate(
            reviewed ? ReviewedSha : null,
            reviewed ? PublicationUrl : null,
            reviewed ? 20 : null,
            disposition,
            out var summary));
        var annotations = AnnotationList(annotationCode);

        Assert.True(ActionHostCompletion.TryCreate(
            Build,
            status,
            summary,
            annotations,
            out var completion));
        Assert.Equal(expectedExitClass, completion!.ExitClass);
        Assert.Equal(expectedProcessExit, completion.ProcessExitCode);
        Assert.Equal(annotationCode is null ? 0 : 1,
            completion.Annotations.Length);
        Assert.Equal(ActionHostPrivacyClass.WorkflowPresentation,
            completion.Privacy);
        Assert.Equal(ActionHostPrivacyClass.WorkflowPresentation,
            completion.Summary.Privacy);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var first));
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var second));
        Assert.Equal(first, second);
        Assert.True(ActionHostJsonCodec.TryReadCompletion(
            first,
            out var parsed,
            out var error));
        Assert.Equal(ActionHostContractError.None, error);
        Assert.Equal(status, parsed!.Status);
        Assert.Equal(Build, parsed.BuildDiscriminator);
    }

    [Fact]
    public void CrossRowSummaryAndAnnotationCombinationsAreRejected()
    {
        Assert.True(ActionHostStepSummary.TryCreate(
            ReviewedSha,
            PublicationUrl,
            1,
            ActionHostStateDisposition.Accepted,
            out var reviewed));
        Assert.False(ActionHostCompletion.TryCreate(
            Build,
            ActionHostStatus.SkippedDraft,
            reviewed,
            [],
            out _));

        Assert.True(ActionHostStepSummary.TryCreate(
            null,
            null,
            null,
            ActionHostStateDisposition.NotCommitted,
            out var failure));
        Assert.False(ActionHostCompletion.TryCreate(
            Build,
            ActionHostStatus.ProviderFailed,
            failure,
            [],
            out _));
        Assert.False(ActionHostCompletion.TryCreate(
            Build,
            ActionHostStatus.ProviderFailed,
            failure,
            AnnotationList(ActionHostAnnotationCode.InternalFailure),
            out _));
        var correct = AnnotationList(ActionHostAnnotationCode.ProviderFailed);
        Assert.False(ActionHostCompletion.TryCreate(
            Build,
            ActionHostStatus.ProviderFailed,
            failure,
            [correct[0], correct[0]],
            out _));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/pull/1")]
    [InlineData("https://user@github.com/owner/repo/pull/1")]
    [InlineData("https://github.com/owner/repo/pull/1?q=secret")]
    [InlineData("https://example.com/owner/repo/pull/1")]
    [InlineData("https://github.com/owner/repo::error::")]
    public void PublicationUrlIsRestrictedWorkflowPresentation(string url)
    {
        Assert.False(ActionHostStepSummary.TryCreate(
            ReviewedSha,
            url,
            1,
            ActionHostStateDisposition.Accepted,
            out _));
    }

    [Fact]
    public void FindingBoundsAndRequiredReviewFieldsAreExact()
    {
        Assert.True(ActionHostStepSummary.TryCreate(
            ReviewedSha,
            PublicationUrl,
            0,
            ActionHostStateDisposition.Accepted,
            out _));
        Assert.True(ActionHostStepSummary.TryCreate(
            ReviewedSha,
            PublicationUrl,
            20,
            ActionHostStateDisposition.Accepted,
            out _));
        Assert.False(ActionHostStepSummary.TryCreate(
            ReviewedSha,
            PublicationUrl,
            21,
            ActionHostStateDisposition.Accepted,
            out _));
        Assert.False(ActionHostStepSummary.TryCreate(
            new string('a', 39),
            PublicationUrl,
            1,
            ActionHostStateDisposition.Accepted,
            out _));
    }

    [Fact]
    public void WireCannotOverrideDerivedExitClassProcessCodeOrFixedAnnotation()
    {
        var completion = CreateFailure(ActionHostStatus.ProviderFailed);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var bytes));
        var json = Encoding.UTF8.GetString(bytes);
        var mutations = new[]
        {
            json.Replace("\"exit_class\":\"provider\"",
                "\"exit_class\":\"internal\"", StringComparison.Ordinal),
            json.Replace("\"process_exit_code\":1",
                "\"process_exit_code\":0", StringComparison.Ordinal),
            json.Replace("\"severity\":\"error\"",
                "\"severity\":\"warning\"", StringComparison.Ordinal),
            json.Replace("The review provider failed.",
                "provider detail leaked", StringComparison.Ordinal),
        };

        Assert.All(mutations, mutation =>
        {
            Assert.False(ActionHostJsonCodec.TryReadCompletion(
                Encoding.UTF8.GetBytes(mutation),
                out var parsed,
                out var error));
            Assert.Null(parsed);
            Assert.Equal(ActionHostContractError.InvalidCompletion, error);
        });
    }

    [Fact]
    public void CompletionJsonIsClosedAtEveryObjectDepth()
    {
        var completion = CreateFailure(ActionHostStatus.ProviderFailed);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var bytes));
        var json = Encoding.UTF8.GetString(bytes);
        var mutations = new[]
        {
            json[..^1] + ",\"unknown\":null}",
            json.Replace(
                "\"status\":\"provider_failed\"",
                "\"status\":\"provider_failed\"," +
                    "\"status\":\"provider_failed\"",
                StringComparison.Ordinal),
            json.Replace(
                "\"state_disposition\":\"not_committed\"",
                "\"state_disposition\":\"not_committed\"," +
                    "\"unknown\":null",
                StringComparison.Ordinal),
            json.Replace(
                "\"message\":\"The review provider failed.\"}",
                "\"message\":\"The review provider failed.\"," +
                    "\"unknown\":null}",
                StringComparison.Ordinal),
            json.Replace("\"status\"", "\"Status\"",
                StringComparison.Ordinal),
        };

        Assert.All(mutations, mutation => Assert.False(
            ActionHostJsonCodec.TryReadCompletion(
                Encoding.UTF8.GetBytes(mutation),
                out _,
                out _)));
    }

    [Fact]
    public void CompletionReaderAcceptsItsOuterCapAndRejectsCapPlusOne()
    {
        var completion = CreateFailure(ActionHostStatus.InternalFailure);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var bytes));
        var atCap = new byte[
            ActionHostContractBounds.MaximumCompletionDocumentBytes];
        bytes.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', bytes.Length,
            atCap.Length - bytes.Length);

        Assert.True(ActionHostJsonCodec.TryReadCompletion(
            atCap,
            out _,
            out _));
        Assert.False(ActionHostJsonCodec.TryReadCompletion(
            [.. atCap, (byte)' '],
            out var rejected,
            out var error));
        Assert.Null(rejected);
        Assert.Equal(ActionHostContractError.InvalidCompletion, error);
        Assert.Equal("invalid_completion", ActionHostErrorCode.Contract(error));
    }

    [Fact]
    public void FixedAnnotationsAreWorkflowSafeAndExhaustive()
    {
        foreach (var code in Enum.GetValues<ActionHostAnnotationCode>())
        {
            Assert.True(ActionHostAnnotation.TryCreate(code, out var annotation));
            Assert.Equal(code ==
                ActionHostAnnotationCode.InlinePublicationIncomplete
                    ? ActionHostAnnotationSeverity.Warning
                    : ActionHostAnnotationSeverity.Error,
                annotation!.Severity);
            Assert.DoesNotContain("::", annotation.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain('\r', annotation.Message);
            Assert.DoesNotContain('\n', annotation.Message);
            Assert.Equal(ActionHostPrivacyClass.WorkflowPresentation,
                annotation.Privacy);
        }
    }

    [Fact]
    public void OpaqueSecretCanaryNeverEntersCompletionBytes()
    {
        var canary = "secret::error::%0Aexception-path";
        var completion = CreateFailure(ActionHostStatus.InternalFailure);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(
            completion,
            out var bytes));

        var leaked = Encoding.UTF8.GetString(bytes)
            .Contains(canary, StringComparison.Ordinal);
        Assert.False(leaked);
    }

    private static ActionHostCompletion CreateFailure(ActionHostStatus status)
    {
        var disposition = status == ActionHostStatus.StateConflict
            ? ActionHostStateDisposition.Conflict
            : ActionHostStateDisposition.NotCommitted;
        Assert.True(ActionHostStepSummary.TryCreate(
            null,
            null,
            null,
            disposition,
            out var summary));
        Assert.True(ActionHostStatusRules.TryClassify(
            status,
            out _,
            out _,
            out var annotation));
        Assert.True(ActionHostCompletion.TryCreate(
            Build,
            status,
            summary,
            AnnotationList(annotation),
            out var completion));
        return completion!;
    }

    private static IReadOnlyList<ActionHostAnnotation> AnnotationList(
        ActionHostAnnotationCode? code)
    {
        if (code is null)
        {
            return [];
        }

        Assert.True(ActionHostAnnotation.TryCreate(
            code.Value,
            out var annotation));
        return [annotation!];
    }
}
