using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// AIREC-003's request: which section of the recipe may change, which version it is being changed from, and
/// what the creator wants out of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own contract rather than the generic proposal request's</strong>, and the goal is why.
/// <see cref="RequestAiProposalViewModel"/> carries a task, a scope and a source version and nothing else —
/// "what is absent is the contract", asserted by its own tests — and stores no task inputs, so a goal has
/// nowhere to travel through it. Widening it would widen every recipe-bound task at once. AIREC-001 and
/// AIREC-002 each took their own contract for the same reason.
/// </para>
/// <para>
/// <strong>What is absent here is still the contract.</strong> There is no task discriminator — this route is
/// the task — no prompt, no model, no provider parameter, no field list, and no workspace. A creator names a
/// section and says what they want; they cannot name the fields within it, because which fields a section
/// permits is the server's answer and the bound it enforces.
/// </para>
/// </remarks>
public sealed class RequestRecipeRevisionViewModel
{
    /// <summary>
    /// The one section of the recipe this revision may change.
    /// </summary>
    /// <remarks>
    /// Required, and never <see cref="AiOperationScope.WholeRecipe"/> by default. A creator asking for a
    /// bounded change is the point of this capability, and a scope that defaulted to everything would make
    /// the bound something they had to remember to ask for.
    /// </remarks>
    public AiOperationScope Scope { get; set; }

    /// <summary>
    /// The exact version to revise, which must be the recipe's current version.
    /// </summary>
    /// <remarks>
    /// Required, for the reason recipes.md gives every other version reference: a proposal computed against
    /// "latest" cannot be checked for staleness later, and that check is what makes accepting one safe.
    /// </remarks>
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// What the creator wants this revision to achieve, in their own words. Optional.
    /// </summary>
    /// <remarks>
    /// The one free-text field, and it is treated as untrusted prompt content throughout: it travels in a
    /// PREFERENCES segment at creator-data trust, never in the task's own instructions. An absent goal is a
    /// real request — "look at this section and tell me what you would change".
    /// </remarks>
    public string? Goal { get; set; }
}

public sealed class RequestRecipeRevisionViewModelValidator : AbstractValidator<RequestRecipeRevisionViewModel>
{
    public RequestRecipeRevisionViewModelValidator()
    {
        RuleFor(model => model.Scope)
            .NotEqual(AiOperationScope.Unspecified)
            .WithMessage("Say which part of the recipe this revision may change.")
            .IsInEnum().WithMessage("That is not a part of a recipe this revision can be scoped to.");

        // A scope with nothing applicable in it would spend a provider budget to produce an answer every
        // change of which the validator would then refuse. Refused at the edge instead, naming the sections
        // that do work, so the creator learns it before waiting rather than after.
        RuleFor(model => model.Scope)
            .Must(scope => AiRevisionScopes.IsRevisable(scope))
            .WithMessage(
                "That part of a recipe cannot be revised yet. Revisable parts: "
                    + $"{string.Join(", ", AiRevisionScopes.Revisable)}.")
            .When(model => model.Scope is not AiOperationScope.Unspecified && Enum.IsDefined(model.Scope));

        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to revise.");

        RuleFor(model => model.Goal).MaximumLength(AiPolicy.RevisionGoalMaxLength);
    }
}

/// <summary>
/// Which scopes a revision can actually work in.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the tables that decide it rather than listed, so a scope becomes revisable the moment the
/// recipe seam can apply something in it — and stops being offered the moment it cannot. Listing them by hand
/// is how a picker comes to offer a section that fails every time it is chosen.
/// </para>
/// <para>
/// <see cref="AiOperationScope.Media"/> is absent today, because no change to an asset link has a path through
/// ordinary recipe validation. <see cref="AiOperationScope.NotApplicable"/> is absent because it means the
/// task is not about a recipe at all.
/// </para>
/// </remarks>
public static class AiRevisionScopes
{
    /// <summary>Every scope in which at least one change kind can reach at least one target.</summary>
    public static IReadOnlyList<AiOperationScope> Revisable { get; } =
    [
        .. Enum.GetValues<AiOperationScope>()
            .Where(scope => scope is not (AiOperationScope.Unspecified or AiOperationScope.NotApplicable))
            .Where(scope => AiPolicy.AllowedTargets(scope)
                .Any(target => AiChangeApplicability.For(target).Count > 0))
            .OrderBy(scope => (int)scope),
    ];

    public static bool IsRevisable(AiOperationScope scope) => Revisable.Contains(scope);
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiRevisionRequestErrors
{
    /// <summary>The request failed shape validation — no scope, an unrevisable one, or no version.</summary>
    public const string RequestInvalid = "ai.recipeRevision.invalid_request";

    /// <summary>AIREC-003 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.recipeRevision.not_enabled";

    /// <summary>Unknown recipe, or one in another workspace: deliberately indistinguishable (tenancy.md).</summary>
    public const string RecipeNotFound = "ai.recipeRevision.recipe.not_found";

    /// <summary>The named version is not the recipe's current version, so a diff from it would start stale.</summary>
    public const string SourceVersionInvalid = "ai.recipeRevisionSource.invalid_request";

    /// <summary>No such revision request in this workspace, or one belonging to another task.</summary>
    public const string RequestNotFound = "ai.recipeRevisionRequest.not_found";
}
