using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

/// <summary>
/// What the other modules say about the records a run holds: whether one is there, and whether a step's completion
/// rule is met. Asked, never decided here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every fact is the owning module's answer</strong>, through its facade under the resolved workspace: a fresh
/// readiness evaluation, a status as stored, the publishing ledger's own state. Nothing here changes a status, runs a
/// transition or re-states a rule, so a workflow can never complete a step the domain would not.
/// </para>
/// <para>
/// <strong>Unknown and another workspace's record read alike</strong>, because every one of these facades answers
/// both with the same not-found; only whether the answer succeeded is ever used.
/// </para>
/// </remarks>
internal interface IWorkflowFacts
{
    /// <summary>Whether the record is one this workspace has, asked of the module that owns it.</summary>
    Task<bool> ExistsAsync(WorkflowResourceKind kind, Guid id, CancellationToken cancellationToken);

    Task<bool> SchedulingZoneSetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What still stands between a step and its completion rule, in plain words — empty when the rule is met. For
    /// every rule but <see cref="WorkflowCompletionRule.ResourceLinked"/>, which is met by the step's results.
    /// </summary>
    /// <param name="links">The run's most recent record of each kind, including any this completion records.</param>
    Task<IReadOnlyList<string>> UnmetAsync(
        WorkflowCompletionRule rule,
        WorkflowRun run,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        CancellationToken cancellationToken);
}

internal sealed class WorkflowFacts(
    IRecipeFacade recipes,
    IRecipeReadinessFacade readiness,
    IMediaAssetLookupFacade assets,
    IContentCardLookupFacade cards,
    ICreativeContextLookupFacade contexts,
    ILongFormDraftFacade longForm,
    IBrandStyleGuideFacade guides,
    IBrandProfileFacade brand,
    ISocialPackageFacade social,
    IPublishingRecordFacade publishing,
    IAiConceptRequestFacade conceptRequests,
    IAiFirstDraftRequestFacade draftRequests) : IWorkflowFacts
{
    /// <summary>
    /// The kinds whose records can be checked. Every <c>StartsFrom</c>, draft kind and result kind in the catalogue is
    /// one; <see cref="WorkflowResourceKind.WorkTask"/> joins when tasks exist (13A.23).
    /// </summary>
    internal static readonly IReadOnlySet<WorkflowResourceKind> CheckableKinds = new HashSet<WorkflowResourceKind>
    {
        WorkflowResourceKind.Recipe,
        WorkflowResourceKind.MediaAsset,
        WorkflowResourceKind.ContentCard,
        WorkflowResourceKind.CreativeContext,
        WorkflowResourceKind.LongFormDraft,
        WorkflowResourceKind.BrandStyleGuide,
        WorkflowResourceKind.PublishingRecord,
        WorkflowResourceKind.AiRequest,
    };

    public async Task<bool> ExistsAsync(WorkflowResourceKind kind, Guid id, CancellationToken cancellationToken) => kind switch
    {
        WorkflowResourceKind.Recipe => (await recipes.GetDetailAsync(id, cancellationToken)).Succeeded,
        WorkflowResourceKind.MediaAsset => await assets.ExistsAsync(id, cancellationToken),
        WorkflowResourceKind.ContentCard => await cards.ExistsAsync(id, cancellationToken),
        WorkflowResourceKind.CreativeContext => await contexts.ExistsAsync(id, cancellationToken),
        WorkflowResourceKind.LongFormDraft => (await longForm.GetAsync(id, cancellationToken)).Succeeded,
        WorkflowResourceKind.BrandStyleGuide => (await guides.GetAsync(id, cancellationToken)).Succeeded,
        WorkflowResourceKind.PublishingRecord => (await publishing.GetAsync(id, cancellationToken)).Succeeded,

        // Recipe ideas, or a first draft made from them: the two requests "Help me draft it" waits on. Each read
        // answers for its own task only, and neither discloses another workspace's.
        WorkflowResourceKind.AiRequest => (await conceptRequests.GetAsync(id, cancellationToken)).Succeeded
            || (await draftRequests.GetAsync(id, cancellationToken)).Succeeded,
        _ => throw new NotSupportedException($"A {kind} cannot be checked yet; see {nameof(CheckableKinds)}."),
    };

    /// <summary>Set, as the brand module holds it. Whether the zone is a real one is publishing's to refuse.</summary>
    public async Task<bool> SchedulingZoneSetAsync(CancellationToken cancellationToken) =>
        await brand.GetAsync(cancellationToken) is { Succeeded: true, Value.TimeZoneId: { Length: > 0 } };

    public async Task<IReadOnlyList<string>> UnmetAsync(
        WorkflowCompletionRule rule,
        WorkflowRun run,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        CancellationToken cancellationToken)
    {
        switch (rule)
        {
            case WorkflowCompletionRule.Acknowledged:
            case WorkflowCompletionRule.ResourceLinked:
                return [];

            case WorkflowCompletionRule.ReadinessRulesMet met:
                return await ReadinessAsync(met.RuleIds, links, cancellationToken);

            case WorkflowCompletionRule.RecipeStatusIn wanted:
            {
                if (!links.TryGetValue(WorkflowResourceKind.Recipe, out var id)
                    || await recipes.GetDetailAsync(id, cancellationToken) is not { Succeeded: true, Value: { } recipe })
                {
                    return [Gone("recipe")];
                }

                return wanted.Statuses.Contains(recipe.Status) ? [] : [$"The recipe is still {Plain(recipe.Status)}."];
            }

            case WorkflowCompletionRule.RecipeSavedDuringRun:
            {
                if (!links.TryGetValue(WorkflowResourceKind.Recipe, out var id)
                    || await recipes.GetDetailAsync(id, cancellationToken) is not { Succeeded: true, Value: { } recipe })
                {
                    return [Gone("recipe")];
                }

                return recipe.CurrentVersion is { } current && current.CreatedAt > run.StartedAt
                    ? []
                    : ["Save a change to the recipe first."];
            }

            case WorkflowCompletionRule.LongFormDraftStatusIn wanted:
            {
                if (!links.TryGetValue(WorkflowResourceKind.LongFormDraft, out var id)
                    || await longForm.GetAsync(id, cancellationToken) is not { Succeeded: true, Value: { } draft })
                {
                    return [Gone("piece of writing")];
                }

                return wanted.Statuses.Contains(draft.Status) ? [] : [$"The piece is still {Plain(draft.Status)}."];
            }

            case WorkflowCompletionRule.BrandGuideIsActive:
            {
                if (!links.TryGetValue(WorkflowResourceKind.BrandStyleGuide, out var id))
                {
                    return [Gone("brand guide")];
                }

                var active = await guides.GetActiveAsync(cancellationToken);

                return active is { Succeeded: true, Value.GuideId: var activeId } && activeId == id
                    ? []
                    : ["Switch the guide on first."];
            }

            case WorkflowCompletionRule.SocialPostAccepted:
            {
                if (!links.TryGetValue(WorkflowResourceKind.CreativeContext, out var id)
                    || await social.GetAsync(id, cancellationToken) is not { Succeeded: true } package)
                {
                    return [Gone("idea")];
                }

                return package.Value?.Channels.Any(channel => channel.Status == ContentProposalStatus.Accepted) == true
                    ? []
                    : ["Keep at least one post first."];
            }

            case WorkflowCompletionRule.PublishingRecordStatusIn wanted:
            {
                if (!links.TryGetValue(WorkflowResourceKind.PublishingRecord, out var id)
                    || await publishing.GetAsync(id, cancellationToken) is not { Succeeded: true, Value: { } record })
                {
                    return [Gone("booking")];
                }

                // An answer the provider has not given is never read as one.
                if (record.OutcomeUncertain)
                {
                    return ["We couldn't tell yet whether the booking went through. Check it again shortly."];
                }

                return wanted.Statuses.Contains(record.Status) ? [] : [$"The booking is {Plain(record.Status)}."];
            }

            default:
                throw new NotSupportedException($"No evaluation for {rule.GetType().Name}.");
        }
    }

    /// <summary>
    /// Each named readiness rule as the recipe module's own, fresh evaluation has it. Satisfied, not applicable, or
    /// switched off by the workspace is met; a finding against it is unmet and says why in that module's words; a rule
    /// with no verdict at all is unmet, never assumed.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadinessAsync(
        IReadOnlyList<string> ruleIds,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> links,
        CancellationToken cancellationToken)
    {
        if (!links.TryGetValue(WorkflowResourceKind.Recipe, out var id)
            || await readiness.EvaluateAsync(id, cancellationToken) is not { Succeeded: true, Value: { } evaluation })
        {
            return [Gone("recipe")];
        }

        var unmet = new List<string>();
        foreach (var ruleId in ruleIds)
        {
            var finding = evaluation.Findings.FirstOrDefault(candidate => candidate.RuleId == ruleId);

            if (finding is { Status: RecipeReadinessStatus.Satisfied or RecipeReadinessStatus.NotApplicable }
                || (finding is null && evaluation.DisabledRuleIds.Contains(ruleId)))
            {
                continue;
            }

            unmet.Add(finding is null
                ? "Part of the recipe couldn't be checked just now. Try again in a moment."
                : finding.Detail ?? finding.Summary);
        }

        return unmet;
    }

    private static string Gone(string noun) => $"This needs the {noun}, which can't be found any more.";

    private static string Plain(RecipeStatus status) => status switch
    {
        RecipeStatus.Draft => "a draft",
        RecipeStatus.InDevelopment => "in development",
        RecipeStatus.Testing => "being tested",
        RecipeStatus.ReadyForReview => "waiting for review",
        RecipeStatus.Approved => "approved",
        RecipeStatus.Archived => "archived",
        _ => "not at this point",
    };

    private static string Plain(LongFormDraftStatus status) => status switch
    {
        LongFormDraftStatus.Draft => "being planned",
        LongFormDraftStatus.Outlined => "planned but not written",
        LongFormDraftStatus.Writing => "being written",
        LongFormDraftStatus.Assembled => "written but not accepted",
        LongFormDraftStatus.Accepted => "accepted",
        LongFormDraftStatus.NeedsReview => "waiting for your review",
        _ => "not at this point",
    };

    private static string Plain(PublishingRecordStatus status) => status switch
    {
        PublishingRecordStatus.Pending => "waiting to be sent",
        PublishingRecordStatus.Submitting => "being sent",
        PublishingRecordStatus.Cancelling => "being cancelled",
        PublishingRecordStatus.Cancelled => "cancelled",
        PublishingRecordStatus.Failed => "not booked — it failed",
        PublishingRecordStatus.NeedsAttention => "waiting for you to look at it",
        _ => "not booked yet",
    };
}
