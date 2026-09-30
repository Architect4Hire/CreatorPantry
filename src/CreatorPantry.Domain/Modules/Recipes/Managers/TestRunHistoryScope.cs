using System.Globalization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Builds the string that identifies one ordered set of a recipe's test runs, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// <para>
/// The two scopes this sits between. <see cref="RecipeVersionHistoryScope"/> names a recipe and no filters,
/// because a version history has none; <see cref="RecipeSearchScope"/> names filters and no recipe, because a
/// library search has no enclosing resource. A test history has both, and both must be in the fingerprint for
/// the same reason each of those states separately: a cursor is a position in one ordered set, and anything that
/// changes which set that is has to change the fingerprint or the cursor silently resumes in the wrong one.
/// </para>
/// <para>
/// <strong>The recipe especially.</strong> Without it, page two of one recipe's test history would decode cleanly
/// against another's and return that recipe's tests from the same timestamp onwards — a wrong answer rather than
/// an error, and the worst kind, because both recipes belong to the same creator and nothing about the result
/// would look wrong.
/// </para>
/// <para>
/// <strong>The workspace too</strong>, for the reason <see cref="RecipeSearchScope"/> gives: the ordered set a
/// cursor names depends on the resolved workspace, and that workspace arrives ambiently through the query filter
/// rather than as a request field. Belt and braces here, since a recipe id is already workspace-unique — but a
/// scope that omitted it would be relying on that rather than saying it.
/// </para>
/// <para>
/// <strong>The ordering is named although there is only one</strong>, so that cursors minted before a second one
/// is ever added cannot silently resume in it.
/// </para>
/// <para>
/// <strong><see cref="TestRunHistoryCriteria.IncludeSummary"/> is deliberately not in it</strong>, exactly as
/// <c>includeTotal</c> is not in the search scope: asking to be counted does not change which rows follow. Nor is
/// the page size, because a position is a position.
/// </para>
/// <para>
/// <strong>List filters are sorted before they are folded in.</strong> Without that,
/// <c>?outcome=Failed,Succeeded</c> and <c>?outcome=Succeeded,Failed</c> are one query with two scopes, and a
/// client that reordered its own parameters between pages would have its cursor refused for no reason it could
/// see. Version numbers are sorted numerically rather than as text, so <c>2,10</c> and <c>10,2</c> agree.
/// </para>
/// <para>
/// Nothing here is free text, so no value can contain the <c>|</c> or <c>=</c> this string is assembled from:
/// every part is a GUID, an enum name, an integer, a round-trip timestamp or a literal. That is why, unlike
/// <see cref="RecipeSearchScope"/>, there is no term to keep last.
/// </para>
/// </remarks>
public static class TestRunHistoryScope
{
    /// <summary>
    /// The resource this scope belongs to, matching the route's collection segment. The value is hashed into
    /// every issued cursor, so changing it invalidates outstanding ones.
    /// </summary>
    public const string Resource = "recipe-test-runs";

    /// <summary>The one ordering this route offers: most recently cooked first.</summary>
    private const string Ordering = "TestedAtDesc";

    public static string Build(Guid workspaceId, Guid recipeId, TestRunHistoryFilters filters) =>
        string.Join(
            '|',
            $"resource={Resource}",
            $"workspace={workspaceId:D}",
            $"recipe={recipeId:D}",
            $"sort={Ordering}",
            $"version={Numbers(filters.VersionNumbers)}",
            $"tester={Ids(filters.TesterMembershipIds)}",
            $"outcome={Names(filters.Outcomes)}",
            $"testedFrom={Timestamp(filters.TestedOnOrAfter)}",
            $"testedBefore={Timestamp(filters.TestedBefore)}",
            $"issues={filters.Issues}");

    private static string Numbers(IReadOnlyList<int>? values) =>
        values is null
            ? string.Empty
            : string.Join(',', values.Order().Select(value => value.ToString(CultureInfo.InvariantCulture)));

    private static string Names<TEnum>(IReadOnlyList<TEnum>? values)
        where TEnum : struct, Enum =>
        values is null
            ? string.Empty
            : string.Join(',', values.Select(value => value.ToString()).Order(StringComparer.Ordinal));

    private static string Ids(IReadOnlyList<Guid>? values) =>
        values is null
            ? string.Empty
            : string.Join(',', values.Select(value => value.ToString("D")).Order(StringComparer.Ordinal));

    private static string Timestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
}
