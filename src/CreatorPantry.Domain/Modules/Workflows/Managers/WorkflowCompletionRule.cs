using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>The fact that says a step is done, checked by the server through an existing facade (13A.8).</summary>
/// <remarks>
/// <para>
/// A closed set, as <see cref="WorkflowPrerequisite"/> is. Each member names a fact the owning module already
/// decides, so a step's completion is never a second copy of a domain rule.
/// </para>
/// <para>
/// <see cref="ResourceLinked"/> is the only rule that puts a resource on the run. Every other rule reads one an
/// earlier required step linked, which the validator checks, so no rule can be satisfied by a record the run never
/// held.
/// </para>
/// <para>
/// Only <see cref="Acknowledged"/> rests on the creator's word, and the validator allows it on an optional step
/// only: a required step is never proved by a client boolean.
/// </para>
/// </remarks>
public abstract record WorkflowCompletionRule
{
    private WorkflowCompletionRule()
    {
    }

    /// <summary>The step linked a resource of every one of these kinds to the run, and the caller can read each.</summary>
    /// <remarks>
    /// More than one kind when one action makes several records at once — starting a long-form piece files its
    /// creative context and its draft together, and the steps after it need both.
    /// </remarks>
    public sealed record ResourceLinked(IReadOnlyList<WorkflowResourceKind> Kinds) : WorkflowCompletionRule
    {
        public ResourceLinked(WorkflowResourceKind kind)
            : this([kind])
        {
        }
    }

    /// <summary>
    /// The linked recipe's readiness evaluation, made by the server, reports every named rule as met.
    /// </summary>
    /// <param name="RuleIds">Ids from <see cref="RecipeReadinessCatalogue"/>, e.g. <c>recipe.ingredients.present</c>.</param>
    /// <remarks>
    /// "Met" rather than "not a blocker": a workspace may configure a rule's severity, and a step about
    /// ingredients is not done because this workspace happens to treat their absence as advice.
    /// </remarks>
    public sealed record ReadinessRulesMet(IReadOnlyList<string> RuleIds) : WorkflowCompletionRule;

    /// <summary>
    /// The linked recipe stands in one of these editorial states — reached through the recipe module's own
    /// transitions, which this never performs.
    /// </summary>
    public sealed record RecipeStatusIn(IReadOnlyList<RecipeStatus> Statuses) : WorkflowCompletionRule;

    /// <summary>The linked recipe has a version saved after the run started, by the creator or by an accepted suggestion.</summary>
    public sealed record RecipeSavedDuringRun : WorkflowCompletionRule;

    /// <summary>The linked long-form draft stands in one of these states.</summary>
    public sealed record LongFormDraftStatusIn(IReadOnlyList<LongFormDraftStatus> Statuses) : WorkflowCompletionRule;

    /// <summary>The linked brand guide is the one the workspace has switched on.</summary>
    public sealed record BrandGuideIsActive : WorkflowCompletionRule;

    /// <summary>The linked creative context has at least one social post the creator accepted.</summary>
    public sealed record SocialPostAccepted : WorkflowCompletionRule;

    /// <summary>
    /// The linked publishing record stands in one of these states — as the publishing module recorded it, which
    /// for a provider whose answer is not yet known is not one of them.
    /// </summary>
    public sealed record PublishingRecordStatusIn(IReadOnlyList<PublishingRecordStatus> Statuses) : WorkflowCompletionRule;

    /// <summary>The creator says they are done. Optional steps only.</summary>
    public sealed record Acknowledged : WorkflowCompletionRule;
}
