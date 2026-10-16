using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Workflows.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>
/// The records a task is being pointed at. Each non-null one is checked; a step is checked against the run
/// beside it, which must then be named too.
/// </summary>
internal sealed record WorkTaskLinkRequest(
    Guid? RecipeId = null,
    Guid? ContentCardId = null,
    Guid? MediaAssetId = null,
    Guid? WorkflowRunId = null,
    string? WorkflowStepKey = null);

/// <summary>
/// Whether a task may point at a record, asked of the module that owns it in the resolved workspace — for a
/// capture (13A.24) and for an edit's newly chosen links (13A.27) alike, so the two can never answer differently.
/// </summary>
internal sealed class WorkTaskLinkChecks(
    IRecipeFacade recipes,
    IContentCardLookupFacade contentCards,
    IMediaAssetLookupFacade mediaAssets,
    IWorkflowRunFacade workflowRuns)
{
    /// <summary>
    /// One sentence for every link that could not be used, whichever of the reasons it was. Telling an
    /// absent record from a neighbour's would make a task a way to ask what a neighbour owns.
    /// </summary>
    public const string NotFoundHere = "That is not something this workspace can point a task at.";

    /// <summary>
    /// Every link that cannot be used, collected rather than returned at the first, so a creator fixes one
    /// request instead of several.
    /// </summary>
    public async Task<List<(string Field, string Message)>> RefusedAsync(
        WorkTaskLinkRequest request, CancellationToken cancellationToken)
    {
        var refused = new List<(string Field, string Message)>();

        if (request.RecipeId is { } recipeId
            && !(await recipes.ListTitlesAsync([recipeId], cancellationToken)).Any(candidate => candidate.Id == recipeId))
        {
            refused.Add(("recipeId", NotFoundHere));
        }

        if (request.ContentCardId is { } cardId && !await contentCards.ExistsAsync(cardId, cancellationToken))
        {
            refused.Add(("contentCardId", NotFoundHere));
        }

        // No version: a task follows the picture as it stands, exactly as it follows a recipe rather than one
        // of its versions.
        if (request.MediaAssetId is { } assetId
            && await mediaAssets.ResolveLinkTargetAsync(assetId, null, cancellationToken) is not MediaAssetLinkTarget.Linkable)
        {
            refused.Add(("mediaAssetId", NotFoundHere));
        }

        if (request.WorkflowRunId is { } runId)
        {
            var run = await workflowRuns.GetAsync(runId, cancellationToken);
            if (!run.Succeeded)
            {
                refused.Add(("workflowRunId", NotFoundHere));
            }
            else if (request.WorkflowStepKey is { } stepKey
                && !run.Value!.Steps.Any(step => string.Equals(step.Key, stepKey, StringComparison.Ordinal)))
            {
                // The run is this workspace's, so naming the step is no disclosure: it says nothing the
                // caller could not read from the run itself.
                refused.Add(("workflowStepKey", "That run has no such step."));
            }
        }

        return refused;
    }
}
