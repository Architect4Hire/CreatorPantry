using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The name a downloaded brand source document is offered under: that it is safe in a header, that it
/// describes the bytes, and that it is the same every time.
/// </summary>
public sealed class BrandSourceDownloadFileNameTests
{
    [Theory]
    [InlineData("House style", 1, "application/pdf", "house-style-v1.pdf")]
    [InlineData("House style", 12, "application/pdf", "house-style-v12.pdf")]
    [InlineData("Autumn Voice & Tone", 1, "text/markdown", "autumn-voice-tone-v1.md")]
    [InlineData("Launch post", 3, "text/plain", "launch-post-v3.txt")]
    [InlineData("Landing copy", 1, "text/html", "landing-copy-v1.html")]
    [InlineData("Hero shot", 1, "image/png", "hero-shot-v1.png")]
    [InlineData("Hero shot", 1, "image/webp", "hero-shot-v1.webp")]
    public void A_name_is_the_title_the_version_and_the_media_types_extension(
        string title, int versionNumber, string mediaType, string expected) =>
        Assert.Equal(expected, BrandSourceDownloadFileName.For(title, versionNumber, mediaType));

    /// <summary>
    /// A JPEG is accepted under two extensions and downloads under one. A download name is ours to choose,
    /// and the same version asking twice must get the same name.
    /// </summary>
    [Fact]
    public void A_jpeg_downloads_as_jpg_whichever_spelling_it_arrived_under() =>
        Assert.Equal("hero-shot-v1.jpg", BrandSourceDownloadFileName.For("Hero shot", 1, "image/jpeg"));

    /// <summary>
    /// Everything a header, a shell or a file system could read as structure is gone, and no run of
    /// punctuation becomes more than one hyphen.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "etc-passwd-v1.pdf")]
    [InlineData("she said \"warm\"", "she-said-warm-v1.pdf")]
    [InlineData("a\r\nb", "a-b-v1.pdf")]
    [InlineData("drop; rm -rf /", "drop-rm-rf-v1.pdf")]
    [InlineData("name\0byte", "name-byte-v1.pdf")]
    [InlineData("  spaced  out  ", "spaced-out-v1.pdf")]
    [InlineData("--leading and trailing--", "leading-and-trailing-v1.pdf")]
    public void A_hostile_title_folds_to_letters_digits_and_hyphens(string title, string expected)
    {
        var name = BrandSourceDownloadFileName.For(title, 1, "application/pdf");

        Assert.Equal(expected, name);

        // Stated separately from the expectation, so the guarantee survives a change to the expected spelling.
        Assert.All(name, character => Assert.True(
            character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.',
            $"'{character}' is not allowed in a download name."));
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Crème brûlée notes", "creme-brulee-notes-v1.pdf")]
    [InlineData("Straßenfest", "strassenfest-v1.pdf")]
    [InlineData("Ærø guide", "aero-guide-v1.pdf")]
    public void Accents_fold_and_the_odd_letters_are_spelled_out(string title, string expected) =>
        Assert.Equal(expected, BrandSourceDownloadFileName.For(title, 1, "application/pdf"));

    /// <summary>A title in a script that folds away entirely still has to produce a download.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("、。「」")]
    [InlineData(null)]
    public void A_title_that_folds_away_falls_back_to_a_word(string? title) =>
        Assert.Equal($"{BrandSourceDownloadFileName.Fallback}-v1.pdf",
            BrandSourceDownloadFileName.For(title, 1, "application/pdf"));

    /// <summary>Windows refuses these as a file name whatever the extension, so they are prefixed, not kept.</summary>
    [Theory]
    [InlineData("con")]
    [InlineData("AUX")]
    [InlineData("lpt1")]
    public void A_reserved_device_name_is_prefixed_rather_than_used(string title) =>
        Assert.Equal(
            $"{BrandSourceDownloadFileName.Fallback}-{title.ToLowerInvariant()}-v1.pdf",
            BrandSourceDownloadFileName.For(title, 1, "application/pdf"));

    [Fact]
    public void A_very_long_title_is_cut_and_never_left_ending_in_a_hyphen()
    {
        var name = BrandSourceDownloadFileName.For(new string('a', 200) + " tail", 1, "application/pdf");

        Assert.Equal($"{new string('a', 60)}-v1.pdf", name);
        Assert.DoesNotContain("--", name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_version_always_gets_the_same_name() =>
        Assert.Equal(
            BrandSourceDownloadFileName.For("House style", 2, "application/pdf"),
            BrandSourceDownloadFileName.For("House style", 2, "application/pdf"));

    /// <summary>
    /// A media type with no extension to offer loses the extension rather than gaining a wrong one — and is
    /// unreachable for anything an upload stored, which the inspector's own test asserts.
    /// </summary>
    [Fact]
    public void An_unknown_media_type_yields_a_name_with_no_extension() =>
        Assert.Equal("house-style-v1", BrandSourceDownloadFileName.For("House style", 1, "application/x-made-up"));
}
