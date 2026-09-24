using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The version history query. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace parameter and no recipe parameter.</strong> Both are route segments, resolved
/// server-side before the action runs — the workspace from the slug and the caller's active membership, the
/// recipe from its own segment. A field here for either would be one a request could set (tenancy.md).
/// </para>
/// <para>
/// <strong>Two parameters, and no filters.</strong> A recipe's history is short, complete and ordered: there is
/// nothing to search within it that a client holding the page cannot do better itself, and a filter would only
/// hide the numbering that makes the list readable. If one is ever wanted — versions by source, say — it is an
/// added parameter, which api-contract.md treats as compatible.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and both parameters carry a <see cref="DescriptionAttribute"/>: the
/// generated OpenAPI document otherwise takes the C# name and, for a parameter with no description of its own,
/// falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record RecipeVersionHistoryViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and recipe it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);
