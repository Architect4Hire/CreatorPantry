using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.AI;

namespace CreatorPantry.AiProvider;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>How a Venice image model is told what size to make (AP.2.3).</summary>
/// <remarks>Each kind rejects the others' fields with a 400, so a request carries one kind's fields only.</remarks>
public enum VeniceSizing
{
    /// <summary>A resolution tier and an aspect ratio, and a quality when the model offers one.</summary>
    Resolution = 1,

    /// <summary>An aspect ratio alone.</summary>
    AspectRatio = 2,

    /// <summary>A width and a height in pixels.</summary>
    Pixels = 3,
}

/// <summary>
/// What one Venice image model accepts, from configuration (<c>Venice:Models:&lt;id&gt;</c>), so a field the model
/// rejects is never sent and a value it does not offer is refused here rather than paid for there.
/// </summary>
/// <remarks>
/// The figures are the model's own, from Venice's <c>GET /models?type=image</c> (<c>model_spec.constraints</c>).
/// The defaults are this deployment's choice where Venice's would cost more: <c>gpt-image-2-5-flare</c> defaults
/// to <c>high</c>, and the profile to <c>medium</c> (PRICE-003).
/// </remarks>
public sealed class VeniceModelProfile
{
    /// <summary>The configuration section the profiles are read from, one per model id.</summary>
    public const string SectionName = "Venice:Models";

    /// <summary>The largest side Venice's request schema allows a pixel-sized model.</summary>
    public const int PixelSchemaMax = 1280;

    public VeniceSizing? Sizing { get; set; }

    public List<string> Resolutions { get; } = [];

    public string? DefaultResolution { get; set; }

    public List<string> Qualities { get; } = [];

    public string? DefaultQuality { get; set; }

    public List<string> AspectRatios { get; } = [];

    public string? DefaultAspectRatio { get; set; }

    public int? WidthHeightDivisor { get; set; }

    public int? MaxDimension { get; set; }

    public int? DefaultWidth { get; set; }

    public int? DefaultHeight { get; set; }

    /// <summary>The longest prompt the model takes. A longer one is refused before it is sent, never cut.</summary>
    public int PromptCharacterLimit { get; set; }

    /// <summary>What is wrong with this profile, in sentences naming the field; empty when nothing is.</summary>
    public IEnumerable<string> Problems()
    {
        if (PromptCharacterLimit <= 0)
        {
            yield return "PromptCharacterLimit must be above nought";
        }

        // What is sent is recorded with the picture, and a value too long for that record would be dropped
        // from provenance without a word. Refused here instead, where it can be named.
        if (Qualities.Any(quality => quality.Length > ImageGenerationSettings.QualitySentMaxLength)
            || Resolutions.Any(resolution => AspectRatios.Any(aspect =>
                resolution.Length + 1 + aspect.Length > ImageGenerationSettings.SizeSentMaxLength))
            || AspectRatios.Any(aspect => aspect.Length > ImageGenerationSettings.SizeSentMaxLength))
        {
            yield return "a quality or size is longer than a picture's provenance can record";
        }

        switch (Sizing)
        {
            case VeniceSizing.Resolution:
                if (!Offers(Resolutions, DefaultResolution))
                {
                    yield return "DefaultResolution must be one of Resolutions";
                }

                if (!Offers(AspectRatios, DefaultAspectRatio))
                {
                    yield return "DefaultAspectRatio must be one of AspectRatios";
                }

                if (Qualities.Count > 0 ? !Offers(Qualities, DefaultQuality) : DefaultQuality is not null)
                {
                    yield return "DefaultQuality must be one of Qualities, and set only when Qualities is";
                }

                break;

            case VeniceSizing.AspectRatio:
                if (!Offers(AspectRatios, DefaultAspectRatio))
                {
                    yield return "DefaultAspectRatio must be one of AspectRatios";
                }

                if (Resolutions.Count > 0 || Qualities.Count > 0)
                {
                    yield return "an AspectRatio model takes no Resolutions or Qualities";
                }

                break;

            case VeniceSizing.Pixels:
                var max = MaxDimension ?? 0;

                if (WidthHeightDivisor is not > 0 || max is <= 0 or > PixelSchemaMax)
                {
                    yield return $"WidthHeightDivisor must be above nought and MaxDimension 1-{PixelSchemaMax}";
                }
                else if (!FitsPixels(DefaultWidth, max) || !FitsPixels(DefaultHeight, max))
                {
                    yield return "DefaultWidth and DefaultHeight must be multiples of WidthHeightDivisor, at most MaxDimension";
                }

                if (Resolutions.Count > 0 || Qualities.Count > 0 || AspectRatios.Count > 0)
                {
                    yield return "a Pixels model takes no Resolutions, Qualities or AspectRatios";
                }

                break;

            default:
                yield return "Sizing must be Resolution, AspectRatio or Pixels";
                break;
        }
    }

    /// <summary>
    /// The fields to send for <paramref name="options"/>, or the reason they cannot be: a value the model does
    /// not offer, or a dialect of size it does not speak.
    /// </summary>
    public VeniceSizingChoice Settle(ImageGenerationOptions? options)
    {
        var properties = options?.AdditionalProperties;
        var quality = ImageGenerationSettings.Read(properties, ImageGenerationSettings.Quality);
        var resolution = ImageGenerationSettings.Read(properties, ImageGenerationSettings.Resolution);
        var aspect = ImageGenerationSettings.Read(properties, ImageGenerationSettings.AspectRatio);
        var pixels = options?.ImageSize;

        switch (Sizing)
        {
            case VeniceSizing.Resolution when pixels is null:
                if (quality is not null && !Offers(Qualities, quality))
                {
                    return VeniceSizingChoice.Refused("the model does not offer that quality");
                }

                if (resolution is not null && !Offers(Resolutions, resolution))
                {
                    return VeniceSizingChoice.Refused("the model does not offer that resolution");
                }

                if (aspect is not null && !Offers(AspectRatios, aspect))
                {
                    return VeniceSizingChoice.Refused("the model does not offer that aspect ratio");
                }

                return new VeniceSizingChoice(
                    Quality: Qualities.Count > 0 ? Named(Qualities, quality ?? DefaultQuality) : null,
                    Resolution: Named(Resolutions, resolution ?? DefaultResolution),
                    AspectRatio: Named(AspectRatios, aspect ?? DefaultAspectRatio));

            case VeniceSizing.AspectRatio when pixels is null && quality is null && resolution is null:
                return aspect is not null && !Offers(AspectRatios, aspect)
                    ? VeniceSizingChoice.Refused("the model does not offer that aspect ratio")
                    : new VeniceSizingChoice(AspectRatio: Named(AspectRatios, aspect ?? DefaultAspectRatio));

            case VeniceSizing.Pixels when quality is null && resolution is null && aspect is null:
                var width = pixels?.Width ?? DefaultWidth!.Value;
                var height = pixels?.Height ?? DefaultHeight!.Value;

                return FitsPixels(width, MaxDimension!.Value) && FitsPixels(height, MaxDimension.Value)
                    ? new VeniceSizingChoice(Width: width, Height: height)
                    : VeniceSizingChoice.Refused("the model cannot make a picture that size");

            default:
                return VeniceSizingChoice.Refused("the model is not sized that way");
        }
    }

    private bool FitsPixels(int? side, int max) =>
        side is > 0 && side <= max && side % WidthHeightDivisor!.Value == 0;

    private static bool Offers(List<string> offered, string? value) =>
        value is not null && offered.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The value as the model spells it, so a request never sends <c>1k</c> for <c>1K</c>.</summary>
    private static string Named(List<string> offered, string? value) =>
        offered.First(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The size and quality fields one request carries, or why there are none. Each is null when the model's kind
/// does not take it, so it is left out of the request rather than sent empty.
/// </summary>
public sealed record VeniceSizingChoice(
    string? Quality = null,
    string? Resolution = null,
    string? AspectRatio = null,
    int? Width = null,
    int? Height = null)
{
    /// <summary>Why nothing can be sent; null when the choice stands.</summary>
    public string? Refusal { get; private init; }

    public static VeniceSizingChoice Refused(string reason) => new() { Refusal = reason };

    /// <summary>The size in the model's own terms, for provenance: <c>1K 1:1</c>, <c>1:1</c> or <c>1024x1024</c>.</summary>
    public string? Size =>
        Width is { } width && Height is { } height ? $"{width}x{height}"
        : Resolution is not null ? $"{Resolution} {AspectRatio}"
        : AspectRatio;
}
