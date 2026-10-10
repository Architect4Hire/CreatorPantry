using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The rendition key grammar: beside its source, in its source's container, and never mistakable for one.
/// </summary>
public sealed class MediaRenditionObjectKeyTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly string StagedKey = GeneratedImageObjectKey.For(Workspace, Guid.NewGuid(), 2);

    private static readonly string VersionKey = MediaAssetObjectKey.For(Workspace, Guid.NewGuid(), 14);

    [Theory]
    [InlineData(MediaRenditionPurpose.Web, "-web")]
    [InlineData(MediaRenditionPurpose.Thumbnail, "-thumbnail")]
    public void A_rendition_is_named_after_its_source_and_lives_in_its_sources_container(
        MediaRenditionPurpose purpose, string suffix)
    {
        foreach (var (source, container) in new[]
        {
            (StagedKey, GeneratedImageObjectKey.Container),
            (VersionKey, MediaAssetObjectKey.Container),
        })
        {
            var key = MediaRenditionObjectKey.For(source, purpose);

            Assert.Equal(source + suffix, key);
            Assert.True(MediaRenditionObjectKey.TryParse(key, out var parts));
            Assert.Equal(new MediaRenditionObjectKeyParts(container, Workspace, source, purpose, key), parts);
            Assert.InRange(key.Length, 1, MediaPolicy.ObjectKeyMaxLength);
        }
    }

    [Fact]
    public void A_rendition_key_is_never_a_source_key_and_a_source_key_is_never_a_renditions()
    {
        var staged = MediaRenditionObjectKey.For(StagedKey, MediaRenditionPurpose.Web);
        var version = MediaRenditionObjectKey.For(VersionKey, MediaRenditionPurpose.Thumbnail);

        // What keeps the staging reconciliation, which lists only keys it can parse as a staged image,
        // from ever seeing a rendition — and what keeps the rendition gateway from ever naming an original.
        Assert.False(GeneratedImageObjectKey.TryParse(staged, out _));
        Assert.False(MediaAssetObjectKey.TryParse(version, out _));
        Assert.False(MediaRenditionObjectKey.TryParse(StagedKey, out _));
        Assert.False(MediaRenditionObjectKey.TryParse(VersionKey, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("web")]
    [InlineData("-web")]
    [InlineData("workspaces/not-a-workspace/generated-images/x/0-web")]
    [InlineData("https://example.com/picture-web")]
    public void Anything_this_would_not_have_written_is_not_a_key(string? candidate)
    {
        Assert.False(MediaRenditionObjectKey.TryParse(candidate, out _));
    }

    [Fact]
    public void A_purpose_it_does_not_know_and_a_rendition_of_a_rendition_are_refused()
    {
        var web = MediaRenditionObjectKey.For(StagedKey, MediaRenditionPurpose.Web);

        Assert.False(MediaRenditionObjectKey.TryParse(StagedKey + "-poster", out _));
        Assert.False(MediaRenditionObjectKey.TryParse(web + "-thumbnail", out _));
        Assert.False(MediaRenditionObjectKey.TryFor(web, MediaRenditionPurpose.Thumbnail, out _));
        Assert.Throws<ArgumentException>(() => MediaRenditionObjectKey.For("not-a-key", MediaRenditionPurpose.Web));
    }
}
