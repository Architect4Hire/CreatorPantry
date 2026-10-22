using CreatorPantry.Domain.Managers.FileInspection;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Decides whether image bytes may be embedded: a PNG or JPEG by file signature (never by a name or a
/// declared type), within the size and dimension limits. The reading is <see cref="ImageFileInspector"/>'s,
/// end to end, so a picture that would be refused as an upload is not embedded either.
/// </summary>
internal static class RecipePdfImageInspector
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxDimension = 8000;

    public static bool IsEmbeddable(byte[]? bytes) =>
        bytes is { Length: > 0 and <= MaxBytes }
        && ImageFileInspector.Inspect(bytes, (long)MaxDimension * MaxDimension) is
        {
            Outcome: ImageFileInspectionOutcome.Accepted,
            MediaType: ImageFileInspector.PngMediaType or ImageFileInspector.JpegMediaType,
            Width: <= MaxDimension,
            Height: <= MaxDimension,
        };
}
