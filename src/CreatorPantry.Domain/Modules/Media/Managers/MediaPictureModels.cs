namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What another module may know about one of this workspace's pictures before it asks for the bytes (AF.3.4).
/// </summary>
/// <param name="VersionNumber">
/// The library version this names — the one asked for, or the asset's current one when none was — or null for
/// a generated image, whose bytes never change and so have no version to pin.
/// </param>
/// <param name="MediaType">The type the stored bytes were established to be when they were written.</param>
/// <param name="SizeBytes">How large the stored bytes are, so a caller can refuse one too big to carry.</param>
/// <param name="ContentChecksum">The checksum of the stored bytes: what "the same picture" means.</param>
/// <remarks>
/// Deliberately no object key, title, file name, provider or model. It crosses a module boundary so that a
/// request can be refused before it is queued; none of those is needed for that, and a key outside this
/// module is a key in one more place than the two allowed to hold one (media.md).
/// </remarks>
public sealed record MediaPictureTarget(int? VersionNumber, string MediaType, long SizeBytes, string ContentChecksum);

/// <summary>How opening a picture for another module ended.</summary>
public enum MediaPictureOpenOutcome
{
    Opened = 1,

    /// <summary>
    /// Nothing this workspace may read under that id: unknown, another workspace's, removed, declined, expired,
    /// or a version the asset does not have. One answer for all of them.
    /// </summary>
    NotFound = 2,

    /// <summary>The picture is there and storage could not be reached. Asking again is the remedy.</summary>
    StorageUnavailable = 3,
}

/// <summary>A picture's bytes, opened for another module to read once.</summary>
/// <remarks>
/// The stream belongs to the storage read that produced it, so this is disposed by whoever opened it. It
/// carries the checksum of exactly these bytes, so a reader that stores something about them can say which
/// bytes it meant.
/// </remarks>
public sealed class MediaPictureContent(
    IAsyncDisposable owner, Stream content, string mediaType, string contentChecksum, int? versionNumber)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string MediaType { get; } = mediaType;

    public string ContentChecksum { get; } = contentChecksum;

    /// <inheritdoc cref="MediaPictureTarget.VersionNumber"/>
    public int? VersionNumber { get; } = versionNumber;

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

/// <summary>One open: how it ended, and the picture when it was opened.</summary>
public sealed record MediaPictureOpen(MediaPictureOpenOutcome Outcome, MediaPictureContent? Picture = null);

/// <summary>One thing a model said it could see in a picture, and how sure it said it was.</summary>
/// <param name="Aspect">Which aspect of the picture this is about — composition, lighting, and so on.</param>
/// <param name="Text">What was observed. Model-written prose: untrusted wherever it is read back.</param>
/// <param name="Confidence">How sure the reading said it was, in the reading's own vocabulary.</param>
public sealed record MediaPictureObservation(string Aspect, string Text, string Confidence);

/// <summary>
/// What is being recorded about one picture: which picture, what was observed, and what produced it.
/// </summary>
/// <param name="MediaAssetId">The library asset, with <paramref name="MediaAssetVersionNumber"/>; or null.</param>
/// <param name="GeneratedImageId">The generated image; or null. Exactly one identity is given.</param>
/// <param name="ContentChecksum">
/// The checksum storage reported for the bytes that were read. It must equal the picture's own, or nothing is kept.
/// </param>
/// <param name="AiOperationId">The operation that produced the reading, for provenance. Not a foreign key.</param>
public sealed record MediaPictureAnalysisRecord(
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? GeneratedImageId,
    string ContentChecksum,
    IReadOnlyList<MediaPictureObservation> Observations,
    Guid AiOperationId,
    string PromptTemplateId,
    string PromptTemplateVersion);

/// <summary>
/// A stored reading of one picture's own pixels, as another module reads it back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An analysis, never alt text.</strong> A model wrote these words about what it could see; no creator
/// wrote or accepted them. They are kept apart from <see cref="MediaAssetDescription.AltText"/> so that
/// nothing can present one as the other, and a caller grounding a generation on them owes them the same
/// fencing as any other untrusted text (ai.md).
/// </para>
/// <para>
/// <strong>It describes the bytes named by <see cref="ContentChecksum"/></strong>, which for a library asset
/// is one version. A reading of version 2 says nothing about version 3.
/// </para>
/// </remarks>
public sealed record MediaPictureAnalysisDescription(
    IReadOnlyList<MediaPictureObservation> Observations,
    string ContentChecksum,
    string PromptTemplateId,
    string PromptTemplateVersion,
    DateTimeOffset AnalyzedAt);

/// <summary>Limits on a stored picture analysis.</summary>
public static class MediaPictureAnalysisPolicy
{
    /// <summary>
    /// How many observations one reading may hold. Above the number of aspects a reading has, and bounded so a
    /// stored analysis is a bounded contribution to whatever is later grounded on it.
    /// </summary>
    public const int MaxObservations = 16;

    /// <summary>The longest one observation's text may be.</summary>
    public const int ObservationTextMaxLength = 1000;

    /// <summary>The longest an aspect or a confidence label may be.</summary>
    public const int LabelMaxLength = 64;

    public const int ChecksumMaxLength = 128;

    public const int PromptTemplateIdMaxLength = 128;

    public const int PromptTemplateVersionMaxLength = 32;
}
