using System.Reflection;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The assembled description: that it names only the facets, that its clauses read for every entry in every
/// catalogue, and that a safety caution travels in the sentence rather than only beside it.
/// </summary>
/// <remarks>
/// <para>
/// <c>ContentSeedDescription</c> is internal and pure, so it is reached by reflection. Worth it: the clause forms
/// have to work for heterogeneous vocabularies — occasions from "weeknight" to "holiday baking", shot styles from
/// "45-degree angle" to "dark and moody" — and a fixture holding two of each would not have caught the version
/// that produced "for budget" and "Shoot it 45-degree angle".
/// </para>
/// <para>
/// These assert clause <em>shape</em> over every shipped entry rather than reviewing the prose, which is what a
/// test can honestly do. Whether a sentence reads well is a judgement for a person; whether every entry lands in a
/// clause built to hold it is a fact.
/// </para>
/// </remarks>
public sealed class ContentSeedDescriptionTests
{
    private static readonly MethodInfo Describe = typeof(ContentSeedQueryViewModel).Assembly
        .GetType("CreatorPantry.Domain.Modules.Content.Managers.ContentSeedDescription", throwOnError: true)!
        .GetMethod("For", BindingFlags.Public | BindingFlags.Static)!;

    private static ContentSeedFacetServiceModel? Facet(string? key, string? displayName) =>
        key is null || displayName is null ? null : new ContentSeedFacetServiceModel(key, displayName, Pinned: false);

    private static string Description(
        (string Key, string Name)? cuisine = null,
        (string Key, string Name)? dishType = null,
        (string Key, string Name, bool Caution)? method = null,
        (string Key, string Name)? shot = null,
        (string Key, string Name)? channel = null,
        DayOfWeek day = DayOfWeek.Monday,
        (string Key, string Name)? theme = null,
        (string Key, string Name)? occasion = null,
        string? recipeTitle = null,
        string? subjectName = null) =>
        (string)Describe.Invoke(null,
        [
            Facet(cuisine?.Key, cuisine?.Name),
            Facet(dishType?.Key, dishType?.Name),
            method is { } m ? new ContentSeedMethodServiceModel(m.Key, m.Name, Pinned: false, m.Caution) : null,
            Facet(shot?.Key, shot?.Name),
            Facet(channel?.Key, channel?.Name),
            new ContentSeedDayServiceModel(day, Pinned: false, Facet(theme?.Key, theme?.Name)),
            Facet(occasion?.Key, occasion?.Name),
            recipeTitle,
            subjectName,
        ])!;

    [Fact]
    public void A_seed_built_around_a_recipe_names_it_and_describes_it() =>
        Assert.Equal(
            "Plan a post about \"Nan's Lemon Tart\", a French dessert made using the bake method, "
                + "with entertaining in mind. Shot: Dark and moody. Publish on Saturday on Instagram.",
            Description(
                cuisine: ("french", "French"),
                dishType: ("dessert", "Dessert"),
                method: ("bake", "Bake", false),
                shot: ("dark-and-moody", "Dark and moody"),
                channel: ("instagram", "Instagram"),
                day: DayOfWeek.Saturday,
                occasion: ("entertaining", "Entertaining"),
                recipeTitle: "Nan's Lemon Tart"));

    [Theory]
    [InlineData(false, false, "Plan a post about \"Soda Bread\". Publish on Monday.")]
    [InlineData(true, false, "Plan a post about \"Soda Bread\", a main course. Publish on Monday.")]
    [InlineData(false, true, "Plan a post about \"Soda Bread\", made using the bake method. Publish on Monday.")]
    public void A_recipe_that_states_little_still_reads(bool hasDishType, bool hasMethod, string expected) =>
        Assert.Equal(
            expected,
            Description(
                dishType: hasDishType ? ("main-course", "Main Course") : null,
                method: hasMethod ? ("bake", "Bake", false) : null,
                recipeTitle: "Soda Bread"));

    [Fact]
    public void A_recipes_own_method_still_carries_its_caution()
    {
        var description = Description(method: ("ferment", "Ferment", true), recipeTitle: "Kimchi");

        Assert.Contains("Follow tested, authoritative guidance for this method", description);
    }

    [Fact]
    public void A_full_seed_reads_as_one_brief() =>
        Assert.Equal(
            "Develop a Thai main course using the stir-fry method, with weeknight in mind. "
                + "Shot: Overhead flat-lay. Publish for Fakeaway Friday on Instagram.",
            Description(
                cuisine: ("thai", "Thai"),
                dishType: ("main-course", "Main Course"),
                method: ("stir-fry", "Stir-Fry", false),
                shot: ("overhead-flat-lay", "Overhead flat-lay"),
                channel: ("instagram", "Instagram"),
                day: DayOfWeek.Friday,
                theme: ("fakeaway-friday", "Fakeaway Friday"),
                occasion: ("weeknight", "Weeknight")));

    [Fact]
    public void A_seed_with_nothing_but_a_day_still_reads() =>
        Assert.Equal("Develop a recipe. Publish on Wednesday.", Description(day: DayOfWeek.Wednesday));

    [Theory]
    [InlineData("Thai", "Main Course", "Develop a Thai main course.")]
    [InlineData("Thai", null, "Develop a Thai dish.")]
    [InlineData(null, "Main Course", "Develop a main course.")]
    [InlineData(null, null, "Develop a recipe.")]
    public void The_subject_reads_with_either_half_missing(string? cuisine, string? dishType, string expected)
    {
        var description = Description(
            cuisine: cuisine is null ? null : ("c", cuisine),
            dishType: dishType is null ? null : ("d", dishType),
            day: DayOfWeek.Monday);

        Assert.StartsWith(expected, description);
    }

    [Fact]
    public void Every_shipped_occasion_lands_in_a_clause_built_to_hold_it()
    {
        // The guard on the defect: "for budget" and "for one-pan" were ungrammatical, and "with ... in mind" is
        // chosen because it reads for a noun phrase of any shape.
        foreach (var occasion in new OccasionCatalog().All)
        {
            var description = Description(occasion: (occasion.Key, occasion.DisplayName));

            Assert.Contains($"with {occasion.DisplayName.ToLowerInvariant()} in mind.", description);
        }
    }

    [Fact]
    public void Every_shipped_shot_style_lands_in_a_clause_built_to_hold_it()
    {
        // A label, because no preposition reads for both "45-degree angle" and "dark and moody".
        foreach (var style in new PhotographyStyleCatalog().All)
        {
            Assert.Contains($"Shot: {style.DisplayName}.", Description(shot: (style.Key, style.DisplayName)));
        }
    }

    [Fact]
    public void Every_method_name_lands_in_a_clause_built_to_hold_it()
    {
        // Methods are verbs in noun form — Bake, Stir-Fry, Water-Bath Canning — so they need "using the ... method"
        // rather than being dropped in as a bare noun.
        foreach (var name in new[] { "Bake", "Stir-Fry", "No-Cook", "Water-Bath Canning", "Sous-Vide" })
        {
            Assert.Contains($"using the {name.ToLowerInvariant()} method", Description(method: ("m", name, false)));
        }
    }

    [Fact]
    public void A_method_carrying_a_caution_says_so_in_the_sentence()
    {
        var description = Description(
            dishType: ("dessert", "Dessert"),
            method: ("pressure-canning", "Pressure Canning", true));

        Assert.Contains("Follow tested, authoritative guidance for this method", description);
        Assert.Contains("food-safety outcome", description);

        // It points at guidance; it never says the method is safe, nor that following guidance makes it so.
        Assert.DoesNotContain("safe ", description);
        Assert.DoesNotContain("safely", description);
    }

    [Fact]
    public void A_method_with_no_caution_attached_gets_no_caution_text()
    {
        // And nothing is said in the other direction either: no caution is not a statement that a method is safe.
        var description = Description(method: ("bake", "Bake", false));

        Assert.DoesNotContain("Follow tested", description);
        Assert.DoesNotContain("safe", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_theme_replaces_the_bare_day_and_a_channel_closes_the_line()
    {
        Assert.EndsWith(
            "Publish for Meat-free Monday on Instagram.",
            Description(day: DayOfWeek.Monday, theme: ("mfm", "Meat-free Monday"), channel: ("instagram", "Instagram")));

        Assert.EndsWith("Publish on Monday.", Description(day: DayOfWeek.Monday));
    }

    [Fact]
    public void Proper_nouns_keep_their_capitals_and_common_nouns_do_not()
    {
        var description = Description(
            cuisine: ("tex-mex", "Tex-Mex"),
            dishType: ("main-course", "Main Course"),
            channel: ("x", "X"));

        // A cuisine leads the subject and a channel follows "on", so both keep their case; the dish type does not.
        Assert.Contains("Develop a Tex-Mex main course", description);
        Assert.EndsWith("on X.", description);
    }

    [Fact]
    public void The_description_adds_no_fact_that_is_not_a_facet()
    {
        // No adjectives and no verdicts: a sentence about a dish nobody has cooked cannot praise it.
        var description = Description(
            cuisine: ("thai", "Thai"),
            dishType: ("dessert", "Dessert"),
            method: ("bake", "Bake", false),
            occasion: ("weeknight", "Weeknight"));

        foreach (var word in new[] { "delicious", "easy", "healthy", "quick", "simple", "best", "perfect", "authentic" })
        {
            Assert.DoesNotContain(word, description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_seed_built_around_a_typed_subject_proposes_the_facets_rather_than_claiming_them()
    {
        var description = Description(
            cuisine: ("thai", "Thai"),
            dishType: ("main-course", "Main Course"),
            method: ("stir-fry", "Stir-fry", false),
            day: DayOfWeek.Tuesday,
            subjectName: "Miso Butter Corn");

        // "Develop X as a Y", not "Plan a post about X, a Y": there is no recipe behind the name, so the cuisine
        // and the dish type are the token's suggestions and the line must not state them as facts about it.
        Assert.Equal(
            "Develop \"Miso Butter Corn\" as a Thai main course using the stir-fry method. Publish on Tuesday.",
            description);
    }

    [Theory]
    [InlineData(false, false, "Develop \"Miso Butter Corn\". Publish on Monday.")]
    [InlineData(true, false, "Develop \"Miso Butter Corn\" as a main course. Publish on Monday.")]
    [InlineData(false, true, "Develop \"Miso Butter Corn\" using the stir-fry method. Publish on Monday.")]
    public void A_typed_subject_reads_with_the_suggestions_missing(bool hasDishType, bool hasMethod, string expected) =>
        Assert.Equal(
            expected,
            Description(
                dishType: hasDishType ? ("main-course", "Main Course") : null,
                method: hasMethod ? ("stir-fry", "Stir-fry", false) : null,
                subjectName: "Miso Butter Corn"));

    [Fact]
    public void A_recipe_wins_over_a_typed_subject_in_the_line()
    {
        // Business never sends both, and the request refuses both — this is the backstop saying which the line
        // would name if one ever arrived that way, so the answer cannot be "whichever branch came first".
        var description = Description(recipeTitle: "Nan's Lemon Tart", subjectName: "Miso Butter Corn");

        Assert.Contains("Nan's Lemon Tart", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Miso Butter Corn", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_subject_is_quoted_verbatim_and_a_caution_still_travels()
    {
        var description = Description(
            method: ("pressure-canning", "Pressure canning", true),
            subjectName: "  Gran's \"Famous\" Pickles  ");

        // Verbatim: the creator's words are not re-spaced, re-cased or stripped of their own quotation marks.
        // Normalizing is Business's job before the line is written, not the line's.
        Assert.Contains("\"  Gran's \"Famous\" Pickles  \"", description, StringComparison.Ordinal);
        Assert.Contains("Follow tested, authoritative guidance for this method", description, StringComparison.Ordinal);
    }
}
