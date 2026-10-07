using System.ComponentModel;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The body of <c>POST /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}/utilization</c> (DAM-009): a creator
/// recording that they used an asset.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The date is required, and that is what makes the derived day unambiguous.</strong>
/// <c>MediaAssetUtilization.UtilizedDay</c> follows from <see cref="UtilizedOn"/> alone — a calendar date has
/// exactly one day of the week, in every zone — so there is no instant to convert and therefore no conversion to get
/// wrong. The alternative was letting the server date the log from "now", which would need a zone the workspace does
/// not have: only <c>BrandProfile.TimeZoneId</c> exists and it is nullable, so "today" would silently become UTC's
/// today for any workspace that never set one.
/// </para>
/// <para>
/// <strong>What is deliberately absent.</strong> No workspace or actor — both come from the resolved context, and an
/// actor a client could set would be a creator attributing their work to somebody else. No <c>utilizedDay</c>,
/// because it is derived rather than stated: a client that could send a day could send one that disagrees with the
/// date. No <c>createdAt</c>, which is when the log was written rather than when the asset was used, and is the
/// clock's to say. No id, and no asset id — the route names the asset.
/// </para>
/// </remarks>
/// <param name="PlatformKey">Where it went out. Opaque workspace vocabulary, as everywhere else platforms appear.</param>
/// <param name="UtilizedOn">
/// The calendar day the asset was used, in the creator's own reckoning. Required.
/// </param>
/// <param name="CampaignName">What this use was part of, in the creator's own words. Optional.</param>
/// <param name="Notes">Anything else worth recording about this use. Optional.</param>
public sealed record LogMediaAssetUtilizationViewModel(
    [property: Description("Where the asset went out, as a platform key from the workspace's own vocabulary. Required.")]
    string? PlatformKey = null,
    [property: Description("The calendar date the asset was used, as yyyy-MM-dd. Required. The day of the week is derived from it, never sent.")]
    DateOnly? UtilizedOn = null,
    [property: Description("What this use was part of, in the creator's own words. Optional.")]
    string? CampaignName = null,
    [property: Description("Anything else worth recording about this use. Optional.")]
    string? Notes = null);

/// <summary>The fixed limits of a utilization log.</summary>
public static class MediaAssetUtilizationPolicy
{
    /// <summary>
    /// How far past the server's own date a <see cref="LogMediaAssetUtilizationViewModel.UtilizedOn"/> may sit
    /// before it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One day, and the reasoning is <c>TestRunPolicy.FutureTolerance</c>'s applied to a date rather than an instant.
    /// <strong>Not zero</strong>, because a creator in UTC+13 logging something this afternoon is already on
    /// tomorrow's date by the server's reckoning, and refusing them would be refusing a correct request — no
    /// inhabited offset exceeds +14 hours, so one day covers every one of them. <strong>Not generous</strong>,
    /// because the error this catches is a mistyped year, and a use dated 2099 would sit at the top of an asset's
    /// history permanently.
    /// </para>
    /// <para>
    /// <strong>There is deliberately no floor.</strong> A creator recording where they used a photograph last year is
    /// entering their own history, and a product that refused it would be telling them their records are wrong.
    /// </para>
    /// </remarks>
    public const int FutureDayTolerance = 1;
}
