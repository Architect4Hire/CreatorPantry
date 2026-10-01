using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The body of the four lifecycle commands — <c>archive</c>, <c>unarchive</c>, <c>remove</c> and
/// <c>restore</c>: the token of the read the command was composed against, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One type for all four</strong>, because all four carry the same one field and none has anything of
/// its own to say. Four identical records would be four places to forget a rule.
/// </para>
/// <para>
/// <strong>No reason.</strong> The trace of these commands is an audit entry, whose summary has to stay safe
/// to display — which creator free text is not something this seam can promise. What the audit records
/// instead is who moved the document, when, and between which states.
/// </para>
/// <para>
/// <strong>No target state.</strong> The route says which way the document is going, so a body that also
/// said it would be a second source of truth and a way for the two to disagree.
/// </para>
/// </remarks>
public sealed record BrandSourceDocumentLifecycleViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the document as the caller last saw it. Required.
    /// </summary>
    /// <remarks>
    /// Required even though these commands are idempotent, and for a reason idempotency does not cover:
    /// removing a document a collaborator is replacing should tell the remover that it moved under them, not
    /// silently shelve work they have not seen. For <c>restore</c> the token comes from the removed-document
    /// list, which is the only place a tombstone's token is published.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }
}

public sealed class BrandSourceDocumentLifecycleViewModelValidator : AbstractValidator<BrandSourceDocumentLifecycleViewModel>
{
    public BrandSourceDocumentLifecycleViewModelValidator() =>
        // The same two rules the replacement's validator states, and deliberately not shared with it: the
        // view models have no common base, and hiding the rule in a helper would obscure that both require
        // the same token for the same reason.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the document's concurrency token with your request.")
            .Must(BrandConcurrencyToken.IsWellFormed).WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(BrandSourceDocumentLifecycleViewModel.ExpectedConcurrencyToken));
}


/// <summary>
/// Which lifecycle command was asked for.
/// </summary>
/// <remarks>
/// <strong>Not a target status, deliberately.</strong> <see cref="Archive"/> and <see cref="Restore"/> both
/// land a document on <see cref="BrandSourceDocumentStatus.Archived"/>, so a target state cannot tell them
/// apart — and they differ in the two ways that matter most: a restore needs Owner where an archive needs
/// Editor, and a restore is the only command that can see a tombstone at all. Naming the command is what
/// keeps an Editor from reaching a restore through the state they share.
/// </remarks>
public enum BrandSourceDocumentLifecycleCommand
{
    /// <summary>Active → Archived. Editor.</summary>
    Archive = 1,

    /// <summary>Archived → Active. Editor.</summary>
    Unarchive = 2,

    /// <summary>Active or Archived → Removed. Editor. Deletes nothing.</summary>
    Remove = 3,

    /// <summary>Removed → Archived. Owner, and the only command that reads through a tombstone.</summary>
    Restore = 4,
}

/// <summary>
/// What would be left behind if this source document were removed: how much of it there is, and what still
/// points at it.
/// </summary>
/// <remarks>
/// <para>
/// A read for the confirmation step, so a creator removing a document sees what the removal does <em>not</em>
/// take with it. Nothing here is a warning about data loss, because removal causes none: it is a tombstone,
/// and every row, version and stored object survives it.
/// </para>
/// <para>
/// <strong>Why <c>Holds</c> is a typed list rather than a <c>guideVersions</c> array.</strong> Style-guide
/// versions are the only thing that can hold a document today. Brand-source chunks and their embeddings will
/// be able to later, and a prompt context never will — it is assembled per request and stored nowhere. A
/// shape that named guides in its field names would have to break to admit the second kind.
/// </para>
/// </remarks>
/// <param name="VersionCount">How many immutable versions the document has. Always at least one.</param>
/// <param name="StoredBytes">The sum of every version's size. What stays in private storage after a removal.</param>
/// <param name="IsHeld">
/// True when at least one <strong>approved</strong> guide version cites one of this document's versions. The
/// fact a future retention job has to honour, which is why it is stated rather than left to be recomputed
/// from <paramref name="Holds"/>.
/// </param>
public sealed record BrandSourceDocumentUsageServiceModel(
    Guid DocumentId,
    int VersionCount,
    long StoredBytes,
    bool IsHeld,
    IReadOnlyList<BrandSourceHoldServiceModel> Holds);

/// <summary>What kind of record is pointing at a source document version.</summary>
public enum BrandSourceHoldKind
{
    /// <summary>A <c>BrandStyleGuideVersion</c> that was written from this document.</summary>
    StyleGuideVersion = 1,
}

/// <summary>
/// One record that points at one version of this document.
/// </summary>
/// <param name="SourceVersionNumber">Which of <em>this document's</em> versions is cited.</param>
/// <param name="HolderName">
/// The guide's display name — creator text, and the only creator text on this model. Present so a
/// confirmation can name what it is about rather than showing an id.
/// </param>
/// <param name="IsApproved">
/// Whether the holder is approved. An approved holder is what makes the document held; a draft one may still
/// be superseded, so it is reported as a link and not as a hold.
/// </param>
public sealed record BrandSourceHoldServiceModel(
    BrandSourceHoldKind Kind,
    Guid HolderId,
    string? HolderName,
    int HolderVersionNumber,
    int SourceVersionNumber,
    bool IsApproved);

/// <summary>One hold as the repository projects it, in SQL.</summary>
public sealed record BrandSourceHoldRecord(
    Guid GuideId,
    string GuideDisplayName,
    Guid GuideVersionId,
    int GuideVersionNumber,
    int SourceVersionNumber,
    bool IsApproved);

/// <summary>How much of a document there is, for the usage read.</summary>
public sealed record BrandSourceDocumentSizeRecord(int VersionCount, long StoredBytes);

/// <summary>
/// The removed-document list query. Carries no workspace and no filters: a bin is one ordered set.
/// </summary>
public sealed record RemovedBrandSourceDocumentListViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>
/// One row of the removed-document bin: enough to recognise a tombstone and to restore it.
/// </summary>
/// <remarks>
/// Deliberately not the library row. It drops the things a bin does not need — tags, channel, audience,
/// extraction state — and adds the three it does: who removed it, when, and the
/// <paramref name="ConcurrencyToken"/> a restore has to quote. This is the <strong>only</strong> place that
/// token is published for a removed document, because every other route answers 404 for one.
/// </remarks>
public sealed record RemovedBrandSourceDocumentServiceModel(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    int CurrentVersionNumber,
    DateTimeOffset RemovedAt,
    Guid RemovedByMembershipId,
    DateTimeOffset CreatedAt,
    string ConcurrencyToken);

/// <summary>One bin row as the repository projects it, in SQL.</summary>
public sealed record RemovedBrandSourceDocumentRecord(
    Guid Id,
    string Title,
    BrandSourceDocumentType DocumentType,
    BrandSourcePurpose Purpose,
    int CurrentVersionNumber,
    DateTimeOffset RemovedAt,
    Guid RemovedByMembershipId,
    DateTimeOffset CreatedAt,
    byte[] RowVersion) : IReferenceRow
{
    string IReferenceRow.SortValue => RemovedBrandSourceDocumentListQueryFactory.FormatTimestamp(RemovedAt);

    string IReferenceRow.TieBreaker => Id.ToString("D");
}

/// <summary>The last row of the previous bin page: where the next one resumes.</summary>
public sealed record RemovedBrandSourceDocumentListPosition(DateTimeOffset RemovedAt, Guid DocumentId);

/// <param name="Scope">The ordered set this page comes from. Cursors are bound to it.</param>
public sealed record RemovedBrandSourceDocumentListCriteria(
    string Scope,
    RemovedBrandSourceDocumentListPosition? Position = null,
    int? RequestedLimit = null)
{
    /// <summary>Clamped on every read, so no copy of this record can ask for an unbounded page.</summary>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// Turns a bound <see cref="RemovedBrandSourceDocumentListViewModel"/> into typed criteria.
/// </summary>
/// <remarks>
/// Much smaller than the library's factory because a bin has no filters to parse — only the cursor has to be
/// checked, and only against the one scope this list has.
/// </remarks>
public static class RemovedBrandSourceDocumentListQueryFactory
{
    /// <summary>Round-trip, so a cursor carries every digit the column holds and never resumes early.</summary>
    private const string TimestampFormat = "O";

    private const char ScopeSeparator = '\u001E';

    public static bool TryCreate(
        RemovedBrandSourceDocumentListViewModel model,
        Guid workspaceId,
        out RemovedBrandSourceDocumentListCriteria? criteria,
        out OperationError? error)
    {
        criteria = null;
        error = null;

        var scope = string.Join(ScopeSeparator, "brand-source-documents-removed", workspaceId.ToString("N"));

        RemovedBrandSourceDocumentListPosition? position = null;

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !TryPosition(cursor, out position)))
        {
            error = OperationError.Validation(
                BrandErrorCodes.SourceInvalidRequest,
                "This cursor was issued for a different workspace or list. Start again without one.",
                [("cursor", "Start the list again without a cursor.")]);

            return false;
        }

        criteria = new RemovedBrandSourceDocumentListCriteria(scope, position, model.Limit);

        return true;
    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryPosition(ReferenceCursor cursor, out RemovedBrandSourceDocumentListPosition? position)
    {
        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var documentId)
            || !DateTimeOffset.TryParseExact(
                cursor.SortValue, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var removedAt))
        {
            return false;
        }

        position = new RemovedBrandSourceDocumentListPosition(removedAt, documentId);

        return true;
    }
}
