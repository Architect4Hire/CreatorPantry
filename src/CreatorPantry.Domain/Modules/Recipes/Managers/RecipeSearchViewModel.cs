using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The recipe library query. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace parameter, and there must not be.</strong> The workspace is resolved from the
/// route segment and the caller's active membership before the action runs, and reaches the query through the
/// global query filter. A field here would be one a request body, a job payload or an AI tool argument could
/// set, which is the thing tenancy.md forbids outright.
/// </para>
/// <para>
/// <strong>Why the lists are strings.</strong> Comma-separated values are compact enough to live in a browser
/// address bar, which is where the library keeps its filter state — but ASP.NET Core does not split a comma into
/// an array of GUIDs or enums, so these arrive as one string and are split in
/// <see cref="RecipeSearchQueryFactory"/>. The upshot is better than natural binding would have been: a
/// malformed id is answered with <see cref="RecipeErrorCodes.SearchInvalidRequest"/> naming the parameter,
/// rather than the framework's generic body-oriented message.
/// </para>
/// <para>
/// <strong>Why the enums are strings too.</strong> Their accepted values are this module's, so this module
/// should name them in the refusal. The same choice, for the same reason, as
/// <c>MeasurementUnitQueryViewModel.Dimension</c>.
/// </para>
/// <para>
/// <strong>Why the dates and numbers are not.</strong> ISO 8601, integers and booleans have one universal
/// spelling that the framework's binder already reports on, and hand-parsing them would add four more error
/// paths to own nothing new. A malformed one is answered by the model-binding failure path, exactly as
/// <c>limit=abc</c> is on every existing paged route.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and every parameter carries a
/// <see cref="DescriptionAttribute"/>: the generated OpenAPI document otherwise takes the C# name and, for a
/// parameter with no description of its own, falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record RecipeSearchViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched as a substring of the title or the description. Terms shorter than two characters are ignored. Not matched against ingredient lines or instructions.")]
    string? Search = null,
    [property: FromQuery(Name = "status")]
    [property: Description("Comma-separated editorial states: Draft, Ready, Archived. Omit for every state, archived recipes included.")]
    string? Status = null,
    [property: FromQuery(Name = "tag")]
    [property: Description("Comma-separated workspace tag ids. A recipe matches if it carries any one of them.")]
    string? Tag = null,
    [property: FromQuery(Name = "cuisine")]
    [property: Description("Comma-separated cuisine ids from the shared reference vocabulary.")]
    string? Cuisine = null,
    [property: FromQuery(Name = "course")]
    [property: Description("Comma-separated course ids from the shared reference vocabulary.")]
    string? Course = null,
    [property: FromQuery(Name = "mine")]
    [property: Description("True to return only recipes you wrote. Authorship is read from your own membership; there is no way to filter by another member.")]
    bool? Mine = null,
    [property: FromQuery(Name = "readiness")]
    [property: Description("Readiness of the recipe's most recent version: Draft or Ready. Editorial only, never a publication claim.")]
    string? Readiness = null,
    [property: FromQuery(Name = "ingredientReview")]
    [property: Description("Whether ingredient lines are resolved against the shared vocabulary: HasUnmatched or AllMatched. Not a dietary, allergen or nutrition state.")]
    string? IngredientReview = null,
    [property: FromQuery(Name = "updatedFrom")]
    [property: Description("Inclusive ISO 8601 lower bound on the last edit.")]
    DateTimeOffset? UpdatedFrom = null,
    [property: FromQuery(Name = "updatedBefore")]
    [property: Description("Exclusive ISO 8601 upper bound on the last edit.")]
    DateTimeOffset? UpdatedBefore = null,
    [property: FromQuery(Name = "createdFrom")]
    [property: Description("Inclusive ISO 8601 lower bound on when the recipe was created.")]
    DateTimeOffset? CreatedFrom = null,
    [property: FromQuery(Name = "createdBefore")]
    [property: Description("Exclusive ISO 8601 upper bound on when the recipe was created.")]
    DateTimeOffset? CreatedBefore = null,
    [property: FromQuery(Name = "sort")]
    [property: Description("Ordering: RecentlyUpdated (the default) or Title. Arbitrary column names are not accepted.")]
    string? Sort = null,
    [property: FromQuery(Name = "includeTotal")]
    [property: Description("Whether to count every match alongside the page. Defaults to true; pass false when following a cursor, since the total will not have changed meaningfully and counting costs a second query.")]
    bool? IncludeTotal = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace, ordering and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);
