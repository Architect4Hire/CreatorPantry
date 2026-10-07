using CreatorPantry.Domain.Modules.Media.Data.Entities;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Applies a metadata patch (DAM-004) over what an asset currently holds, producing the same shape the create
/// path validates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Merging into <see cref="MediaAssetMetadataInput"/> rather than checking fields one at a time is the
/// point.</strong> The merged result goes through <c>MediaAssetInputChecks.Metadata</c> — the very checks a
/// creation runs — so a patch cannot produce a state a create would have refused, and no rule has to be written
/// twice or kept in step. One consequence falls out for free: clearing the title fails with "A title is
/// required", because that is already what the shared check says about a missing one.
/// </para>
/// <para>
/// Every line is <c>patch.X.Or(current.X)</c>, so "a field nobody mentioned is left alone" is the shape of the
/// code rather than a rule someone has to remember to apply at each field.
/// </para>
/// </remarks>
public static class MediaAssetMetadataMerge
{
    /// <summary>What the asset's metadata would become if this patch were applied.</summary>
    /// <param name="asset">
    /// The asset as it stands, with its <see cref="MediaAsset.Tags"/> loaded. Read, never modified.
    /// </param>
    public static MediaAssetMetadataInput Apply(MediaAsset asset, MediaAssetMetadataPatchViewModel patch)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(patch);

        return new MediaAssetMetadataInput
        {
            Title = patch.Title.Or(asset.Title),
            Description = patch.Description.Or(asset.Description),
            AltText = patch.AltText.Or(asset.AltText),
            ChannelKey = patch.ChannelKey.Or(asset.ChannelKey),
            PlatformKey = patch.PlatformKey.Or(asset.PlatformKey),
            Day = patch.Day.Or(asset.Day),
            StyleKey = patch.StyleKey.Or(asset.StyleKey),
            CuisineId = patch.CuisineId.Or(asset.CuisineId),
            CourseId = patch.CourseId.Or(asset.CourseId),
            RightsHolder = patch.RightsHolder.Or(asset.RightsHolder),
            AttributionText = patch.AttributionText.Or(asset.AttributionText),

            // Tags replace rather than merge, and an explicit null is the same request as an empty list: both say
            // "this asset's complete set of tags is none".
            WorkspaceTagIds = patch.Tags.IsSubmitted
                ? patch.Tags.Value ?? []
                : [.. asset.Tags.Select(tag => tag.WorkspaceTagId)],
        };
    }

    /// <summary>
    /// Whether applying <paramref name="merged"/> would actually change anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A patch that asks for what is already there writes nothing — no row, no <c>UpdatedAt</c>, no new
    /// concurrency token — so the caller's token stays valid and a retry of the same edit is still accepted. The
    /// same behaviour the recipe patch has, and worth stating as a consequence rather than only as an
    /// optimisation: <strong>there is no way to "touch" an asset</strong>, because a write with nothing to write
    /// is not an event.
    /// </para>
    /// <para>
    /// Tags are compared as sets. Reordering the same ids is not a change, and submitting a list that happens to
    /// match is not either — which is what a client re-sending the whole tag panel does on every save.
    /// </para>
    /// </remarks>
    public static bool Changes(MediaAsset asset, MediaAssetMetadataInput merged)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(merged);

        var current = asset.Tags.Select(tag => tag.WorkspaceTagId).ToHashSet();
        var requested = (merged.WorkspaceTagIds ?? []).ToHashSet();

        return merged.Title != asset.Title
            || merged.Description != asset.Description
            || merged.AltText != asset.AltText
            || merged.ChannelKey != asset.ChannelKey
            || merged.PlatformKey != asset.PlatformKey
            || merged.Day != asset.Day
            || merged.StyleKey != asset.StyleKey
            || merged.CuisineId != asset.CuisineId
            || merged.CourseId != asset.CourseId
            || merged.RightsHolder != asset.RightsHolder
            || merged.AttributionText != asset.AttributionText
            || !current.SetEquals(requested);
    }
}
