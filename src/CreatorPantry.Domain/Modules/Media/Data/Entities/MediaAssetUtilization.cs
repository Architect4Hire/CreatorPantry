using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One recorded use of an asset: where it went out, when, and under what campaign (DAM-009).
/// </summary>
/// <remarks>
/// <para>
/// Append-only but not <see cref="IImmutableRecord"/>: a creator logging yesterday's post may well have
/// the date or the campaign wrong, and a log they cannot correct is a log they stop keeping. What it is
/// not is a status — an asset's utilization is a history, so the same asset may appear many times.
/// </para>
/// <para>
/// <see cref="UtilizedDay"/> is stored rather than computed from <see cref="UtilizedOn"/> at read time.
/// The day of a date depends on the zone it is read in, and a weekly-theme report that answered
/// differently in two timezones would be reporting the reader rather than the creator. DAM-009 says the
/// derivation uses an explicit zone; this column is where that decision is kept.
/// </para>
/// </remarks>
public class MediaAssetUtilization : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The asset that was used, workspace-paired so a neighbour's is unrepresentable.</summary>
    public Guid MediaAssetId { get; set; }

    /// <summary>Where it went out. Opaque, as everywhere else platforms appear.</summary>
    public string PlatformKey { get; set; } = string.Empty;

    /// <summary>The calendar date it was used, in the workspace's own zone. A date, not an instant.</summary>
    public DateOnly UtilizedOn { get; set; }

    /// <inheritdoc cref="MediaAssetUtilization"/>
    public DayOfWeek UtilizedDay { get; set; }

    public string? CampaignName { get; set; }

    public string? Notes { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership.</summary>
    public Guid LoggedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
