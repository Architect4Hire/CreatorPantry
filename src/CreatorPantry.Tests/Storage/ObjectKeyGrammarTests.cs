using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Storage;

/// <summary>
/// The grammar every key family shares and the store enforces as its second line (15.2, SEC-005).
/// </summary>
public sealed class ObjectKeyGrammarTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid Other = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static TheoryData<string> EveryFamily() =>
    [
        MediaAssetObjectKey.For(Workspace, Other, 1),
        MediaAssetObjectKey.For(Workspace, Other, 999_999_999),
        GeneratedImageObjectKey.For(Workspace, Other, 0),
        MediaRenditionObjectKey.For(MediaAssetObjectKey.For(Workspace, Other, 3), MediaRenditionPurpose.Thumbnail),
        MediaRenditionObjectKey.For(GeneratedImageObjectKey.For(Workspace, Other, 1), MediaRenditionPurpose.Web),
        BrandSourceObjectKey.ForOriginal(Workspace, Other, Other),
        BrandSourceObjectKey.ForExtractedText(Workspace, Other, Other, 12),
    ];

    [Theory]
    [MemberData(nameof(EveryFamily))]
    public void Every_key_any_family_writes_is_inside_the_shared_grammar(string key)
    {
        Assert.True(ObjectKeyGrammar.IsWellFormed(key), key);
        Assert.InRange(key.Length, 1, ObjectKeyGrammar.MaxLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("original")]
    [InlineData("workspaces/../media-assets/x/1")]
    [InlineData("workspaces/aaaa/./1")]
    [InlineData("/workspaces/aaaa/1")]
    [InlineData("workspaces/aaaa/1/")]
    [InlineData("workspaces//aaaa/1")]
    [InlineData("workspaces\\aaaa\\1")]
    [InlineData("workspaces/%2e%2e/1")]
    [InlineData("https://example.invalid/x")]
    [InlineData("workspaces/AAAA/1")]
    [InlineData("workspaces/aaaa/1.png")]
    [InlineData("workspaces/aaaa/-web")]
    [InlineData("workspaces/aaaa/1--web")]
    [InlineData("workspaces/aaaa/1\n")]
    [InlineData("workspaces/aaaa/1 ")]
    public void Anything_a_traversal_a_scheme_or_an_escape_could_be_made_of_is_outside_it(string candidate)
    {
        Assert.False(ObjectKeyGrammar.IsWellFormed(candidate));
        Assert.Throws<ArgumentException>(() => ObjectKeyGrammar.ThrowIfMalformed("media-assets", candidate));
    }

    [Fact]
    public void A_key_past_the_shared_cap_is_outside_it()
    {
        var key = "workspaces/" + new string('a', ObjectKeyGrammar.MaxLength);

        Assert.False(ObjectKeyGrammar.IsWellFormed(key));
        Assert.False(MediaAssetObjectKey.TryParse(key, out _));
        Assert.False(BrandSourceObjectKey.TryParse(key, out _));
    }

    [Theory]
    [InlineData("workspaces/aaaa/media-assets/", true)]
    [InlineData("workspaces/aaaa/media-assets", false)]
    [InlineData("workspaces/aa", false)]
    [InlineData("/", false)]
    [InlineData("workspaces/../", false)]
    public void A_listing_prefix_is_whole_segments_ending_at_a_slash(string prefix, bool wellFormed)
    {
        Assert.Equal(wellFormed, ObjectKeyGrammar.IsWellFormedPrefix(prefix));
    }

    [Theory]
    [InlineData("media-assets", true)]
    [InlineData("brand-sources", true)]
    [InlineData("generated-images", true)]
    [InlineData("Media-Assets", false)]
    [InlineData("../media-assets", false)]
    [InlineData("ab", false)]
    [InlineData("media--assets", false)]
    public void A_container_is_a_name_this_application_could_have_declared(string container, bool wellFormed)
    {
        Assert.Equal(wellFormed, ObjectKeyGrammar.IsWellFormedContainer(container));
    }

    [Fact]
    public void No_family_names_the_empty_identifier()
    {
        Assert.Throws<ArgumentException>(() => MediaAssetObjectKey.For(Guid.Empty, Other, 1));
        Assert.Throws<ArgumentException>(() => MediaAssetObjectKey.For(Workspace, Guid.Empty, 1));
        Assert.Throws<ArgumentException>(() => GeneratedImageObjectKey.For(Workspace, Guid.Empty, 0));
        Assert.Throws<ArgumentException>(() => BrandSourceObjectKey.ForOriginal(Workspace, Other, Guid.Empty));
    }
}
