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
/// <para>
/// <strong>The hero picture is here, unlike the tags</strong>, and the difference is what it costs: it is one
/// row per recipe rather than many, so it is an apply in the same statement rather than a join that multiplies
/// rows. It arrived later as an optional field, which is the compatible way to add one.
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
/// <param name="HeroAsset">
/// The recipe's lead picture, or <c>null</c> when it has none a caller can show. Optional and additive: a
/// client that does not read it is unaffected.
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
    bool HasUnmatchedIngredients,
    RecipeHeroAssetServiceModel? HeroAsset = null);

/// <summary>
/// The recipe's lead picture as a card shows it: which asset, which version of it, and what it shows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An identifier and a number, never an address.</strong> No URL, no object key, no signed link —
/// issuing access to bytes is the media seam's decision, made per request with a short lifetime (media.md).
/// A card reads the pixels through the media render routes, which authorize the caller again, and asks for a
/// rendition rather than the original: showing a library should not cost a creator a page of full-size
/// photographs.
/// </para>
/// <para>
/// <strong>Absent rather than refused.</strong> The whole field is null when there is nothing to show — no
/// hero link, a soft-deleted asset, or an asset this workspace does not hold, which the query filter makes
/// invisible rather than denied. One answer for all three, because a card can act on none of them and the
/// difference between them is exactly what tenancy.md says must not be disclosed. A recipe whose photograph
/// is gone is still a recipe, so this is never a 404 and never an error.
/// </para>
/// </remarks>
/// <param name="AltText">
/// What the creator has said the picture shows, or null when nobody has written any. A client may treat null
/// as "nobody has described it" and must never fill it in from the recipe's title — that would be describing
/// pixels nothing has looked at (media.md).
/// <para>
/// One caveat, and it is the asset's rather than this field's: alt text is one value on the asset, not one per
/// version, so for a use pinned to an older copy it may have been written for pixels that have since been
/// replaced. Media's own <c>MediaAssetDescription</c> records the same limit. Publishing it per version is a change
/// to the media aggregate, not to this read, and until then <see cref="VersionNumber"/> is what tells a client
/// that the words and the pixels may be of different ages.
/// </para>
/// </param>
/// <param name="VersionNumber">
/// The version to show: the pin when the recipe's use of the asset is pinned to one, and the current version
/// otherwise.
/// </param>
/// <param name="CurrentVersionNumber">
/// The asset's newest version, so a client can tell a pinned older copy from the current one.
/// </param>
public sealed record RecipeHeroAssetServiceModel(
    Guid MediaAssetId,
    string Title,
    string? AltText,
    int VersionNumber,
    int CurrentVersionNumber);

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
