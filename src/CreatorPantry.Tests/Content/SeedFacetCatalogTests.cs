using CreatorPantry.Domain.Managers.Reference;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The two code-owned catalogues the content seed adds: their keys, their shape, and their registration.
/// </summary>
/// <remarks>
/// Both lists are provisional — the requirement text behind SEED-001 is not in the repository — so these tests
/// pin the <em>rules</em> a correction has to keep, not the editorial choices. The one exception is the count,
/// asserted so that changing a list is a deliberate edit to a test rather than a silent drift.
/// </remarks>
public sealed class SeedFacetCatalogTests
{
    public static TheoryData<string, IReadOnlyList<(string Key, string DisplayName, bool IsActive)>> Catalogues =>
        new()
        {
            {
                "photography styles",
                [.. new PhotographyStyleCatalog().All.Select(style => (style.Key, style.DisplayName, style.IsActive))]
            },
            {
                "occasions",
                [.. new OccasionCatalog().All.Select(occasion => (occasion.Key, occasion.DisplayName, occasion.IsActive))]
            },
        };

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void Every_key_is_a_lowercase_slug(
        string name, IReadOnlyList<(string Key, string DisplayName, bool IsActive)> entries)
    {
        Assert.NotEmpty(entries);

        foreach (var (key, _, _) in entries)
        {
            Assert.Matches("^[a-z0-9][a-z0-9-]*$", key);
            Assert.True(key.Length <= 64, $"{name}: '{key}' is longer than a key may be.");
        }
    }

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void Keys_are_unique_and_names_are_present(
        string name, IReadOnlyList<(string Key, string DisplayName, bool IsActive)> entries)
    {
        Assert.Equal(entries.Count, entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName), name));
    }

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void Everything_shipped_is_active(
        string name, IReadOnlyList<(string Key, string DisplayName, bool IsActive)> entries) =>
        Assert.All(entries, entry => Assert.True(entry.IsActive, $"{name}: '{entry.Key}' ships retired."));

    [Fact]
    public void The_shipped_lists_are_the_ones_the_delivery_note_describes()
    {
        // Ten shot types and twelve occasions. A correction is welcome and should change this line with it.
        Assert.Equal(10, new PhotographyStyleCatalog().All.Count);
        Assert.Equal(12, new OccasionCatalog().All.Count);
    }

    [Fact]
    public void A_key_resolves_and_an_unknown_one_does_not()
    {
        var styles = new PhotographyStyleCatalog();
        Assert.Equal("Overhead flat-lay", styles.Find("overhead-flat-lay")!.DisplayName);
        Assert.Null(styles.Find("daguerreotype"));

        var occasions = new OccasionCatalog();
        Assert.Equal("Weeknight", occasions.Find("weeknight")!.DisplayName);
        Assert.Null(occasions.Find("coronation"));
    }

    [Fact]
    public void A_retired_entry_still_resolves()
    {
        // The same rule as a retired channel or vocabulary entry: readable where already stored, refused as new.
        var styles = new PhotographyStyleCatalog([new PhotographyStyle("polaroid", "Polaroid", IsActive: false)]);

        var found = styles.Find("polaroid");

        Assert.NotNull(found);
        Assert.False(found.IsActive);
    }

    [Fact]
    public void Registration_yields_the_default_lists_rather_than_empty_ones()
    {
        // The trap AddContentChannelCatalog documents: the list-taking constructor would win and resolve nothing.
        using var provider = new ServiceCollection()
            .AddPhotographyStyleCatalog()
            .AddOccasionCatalog()
            .BuildServiceProvider();

        Assert.NotEmpty(provider.GetRequiredService<IPhotographyStyleCatalog>().All);
        Assert.NotEmpty(provider.GetRequiredService<IOccasionCatalog>().All);
    }

    [Fact]
    public void Registration_leaves_a_catalogue_a_host_already_chose()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IOccasionCatalog>(new OccasionCatalog([new Occasion("only-one", "Only one")]))
            .AddOccasionCatalog()
            .BuildServiceProvider();

        Assert.Equal("only-one", Assert.Single(provider.GetRequiredService<IOccasionCatalog>().All).Key);
    }

    [Fact]
    public void A_photography_style_is_not_a_brand_statement()
    {
        // Guards the boundary the catalogue's own remarks draw: the creator's photography direction is prose in
        // their style guide, and nothing here may grow fields that would make this a second source for it.
        var properties = typeof(PhotographyStyle).GetProperties().Select(property => property.Name).ToList();

        Assert.Equal(["Key", "DisplayName", "IsActive"], properties);
    }
}
