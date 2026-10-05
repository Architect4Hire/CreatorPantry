using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a creator is narrowing their prompt library down to. Both members are optional, and an empty instance
/// means "every prompt in this workspace".
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace member, and there must not be.</strong> The workspace is resolved from the
/// route and the caller's membership and reaches the query through the global EF Core query filter; a
/// <c>WorkspaceId</c> here would be a field something could set from a query string, an AI tool argument or a
/// job payload, which is the one thing tenancy.md forbids outright.
/// </para>
/// <para>
/// Two filters, because 12.3b's scope names two. Kind, source and recipe filters are all cheap — the recipe one
/// is even already indexed, by the recipe foreign key's leading columns — and all three are additive later.
/// </para>
/// </remarks>
/// <param name="Search">
/// A term from <see cref="PromptSearchPolicy.NormalizeSearch"/>, matched as a substring of the prompt text or
/// the creator's label.
/// <para>
/// <strong>Never matched against <c>GeneratedText</c></strong>, and that is the point rather than an omission:
/// the draft column exists to record what the creator changed, so searching it would surface a prompt by the
/// very words they deleted.
/// </para>
/// </param>
/// <param name="ChannelKey">
/// One <c>ContentChannel.Key</c>, matched exactly. One rather than a list because
/// <c>IX_PromptRecords_Workspace_Channel_Created</c> is a seek on a single value; a comma-separated list is a
/// compatible widening later, since a channel key can never contain a comma.
/// </param>
public sealed record PromptSearchFilters(string? Search = null, string? ChannelKey = null);

/// <summary>
/// The position a page resumes from: the last row's creation instant and its id.
/// </summary>
/// <remarks>
/// <para>
/// The typed counterpart of <see cref="ReferenceCursor"/>, which carries the same position as two strings
/// because it has to survive a query string. Converting once, here, is what keeps the repository's predicate
/// typed and therefore indexable, and it puts the one place a malformed cursor is detected above the repository
/// — where it is an ordinary bad request rather than an exception from a query.
/// </para>
/// <para>
/// The tie-breaker is the row's id, as <see cref="Recipes.Managers.RecipeSearchPosition"/> uses, and for the
/// same reason: a prompt has no unique natural string key. Two prompts saved in the same tick would otherwise
/// have no defined order between them, and a page boundary falling between them would repeat one and skip the
/// other on every read. The GUID ordering mismatch between C# and SQL Server is harmless because both the
/// <c>WHERE</c> and the <c>ORDER BY</c> are evaluated by the same database; what it does mean is that a cursor
/// is not portable between SQL Server and SQLite.
/// </para>
/// </remarks>
public sealed record PromptSearchPosition
{
    /// <summary>
    /// Round-trip ("O"), so the encoded value carries every digit of precision the column holds. A format that
    /// truncated would produce a cursor resuming slightly before where it claims, quietly repeating rows.
    /// </summary>
    private const string TimestampFormat = "O";

    private PromptSearchPosition(Guid promptRecordId, DateTimeOffset createdAt)
    {
        PromptRecordId = promptRecordId;
        CreatedAt = createdAt;
    }

    public Guid PromptRecordId { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Converts a decoded cursor into a position, or returns <c>false</c> when it does not describe one.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> rather than throwing, because both halves arrived from a query string. Note what is
    /// <em>not</em> checked here — whether the cursor was issued for this workspace and these filters. That is
    /// the scope fingerprint's job, and the caller must already have resolved it through
    /// <see cref="ReferenceCursor.TryResolve"/>.
    /// </remarks>
    public static bool TryCreate(ReferenceCursor cursor, out PromptSearchPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var promptRecordId)
            || !TryParseTimestamp(cursor.SortValue, out var createdAt))
        {
            return false;
        }

        position = new PromptSearchPosition(promptRecordId, createdAt);

        return true;
    }

    /// <summary>
    /// How an instant is written into a cursor. Paired with <see cref="TryParseTimestamp"/> and used by
    /// <see cref="PromptSummaryRecord.SortValue"/>, so the value a page mints and the value the next page parses
    /// cannot drift into two formats.
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseTimestamp(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
}

/// <summary>
/// One prompt-library query, complete: what to filter by, where to resume, and how many rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no sort member.</strong> Newest-first is the only ordering, because both indexes 12.3 built
/// are <c>(…, CreatedAt DESC, Id DESC)</c> and a second ordering would be either unindexed or a migration this
/// prompt does not own. <see cref="PromptSearchScope"/> still pins the ordering by name, so adding one later
/// does not invalidate cursors already in flight.
/// </para>
/// <para>
/// <strong>An unclamped page size is unrepresentable.</strong> <see cref="Limit"/> is derived, not stored, so
/// no constructor, initializer or <c>with</c> expression can produce a criteria asking for ten thousand rows.
/// <see cref="RequestedLimit"/> keeps what was asked for, because a validator's message wants the request
/// rather than the clamp.
/// </para>
/// </remarks>
/// <param name="Scope">
/// Identifies the ordered set these filters select, in the resolved workspace. Cursors minted for a page of it
/// are bound to this, so one cannot be replayed against another workspace or another filter. Built by
/// <see cref="PromptSearchScope"/>; the repository ignores it.
/// </param>
/// <param name="IncludeTotal">
/// Whether to count the whole filtered set alongside the page. Defaults to <c>true</c>, because a library
/// screen wants to say how many prompts it is showing of how many; a caller following a cursor already knows
/// and should turn it off rather than pay for a second statement per page.
/// </param>
public sealed record PromptSearchCriteria(
    PromptSearchFilters Filters,
    string Scope,
    PromptSearchPosition? Position = null,
    int? RequestedLimit = null,
    bool IncludeTotal = true)
{
    /// <inheritdoc cref="Recipes.Managers.RecipeSearchCriteria.Limit"/>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One row of a prompt search: enough to recognise a prompt in a list and decide which to open.
/// </summary>
/// <remarks>
/// <para>
/// Projected entirely in SQL, and deliberately <strong>not the prompt</strong>.
/// <see cref="TextPreview"/> is truncated by the database to
/// <see cref="ContentPolicy.PromptPreviewMaxLength"/> characters, so a hundred-row page does not carry a
/// hundred full prompts — that is what PRM-003's detail route is for, and a summary that duplicated it would
/// make every list page the size of the library.
/// </para>
/// <para>
/// <see cref="TextLength"/> comes with it so a client can render an ellipsis honestly rather than guessing
/// whether the preview is the whole prompt. A preview can split a surrogate pair, which renders as one
/// replacement character; accepted, because the alternative is grapheme logic in SQL.
/// </para>
/// <para>
/// <strong>No membership id, no workspace id, no template triple, no model draft.</strong> The first two never
/// leave the server (tenancy.md); the other two are detail, and a list that carried them would be publishing a
/// prompt's full provenance to answer "which one was it".
/// </para>
/// </remarks>
public sealed record PromptSummaryRecord(
    Guid Id,
    string ChannelKey,
    PromptImageKind ImageKind,
    string TextPreview,
    int TextLength,
    string? Label,
    PromptRecordSource Source,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    DateTimeOffset CreatedAt) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => PromptSearchPosition.FormatTimestamp(CreatedAt);

    /// <inheritdoc />
    /// <remarks>The id, formatted the way <see cref="PromptSearchPosition.TryCreate"/> parses it.</remarks>
    public string TieBreaker => Id.ToString("D");
}
