using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The frame count IMG-004's "only the opening frame" warning depends on.
/// </summary>
/// <remarks>
/// GIFs are built byte by byte here rather than loaded from a file: there is no image dependency in this
/// repository to make one with, and a hand-built file is the only kind whose structure a test can state
/// exactly. Each is a 1×1 image, which is all a frame count cares about.
/// </remarks>
public sealed class GifFramesTests
{
    [Fact]
    public void A_single_frame_gif_counts_one()
    {
        Assert.Equal(1, GifFrames.Count(Gif(frames: 1)));
        Assert.False(GifFrames.IsAnimated(Gif(frames: 1)));
    }

    [Fact]
    public void A_three_frame_gif_counts_three_and_reads_as_animated()
    {
        Assert.Equal(3, GifFrames.Count(Gif(frames: 3)));
        Assert.True(GifFrames.IsAnimated(Gif(frames: 3)));
    }

    /// <summary>
    /// A graphic control extension before each frame is what a real animation writes, and it must not be
    /// counted as a frame of its own.
    /// </summary>
    [Fact]
    public void Extensions_between_frames_are_not_counted_as_frames()
    {
        Assert.Equal(2, GifFrames.Count(Gif(frames: 2, withGraphicControl: true)));
    }

    /// <summary>A file that ends before its trailer cannot be counted, and says so rather than guessing.</summary>
    [Fact]
    public void A_truncated_gif_counts_nothing()
    {
        var whole = Gif(frames: 2);

        Assert.Null(GifFrames.Count(whole.AsSpan(0, whole.Length - 6)));
        Assert.Null(GifFrames.IsAnimated(whole.AsSpan(0, whole.Length - 6)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public void Anything_too_short_to_be_a_gif_counts_nothing(int length)
    {
        Assert.Null(GifFrames.Count(Gif(frames: 1).AsSpan(0, length)));
    }

    [Fact]
    public void Something_that_is_not_a_gif_counts_nothing()
    {
        Assert.Null(GifFrames.Count(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0 }));
    }

    /// <summary>A block the format does not define stops the walk rather than being skipped past.</summary>
    [Fact]
    public void An_unknown_block_counts_nothing()
    {
        var gif = Gif(frames: 1).ToList();

        // Replace the trailer with a byte that is no block at all.
        gif[^1] = 0x7F;

        Assert.Null(GifFrames.Count([.. gif]));
    }

    /// <summary>GIF87a is still a GIF, and a reference image saved years ago may be one.</summary>
    [Fact]
    public void The_older_header_is_accepted()
    {
        var gif = Gif(frames: 1);
        "GIF87a"u8.CopyTo(gif.AsSpan(0, 6));

        Assert.Equal(1, GifFrames.Count(gif));
    }

    /// <summary>
    /// A 1×1 GIF with a two-entry global colour table, and one image block per frame.
    /// </summary>
    private static byte[] Gif(int frames, bool withGraphicControl = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8);

        // Logical screen: 1×1, global colour table of two entries, no background, no aspect.
        bytes.AddRange([0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00]);

        // The table itself: two RGB triples.
        bytes.AddRange([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);

        for (var frame = 0; frame < frames; frame++)
        {
            if (withGraphicControl)
            {
                // Extension, graphic-control label, one four-byte sub-block, terminator.
                bytes.AddRange([0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00]);
            }

            // Image descriptor at 0,0 sized 1×1 with no local colour table.
            bytes.AddRange([0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

            // LZW minimum code size, one sub-block of compressed data, terminator.
            bytes.AddRange([0x02, 0x02, 0x4C, 0x01, 0x00]);
        }

        bytes.Add(0x3B);

        return [.. bytes];
    }
}
