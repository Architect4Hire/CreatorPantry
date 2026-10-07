using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The downloaded file's name (DAM-007): deterministic, safe, and describing the bytes rather than the upload.
/// </summary>
/// <remarks>
/// A pure function, so these are the cheapest tests in the feature and the ones that actually pin the contract.
/// What a creator's filesystem ends up holding is decided entirely here.
/// </remarks>
public sealed class MediaAssetDownloadFileNameTests
{
    [Fact]
    public void A_title_becomes_a_slug_with_the_version_and_the_stored_extension()
    {
        Assert.Equal(
            "soda-bread-hero-v2.jpg",
            MediaAssetDownloadFileName.For("Soda bread hero", 2, "image/jpeg"));
    }

    /// <summary>
    /// The same inputs always give the same name, which is what "deterministic" means here — no timestamp, no
    /// counter, nothing that varies between two downloads of one version.
    /// </summary>
    [Fact]
    public void The_same_asset_version_and_type_always_give_the_same_name()
    {
        var first = MediaAssetDownloadFileName.For("Soda bread hero", 2, "image/jpeg");
        var second = MediaAssetDownloadFileName.For("Soda bread hero", 2, "image/jpeg");

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The extension follows the stored media type, so the same title stored as a PNG is named as a PNG. This is the
    /// "accurate extension" half of the requirement.
    /// </summary>
    [Theory]
    [InlineData("image/jpeg", "soda-bread-v1.jpg")]
    [InlineData("image/png", "soda-bread-v1.png")]
    [InlineData("image/webp", "soda-bread-v1.webp")]
    [InlineData("image/gif", "soda-bread-v1.gif")]
    public void The_extension_comes_from_the_stored_media_type(string mediaType, string expected)
    {
        Assert.Equal(expected, MediaAssetDownloadFileName.For("Soda bread", 1, mediaType));
    }

    /// <summary>
    /// A media type with no known extension yields a name with none, rather than a guessed one: a missing extension
    /// is recoverable where a wrong one misleads whatever opens the file.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("image/tiff")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_media_type_gets_no_extension_rather_than_a_wrong_one(string? mediaType)
    {
        Assert.Equal("soda-bread-v1", MediaAssetDownloadFileName.For("Soda bread", 1, mediaType));
    }

    /// <summary>
    /// The version suffix is what stops two downloads of one asset colliding, which is the whole reason it is there.
    /// </summary>
    [Fact]
    public void Two_versions_of_one_asset_have_different_names()
    {
        Assert.NotEqual(
            MediaAssetDownloadFileName.For("Soda bread hero", 1, "image/jpeg"),
            MediaAssetDownloadFileName.For("Soda bread hero", 2, "image/jpeg"));
    }

    /// <summary>
    /// A title that folds away entirely falls back to a word, because <c>-v2.jpg</c> is worse than
    /// <c>image-v2.jpg</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void A_title_that_slugs_to_nothing_falls_back(string? title)
    {
        Assert.Equal($"{MediaAssetDownloadFileName.Fallback}-v1.jpg", MediaAssetDownloadFileName.For(title, 1, "image/jpeg"));
    }

    /// <summary>
    /// A title carrying path separators, traversal, quotes, newlines or a header separator produces a name with none
    /// of them. This is the test that matters: the name goes straight into a <c>Content-Disposition</c> header, and
    /// anything that survived here would be either a traversal or a header injection.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers")]
    [InlineData("soda\"bread")]
    [InlineData("soda\r\nX-Injected: yes")]
    [InlineData("soda; filename=other.jpg")]
    [InlineData("soda/bread\\hero")]
    [InlineData("..")]
    [InlineData(".")]
    public void A_dangerous_title_cannot_survive_into_the_name(string title)
    {
        var name = MediaAssetDownloadFileName.For(title, 1, "image/jpeg");

        // One dot, and it is the extension's. Everything else is ASCII letters, digits or single hyphens.
        Assert.Equal(1, name.Count(character => character == '.'));
        Assert.EndsWith(".jpg", name, StringComparison.Ordinal);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.All(
            name[..^4],
            character => Assert.True(
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-',
                $"'{character}' should not survive into a file name"));
    }

    /// <summary>A long title is bounded, so no header and no filesystem is handed something unbounded.</summary>
    [Fact]
    public void A_very_long_title_is_bounded()
    {
        var name = MediaAssetDownloadFileName.For(new string('a', 500), 12, "image/jpeg");

        Assert.StartsWith("aaa", name, StringComparison.Ordinal);
        Assert.EndsWith("-v12.jpg", name, StringComparison.Ordinal);
        Assert.True(name.Length < 100, $"a name of {name.Length} characters is not bounded");
    }

    /// <summary>A version number is one-based, so zero or negative is a programming error rather than a name.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_version_number_below_one_is_refused(int versionNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaAssetDownloadFileName.For("Soda bread", versionNumber, "image/jpeg"));
    }
}
