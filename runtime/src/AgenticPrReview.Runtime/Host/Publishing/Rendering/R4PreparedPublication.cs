using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

/// <summary>
/// P1-owned, in-memory proof that one completed Agent outcome was validated
/// and rendered as one exact R4 sticky comment. The outcome and rendering
/// cannot be supplied independently to retained-state preparation.
/// </summary>
internal sealed class R4PreparedPublication
{
    private readonly AgentRunOutcome outcome;
    private readonly R4RenderedStickyComment rendered;
    private readonly R4PublicationScopeV1 scope;

    private R4PreparedPublication(
        AgentRunOutcome outcome,
        R4RenderedStickyComment rendered,
        R4PublicationScopeV1 scope)
    {
        this.outcome = outcome;
        this.rendered = rendered;
        this.scope = scope;
    }

    internal static bool TryCreate(
        AgentRunOutcome? outcome,
        R4PublicationScopeV1? scope,
        out R4PreparedPublication? publication)
    {
        publication = null;
        if (!R4ValidatedPublicationReview.TryCreate(
                outcome,
                scope,
                out var validated) ||
            validated is null)
        {
            return false;
        }

        try
        {
            var rendered = R4StickyRenderer.Render(validated);
            publication = new R4PreparedPublication(
                outcome!,
                rendered,
                scope!);
            return true;
        }
        catch (R4PublicationException)
        {
            return false;
        }
    }

    internal bool TryProject(
        out AgentRunOutcome? exactOutcome,
        out R4RenderedStickyComment? exactRendered,
        out R4PublicationScopeV1? exactScope)
    {
        exactOutcome = outcome;
        exactRendered = rendered;
        exactScope = scope;
        return outcome.CompletedSessionEligible &&
            R4PublicationIdentityV1.IsValidScope(scope) &&
            StringComparer.Ordinal.Equals(
                rendered.Identity.ScopeSha256,
                R4PublicationIdentityV1.ComputeScopeSha256(scope));
    }

    public override string ToString() => "[PRIVATE]";
}
