using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The version-history query, bound from the query string. Carries neither the workspace nor the document:
/// the workspace is resolved from the route and the caller's membership, and the document is a route segment.
/// </summary>
/// <remarks>
/// No filter and no sort parameter, for the reason a guide's history has none: a document's versions are a
/// short numbered history, the one useful order is newest first, and every row is worth showing.
/// </remarks>
public sealed record BrandSourceDocumentVersionListViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and the document it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>
/// One row of a document's version history: what the file is, who added it and when, whether it is the one
/// in force, and where its own text stands.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never an object key, a container or a URL</strong>, and never the bytes or the extracted text. A
/// version's file is reached through the download route, which streams it (media.md).
/// </para>
/// <para>
/// <strong>Every version carries its own extraction state</strong>, not the document's. Text extracted from a
/// file stays attached to the version it was read from, so a replaced version keeps the text it had while the
/// new one reads <c>NotExtracted</c> until something reads it — and this list is the only place that history
/// is visible.
/// </para>
/// </remarks>
/// <param name="IsCurrent">
/// Whether this is the version the document points at. Exactly one row of a complete history is current, and
/// it is the only one a replacement, a retry or a correction may act on.
/// </param>
/// <param name="CreatedByMembershipId">The membership that added the version. Not a name or an address.</param>
/// <param name="ContentChecksum">
/// <c>sha256:</c> and the digest of the stored bytes. Also this version's download entity tag, so a client
/// holding both can tell whether the bytes it has are still the bytes this version names.
/// </param>
public sealed record BrandSourceDocumentVersionSummaryServiceModel(
    Guid Id,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    string ContentChecksum,
    Guid CreatedByMembershipId,
    DateTimeOffset CreatedAt,
    bool IsCurrent,
    BrandSourceExtractionSummaryServiceModel Extraction);

/// <summary>The last row of the previous page: where the next one resumes.</summary>
/// <remarks>
/// One column, unlike the source library's position. A version number is unique within its document, so the
/// keyset is a strict comparison on it alone and there is never a tie to break.
/// </remarks>
public sealed record BrandSourceDocumentVersionListPosition(int VersionNumber);

/// <param name="DocumentId">From the route. Part of the scope, so a cursor cannot cross to another document.</param>
/// <param name="Scope">The ordered set this query selects in the resolved workspace. Cursors are bound to it.</param>
/// <param name="RequestedLimit">The page size asked for. <see cref="Limit"/> is what is used.</param>
public sealed record BrandSourceDocumentVersionListCriteria(
    Guid DocumentId,
    string Scope,
    BrandSourceDocumentVersionListPosition? Position = null,
    int? RequestedLimit = null)
{
    /// <summary>Clamped on every read, so no copy of this record can ask for an unbounded page.</summary>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>One history row as the repository projects it, in SQL.</summary>
/// <remarks>
/// No <c>IsCurrent</c>: which version is current is one fact about the document, read once beside the status,
/// rather than a correlated subquery repeated on every row of every page.
/// </remarks>
public sealed record BrandSourceDocumentVersionSummaryRecord(
    Guid Id,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    string ContentChecksum,
    Guid CreatedByMembershipId,
    DateTimeOffset CreatedAt,
    BrandSourceExtractionStatus? ExtractionStatus,
    BrandSourceExtractionOrigin? ExtractionOrigin,
    DateTimeOffset? ExtractionAt) : IReferenceRow
{
    string IReferenceRow.SortValue => BrandSourceDocumentVersionListQueryFactory.FormatVersionNumber(VersionNumber);

    /// <summary>
    /// Carried because <see cref="ReferenceCursor"/> requires a non-empty tie-breaker, and never consulted:
    /// the sort value is already unique within the document, so the keyset predicate needs nothing else.
    /// </summary>
    string IReferenceRow.TieBreaker => Id.ToString("D");
}

/// <summary>
/// The two facts about the document itself that its history needs: whether it may be read at all, and which
/// of its versions is in force.
/// </summary>
/// <remarks>
/// A read of its own rather than a column on every row. It also answers the existence question before any
/// version is selected, so an unknown document, another workspace's and a removed one are one refusal rather
/// than an empty page.
/// </remarks>
public sealed record BrandSourceDocumentVersionListContextRecord(
    BrandSourceDocumentStatus Status, int CurrentVersionNumber);

/// <summary>
/// Turns a bound <see cref="BrandSourceDocumentVersionListViewModel"/> into typed criteria: scope built from
/// the workspace and the document, cursor checked against that scope, page size left to be clamped.
/// </summary>
public static class BrandSourceDocumentVersionListQueryFactory
{
    private const char ScopeSeparator = '\u001E';

    /// <summary>Fixed width, so the encoded position orders the same way the column does.</summary>
    private const string VersionNumberFormat = "D10";

    public static bool TryCreate(
        BrandSourceDocumentVersionListViewModel model,
        Guid workspaceId,
        Guid documentId,
        out BrandSourceDocumentVersionListCriteria? criteria,
        out OperationError? error)
    {
        ArgumentNullException.ThrowIfNull(model);

        criteria = null;
        error = null;

        // The document is in the scope as well as the workspace: paging one document's history with a cursor
        // issued for another's would still decode and still produce a page, just not the page it names.
        var scope = string.Join(
            ScopeSeparator, "brand-source-document-versions", workspaceId.ToString("N"), documentId.ToString("N"));

        BrandSourceDocumentVersionListPosition? position = null;

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !TryPosition(cursor, out position)))
        {
            error = OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                "This cursor was issued for a different workspace or a different document. Start again without one.",
                [("cursor", "Start the list again without a cursor.")]);

            return false;
        }

        criteria = new BrandSourceDocumentVersionListCriteria(documentId, scope, position, model.Limit);

        return true;
    }

    internal static string FormatVersionNumber(int versionNumber) =>
        versionNumber.ToString(VersionNumberFormat, CultureInfo.InvariantCulture);

    private static bool TryPosition(ReferenceCursor cursor, out BrandSourceDocumentVersionListPosition? position)
    {
        position = null;

        if (!int.TryParse(cursor.SortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
            || versionNumber <= 0)
        {
            return false;
        }

        position = new BrandSourceDocumentVersionListPosition(versionNumber);

        return true;
    }
}
