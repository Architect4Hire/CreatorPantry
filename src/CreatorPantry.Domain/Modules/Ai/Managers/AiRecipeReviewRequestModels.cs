using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// AIREC-006's request: which pinned version to review. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Its own contract rather than the generic proposal request's, for the reason
/// <see cref="RequestIngredientSubstitutionViewModel"/> gives: <see cref="RequestAiProposalViewModel"/> lets
/// the client choose <see cref="AiOperationScope"/>, and a review's scope is the server's to fix to
/// <see cref="AiOperationScope.Advisory"/> — never a creator's to choose, because a review addresses no change
/// to the recipe regardless of what a client asked for.
/// </para>
/// <para>
/// <strong>Unlike AIREC-004 and AIREC-005, there is no capability-specific field beyond the version.</strong> A
/// review is not about one selected ingredient or one declared goal; it reads the whole pinned recipe. This
/// route exists for the scope alone, and <see cref="AiTaskCatalog.RequiresTaskInputs"/> documents that
/// distinction rather than leaving it to be inferred from this type carrying no second field.
/// </para>
/// </remarks>
public sealed class RequestRecipeReviewViewModel
{
    /// <summary>
    /// The exact version to review, which must be the recipe's current version.
    /// </summary>
    /// <remarks>
    /// Required and pinned for the reason every recipe-bound capability in this module shares: a review is a
    /// review of a particular method, and one computed against a version that has since moved on is a review of
    /// a recipe that no longer exists.
    /// </remarks>
    public Guid SourceVersionId { get; set; }
}

public sealed class RequestRecipeReviewViewModelValidator : AbstractValidator<RequestRecipeReviewViewModel>
{
    public RequestRecipeReviewViewModelValidator()
    {
        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to review.");
    }
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiRecipeReviewRequestErrors
{
    /// <summary>The request failed shape validation — no version.</summary>
    public const string RequestInvalid = "ai.recipeReview.invalid_request";

    /// <summary>AIREC-006 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.recipeReview.not_enabled";

    /// <summary>Unknown recipe, or one in another workspace: deliberately indistinguishable (tenancy.md).</summary>
    public const string RecipeNotFound = "ai.recipeReview.recipe.not_found";

    /// <summary>The named version is not the recipe's current version.</summary>
    public const string SourceVersionInvalid = "ai.recipeReviewSource.invalid_request";

    /// <summary>No such review request in this workspace, or one belonging to a different recipe or task.</summary>
    public const string RequestNotFound = "ai.recipeReviewRequest.not_found";
}
