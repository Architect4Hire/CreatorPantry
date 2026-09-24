using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The position a page of history resumes from: the version number of the last row returned.
/// </summary>
/// <remarks>
/// <para>
/// Simpler than <see cref="RecipeSearchPosition"/>, and for a reason that is worth stating rather than
/// inferring from the missing code. A library search orders by a timestamp or a title, neither of which is
/// unique, so its keyset needs a tie-breaker to have a defined order at all. A recipe's versions are ordered by
/// <c>VersionNumber</c>, which <c>UX_RecipeVersions_Workspace_Recipe_VersionNumber</c> makes unique within one
/// recipe — the ordering is already total, and nothing remains for a tie-breaker to decide.
/// </para>
/// <para>
/// The cursor still carries one, because <see cref="ReferenceCursor"/>'s layout has a slot for it and refuses
/// to decode a cursor whose tie-breaker is empty. <see cref="RecipeVersionHistoryRecord.TieBreaker"/> fills it
/// with the version's id, which is honest — it is that row's unique key — and
/// <see cref="TryCreate"/> never reads it back, because the predicate it feeds has no use for it.
/// </para>
/// </remarks>
public sealed record RecipeVersionHistoryPosition
{
    private RecipeVersionHistoryPosition(int versionNumber) => VersionNumber = versionNumber;

    /// <summary>The last row's version number. The page resumes strictly below it.</summary>
    public int VersionNumber { get; }

    /// <summary>
    /// Converts a decoded cursor into a position, or returns <c>false</c> when it does not describe one.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> rather than throwing, for the reason <see cref="RecipeSearchPosition.TryCreate"/>
    /// gives: the value arrived from a query string, so a cursor whose sort value is not a version number is a
    /// caller's mistake and belongs in a refusal rather than an exception from a query. Whether the cursor was
    /// issued for this workspace and this recipe is a different question, already settled by
    /// <see cref="ReferenceCursor.TryResolve"/> against the scope.
    /// </remarks>
    public static bool TryCreate(ReferenceCursor cursor, out RecipeVersionHistoryPosition? position)
    {
        position = null;

        if (!int.TryParse(cursor.SortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber))
        {
            return false;
        }

        position = new RecipeVersionHistoryPosition(versionNumber);

        return true;
    }

    /// <summary>
    /// How a version number is written into a cursor. Paired with <see cref="TryCreate"/> and used by
    /// <see cref="RecipeVersionHistoryRecord.SortValue"/>, so the value a page mints and the value the next
    /// page parses cannot drift into two spellings.
    /// </summary>
    /// <remarks>
    /// Invariant culture and no sign, matched by <see cref="NumberStyles.None"/> on the way back in: a version
    /// number is positive by check constraint, so a cursor carrying <c>-1</c> or <c>1,024</c> is not one this
    /// route issued.
    /// </remarks>
    internal static string FormatVersionNumber(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One request for a page of a recipe's history: which recipe, where to resume, and how many rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace member, and there must not be.</strong> The workspace is resolved from the
/// route and the caller's membership and reaches the query through the global EF Core query filter; a field
/// here would be one a request body, a job payload or an AI tool argument could set (tenancy.md).
/// </para>
/// <para>
/// <strong>There is no sort either.</strong> Newest first is the contract, not a preference — a history is read
/// from the most recent change backwards, and offering the reverse would be a second ordering to index, to
/// bind cursors to, and to explain.
/// </para>
/// <para>
/// <strong>An unclamped page size is unrepresentable</strong>, exactly as on <see cref="RecipeSearchCriteria"/>:
/// <see cref="Limit"/> is derived rather than stored, so no constructor or <c>with</c> expression can produce a
/// criteria asking for ten thousand rows.
/// </para>
/// </remarks>
/// <param name="RecipeId">The recipe whose history this is, resolved from the route.</param>
/// <param name="Scope">
/// Identifies this recipe's history in the resolved workspace. Cursors minted for a page of it are bound to
/// this, so one cannot be replayed against another recipe or another workspace. Built by
/// <see cref="RecipeVersionHistoryScope"/>; the repository ignores it.
/// </param>
/// <param name="Position">Where to resume, or <c>null</c> for the first page.</param>
/// <param name="RequestedLimit">
/// The page size asked for, or <c>null</c> for the default. Out-of-range values are clamped rather than
/// rejected, so a client cannot fail a read by asking for too much.
/// </param>
public sealed record RecipeVersionHistoryCriteria(
    Guid RecipeId,
    string Scope,
    RecipeVersionHistoryPosition? Position = null,
    int? RequestedLimit = null)
{
    /// <inheritdoc cref="RecipeSearchCriteria.Limit"/>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One row of a recipe's history, as the repository projects it: the version's own account of itself, and no
/// part of what it contains.
/// </summary>
/// <remarks>
/// <para>
/// Projected entirely in SQL from <c>RecipeVersions</c>. <c>RecipeVersionSnapshots</c> is never joined, never
/// included and never named — listing a history must not read the archive it indexes.
/// </para>
/// <para>
/// <strong>The membership column is read and never published.</strong> It is what the Facade resolves into a
/// display name through the Tenancy facade — an author is a person, not an id, and this module can neither
/// read memberships nor read user names. It travels two layers up to be exchanged for a name and is dropped
/// there; see <see cref="RecipeVersionHistoryServiceModel.CreatedByName"/>.
/// </para>
/// </remarks>
public sealed record RecipeVersionHistoryRecord(
    Guid Id,
    int VersionNumber,
    Guid CreatedByMembershipId,
    RecipeVersionSource Source,
    RecipeVersionReadiness Readiness,
    string? Reason,
    DateTimeOffset CreatedAt,
    Guid? ParentVersionId,
    Guid? RestoredFromVersionId,
    Guid? AiProposalId) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => RecipeVersionHistoryPosition.FormatVersionNumber(VersionNumber);

    /// <inheritdoc />
    /// <remarks>
    /// The version's id. Unique by definition, and never consulted when resuming — see
    /// <see cref="RecipeVersionHistoryPosition"/> for why this ordering has no ties to break.
    /// </remarks>
    public string TieBreaker => Id.ToString("D");
}
