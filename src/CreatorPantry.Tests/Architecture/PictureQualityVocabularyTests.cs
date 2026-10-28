using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// The shared picture-quality vocabulary and Media's enum name the same classes, in the same order
/// (PRICE-006, AP.4.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why there are two of them at all.</strong> The typed form belongs to Media, which decides what a
/// class sends to; the names belong in the shared kernel, because a plan's configured class list and the public
/// catalogue are both text and neither should reach into Media to learn a spelling. The cost of that is this
/// test: a class added to the enum and not to the vocabulary would be one the catalogue can never name, and one
/// added the other way would be a name nothing can make a picture at.
/// </para>
/// <para>
/// Order matters as much as membership. Both are ascending by quality, and every list a reader sees — the credit
/// menu, a plan's classes, the studio's picker — is given in it, so the two drifting apart would reorder a page.
/// </para>
/// </remarks>
public sealed class PictureQualityVocabularyTests
{
    [Fact]
    public void The_vocabulary_names_every_quality_class_in_the_enums_order() =>
        Assert.Equal(Enum.GetNames<GeneratedImageQualityClass>(), PictureQualityVocabulary.Names);

    /// <summary>Every name the vocabulary has parses to the class it names, and nothing else does.</summary>
    [Fact]
    public void Every_name_is_known_and_canonical_and_a_non_class_is_neither()
    {
        Assert.All(PictureQualityVocabulary.Names, name =>
        {
            Assert.True(PictureQualityVocabulary.IsKnown(name));
            Assert.Equal(name, PictureQualityVocabulary.Canonical(name));
            Assert.True(Enum.TryParse<GeneratedImageQualityClass>(name, out _));
        });

        Assert.False(PictureQualityVocabulary.IsKnown("Ultra8K"));
        Assert.Null(PictureQualityVocabulary.Canonical("Ultra8K"));
    }

    /// <summary>
    /// The spelling rule AP.4.3 settled: the catalogue's hyphens and case carry no meaning, so the figures a
    /// section writes by hand reach the same class the enum does.
    /// </summary>
    [Theory]
    [InlineData("high-2k", "High2K")]
    [InlineData("HIGH-2K", "High2K")]
    [InlineData("High2K", "High2K")]
    [InlineData("  standard  ", "Standard")]
    [InlineData("DRAFT", "Draft")]
    [InlineData("high-4k", "High4K")]
    public void A_class_is_matched_however_it_is_spelled(string written, string canonical)
    {
        Assert.True(PictureQualityVocabulary.IsKnown(written));
        Assert.Equal(canonical, PictureQualityVocabulary.Canonical(written));
    }

    /// <summary>
    /// A list comes back ascending whatever order it was written in, which is what lets a plan's classes be
    /// published in one order however an owner typed them.
    /// </summary>
    [Fact]
    public void A_list_is_canonical_and_ascending_whatever_order_it_was_written_in()
    {
        Assert.Equal(
            ["Draft", "Standard", "High2K"],
            PictureQualityVocabulary.CanonicalAll(["high-2k", "DRAFT", " standard "]));

        // A name matching no class is left out rather than published raw.
        Assert.Equal(["Standard"], PictureQualityVocabulary.CanonicalAll(["standard", "ultra-8k"]));
        Assert.Empty(PictureQualityVocabulary.CanonicalAll([]));
    }
}
