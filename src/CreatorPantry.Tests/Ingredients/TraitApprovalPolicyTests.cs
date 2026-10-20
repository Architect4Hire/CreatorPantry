using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Tests.Ingredients;

/// <summary>
/// The rule 14.7's H-7 found missing: an approved trait may cite only a source the product has approved for
/// that kind of evidence.
/// </summary>
/// <remarks>
/// <para>
/// Approved is the only review status an analysis acts on, so it is the state that decides whether a claim
/// reaches a creator. The schema checks a trait's source *kind* (no AI estimate, no unvetted absence) and a
/// composite key stops a row lying about that kind — but nothing checked the source was approved at all. It
/// held only because no write path creates an approved trait today, which is a fact about what has been built
/// rather than a rule, and the first writer added would have inherited no rule to follow.
/// </para>
/// <para>
/// These tests are the rule. <c>EvidenceSourceCatalogTests</c> and <c>DietaryAllergenTraitConstraintTests</c>
/// cover the catalogue and the schema either side of it.
/// </para>
/// </remarks>
public sealed class TraitApprovalPolicyTests
{
    /// <summary>
    /// Nothing may be approved for allergen, dietary or food-safety evidence, because B-33 approves no source
    /// for any of them.
    /// </summary>
    /// <remarks>
    /// The test to read first, and the one that should fail loudly if a source is ever added to the catalogue
    /// for one of these kinds without the decision being recorded. "No approved source" is why a real analysis
    /// reads almost entirely as unknown, which is the correct answer for records that do not exist.
    /// </remarks>
    [Theory]
    [InlineData(ReferenceEvidenceKind.Allergen)]
    [InlineData(ReferenceEvidenceKind.Dietary)]
    [InlineData(ReferenceEvidenceKind.FoodSafety)]
    public void No_source_may_be_approved_for_a_kind_the_product_approves_none_for(ReferenceEvidenceKind kind)
    {
        Assert.Null(EvidenceSourceCatalog.For(kind));
        Assert.False(TraitPolicy.MayBeApproved("usda-fdc", kind));
        Assert.False(TraitPolicy.MayBeApproved("fsis", kind));
        Assert.False(TraitPolicy.MayBeApproved("nchfp", kind));
    }

    [Fact]
    public void The_approved_nutrition_source_may_be_approved_for_nutrition_and_density()
    {
        Assert.True(TraitPolicy.MayBeApproved("usda-fdc", ReferenceEvidenceKind.Nutrition));
        Assert.True(TraitPolicy.MayBeApproved("usda-fdc", ReferenceEvidenceKind.Density));
    }

    /// <remarks>
    /// A source code is a stable machine key, and a writer reading one out of a row should not be defeated by
    /// its casing.
    /// </remarks>
    [Fact]
    public void The_source_code_is_matched_without_regard_to_case()
    {
        Assert.True(TraitPolicy.MayBeApproved("USDA-FDC", ReferenceEvidenceKind.Nutrition));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some-blog")]
    [InlineData("usda-fdc-2")]
    public void An_unapproved_or_missing_source_may_never_be_approved(string? sourceCode)
    {
        Assert.False(TraitPolicy.MayBeApproved(sourceCode, ReferenceEvidenceKind.Nutrition));
    }

    /// <summary>
    /// The rule the schema keeps underneath this one, restated so the two are read together: an unvetted kind
    /// cannot assert an absence at all, approved or not.
    /// </summary>
    [Theory]
    [InlineData(ReferenceSourceKind.CommunityContributed, false)]
    [InlineData(ReferenceSourceKind.AiEstimated, false)]
    [InlineData(ReferenceSourceKind.OfficialDatabase, true)]
    [InlineData(ReferenceSourceKind.Publisher, true)]
    [InlineData(ReferenceSourceKind.LabMeasured, true)]
    public void Only_a_vetted_kind_may_assert_an_absence(ReferenceSourceKind kind, bool mayAssert)
    {
        Assert.Equal(mayAssert, TraitPolicy.MayAssertAllergenAbsence(kind));
    }
}
