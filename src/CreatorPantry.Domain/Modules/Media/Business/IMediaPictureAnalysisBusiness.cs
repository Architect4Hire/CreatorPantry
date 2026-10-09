using System.Text.Json;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>The rules for keeping and reading back a stored picture analysis (AF.3.4).</summary>
public interface IMediaPictureAnalysisBusiness
{
    /// <summary>
    /// Keeps a reading of one of this workspace's pictures, replacing any earlier reading of it.
    /// </summary>
    /// <returns>
    /// False when nothing was kept: the record names no picture or two, the picture is not one this workspace
    /// may read, the reading is not of the bytes the picture now holds, or it is out of bounds. One answer for
    /// all of them — the caller is a background task with nobody to explain the difference to, and a reading
    /// that is not kept is only ever a second look later.
    /// </returns>
    Task<bool> KeepAsync(MediaPictureAnalysisRecord record, CancellationToken cancellationToken);

    /// <summary>The stored reading of one version of a library asset, or null.</summary>
    Task<MediaPictureAnalysisDescription?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>The stored reading of a generated image, or null.</summary>
    Task<MediaPictureAnalysisDescription?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaPictureAnalysisBusiness"/>
internal sealed class MediaPictureAnalysisBusiness(
    IMediaPictureAnalysisDataLayer analyses,
    IMediaAssetDataLayer assets,
    IGeneratedImageDataLayer images) : IMediaPictureAnalysisBusiness
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> KeepAsync(MediaPictureAnalysisRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!InBounds(record))
        {
            return false;
        }

        // The picture is resolved here, inside this workspace, and not taken on the caller's word: an id that
        // resolves nowhere, or in another workspace, keeps nothing. The composite foreign key would refuse it
        // too — this is what turns that into an answer rather than a storage exception.
        MediaPictureTarget? target;

        if (record.MediaAssetId is { } assetId && record.GeneratedImageId is null)
        {
            if (record.MediaAssetVersionNumber is not { } versionNumber)
            {
                return false;
            }

            target = await assets.ResolvePictureAsync(assetId, versionNumber, cancellationToken);
        }
        else if (record.GeneratedImageId is { } imageId
            && record.MediaAssetId is null
            && record.MediaAssetVersionNumber is null)
        {
            target = await images.ResolvePictureAsync(imageId, cancellationToken);
        }
        else
        {
            return false;
        }

        // A reading of other bytes than the picture holds describes a different picture. It is refused rather
        // than stored under this one's name, where it would be read back as a description of it.
        if (target is null
            || !string.Equals(target.ContentChecksum, record.ContentChecksum, StringComparison.Ordinal))
        {
            return false;
        }

        return await analyses.KeepAsync(
            record, JsonSerializer.Serialize(record.Observations, Json), cancellationToken);
    }

    public async Task<MediaPictureAnalysisDescription?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken)
    {
        if (mediaAssetId == Guid.Empty || versionNumber < 1)
        {
            return null;
        }

        // The picture first. A reading outlives nothing: an asset the creator removed is no longer theirs to
        // have read, so its reading is no longer anyone's to ground work on — the same rule the request and
        // the open apply, so the three cannot disagree about which pictures exist.
        var target = await assets.ResolvePictureAsync(mediaAssetId, versionNumber, cancellationToken);

        return target is null
            ? null
            : Describe(await analyses.FindForAssetAsync(mediaAssetId, versionNumber, cancellationToken), target);
    }

    public async Task<MediaPictureAnalysisDescription?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken)
    {
        if (generatedImageId == Guid.Empty)
        {
            return null;
        }

        // A declined or expired image, for the reason above.
        var target = await images.ResolvePictureAsync(generatedImageId, cancellationToken);

        return target is null
            ? null
            : Describe(await analyses.FindForGeneratedImageAsync(generatedImageId, cancellationToken), target);
    }

    private static bool InBounds(MediaPictureAnalysisRecord record) =>
        record.AiOperationId != Guid.Empty
        && Within(record.ContentChecksum, MediaPictureAnalysisPolicy.ChecksumMaxLength)
        && Within(record.PromptTemplateId, MediaPictureAnalysisPolicy.PromptTemplateIdMaxLength)
        && Within(record.PromptTemplateVersion, MediaPictureAnalysisPolicy.PromptTemplateVersionMaxLength)

        // A reading with nothing observed is not worth a row: reading it back would say "analysed" about a
        // picture nothing was said of.
        && record.Observations is { Count: > 0 and <= MediaPictureAnalysisPolicy.MaxObservations }
        && record.Observations.All(observation =>
            Within(observation.Aspect, MediaPictureAnalysisPolicy.LabelMaxLength)
            && Within(observation.Confidence, MediaPictureAnalysisPolicy.LabelMaxLength)
            && Within(observation.Text, MediaPictureAnalysisPolicy.ObservationTextMaxLength));

    private static bool Within(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static MediaPictureAnalysisDescription? Describe(
        MediaPictureAnalysisStored? stored, MediaPictureTarget target)
    {
        // A reading of other bytes than the picture now holds is a reading of some other picture.
        if (stored is null
            || !string.Equals(stored.ContentChecksum, target.ContentChecksum, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<MediaPictureObservation>? observations;

        try
        {
            observations = JsonSerializer.Deserialize<List<MediaPictureObservation>>(stored.ObservationsJson, Json);
        }
        catch (JsonException)
        {
            // A row this build cannot read is no description. Never a partial one: a caller would ground work
            // on whichever observations happened to parse.
            return null;
        }

        return observations is { Count: > 0 }
            ? new MediaPictureAnalysisDescription(
                observations,
                stored.ContentChecksum,
                stored.PromptTemplateId,
                stored.PromptTemplateVersion,
                stored.AnalyzedAt)
            : null;
    }
}
