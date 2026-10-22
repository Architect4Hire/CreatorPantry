using CreatorPantry.Domain.Managers.FileInspection;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// How many frames a GIF holds, read by walking its block structure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> IMG-004 accepts an animated GIF rather than refusing one, and the
/// analysis then describes its opening frame — so the creator has to be told that is what happened. Telling
/// them requires knowing whether there was more than one frame.
/// </para>
/// <para>
/// <strong>It counts frames and decodes nothing.</strong> The walk is <see cref="ImageFileInspector"/>'s, the
/// one every module shares (15.2): colour tables, extensions and their sub-blocks, image descriptors, through
/// the trailer, skipping every payload rather than inflating it.
/// </para>
/// <para>
/// <strong>Null means "could not tell", and that is a real answer.</strong> A truncated or malformed GIF here
/// is one stored before the walk was required at upload, so refusing it would be a second opinion on validity
/// this type is not asked for. The handler warns in the same words it uses for an animated file, because "this
/// may have had frames you did not see" is true either way.
/// </para>
/// </remarks>
public static class GifFrames
{
    /// <summary>
    /// The number of image frames, or null when the bytes are not a GIF this can walk to the end of.
    /// </summary>
    public static int? Count(ReadOnlySpan<byte> gif) =>
        ImageFileInspector.MediaTypeOf(gif) == ImageFileInspector.GifMediaType
        && ImageFileInspector.Inspect(gif, int.MaxValue) is { Outcome: ImageFileInspectionOutcome.Accepted } inspection
            ? inspection.FrameCount
            : null;

    /// <summary>Whether the GIF holds more than one frame. Null when that cannot be determined.</summary>
    public static bool? IsAnimated(ReadOnlySpan<byte> gif) => Count(gif) is { } frames ? frames > 1 : null;
}
