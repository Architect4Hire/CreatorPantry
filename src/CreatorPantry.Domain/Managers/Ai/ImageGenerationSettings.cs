using Microsoft.Extensions.AI;

namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// The provider-neutral names an image request and its answer use for quality and size (AP.2.3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ImageGenerationOptions"/> has a pixel size and nothing else: no quality, no resolution tier, no
/// aspect ratio. These keys carry those through its <see cref="ImageGenerationOptions.AdditionalProperties"/>
/// without the Media module naming a provider — a generator reads the ones its model understands and refuses a
/// value its model does not offer. Nothing sets them yet, so each model's configured default applies; quality
/// classes (AP.3.1) are what will.
/// </para>
/// <para>
/// The answer goes the other way: a generator stamps what it actually sent onto the image it returns
/// (<see cref="AIContent.AdditionalProperties"/>), under <see cref="QualitySent"/> and <see cref="SizeSent"/>,
/// so provenance records the request the picture came from rather than what was hoped for.
/// </para>
/// </remarks>
public static class ImageGenerationSettings
{
    /// <summary>Requested quality, as the model names it: <c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public const string Quality = "cp.image.quality";

    /// <summary>Requested resolution tier, as the model names it: <c>1K</c>, <c>2K</c> or <c>4K</c>.</summary>
    public const string Resolution = "cp.image.resolution";

    /// <summary>Requested aspect ratio, as <c>width:height</c> (<c>1:1</c>, <c>3:2</c>).</summary>
    public const string AspectRatio = "cp.image.aspect_ratio";

    /// <summary>The quality that was sent, on the returned image. Absent when the model takes none.</summary>
    public const string QualitySent = "cp.image.quality_sent";

    /// <summary>
    /// The size that was sent, on the returned image, in the model's own terms: <c>1K 1:1</c> for a resolution
    /// tier, <c>1:1</c> for an aspect ratio, <c>1024x1024</c> for pixels.
    /// </summary>
    public const string SizeSent = "cp.image.size_sent";

    /// <summary>The longest <see cref="QualitySent"/> a picture's provenance can hold.</summary>
    public const int QualitySentMaxLength = 16;

    /// <summary>The longest <see cref="SizeSent"/> a picture's provenance can hold.</summary>
    public const int SizeSentMaxLength = 32;

    /// <summary>A value under <paramref name="key"/> as text, or null when it is absent or blank.</summary>
    public static string? Read(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value)
            && value?.ToString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
