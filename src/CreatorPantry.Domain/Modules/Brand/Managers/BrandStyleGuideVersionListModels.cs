using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The version-history query, bound from the query string. Carries neither the workspace nor the guide: the
/// workspace is resolved from the route and the caller's membership, and the guide is a route segment.
/// </summary>
/// <remarks>
/// No filter and no sort parameter. A guide's versions are a short numbered history, so the one useful order
/// is newest first and every row is worth showing — a status filter would be a client-side concern over a set
/// that fits on a page or two.
/// </remarks>
public sealed record BrandStyleGuideVersionListViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and the guide it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>
/// Where one version stands with approval.
/// </summary>
/// <remarks>
/// Derived, never stored: a <see cref="Data.Entities.BrandStyleGuideVersion"/> has no status column, because
/// approval is recorded beside it as a <see cref="Data.Entities.BrandStyleGuideApproval"/> and that is what
/// lets the version be immutable from the moment it is written. Approval is never withdrawn, so these two
/// values are the whole set.
/// </remarks>
public enum BrandStyleGuideVersionStatus
{
    /// <summary>No approval exists for the version.</summary>
    Draft = 0,

    Approved = 1,
}

/// <summary>
/// One row of a guide's version history: what the version is, who wrote it and why, and how it stands. Never
/// a section, a rule, a body or the citation list — the list says which versions exist, and the read says
/// what one of them says.
/// </summary>
/// <param name="SourceCount">How many source document versions the version cites.</param>
/// <param name="StaleSourceCount">
/// How many of those citations name a source document version the document has since superseded. Zero means
/// every cited source is still its document's current version; the row is stale when this is above zero.
/// </param>
/// <param name="CreatedByMembershipId">The membership that wrote the version. Not a name or an address.</param>
/// <param name="IsActive">Whether the workspace default points at this version.</param>
public sealed record BrandStyleGuideVersionSummaryServiceModel(
    Guid Id,
    int VersionNumber,
    BrandStyleGuideVersionStatus Status,
    int SourceCount,
    int StaleSourceCount,
    Guid CreatedByMembershipId,
    string? ChangeReason,
    DateTimeOffset CreatedAt,
    bool IsActive);

/// <summary>The last row of the previous page: where the next one resumes.</summary>
/// <remarks>
/// One column, unlike the source library's position. A version number is unique within its guide, so the
/// keyset is a strict comparison on it alone and there is never a tie to break.
/// </remarks>
public sealed record BrandStyleGuideVersionListPosition(int VersionNumber);

/// <param name="GuideId">From the route. Part of the scope, so a cursor cannot cross to another guide.</param>
/// <param name="Scope">The ordered set this query selects in the resolved workspace. Cursors are bound to it.</param>
/// <param name="RequestedLimit">The page size asked for. <see cref="Limit"/> is what is used.</param>
public sealed record BrandStyleGuideVersionListCriteria(
    Guid GuideId,
    string Scope,
    BrandStyleGuideVersionListPosition? Position = null,
    int? RequestedLimit = null)
{
    /// <summary>Clamped on every read, so no copy of this record can ask for an unbounded page.</summary>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One history row as the repository projects it, in SQL.
/// </summary>
/// <remarks>
/// No stale count: that one needs three tables, so it is read once for the page's own version ids rather than
/// as a correlated subquery per row — the same shape as the source library's tag names.
/// </remarks>
/// <param name="IsApproved">Whether an approval row exists. Business turns it into the status.</param>
public sealed record BrandStyleGuideVersionSummaryRecord(
    Guid Id,
    int VersionNumber,
    string? ChangeReason,
    DateTimeOffset CreatedAt,
    Guid CreatedByMembershipId,
    bool IsApproved,
    int SourceCount,
    bool IsActive) : IReferenceRow
{
    string IReferenceRow.SortValue => BrandStyleGuideVersionListQueryFactory.FormatVersionNumber(VersionNumber);

    /// <summary>
    /// Carried because <see cref="ReferenceCursor"/> requires a non-empty tie-breaker, and never consulted:
    /// the sort value is already unique within the guide, so the keyset predicate needs nothing else.
    /// </summary>
    string IReferenceRow.TieBreaker => Id.ToString("D");
}

/// <summary>
/// Turns a bound <see cref="BrandStyleGuideVersionListViewModel"/> into typed criteria: scope built from the
/// workspace and the guide, cursor checked against that scope, page size left to be clamped.
/// </summary>
public static class BrandStyleGuideVersionListQueryFactory
{
    private const char ScopeSeparator = '\u001E';

    /// <summary>Fixed width, so the encoded position orders the same way the column does.</summary>
    private const string VersionNumberFormat = "D10";

    public static bool TryCreate(
        BrandStyleGuideVersionListViewModel model,
        Guid workspaceId,
        Guid guideId,
        out BrandStyleGuideVersionListCriteria? criteria,
        out OperationError? error)
    {
        ArgumentNullException.ThrowIfNull(model);

        criteria = null;
        error = null;

        // The guide is in the scope as well as the workspace: paging one guide's history with a cursor issued
        // for another's would still decode and still produce a page, just not the page it names.
        var scope = string.Join(
            ScopeSeparator, "brand-style-guide-versions", workspaceId.ToString("N"), guideId.ToString("N"));

        BrandStyleGuideVersionListPosition? position = null;

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !TryPosition(cursor, out position)))
        {
            error = OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "This cursor was issued for a different workspace or a different guide. Start again without one.",
                [("cursor", "Start the list again without a cursor.")]);

            return false;
        }

        criteria = new BrandStyleGuideVersionListCriteria(guideId, scope, position, model.Limit);

        return true;
    }

    internal static string FormatVersionNumber(int versionNumber) =>
        versionNumber.ToString(VersionNumberFormat, CultureInfo.InvariantCulture);

    private static bool TryPosition(ReferenceCursor cursor, out BrandStyleGuideVersionListPosition? position)
    {
        position = null;

        if (!int.TryParse(cursor.SortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
            || versionNumber <= 0)
        {
            return false;
        }

        position = new BrandStyleGuideVersionListPosition(versionNumber);

        return true;
    }
}
