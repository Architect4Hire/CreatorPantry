using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The chunker is pure, so these pin the properties a resumed run, a replay and a checksum all depend on:
/// the same text always gives the same passages, and every passage is provably a slice of the artifact.
/// </summary>
public sealed class BrandSourceChunkerTests
{
    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(index =>
            $"Paragraph {index}. " + string.Concat(Enumerable.Repeat("Warm, plain and never breathless. ", 8))));

    [Fact]
    public void The_same_text_always_gives_the_same_passages()
    {
        var text = Paragraphs(40);

        Assert.Equal(BrandSourceChunker.Chunk(text), BrandSourceChunker.Chunk(text));
    }

    [Fact]
    public void Empty_and_blank_text_give_no_passages()
    {
        Assert.Empty(BrandSourceChunker.Chunk(string.Empty));
        Assert.Empty(BrandSourceChunker.Chunk("  \n\n \t "));
    }

    [Fact]
    public void Short_text_is_one_passage_exactly_as_written()
    {
        const string text = "  Evergreen first.  \n";

        var passage = Assert.Single(BrandSourceChunker.Chunk(text));

        Assert.Equal(1, passage.Ordinal);
        Assert.Equal(0, passage.StartByteOffset);
        Assert.Equal(text, passage.Text);
    }

    [Fact]
    public void Every_passage_is_a_byte_exact_slice_of_the_artifact_with_the_checksum_of_that_slice()
    {
        // Multi-byte text, so a character offset and a byte offset disagree after the first passage.
        var text = string.Join("\n\n", Enumerable.Range(1, 60).Select(index =>
            $"Crème brûlée №{index} — 🍮 " + string.Concat(Enumerable.Repeat("délicieux et simple. ", 20))));

        var bytes = Encoding.UTF8.GetBytes(text);
        var passages = BrandSourceChunker.Chunk(text);

        Assert.True(passages.Count > 3);

        foreach (var passage in passages)
        {
            var slice = bytes.AsSpan((int)passage.StartByteOffset, passage.ByteLength);

            Assert.Equal(passage.Text, Encoding.UTF8.GetString(slice));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(slice)), passage.ContentChecksum);
        }
    }

    [Fact]
    public void Passages_are_numbered_in_reading_order_and_cover_the_text_without_a_gap()
    {
        var text = Paragraphs(40);
        var bytes = Encoding.UTF8.GetBytes(text);
        var passages = BrandSourceChunker.Chunk(text);

        Assert.Equal(Enumerable.Range(1, passages.Count), passages.Select(passage => passage.Ordinal));
        Assert.Equal(0, passages[0].StartByteOffset);

        for (var index = 1; index < passages.Count; index++)
        {
            var previousEnd = passages[index - 1].StartByteOffset + passages[index - 1].ByteLength;

            // Each starts at or before where the last ended (the overlap) and after where the last started.
            Assert.InRange(passages[index].StartByteOffset, passages[index - 1].StartByteOffset + 1, previousEnd);
        }

        var last = passages[^1];
        Assert.Equal(bytes.Length, last.StartByteOffset + last.ByteLength);
    }

    [Fact]
    public void No_passage_exceeds_the_stored_limits()
    {
        var passages = BrandSourceChunker.Chunk(Paragraphs(80));

        Assert.All(passages, passage =>
        {
            Assert.InRange(passage.Text.Length, 1, BrandPolicy.ChunkTargetLength);
            Assert.InRange(passage.ByteLength, 1, BrandPolicy.ChunkMaxBytes);
        });
    }

    [Fact]
    public void Passages_prefer_to_end_on_a_paragraph_and_repeat_a_little_of_the_one_before()
    {
        var passages = BrandSourceChunker.Chunk(Paragraphs(40));

        Assert.All(passages.SkipLast(1), passage => Assert.EndsWith("\n\n", passage.Text));

        // The overlap: the next passage begins with text the previous one ended with.
        var tail = passages[0].Text[^BrandPolicy.ChunkOverlapLength..];
        var head = passages[1].Text[..Math.Min(passages[1].Text.Length, BrandPolicy.ChunkOverlapLength)];

        Assert.Contains(head.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], tail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_unbroken_run_is_hard_split_and_never_inside_a_surrogate_pair()
    {
        var text = string.Concat(Enumerable.Repeat("🍮", 3000));
        var passages = BrandSourceChunker.Chunk(text);

        Assert.True(passages.Count > 1);

        Assert.All(passages, passage =>
        {
            Assert.False(char.IsLowSurrogate(passage.Text[0]), "a passage began inside a surrogate pair");
            Assert.False(char.IsHighSurrogate(passage.Text[^1]), "a passage ended inside a surrogate pair");
            Assert.Equal(passage.ByteLength, Encoding.UTF8.GetByteCount(passage.Text));
        });
    }
}
