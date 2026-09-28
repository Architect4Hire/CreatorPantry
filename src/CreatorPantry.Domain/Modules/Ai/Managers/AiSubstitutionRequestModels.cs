using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// AIREC-004's request: which ingredient, in which version, and why the creator is asking.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own contract rather than the generic proposal request's</strong>, and the ingredient is why.
/// <see cref="RequestAiProposalViewModel"/> names a task, a scope and a source version and stores no task
/// inputs, so a selected line has nowhere to travel through it. Widening it would widen every recipe-bound
/// task at once. AIREC-001, AIREC-002 and AIREC-003 each took their own contract for the same reason.
/// </para>
/// <para>
/// <strong>What is absent is still the contract.</strong> There is no task discriminator — this route is the
/// task — no scope, because a substitution changes nothing and the server records
/// <see cref="AiOperationScope.Advisory"/> itself; no prompt, no model, no provider parameter, and no
/// workspace. A creator names a line and says why; they cannot say what kind of answer to give.
/// </para>
/// </remarks>
public sealed class RequestIngredientSubstitutionViewModel
{
    /// <summary>
    /// The exact version the ingredient was selected in, which must be the recipe's current version.
    /// </summary>
    /// <remarks>
    /// Required, and pinned for a reason this capability shares with every other: advice about an ingredient
    /// is advice about what it does in a particular method, and a method that moved underneath the request
    /// makes the answer about a recipe that no longer exists.
    /// </remarks>
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// The ingredient line to find alternatives for, by its id in that version.
    /// </summary>
    /// <remarks>
    /// Checked against the pinned version's snapshot before anything is queued, so a line from another recipe
    /// — or from another workspace, which is invisible here anyway — is refused at the edge rather than
    /// spending a provider budget to produce an answer about nothing.
    /// </remarks>
    public Guid IngredientId { get; set; }

    /// <summary>
    /// Why the creator is asking, in their own words. Optional.
    /// </summary>
    /// <remarks>
    /// The one free-text field, and the most sensitive one this module accepts: it is where somebody writes
    /// that a reader is allergic. It is treated as untrusted prompt content throughout — carried in a
    /// PREFERENCES segment at creator-data trust, never in the task's instructions — and it never becomes
    /// permission to declare anything safe for anyone. See <see cref="AiSubstitutionOutputValidator"/> for the
    /// half of that which does not depend on the model cooperating.
    /// </remarks>
    public string? Reason { get; set; }
}

public sealed class RequestIngredientSubstitutionViewModelValidator
    : AbstractValidator<RequestIngredientSubstitutionViewModel>
{
    public RequestIngredientSubstitutionViewModelValidator()
    {
        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version the ingredient was selected in.");

        RuleFor(model => model.IngredientId)
            .NotEmpty().WithMessage("Name the ingredient to find alternatives for.");

        RuleFor(model => model.Reason).MaximumLength(AiPolicy.SubstitutionReasonMaxLength);
    }
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiSubstitutionRequestErrors
{
    /// <summary>The request failed shape validation — no version, or no ingredient.</summary>
    public const string RequestInvalid = "ai.ingredientSubstitution.invalid_request";

    /// <summary>AIREC-004 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.ingredientSubstitution.not_enabled";

    /// <summary>Unknown recipe, or one in another workspace: deliberately indistinguishable (tenancy.md).</summary>
    public const string RecipeNotFound = "ai.ingredientSubstitution.recipe.not_found";

    /// <summary>The named version is not the recipe's current version.</summary>
    public const string SourceVersionInvalid = "ai.ingredientSubstitutionSource.invalid_request";

    /// <summary>
    /// No ingredient with that id in the pinned version.
    /// </summary>
    /// <remarks>
    /// A validation code rather than a not-found one. The ingredient is not a resource this route addresses —
    /// the recipe is — so reporting it missing would invite a caller to probe ids and read the difference
    /// between two answers. Whether a line exists inside a recipe the caller can already read discloses
    /// nothing either way, but the shape of the answer should not teach anyone to ask.
    /// </remarks>
    public const string IngredientInvalid = "ai.ingredientSubstitutionIngredient.invalid_request";

    /// <summary>No such substitution request in this workspace, or one belonging to another task.</summary>
    public const string RequestNotFound = "ai.ingredientSubstitutionRequest.not_found";
}
