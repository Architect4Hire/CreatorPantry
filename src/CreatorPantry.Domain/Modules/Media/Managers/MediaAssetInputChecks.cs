namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Shape checks on a DAM creation request, independent of where it came from.
/// </summary>
/// <remarks>
/// The same place for both entry points, so an upload and a kept generated image cannot drift on what
/// counts as a well-formed asset. Lengths match the columns; the database's check constraints remain the
/// guarantee and these are the message a creator can act on.
/// </remarks>
public static class MediaAssetInputChecks
{
    /// <summary>The most tags one asset may carry at creation.</summary>
    /// <remarks>
    /// A bound on the request rather than on the asset: nothing stops a creator adding more later, and an
    /// unbounded list is an unbounded number of foreign-key checks inside one transaction.
    /// </remarks>
    public const int MaxTags = 25;

    public static IEnumerable<(string Field, string Error)> Metadata(MediaAssetMetadataInput? metadata)
    {
        if (metadata is null)
        {
            yield return (nameof(MediaAssetMetadataInput), "Metadata is required.");

            yield break;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            // Required, because a library nobody can scan is not a library — and because the only other
            // candidate for a default is the creator's filename, which is untrusted display text.
            yield return (nameof(metadata.Title), "A title is required.");
        }
        else if (metadata.Title.Length > MediaPolicy.AssetTitleMaxLength)
        {
            yield return (nameof(metadata.Title),
                $"A title may be at most {MediaPolicy.AssetTitleMaxLength} characters.");
        }

        foreach (var error in TooLong(
            metadata.Description, nameof(metadata.Description), MediaPolicy.AssetDescriptionMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(metadata.AltText, nameof(metadata.AltText), MediaPolicy.AltTextMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(
            metadata.ChannelKey, nameof(metadata.ChannelKey), MediaPolicy.VocabularyKeyMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(
            metadata.PlatformKey, nameof(metadata.PlatformKey), MediaPolicy.VocabularyKeyMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(
            metadata.StyleKey, nameof(metadata.StyleKey), MediaPolicy.VocabularyKeyMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(
            metadata.RightsHolder, nameof(metadata.RightsHolder), MediaPolicy.RightsTextMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(
            metadata.AttributionText, nameof(metadata.AttributionText), MediaPolicy.RightsTextMaxLength))
        {
            yield return error;
        }

        if (metadata.Day is { } day && !Enum.IsDefined(day))
        {
            yield return (nameof(metadata.Day), "That is not a day of the week.");
        }

        if (metadata.WorkspaceTagIds is { } tags)
        {
            if (tags.Count > MaxTags)
            {
                yield return (nameof(metadata.WorkspaceTagIds), $"An asset may carry at most {MaxTags} tags.");
            }

            if (tags.Any(tag => tag == Guid.Empty))
            {
                yield return (nameof(metadata.WorkspaceTagIds), "A tag id is required.");
            }

            if (tags.Distinct().Count() != tags.Count)
            {
                // The pair is the primary key, so a repeated tag would fail at the database as a duplicate
                // key rather than as something a creator could read.
                yield return (nameof(metadata.WorkspaceTagIds), "A tag may appear once.");
            }
        }
    }

    public static IEnumerable<(string Field, string Error)> RecipeLink(MediaAssetRecipeLinkInput? link)
    {
        if (link is null)
        {
            yield break;
        }

        if (link.RecipeId == Guid.Empty)
        {
            yield return (nameof(link.RecipeId), "A recipe id is required.");
        }

        foreach (var error in TooLong(link.Caption, nameof(link.Caption), MediaPolicy.AssetDescriptionMaxLength))
        {
            yield return error;
        }
    }

    /// <summary>
    /// Shape checks on a utilization log (DAM-009), collecting every failure rather than the first.
    /// </summary>
    /// <param name="today">
    /// The server's own date, for the future bound. Passed in rather than read from a clock here so this stays a pure
    /// function — a validation rule that reads a clock cannot be tested at a boundary without moving time.
    /// </param>
    public static IEnumerable<(string Field, string Error)> Utilization(
        LogMediaAssetUtilizationViewModel? model, DateOnly today)
    {
        if (model is null)
        {
            yield return (nameof(LogMediaAssetUtilizationViewModel), "A utilization log is required.");

            yield break;
        }

        if (string.IsNullOrWhiteSpace(model.PlatformKey))
        {
            yield return (nameof(model.PlatformKey), "A platform is required.");
        }
        else if (model.PlatformKey.Length > MediaPolicy.VocabularyKeyMaxLength)
        {
            yield return (nameof(model.PlatformKey),
                $"A platform key may be at most {MediaPolicy.VocabularyKeyMaxLength} characters.");
        }

        if (model.UtilizedOn is not { } utilizedOn)
        {
            yield return (nameof(model.UtilizedOn), "The date the asset was used is required.");
        }
        else if (utilizedOn > today.AddDays(MediaAssetUtilizationPolicy.FutureDayTolerance))
        {
            // Not "after today", so a creator east of UTC logging this afternoon is not refused. See
            // MediaAssetUtilizationPolicy.FutureDayTolerance for why one day and no floor.
            yield return (nameof(model.UtilizedOn), "That date is in the future.");
        }

        foreach (var error in TooLong(
            model.CampaignName, nameof(model.CampaignName), MediaPolicy.CampaignNameMaxLength))
        {
            yield return error;
        }

        foreach (var error in TooLong(model.Notes, nameof(model.Notes), MediaPolicy.NotesMaxLength))
        {
            yield return error;
        }
    }

    private static IEnumerable<(string Field, string Error)> TooLong(string? value, string field, int max)
    {
        if (value is not null && value.Length > max)
        {
            yield return (field, $"{field} may be at most {max} characters.");
        }
    }
}
