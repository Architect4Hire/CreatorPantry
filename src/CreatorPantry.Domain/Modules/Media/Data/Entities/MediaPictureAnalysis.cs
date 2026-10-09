using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// A stored reading of one picture's own pixels: what a model said it could see in a library asset version or
/// a generated image (AF.3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Private creator content.</strong> Workspace-owned, carrying the global query filter like every
/// other creator record. The observations describe a creator's picture and are never logged.
/// </para>
/// <para>
/// <strong>One row per picture, and the newest reading wins.</strong> A library asset's picture is one
/// version of it (<see cref="MediaAssetId"/> with <see cref="MediaAssetVersionNumber"/>); a generated image's
/// is the image itself (<see cref="GeneratedImageId"/>). Exactly one of the two identities is set, which the
/// table's own check holds. Reading a picture again replaces the row rather than adding a second, so there is
/// never a question of which reading a caller got.
/// </para>
/// <para>
/// <strong>Observations only.</strong> The reading's suggested prompt is not here: that is a draft for the
/// creator to edit, and stays with the proposal it arrived in. What is kept is what was seen, so that work
/// grounded on this picture later need not pay for a second look (AF.1.5).
/// </para>
/// <para>
/// <strong>Model output, kept as such.</strong> No creator wrote or accepted these words. They are stored
/// apart from the asset's alt text and are never copied into it.
/// </para>
/// </remarks>
public class MediaPictureAnalysis : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The library asset this reads, or null for a generated image.</summary>
    public Guid? MediaAssetId { get; set; }

    /// <summary>Which version of that asset. Set exactly when <see cref="MediaAssetId"/> is.</summary>
    public int? MediaAssetVersionNumber { get; set; }

    /// <summary>The generated image this reads, or null for a library asset.</summary>
    public Guid? GeneratedImageId { get; set; }

    /// <summary>The checksum of the bytes that were read, so a reader can tell it is about the same picture.</summary>
    public string ContentChecksum { get; set; } = string.Empty;

    /// <summary>The observations, as a JSON array of aspect, text and confidence.</summary>
    public string ObservationsJson { get; set; } = string.Empty;

    /// <summary>The operation that produced this reading. Provenance, and deliberately not a foreign key.</summary>
    public Guid AiOperationId { get; set; }

    public string PromptTemplateId { get; set; } = string.Empty;

    public string PromptTemplateVersion { get; set; } = string.Empty;

    public DateTimeOffset AnalyzedAt { get; set; }
}
