using Asp.Versioning;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The shared platform catalogue: ingredients, units, and the controlled vocabularies a recipe is described
/// with. Read-only, and global rather than workspace-scoped.
/// </summary>
/// <remarks>
/// These routes deliberately sit outside <c>/workspaces/{workspaceSlug}</c>. Reference data has no
/// <c>WorkspaceId</c> to filter by (tenancy.md), so no workspace is resolved while they run and no membership
/// policy applies — only the default requirement that the caller is signed in. Every response is cached under
/// a global key; none can be scoped to, or leak between, workspaces.
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/reference")]
public sealed class ReferenceController(
    IMeasurementFacade measurementFacade,
    IVocabularyFacade vocabularyFacade,
    IIngredientFacade ingredientFacade) : ControllerBase
{
    /// <summary>Active ingredients in the shared catalogue, with the aliases that resolve to each one. Retired entries are not listed.</summary>
    [HttpGet("ingredients")]
    [ProducesResponseType<CursorPageServiceModel<IngredientServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Ingredients(
        [FromQuery] IngredientQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await ingredientFacade.ListIngredientsAsync(query, cancellationToken));

    /// <summary>Active units of measure, with the factor each one converts by where it has one. Retired entries are not listed.</summary>
    [HttpGet("units")]
    [ProducesResponseType<CursorPageServiceModel<MeasurementUnitServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Units(
        [FromQuery] MeasurementUnitQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await measurementFacade.ListUnitsAsync(query, cancellationToken));

    /// <summary>Active coarse groupings an ingredient belongs to. Retired entries are not listed.</summary>
    [HttpGet("food-categories")]
    [ProducesResponseType<CursorPageServiceModel<ReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> FoodCategories(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListFoodCategoriesAsync(query, cancellationToken));

    /// <summary>Active regional and cultural cooking traditions a recipe can be described against. Retired entries are not listed.</summary>
    [HttpGet("cuisines")]
    [ProducesResponseType<CursorPageServiceModel<ReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Cuisines(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListCuisinesAsync(query, cancellationToken));

    /// <summary>The active roles a recipe can play in a meal. Retired entries are not listed.</summary>
    [HttpGet("courses")]
    [ProducesResponseType<CursorPageServiceModel<ReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Courses(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListCoursesAsync(query, cancellationToken));

    /// <summary>
    /// Active cooking methods; retired entries are not listed. A <c>requiresSafetyCaution</c> of <c>false</c>
    /// means no caution has been attached to that technique — it is not a statement that the technique is safe.
    /// </summary>
    [HttpGet("techniques")]
    [ProducesResponseType<CursorPageServiceModel<CookingTechniqueServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Techniques(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListTechniquesAsync(query, cancellationToken));

    /// <summary>Active kinds of equipment a recipe can call for. Kinds only, never a specific creator’s item. Retired entries are not listed.</summary>
    [HttpGet("equipment-types")]
    [ProducesResponseType<CursorPageServiceModel<ReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> EquipmentTypes(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListEquipmentTypesAsync(query, cancellationToken));

    /// <summary>
    /// Active dietary patterns a recipe can be described against; retired entries are not listed. Each entry's
    /// description states what it covers; none of them is a certification or a suitability ruling.
    /// </summary>
    [HttpGet("dietary-profiles")]
    [ProducesResponseType<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> DietaryProfiles(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListDietaryProfilesAsync(query, cancellationToken));

    /// <summary>
    /// The active allergen vocabulary; retired entries are not listed. Naming an allergen only; what any
    /// ingredient contains is recorded separately, with evidence and a cited source.
    /// </summary>
    [HttpGet("allergens")]
    [ProducesResponseType<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Allergens(
        [FromQuery] ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
        Respond(await vocabularyFacade.ListAllergensAsync(query, cancellationToken));

    private IActionResult Respond<T>(Domain.Managers.Results.OperationResult<CursorPageServiceModel<T>> result) =>
        result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
}
