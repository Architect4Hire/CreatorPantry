using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The DAM version key family: built only from server identifiers, parsed only back from its own shape (15.2).
/// </summary>
public sealed class MediaAssetObjectKeyTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid Asset = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_key_is_the_workspace_then_the_asset_then_the_version()
    {
        Assert.Equal($"workspaces/{Workspace:N}/media-assets/{Asset:N}/4", MediaAssetObjectKey.For(Workspace, Asset, 4));
    }

    [Fact]
    public void A_key_this_type_wrote_parses_back_into_its_parts()
    {
        Assert.True(MediaAssetObjectKey.TryParse(MediaAssetObjectKey.For(Workspace, Asset, 7), out var parts));

        Assert.Equal(new MediaAssetObjectKeyParts(Workspace, Asset, 7), parts);
    }

    [Fact]
    public void Every_key_sits_under_its_own_workspaces_prefix()
    {
        Assert.StartsWith(MediaAssetObjectKey.PrefixFor(Workspace), MediaAssetObjectKey.For(Workspace, Asset, 1), StringComparison.Ordinal);
        Assert.DoesNotContain(Workspace.ToString("N"), MediaAssetObjectKey.PrefixFor(Asset), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("workspaces/../media-assets/x/1")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/../../etc/passwd")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/1/..")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/01")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/0")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/1234567890")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/1\n")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/1-web")]
    [InlineData("workspaces/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/media-assets/11111111222233334444555555555555/1")]
    [InlineData("workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/media-assets/11111111222233334444555555555555/1")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/generated-images/11111111222233334444555555555555/1")]
    [InlineData("workspaces\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\media-assets\\11111111222233334444555555555555\\1")]
    [InlineData("workspaces/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/media-assets/11111111222233334444555555555555/photo.jpg")]
    [InlineData("https://example.invalid/media-assets/1")]
    public void A_key_this_type_would_not_have_written_is_refused(string? candidate)
    {
        Assert.False(MediaAssetObjectKey.TryParse(candidate, out _));
    }

    [Fact]
    public void A_version_below_one_has_no_key()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaAssetObjectKey.For(Workspace, Asset, 0));
    }
}
