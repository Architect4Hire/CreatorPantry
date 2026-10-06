using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Where a page of one asset's utilization history resumes: the last row's date and its id.
/// </summary>
/// <remarks>
/// <para>
/// The date is a <see cref="DateOnly"/>, not an instant, because that is what the column holds — a use is logged
/// against a calendar day in the workspace's zone (DAM-009). Formatted round-trip so the cursor carries exactly
/// what the column holds and nothing is lost in the encoding.
/// </para>
/// <para>
/// The id breaks every tie, and here it has to: an asset used three times on one day gives three rows sharing a
/// sort value, and a page boundary falling inside that day would otherwise repeat one and skip another.
/// </para>
/// </remarks>
public sealed record MediaAssetUtilizationPosition
{
    /// <summary>ISO 8601 calendar date. Fixed width, so it also sorts as a string if anything ever needs to.</summary>
    private const string DateFormat = "yyyy-MM-dd";

    private MediaAssetUtilizationPosition(Guid utilizationId, DateOnly utilizedOn)
    {
        UtilizationId = utilizationId;
        UtilizedOn = utilizedOn;
    }

    public Guid UtilizationId { get; }

    public DateOnly UtilizedOn { get; }

    /// <summary>
    /// Converts a decoded cursor into a position, or false when it does not describe one.
    /// </summary>
    /// <remarks>
    /// False rather than throwing: both halves arrived from a query string, so a tie-breaker that is not a GUID
    /// or a sort value that is not a date is a caller's mistake and belongs in a 400. It does <em>not</em> check
    /// that the cursor was issued for this asset and this workspace — that is the scope fingerprint's job, which
    /// <see cref="ReferenceCursor.TryResolve"/> must already have done.
    /// </remarks>
    public static bool TryCreate(ReferenceCursor cursor, out MediaAssetUtilizationPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var utilizationId))
        {
            return false;
        }

        if (!DateOnly.TryParseExact(
            cursor.SortValue, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utilizedOn))
        {
            return false;
        }

        position = new MediaAssetUtilizationPosition(utilizationId, utilizedOn);

        return true;
    }

    /// <summary>The value a row publishes as its cursor's sort half.</summary>
    public static string SortValueFor(DateOnly utilizedOn) =>
        utilizedOn.ToString(DateFormat, CultureInfo.InvariantCulture);
}

/// <summary>
/// Identifies one asset's utilization history, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// The asset and the workspace both go in, which is what makes a cursor from one asset's history useless against
/// another's — and a cursor from another workspace useless outright. There are no filters to fold in: this
/// history is read whole, newest first.
/// </remarks>
public static class MediaAssetUtilizationScope
{
    public const string Resource = "dam-asset-utilization";

    public static string Build(Guid workspaceId, Guid mediaAssetId) =>
        $"resource={Resource}|workspace={workspaceId:D}|asset={mediaAssetId:D}";
}

/// <summary>One page of one asset's utilization history, parsed and clamped.</summary>
public sealed record MediaAssetUtilizationCriteria(
    Guid MediaAssetId,
    string Scope,
    MediaAssetUtilizationPosition? Position = null,
    int? RequestedLimit = null,
    bool IncludeTotal = true)
{
    /// <inheritdoc cref="MediaAssetSearchCriteria.Limit"/>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>One row of a utilization history, as the repository projects it.</summary>
public sealed record MediaAssetUtilizationRecord(
    Guid Id,
    string PlatformKey,
    DateOnly UtilizedOn,
    DayOfWeek UtilizedDay,
    string? CampaignName,
    string? Notes,
    DateTimeOffset CreatedAt) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => MediaAssetUtilizationPosition.SortValueFor(UtilizedOn);

    /// <inheritdoc />
    public string TieBreaker => Id.ToString("D");
}

/// <param name="TotalCount">
/// How many times the asset has been used in total, or null when the caller did not ask. Named to match the
/// library page and the recipe and prompt pages, not the <c>Total</c> the layers below call it.
/// </param>
public sealed record MediaAssetUtilizationPageServiceModel(
    IReadOnlyList<MediaAssetUtilizationServiceModel> Items, string? NextCursor, int? TotalCount);
