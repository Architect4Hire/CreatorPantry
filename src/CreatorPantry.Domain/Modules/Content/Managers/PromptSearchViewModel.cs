using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The prompt library query. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace parameter, and there must not be.</strong> The workspace is resolved from the
/// route segment and the caller's active membership before the action runs, and reaches the query through the
/// global query filter. A field here would be one a query string, a job payload or an AI tool argument could
/// set, which is the thing tenancy.md forbids outright.
/// </para>
/// <para>
/// <strong>There is no <c>sort</c>.</strong> Newest-first is the only ordering, because both indexes behind
/// this read are <c>(…, CreatedAt DESC, Id DESC)</c>; a <c>sort</c> parameter accepting one value would be a
/// contract promising a choice that does not exist.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and every parameter carries a
/// <see cref="DescriptionAttribute"/>: the generated OpenAPI document otherwise takes the C# name and, for a
/// parameter with no description of its own, falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record PromptSearchViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched as a substring of the prompt text or your label. Terms shorter than two characters are ignored. Not matched against the model's draft, so a prompt is never found by words you removed from it.")]
    string? Search = null,
    [property: FromQuery(Name = "channel")]
    [property: Description("One content channel key, such as instagram. A key no channel has, or one retired since a prompt stored it, is an empty page rather than an error.")]
    string? Channel = null,
    [property: FromQuery(Name = "includeTotal")]
    [property: Description("Whether to count every match alongside the page. Defaults to true; pass false when following a cursor, since counting costs a second query.")]
    bool? IncludeTotal = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);
