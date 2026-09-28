using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// AIREC-002's request: a selected concept, the declared brief fields, or both. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is absent is the contract</strong>, the same way it is for
/// <see cref="RequestRecipeConceptsViewModel"/>. There is no task discriminator — this route is the task, so
/// the server names <see cref="AiTaskType.RecipeFirstDraft"/> itself — no scope, no recipe id, no provider, no
/// model, no system prompt, and no free-text field outside the declared ones.
/// </para>
/// <para>
/// <strong>The eleven brief fields are AIREC-001's own, deliberately.</strong> SCOPE's "declared brief fields"
/// refers back to that established vocabulary, and
/// <see cref="RecipeFirstDraftAiTaskHandler"/> already renders exactly those keys plus
/// <c>selectedConcept</c>. Restating them here as a second, slightly different set of fields would give the
/// creator two vocabularies for one idea and give the handler keys it never reads.
/// </para>
/// <para>
/// <strong>A selected concept is named by id, never by its text.</strong>
/// <see cref="SourceConceptRequestId"/> and <see cref="SourceConceptId"/> are resolved server-side against the
/// caller's own workspace, and the concept's title and summary are composed from the stored proposal. A client
/// cannot supply concept prose directly, so it cannot present text the model wrote as text the model wrote
/// about some other workspace's recipe.
/// </para>
/// </remarks>
public sealed class RequestRecipeFirstDraftViewModel
{
    /// <summary>
    /// The concept request the chosen concept came from — the id <c>POST .../recipe-concept-requests</c>
    /// returned. Supplied together with <see cref="SourceConceptId"/> or not at all.
    /// </summary>
    public Guid? SourceConceptRequestId { get; set; }

    /// <summary>
    /// The chosen concept, named by the server-minted id its change rows share (<c>targetId</c>), not by the
    /// <c>Add</c> row's own change id.
    /// </summary>
    public Guid? SourceConceptId { get; set; }

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

    /// <summary>Whether the request names a concept to draft from.</summary>
    internal bool NamesAConcept => SourceConceptRequestId is not null || SourceConceptId is not null;

    /// <summary>Whether the request supplies anything at all in the declared brief fields.</summary>
    internal bool NamesABriefField =>
        !string.IsNullOrWhiteSpace(Audience)
        || !string.IsNullOrWhiteSpace(Course)
        || !string.IsNullOrWhiteSpace(Cuisine)
        || !string.IsNullOrWhiteSpace(DietaryGoals)
        || !string.IsNullOrWhiteSpace(AvailableIngredients)
        || !string.IsNullOrWhiteSpace(Exclusions)
        || !string.IsNullOrWhiteSpace(Equipment)
        || !string.IsNullOrWhiteSpace(Skill)
        || !string.IsNullOrWhiteSpace(Season)
        || !string.IsNullOrWhiteSpace(TimeBudget)
        || !string.IsNullOrWhiteSpace(CreatorStyle);
}

/// <remarks>
/// <para>
/// <strong>An empty request is refused here, unlike AIREC-001's.</strong> "Pitch me anything" is a legitimate
/// open-ended ask for concepts; "write me a complete recipe from nothing" is not the creator's recipe in any
/// sense, and it still spends a provider budget. SCOPE's "from a selected concept <em>or</em> declared brief
/// fields" is read as requiring one of the two to actually carry something.
/// </para>
/// <para>
/// <strong>The two source ids travel together.</strong> One without the other is a client that has lost track
/// of what it is asking for, and guessing which half it meant would either draft from a concept nobody chose
/// or silently drop the one they did.
/// </para>
/// </remarks>
public sealed class RequestRecipeFirstDraftViewModelValidator : AbstractValidator<RequestRecipeFirstDraftViewModel>
{
    public RequestRecipeFirstDraftViewModelValidator()
    {
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

        RuleFor(model => model.SourceConceptRequestId)
            .NotEqual(Guid.Empty).WithMessage("That is not a concept request id.")
            .NotNull().When(model => model.SourceConceptId is not null)
            .WithMessage("Name the concept request the chosen concept came from.");

        RuleFor(model => model.SourceConceptId)
            .NotEqual(Guid.Empty).WithMessage("That is not a concept id.")
            .NotNull().When(model => model.SourceConceptRequestId is not null)
            .WithMessage("Name the concept to draft from.");

        RuleFor(model => model)
            .Must(model => model.NamesAConcept || model.NamesABriefField)
            .WithName(nameof(RequestRecipeFirstDraftViewModel.SourceConceptId))
            .WithMessage("Choose a concept to draft from, or describe what you want in the brief.");
    }
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
/// <remarks>
/// The dotted suffix selects the HTTP status (see <c>ProblemResults.StatusFor</c>), so the two
/// <c>.not_found</c> codes answer 404 and the rest answer 400.
/// </remarks>
public static class AiFirstDraftRequestErrors
{
    /// <summary>The request failed shape validation — a field over its bound, or nothing to draft from.</summary>
    public const string RequestInvalid = "ai.recipeFirstDraft.invalid_request";

    /// <summary>AIREC-002 is a real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.recipeFirstDraft.not_enabled";

    /// <summary>
    /// The request and the concept it names do not fit in an operation's stored inputs.
    /// </summary>
    /// <remarks>
    /// Only reachable from a brief near its declared bounds in every field alongside a maximal concept. Refused
    /// rather than truncated: a draft generated from a brief the creator can see but the model was only shown
    /// half of is worse than a refusal they can act on.
    /// </remarks>
    public const string RequestTooLarge = "ai.recipeFirstDraft.too_large";

    /// <summary>
    /// No such concept, or one belonging to another workspace: deliberately indistinguishable (tenancy.md).
    /// </summary>
    public const string ConceptNotFound = "ai.recipeConcept.not_found";

    /// <summary>No such draft request, or one belonging to another workspace or another task.</summary>
    public const string RequestNotFound = "ai.recipeDraftRequest.not_found";
}
