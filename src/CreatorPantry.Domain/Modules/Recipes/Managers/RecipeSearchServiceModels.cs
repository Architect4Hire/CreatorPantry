namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One recipe as a library screen reads it: enough to render a card and decide what to open.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Vocabulary references are ids, not names</strong>, for the reasons
/// <see cref="RecipeDetailServiceModel"/> gives at length: cuisine and course belong to another module whose
/// display names are already published by <c>/api/v1/reference/*</c> — the same lists a library's filter
/// controls have to load anyway — and resolving them here would mean reaching into another module on every page
/// to produce strings the client already holds.
/// </para>
/// <para>
/// <strong>No membership columns, and no author at all.</strong> This follows the same rule
/// <see cref="RecipeDetailServiceModel"/> states: <c>WorkspaceId</c> and the membership ids never leave the
/// server. It has a consequence worth stating rather than discovering — a library card cannot show who wrote a
/// recipe, and could not render it usefully if it could, because no route publishes another member's name or
/// membership id. When a workspace members endpoint exists, adding an author here is a compatible change.
/// </para>
/// <para>
/// <strong>No tags.</strong> Naming them per row is either a join that multiplies rows or a second batched
/// query, and nothing yet says a card shows them. Adding an optional field later is compatible; removing one is
/// not (api-contract.md).
/// </para>
/// </remarks>
/// <param name="LatestVersionNumber">
/// The highest version number, or <c>null</c> for a recipe with no versions. Null rather than zero, which would
/// read as a version.
/// </param>
/// <param name="LatestVersionReadiness">
/// The readiness of that latest version. Editorial only — never a claim that the recipe has been published
/// anywhere (content.md).
/// </param>
/// <param name="HasUnmatchedIngredients">
/// Whether any ingredient line is still unresolved against the shared vocabulary. A statement about ingredient
/// references and <strong>never</strong> a dietary, allergen or nutrition finding — see
/// <see cref="RecipeIngredientReviewFilter"/>.
/// </param>
public sealed record RecipeSummaryServiceModel(
    Guid Id,
    string Title,
    string? Description,
    RecipeStatus Status,
    Guid? CuisineId,
    Guid? CourseId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? LatestVersionNumber,
    RecipeVersionReadiness? LatestVersionReadiness,
    bool HasUnmatchedIngredients);

/// <summary>
/// One page of a recipe search: the rows, where to resume, and how many there are in total.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> the shared <c>CursorPageServiceModel&lt;T&gt;</c>, which is the published
/// <c>CursorPage</c> component and carries exactly two fields. Adding a total to that envelope would change a
/// shape already shipped on nine reference routes that have no total to report; a module-owned page that carries
/// one is the compatible way to differ.
/// </para>
/// <para>
/// <see cref="NextCursor"/> is <c>null</c> on the last page, so a client loops until it is null rather than
/// comparing counts against a page size it may not have chosen.
/// </para>
/// </remarks>
/// <param name="TotalCount">
/// How many recipes match the filters across every page, or <c>null</c> when the caller asked not to be told.
/// <para>
/// Counted by a second statement, so under a concurrent write it can disagree with
/// <see cref="Items"/> by however many recipes were written in between. That is the accepted cost of not paying
/// for a windowed count on every page; a creator seeing "25 of 41" briefly read "of 40" is not worth that price.
/// It is a count of rows, never a page number — the ordering is a keyset, and there is no page N to jump to.
/// </para>
/// </param>
public sealed record RecipeSearchPageServiceModel(
    IReadOnlyList<RecipeSummaryServiceModel> Items,
    string? NextCursor,
    int? TotalCount);
