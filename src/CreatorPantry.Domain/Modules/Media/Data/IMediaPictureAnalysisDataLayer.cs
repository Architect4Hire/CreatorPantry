using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>A stored reading as the data layer hands it up: the row's facts, with the JSON still as text.</summary>
public sealed record MediaPictureAnalysisStored(
    string ObservationsJson,
    string ContentChecksum,
    string PromptTemplateId,
    string PromptTemplateVersion,
    DateTimeOffset AnalyzedAt);

/// <summary>Complete persistence operations for stored picture analyses (AF.3.4).</summary>
public interface IMediaPictureAnalysisDataLayer
{
    /// <summary>
    /// Keeps a reading of one picture, replacing any earlier reading of the same picture.
    /// </summary>
    /// <returns>False when it could not be kept — which costs the caller nothing but a later second look.</returns>
    Task<bool> KeepAsync(
        MediaPictureAnalysisRecord record, string observationsJson, CancellationToken cancellationToken);

    Task<MediaPictureAnalysisStored?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken);

    Task<MediaPictureAnalysisStored?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMediaPictureAnalysisDataLayer"/>
internal sealed class MediaPictureAnalysisDataLayer(
    IMediaPictureAnalysisRepository analyses,
    IClock clock,
    ILogger<MediaPictureAnalysisDataLayer> logger) : IMediaPictureAnalysisDataLayer
{
    public async Task<bool> KeepAsync(
        MediaPictureAnalysisRecord record, string observationsJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var existing = record.MediaAssetId is { } assetId
            ? await analyses.FindForAssetAsync(assetId, record.MediaAssetVersionNumber!.Value, cancellationToken)
            : await analyses.FindForGeneratedImageAsync(record.GeneratedImageId!.Value, cancellationToken);

        var analysis = existing;

        if (analysis is null)
        {
            analysis = new MediaPictureAnalysis
            {
                Id = Guid.NewGuid(),
                MediaAssetId = record.MediaAssetId,
                MediaAssetVersionNumber = record.MediaAssetVersionNumber,
                GeneratedImageId = record.GeneratedImageId,
            };
            analyses.Add(analysis);
        }

        analysis.ContentChecksum = record.ContentChecksum;
        analysis.ObservationsJson = observationsJson;
        analysis.AiOperationId = record.AiOperationId;
        analysis.PromptTemplateId = record.PromptTemplateId;
        analysis.PromptTemplateVersion = record.PromptTemplateVersion;
        analysis.AnalyzedAt = clock.UtcNow;

        try
        {
            await analyses.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception)
        {
            // Two readings of one picture finishing together, or a picture removed underneath this. Either
            // way nothing is lost that cannot be had again by reading the picture once more, so this is
            // reported and not thrown. Logged by operation, never by anything about the picture.
            logger.LogWarning(
                exception,
                "A picture analysis could not be kept. aiOperationId={AiOperationId}",
                record.AiOperationId);

            // The worker's unit of work carries on after this — it has a proposal to save — and a row left
            // tracked here would be tried again with it, failing a reading that did nothing wrong.
            analyses.Forget(analysis);

            return false;
        }
    }

    public async Task<MediaPictureAnalysisStored?> FindForAssetAsync(
        Guid mediaAssetId, int versionNumber, CancellationToken cancellationToken) =>
        Stored(await analyses.FindForAssetAsync(mediaAssetId, versionNumber, cancellationToken));

    public async Task<MediaPictureAnalysisStored?> FindForGeneratedImageAsync(
        Guid generatedImageId, CancellationToken cancellationToken) =>
        Stored(await analyses.FindForGeneratedImageAsync(generatedImageId, cancellationToken));

    private static MediaPictureAnalysisStored? Stored(MediaPictureAnalysis? analysis) =>
        analysis is null
            ? null
            : new MediaPictureAnalysisStored(
                analysis.ObservationsJson,
                analysis.ContentChecksum,
                analysis.PromptTemplateId,
                analysis.PromptTemplateVersion,
                analysis.AnalyzedAt);
}
