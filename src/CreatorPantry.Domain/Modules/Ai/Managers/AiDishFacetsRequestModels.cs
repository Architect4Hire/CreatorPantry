using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The request to read a dish name into catalogue facets: the name, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is absent is the contract</strong>, as it is for <see cref="RequestRecipeConceptsViewModel"/>.
/// There is no task discriminator — this route is the task — no recipe id, no scope, no provider, no system
/// prompt, no candidate list, and no field for the facets the creator has already chosen.
/// </para>
/// <para>
/// <strong>The candidate list is deliberately not a request field.</strong> A client that could name the
/// values a model may choose from could narrow them to one and have the answer confirm a choice the creator
/// never made. <see cref="DishFacetSuggestionAiTaskHandler"/> reads the vocabulary server-side instead, which
/// is also what lets it reject a code the vocabulary does not have.
/// </para>
/// <para>
/// <strong>And neither are the creator's own answers.</strong> It is tempting to send the facets they already
/// set so the model can "stay consistent" with them, but that invites it to reason from those instead of from
/// the name, and a suggestion that agrees with itself is not evidence about the dish.
/// </para>
/// <para>
/// <strong>The consequence is worth stating plainly: every reading covers all three facets, and nothing on
/// the server stops one reaching a facet the creator has answered.</strong> "A suggestion never overwrites
/// their own choice" is real and it is the <em>caller's</em> guarantee to keep, decided against the controls
/// in front of them at the moment an answer lands. It is not enforced here, and a second caller would have to
/// keep it again.
/// </para>
/// </remarks>
public sealed class RequestDishFacetsViewModel
{
    /// <summary>
    /// What the creator calls the dish, in their own words. Required: the task is nothing without it.
    /// </summary>
    /// <remarks>
    /// The same words the Content Pipeline keeps as a creative context's working title, and the only input
    /// this capability has. It is read as a name — never as an instruction, which the prompt says and the
    /// envelope's untrusted fence enforces structurally.
    /// </remarks>
    public string? DishName { get; set; }
}

public sealed class RequestDishFacetsViewModelValidator : AbstractValidator<RequestDishFacetsViewModel>
{
    public RequestDishFacetsViewModelValidator() =>
        RuleFor(model => model.DishName)
            .NotEmpty()
            .WithMessage("Name the dish. Reading the facets of a blank name is not a question with an answer.")
            .MaximumLength(AiPolicy.DishFacetNameMaxLength)
            .WithMessage($"Keep the dish name to {AiPolicy.DishFacetNameMaxLength} characters or fewer.");
}

/// <summary>Stable error codes this request seam introduces. Renaming one is a breaking API change.</summary>
public static class AiDishFacetsRequestErrors
{
    /// <summary>The request failed shape validation — a missing name, or one longer than its bound.</summary>
    public const string RequestInvalid = "ai.dishFacets.invalid_request";

    /// <summary>A real task, switched off for this deployment.</summary>
    public const string TaskNotEnabled = "ai.dishFacets.not_enabled";

    /// <summary>No such request, or one belonging to another workspace: deliberately indistinguishable.</summary>
    public const string RequestNotFound = "ai.dishFacetRequest.not_found";
}
