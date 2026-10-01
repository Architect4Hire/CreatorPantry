using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The brand source library query, bound from the query string. Carries no workspace: that comes from the
/// route and the caller's membership, and reaches the query through the global filter.
/// </summary>
/// <remarks>
/// The two enum filters are strings so that this module names their accepted values in the refusal, as the
/// recipe library's do.
/// </remarks>
public sealed record BrandSourceDocumentListViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched as a substring of the title or the current version's original filename. Terms shorter than two characters are ignored. Never matched against a document's contents.")]
    string? Search = null,
    [property: FromQuery(Name = "status")]
    [property: Description("Active (the default) or Archived. One value; removed documents are never listed.")]
    string? Status = null,
    [property: FromQuery(Name = "documentType")]
    [property: Description("What the creator classified the document as: StyleGuide, WritingSample, PublishedPost, Newsletter, SocialSample, VisualReference or Other.")]
    string? DocumentType = null,
    [property: FromQuery(Name = "channelKey")]
    [property: Description("The channel a document exemplifies, as its key. A key no document carries matches nothing.")]
    string? ChannelKey = null,
    [property: FromQuery(Name = "tag")]
    [property: Description("A tag name; repeat the parameter for several. A document matches if it carries any one of them.")]
    IReadOnlyList<string?>? Tag = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>
/// Where the current version stands with text extraction. <see cref="NotExtracted"/> is the absence of any
/// attempt; the other three are how the latest attempt ended.
/// </summary>
public enum BrandSourceExtractionState
{
    NotExtracted = 0,

    Succeeded = 1,

    /// <summary>The format cannot be read as text. A review state, not an error.</summary>
    Unsupported = 2,

    Failed = 3,
}

/// <param name="Origin">How the latest text came to be, or null when there has been no attempt.</param>
/// <param name="At">When the latest attempt or correction was recorded.</param>
public sealed record BrandSourceExtractionSummaryServiceModel(
    BrandSourceExtractionState State, BrandSourceExtractionOrigin? Origin, DateTimeOffset? At);

/// <summary>
/// One row of the brand source library: what the document is, its current version's metadata and whether its
/// text has been extracted. Never bytes, extracted text, a checksum, an object key or a URL.
/// </summary>
public sealed record BrandSourceDocumentSummaryServiceModel(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    string? ChannelKey,
    string? Audience,
    IReadOnlyList<string> Tags,
    BrandSourceDocumentStatus Status,
    BrandSourceDocumentVersionServiceModel CurrentVersion,
    BrandSourceExtractionSummaryServiceModel Extraction,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Status">Always one status, so the status-and-recency index both filters and orders.</param>
/// <param name="TagNames">Normalized tag names, sorted; a document matches on any one. Empty for no tag filter.</param>
/// <param name="Search">Lowered, at least two characters, or null.</param>
public sealed record BrandSourceDocumentListFilters(
    BrandSourceDocumentStatus Status,
    BrandSourceDocumentType? DocumentType,
    string? ChannelKey,
    IReadOnlyList<string> TagNames,
    string? Search);

/// <summary>The last row of the previous page: where the next one resumes.</summary>
public sealed record BrandSourceDocumentListPosition(DateTimeOffset UpdatedAt, Guid DocumentId);

/// <param name="Scope">The ordered set these filters select in the resolved workspace. Cursors are bound to it.</param>
/// <param name="RequestedLimit">The page size asked for. <see cref="Limit"/> is what is used.</param>
public sealed record BrandSourceDocumentListCriteria(
    BrandSourceDocumentListFilters Filters,
    string Scope,
    BrandSourceDocumentListPosition? Position = null,
    int? RequestedLimit = null)
{
    /// <summary>Clamped on every read, so no copy of this record can ask for an unbounded page.</summary>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>One library row as the repository projects it, in SQL.</summary>
public sealed record BrandSourceDocumentSummaryRecord(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    string? ChannelKey,
    string? Audience,
    BrandSourceDocumentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid VersionId,
    int VersionNumber,
    string MediaType,
    long SizeBytes,
    string OriginalFileName,
    DateTimeOffset VersionCreatedAt,
    BrandSourceExtractionStatus? ExtractionStatus,
    BrandSourceExtractionOrigin? ExtractionOrigin,
    DateTimeOffset? ExtractionAt) : IReferenceRow
{
    string IReferenceRow.SortValue => BrandSourceDocumentListQueryFactory.FormatTimestamp(UpdatedAt);

    string IReferenceRow.TieBreaker => Id.ToString("D");
}

/// <summary>
/// Turns a bound <see cref="BrandSourceDocumentListViewModel"/> into typed criteria: filters parsed, scope
/// built, cursor checked against that scope, page size left to be clamped. Filter errors are reported before
/// the cursor is considered, because a mistyped filter is the thing to fix and it changes the scope anyway.
/// </summary>
public static class BrandSourceDocumentListQueryFactory
{
    /// <summary>Round-trip, so a cursor carries every digit the column holds and never resumes early.</summary>
    private const string TimestampFormat = "O";

    private const char ScopeSeparator = '\u001E';

    public static bool TryCreate(
        BrandSourceDocumentListViewModel model,
        Guid workspaceId,
        out BrandSourceDocumentListCriteria? criteria,
        out OperationError? error)
    {
        criteria = null;
        error = null;

        var errors = new List<(string Field, string Error)>();

        var status = Status(model.Status, errors);
        var documentType = DocumentType(model.DocumentType, errors);
        var channelKey = ChannelKey(model.ChannelKey, errors);
        var tagNames = Tags(model.Tag, errors);
        var search = Search(model.Search, errors);

        if (errors.Count > 0)
        {
            error = OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest, "That list cannot be produced as described.", errors);

            return false;
        }

        var filters = new BrandSourceDocumentListFilters(status, documentType, channelKey, tagNames, search);
        var scope = string.Join(
            ScopeSeparator,
            "brand-source-documents",
            workspaceId.ToString("N"),
            (int)status,
            (int?)documentType,
            channelKey,
            string.Join('\u001F', tagNames),
            search);

        BrandSourceDocumentListPosition? position = null;

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !TryPosition(cursor, out position)))
        {
            error = OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                "This cursor was issued for a different workspace or set of filters. Start again without one.",
                [("cursor", "Start the list again without a cursor.")]);

            return false;
        }

        criteria = new BrandSourceDocumentListCriteria(filters, scope, position, model.Limit);

        return true;
    }

    internal static string FormatTimestamp(DateTimeOffset value) => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryPosition(ReferenceCursor cursor, out BrandSourceDocumentListPosition? position)
    {
        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var documentId)
            || !DateTimeOffset.TryParseExact(
                cursor.SortValue, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt))
        {
            return false;
        }

        position = new BrandSourceDocumentListPosition(updatedAt, documentId);

        return true;
    }

    private static BrandSourceDocumentStatus Status(string? value, List<(string, string)> errors)
    {
        if (BrandProfileInputChecks.Normalize(value) is not { } text)
        {
            return BrandSourceDocumentStatus.Active;
        }

        // A tombstone is not library content, so Removed is refused rather than listed.
        if (TryName<BrandSourceDocumentStatus>(text, out var status) && status != BrandSourceDocumentStatus.Removed)
        {
            return status;
        }

        errors.Add(("status", "Use Active or Archived."));

        return BrandSourceDocumentStatus.Active;
    }

    private static BrandSourceDocumentType? DocumentType(string? value, List<(string, string)> errors)
    {
        if (BrandProfileInputChecks.Normalize(value) is not { } text)
        {
            return null;
        }

        if (TryName<BrandSourceDocumentType>(text, out var type))
        {
            return type;
        }

        errors.Add(("documentType", $"Use one of: {string.Join(", ", Enum.GetNames<BrandSourceDocumentType>())}."));

        return null;
    }

    private static string? ChannelKey(string? value, List<(string, string)> errors)
    {
        var key = BrandProfileInputChecks.Normalize(value);

        if (key is { Length: > BrandPolicy.ChannelKeyMaxLength })
        {
            errors.Add(("channelKey", "That is not a channel key."));
        }

        return key;
    }

    private static IReadOnlyList<string> Tags(IReadOnlyList<string?>? values, List<(string, string)> errors)
    {
        var names = (values ?? []).Select(BrandProfileInputChecks.Normalize).OfType<string>().ToList();

        if (names.Count > BrandPolicy.MaxSourceDocumentTags || names.Any(name => name.Length > BrandPolicy.SourceTagNameMaxLength))
        {
            errors.Add(("tag", $"Filter by at most {BrandPolicy.MaxSourceDocumentTags} tags of at most {BrandPolicy.SourceTagNameMaxLength} characters each."));

            return [];
        }

        // Sorted and de-duplicated, so the same tags in another order are the same scope and the same cursor.
        return [.. names
            .Select(NameNormalization.NormalizeName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static string? Search(string? value, List<(string, string)> errors)
    {
        var term = value?.Trim().ToLowerInvariant();

        if (term is { Length: > ReferencePolicy.SearchMaxLength })
        {
            errors.Add(("search", $"Search for at most {ReferencePolicy.SearchMaxLength} characters."));

            return null;
        }

        return string.IsNullOrEmpty(term) || term.Length < ReferencePolicy.MinSearchLength ? null : term;
    }

    /// <summary>A declared name, in any case. Never a number: <c>status=3</c> is not a way to say Removed.</summary>
    private static bool TryName<TEnum>(string text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        return !char.IsAsciiDigit(text[0]) && text[0] != '-' && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}
