using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The test-history query. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace parameter and no recipe parameter.</strong> Both are route segments, resolved
/// server-side before the action runs — the workspace from the slug and the caller's active membership, the recipe
/// from its own segment. A field here for either would be one a request could set (tenancy.md).
/// </para>
/// <para>
/// <strong>Why the lists are strings</strong>, and why the dates and booleans are not: the reasoning on
/// <see cref="RecipeSearchViewModel"/> applies unchanged. Comma-separated values live in a browser address bar,
/// which is where a filtered history keeps its state, and ASP.NET Core will not split a comma into an array of
/// GUIDs, ints or enums — so these arrive as one string and <see cref="TestRunHistoryQueryFactory"/> splits them,
/// which also means a malformed value is refused with this module's own code naming the parameter. ISO 8601
/// timestamps and booleans have one universal spelling the binder already reports on.
/// </para>
/// <para>
/// <strong>There is no <c>sort</c></strong>: most recently cooked first is the contract, as on the version
/// history. If a second ordering is ever wanted it is an added parameter, which api-contract.md treats as
/// compatible — and it would arrive with the index that makes it affordable.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and every parameter carries a <see cref="DescriptionAttribute"/>: the
/// generated OpenAPI document otherwise takes the C# name and, for a parameter with no description of its own,
/// falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record TestRunHistoryViewModel(
    [property: FromQuery(Name = "version")]
    [property: Description("Comma-separated version numbers, as the recipe's history lists them. A test is always against one exact version, so this names versions rather than a range.")]
    string? Version = null,
    [property: FromQuery(Name = "testedBy")]
    [property: Description("Comma-separated membership ids of the people who cooked the tests, as testedByMembershipId returns them. Whoever cooked it, not whoever typed it up.")]
    string? TestedBy = null,
    [property: FromQuery(Name = "outcome")]
    [property: Description("Comma-separated verdicts: NotStated, Succeeded, SucceededWithIssues, Failed. The tester's own judgement, never derived from the issues.")]
    string? Outcome = null,
    [property: FromQuery(Name = "testedFrom")]
    [property: Description("Inclusive ISO 8601 lower bound on when the cooking happened — not on when the notes were written up.")]
    DateTimeOffset? TestedFrom = null,
    [property: FromQuery(Name = "testedBefore")]
    [property: Description("Exclusive ISO 8601 upper bound on when the cooking happened.")]
    DateTimeOffset? TestedBefore = null,
    [property: FromQuery(Name = "issues")]
    [property: Description("Whether the test still has a problem nobody has decided anything about: HasUnresolved or AllResolved. A record of what testers wrote, never a readiness, safety or dietary finding.")]
    string? Issues = null,
    [property: FromQuery(Name = "includeSummary")]
    [property: Description("Whether to return the counts alongside the page. Defaults to true; pass false when following a cursor, since the figures will not have changed meaningfully and counting costs two more queries.")]
    bool? IncludeSummary = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace, recipe and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);
