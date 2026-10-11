using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// AIREC-001's request: the twelve declared brief fields, and nothing else.
/// </summary>
/// <remarks>
/// <strong>What is absent is the contract</strong>, the same way it is for
/// <see cref="RequestAiProposalViewModel"/>. There is no task discriminator — this route is the task, so the
/// server names <see cref="AiTaskType.RecipeConcepts"/> itself rather than resolving one from a field — no
/// recipe id, no scope, no provider, no system prompt, and no free-text field outside the twelve declared
/// ones. Every field is optional: a brief that supplies none of them is a legitimate open-ended request, and
/// <see cref="RecipeConceptsAiTaskHandler"/> already reads an absent field as "not specified".
/// </remarks>
public sealed class RequestRecipeConceptsViewModel
{
    /// <summary>
    /// What the creator calls the dish, when they already know. Their own words, and a name only.
    /// </summary>
    /// <remarks>
    /// It says what the dish is called, never how it is cooked, so nothing downstream may read a cuisine, a
    /// course or an ingredient out of it. The same words the Image Studio and the Content Pipeline keep as a
    /// creative context's working title, which is why the bound matches theirs in spirit if not in number: a
    /// brief field's bound is the brief's.
    /// </remarks>
    public string? DishName { get; set; }

    public string? Audience { get; set; }

    public string? Course { get; set; }

    public string? Cuisine { get; set; }

    public string? DietaryGoals { get; set; }

    public string? AvailableIngredients { get; set; }

    public string? Exclusions { get; set; }

    public string? Equipment { get; set; }

    public string? Skill { get; set; }

    public string? Season { get; set; }

    public string? TimeBudget { get; set; }

    public string? CreatorStyle { get; set; }

    /// <summary>
    /// The piece of work these concepts are for, when they are for one (AF.7.4). Only its themed day is read
    /// from it — the theme, its standing notes and what was already made for it — because the brief above is
    /// already everything else the creator said.
    /// </summary>
    [System.ComponentModel.Description("The creative context these concepts are for. Optional. Only its themed day is read: the theme, its standing notes, and titles of work already made for it.")]
    public Guid? CreativeContextId { get; set; }

    /// <summary>False to ask for these concepts without the themed day's standing notes. Needs a creative context to mean anything.</summary>
    [System.ComponentModel.Description("Send false to ask for these concepts without the themed day's standing notes, for this request only. Omit it, or send true, to follow them.")]
    public bool? ApplyDayNotes { get; set; }
}

public sealed class RequestRecipeConceptsViewModelValidator : AbstractValidator<RequestRecipeConceptsViewModel>
{
    public RequestRecipeConceptsViewModelValidator()
    {
        RuleFor(model => model.DishName).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.Audience).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.Course).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.Cuisine).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.Skill).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.Season).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.TimeBudget).MaximumLength(AiPolicy.BriefFieldMaxLength);
        RuleFor(model => model.CreatorStyle).MaximumLength(AiPolicy.BriefFieldMaxLength);

        RuleFor(model => model.DietaryGoals).MaximumLength(AiPolicy.BriefListFieldMaxLength);
        RuleFor(model => model.AvailableIngredients).MaximumLength(AiPolicy.BriefListFieldMaxLength);
        RuleFor(model => model.Exclusions).MaximumLength(AiPolicy.BriefListFieldMaxLength);
        RuleFor(model => model.Equipment).MaximumLength(AiPolicy.BriefListFieldMaxLength);
    }
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiConceptRequestErrors
{
    /// <summary>The brief failed shape validation -- a field longer than its declared bound.</summary>
    public const string RequestInvalid = "ai.recipeConcepts.invalid_request";

    /// <summary>AIREC-001 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.recipeConcepts.not_enabled";

    /// <summary>
    /// The piece of work the request names is not one of this workspace's. One answer for an id nobody has and
    /// for a neighbour's, so the route cannot be used to ask what a neighbour owns (tenancy.md).
    /// </summary>
    public const string ContextNotFound = "ai.recipeConceptContext.not_found";

    /// <summary>No such request, or one belonging to another workspace: deliberately indistinguishable.</summary>
    public const string RequestNotFound = "ai.recipeConceptRequest.not_found";
}
