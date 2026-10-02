using System.Reflection;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// That the version comparison accounts for every stored property of the three collections a guide version
/// owns — each one either compared or excluded on a stated ground.
/// </summary>
/// <remarks>
/// <para>
/// The defect this exists to catch is silent: adding a column to <see cref="BrandStyleGuideSection"/> and
/// forgetting the comparer leaves a diff that renders perfectly and quietly fails to mention the new field,
/// so a creator approves a version believing nothing else changed. Nothing else in the suite would fail.
/// </para>
/// <para>
/// It is a tripwire rather than a proof: the tables below are hand-maintained, so adding a property forces a
/// decision here, and <see cref="BrandStyleGuideComparerTests"/> is what proves the compared ones behave.
/// Mirrors <c>RecipeComparisonCompletenessTests</c>, which guards the same class of defect for recipes.
/// </para>
/// </remarks>
public sealed class BrandStyleGuideComparisonCompletenessTests
{
    /// <summary>Properties the comparison reads, and what it reports them as.</summary>
    private static readonly Dictionary<string, string> Compared = new(StringComparer.Ordinal)
    {
        // Sections: the key pair identifies the section, and the body is the thing compared.
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.SectionKey)}"] = "identity",
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.ChannelKey)}"] = "identity",
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.Body)}"] = "Changed when it differs",

        // Rules: kind and text together are the identity, so position is all that is left to report.
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.Kind)}"] = "identity",
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.Text)}"] = "identity",
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.SortOrder)}"] = "reported as a rank within the kind",

        // Source links: resolved to the document and the cited version number before comparison.
        [$"{nameof(BrandStyleGuideSourceLink)}.{nameof(BrandStyleGuideSourceLink.BrandSourceDocumentVersionId)}"] =
            "resolved to (documentId, versionNumber)",
    };

    /// <summary>Properties a comparison must not read, and why.</summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        // A row's own id is fresh on every version, so it identifies a row rather than describing content.
        // Comparing it would report every single item as changed.
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.Id)}"] = "row identity",
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.Id)}"] = "row identity",

        // Tenancy, never content. Both sides are the same workspace by construction.
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.WorkspaceId)}"] = "tenancy",
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.WorkspaceId)}"] = "tenancy",
        [$"{nameof(BrandStyleGuideSourceLink)}.{nameof(BrandStyleGuideSourceLink.WorkspaceId)}"] = "tenancy",

        // The parent is what the two sides of the comparison are.
        [$"{nameof(BrandStyleGuideSection)}.{nameof(BrandStyleGuideSection.BrandStyleGuideVersionId)}"] = "the side itself",
        [$"{nameof(BrandStyleGuideRule)}.{nameof(BrandStyleGuideRule.BrandStyleGuideVersionId)}"] = "the side itself",
        [$"{nameof(BrandStyleGuideSourceLink)}.{nameof(BrandStyleGuideSourceLink.BrandStyleGuideVersionId)}"] = "the side itself",
    };

    /// <summary>The three collections a guide version owns. Nothing else is versioned.</summary>
    private static readonly Type[] Entities =
        [typeof(BrandStyleGuideSection), typeof(BrandStyleGuideRule), typeof(BrandStyleGuideSourceLink)];

    public static TheoryData<Type> VersionOwnedEntities => [.. Entities];

    [Theory]
    [MemberData(nameof(VersionOwnedEntities))]
    public void Every_stored_property_is_either_compared_or_excluded(Type entity)
    {
        var unaccounted = Properties(entity)
            .Where(name => !Compared.ContainsKey(name) && !Excluded.ContainsKey(name))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"These properties are neither compared nor excluded: {string.Join(", ", unaccounted)}. "
                + "A version comparison that silently omits a stored field lets a creator approve a version "
                + "believing nothing else changed. Add each to Compared (and to BrandStyleGuideComparer) or to "
                + "Excluded with its ground.");
    }

    [Theory]
    [MemberData(nameof(VersionOwnedEntities))]
    public void No_property_is_both_compared_and_excluded(Type entity)
    {
        var both = Properties(entity).Where(name => Compared.ContainsKey(name) && Excluded.ContainsKey(name));

        Assert.Empty(both);
    }

    [Fact]
    public void Neither_table_names_a_property_that_no_longer_exists()
    {
        var existing = Entities.SelectMany(Properties).ToHashSet(StringComparer.Ordinal);

        // A renamed or dropped column must take its entry with it, or the tables above stop describing the
        // code and the test above starts passing for the wrong reason.
        Assert.Empty(Compared.Keys.Concat(Excluded.Keys).Where(name => !existing.Contains(name)));
    }

    private static IEnumerable<string> Properties(Type entity) =>
        entity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is not null)
            .Select(property => $"{entity.Name}.{property.Name}");
}
