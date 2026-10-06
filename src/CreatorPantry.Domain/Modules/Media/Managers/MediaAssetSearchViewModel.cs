using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The query string of a library search (DAM-002).
/// </summary>
/// <remarks>
/// <para>
/// Lists and vocabulary keys arrive as strings so that a malformed filter is this feature's own field-named
/// error rather than the model binder's, which cannot say which of a creator's filters was the problem.
/// Repeated values are comma-separated, as they are on every other filtered route here.
/// </para>
/// <para>
/// <strong>Why the dates and numbers are not.</strong> ISO 8601, integers and booleans have one universal
/// spelling the framework's binder already reports on, and hand-parsing them would add four more error paths
/// to own nothing new — the same trade <c>RecipeSearchViewModel</c> makes.
/// </para>
/// <para>
/// Names are given explicitly in camelCase and every parameter carries a
/// <see cref="DescriptionAttribute"/>: the generated OpenAPI document otherwise takes the C# name — which
/// would publish <c>Search</c> rather than <c>search</c> — and, for a parameter with no description of its
/// own, falls back to repeating its endpoint's summary.
/// </para>
/// <para>
/// <strong>No workspace and no owner.</strong> The route segment carries the first and the resolved
/// membership the second; a filter a client could set would be one tenancy.md forbids.
/// </para>
/// </remarks>
public sealed record MediaAssetSearchViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched as a substring of the title or the description, case-insensitively. Not matched against alt text, tags or the file name.")]
    string? Search = null,
    [property: FromQuery(Name = "channel")]
    [property: Description("One channel key from the workspace's own vocabulary. An asset that never said which channel it was for does not match any channel.")]
    string? Channel = null,
    [property: FromQuery(Name = "platform")]
    [property: Description("One platform key from the workspace's own vocabulary.")]
    string? Platform = null,
    [property: FromQuery(Name = "day")]
    [property: Description("One day of the week by name: Sunday through Saturday. Matches the weekly-theme day the asset belongs to.")]
    string? Day = null,
    [property: FromQuery(Name = "style")]
    [property: Description("One visual style key from the workspace's own vocabulary.")]
    string? Style = null,
    [property: FromQuery(Name = "cuisine")]
    [property: Description("Comma-separated cuisine ids from the shared reference vocabulary. An asset matches if it carries any one of them.")]
    string? Cuisine = null,
    [property: FromQuery(Name = "course")]
    [property: Description("Comma-separated course ids from the shared reference vocabulary. An asset matches if it carries any one of them.")]
    string? Course = null,
    [property: FromQuery(Name = "tag")]
    [property: Description("Comma-separated workspace tag ids. An asset matches if it carries any one of them.")]
    string? Tag = null,
    [property: FromQuery(Name = "recipeId")]
    [property: Description("Only assets linked to this recipe. An asset linked to it more than once is still listed once.")]
    Guid? RecipeId = null,
    [property: FromQuery(Name = "createdFrom")]
    [property: Description("Inclusive ISO 8601 lower bound on when the asset entered the library. Not when it was last used.")]
    DateTimeOffset? CreatedFrom = null,
    [property: FromQuery(Name = "createdBefore")]
    [property: Description("Exclusive ISO 8601 upper bound on when the asset entered the library, so adjacent ranges neither overlap nor leave a gap.")]
    DateTimeOffset? CreatedBefore = null,
    [property: FromQuery(Name = "sort")]
    [property: Description("Ordering: RecentlyAdded (the default) or Title. Arbitrary column names are not accepted.")]
    string? Sort = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace, ordering and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null,
    [property: FromQuery(Name = "includeTotal")]
    [property: Description("Whether to count the whole filtered set alongside the page. Defaults to true; pass false when following a cursor, since counting costs a second query per page.")]
    bool? IncludeTotal = null);
