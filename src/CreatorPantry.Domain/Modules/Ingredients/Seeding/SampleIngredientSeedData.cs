using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

namespace CreatorPantry.Domain.Modules.Ingredients.Seeding;

/// <summary>
/// A compact ingredient set with illustrative densities, dietary traits, and allergen traits. Tier B — seeded
/// outside Production only.
/// </summary>
/// <remarks>
/// <para>
/// Every fact in this file cites one source, <see cref="SourceCode"/>, whose citation says plainly that it is
/// development seed data and not a vetted reference. No figure here is attributed to USDA, to a publisher, or
/// to anyone else: inventing a citation would be worse than carrying none, so these are labelled as what they
/// are and nothing more.
/// </para>
/// <para>
/// That provenance is load-bearing rather than decorative. Because the source is
/// <see cref="ReferenceSourceKind.CommunityContributed"/>,
/// <c>CK_IngredientAllergenTraits_Absence_RequiresVettedSource</c> makes it <em>impossible</em> for this seed
/// set to record <see cref="AllergenPresence.NotListedBySource"/> — the database rejects the insert rather
/// than trusting this file to be careful. And because every density and trait is left
/// <c>Unreviewed</c>, nothing seeded here is in the one state an analysis will act on.
/// </para>
/// <para>
/// The set is chosen to exercise every modelled relationship — a default count unit, a US/UK alias divergence,
/// one ingredient with two densities under different conditions, both evidence-note constraints — rather than
/// to be a usable pantry. Real reference data arrives later through a cited import, not through a seeder.
/// </para>
/// </remarks>
internal static class SampleIngredientSeedData
{
    private const string SourceTable = "ReferenceSources";
    private const string IngredientTable = "Ingredients";
    private const string IngredientAliasTable = "IngredientAliases";
    private const string DensityTable = "IngredientDensityReferences";
    private const string DietaryTraitTable = "IngredientDietaryTraits";
    private const string AllergenTraitTable = "IngredientAllergenTraits";

    private const string SourceCode = "creatorpantry-dev-seed";

    private const ReferenceSourceKind SourceKind = ReferenceSourceKind.CommunityContributed;

    /// <summary>
    /// Fixed rather than "today". The effective date is part of every trait's and density's natural key, so a
    /// clock reading here would give the same fact a different identity on every machine and turn the second
    /// seeding run into an insert.
    /// </summary>
    private static readonly DateOnly EffectiveFrom = new(2026, 1, 1);

    private static Guid SourceId => SeedId.For(SourceTable, SourceCode);

    private static Guid IngredientId(string canonicalName) =>
        SeedId.For(IngredientTable, NameNormalization.NormalizeName(canonicalName));

    public static IReadOnlyList<ReferenceSource> Sources() =>
    [
        new()
        {
            Id = SourceId,
            Code = SourceCode,
            Name = "CreatorPantry development seed data",
            Kind = SourceKind,
            Url = null,
            Citation =
                "CreatorPantry development seed data. Illustrative figures for local development and automated tests. "
                    + "Not a vetted reference, not a nutrition or food-safety source, and not suitable for production use.",
            IsActive = true,
        },
    ];

    public static IReadOnlyList<Ingredient> Ingredients() =>
    [
        .. IngredientDefinitions.Select(definition =>
        {
            var normalizedName = NameNormalization.NormalizeName(definition.CanonicalName);

            return new Ingredient
            {
                Id = IngredientId(definition.CanonicalName),
                CanonicalName = definition.CanonicalName,
                NormalizedName = normalizedName,
                SearchText = NameNormalization.BuildSearchText(
                    normalizedName,
                    definition.Aliases.Select(NameNormalization.NormalizeName)),
                FoodCategoryId = CatalogueSeedData.FoodCategoryId(definition.CategoryCode),
                DefaultCountUnitId = definition.CountUnitCode is null
                    ? null
                    : MeasurementSeedData.UnitId(definition.CountUnitCode),
                DefaultCountUnitDimension = definition.CountUnitCode is null
                    ? null
                    : MeasurementDimension.Count,
                IsActive = true,
            };
        }),
    ];

    public static IReadOnlyList<IngredientAlias> IngredientAliases() =>
    [
        .. IngredientDefinitions.SelectMany(definition => definition.Aliases.Select(alias => new IngredientAlias
        {
            Id = SeedId.For(IngredientAliasTable, NameNormalization.NormalizeName(alias)),
            IngredientId = IngredientId(definition.CanonicalName),
            Alias = alias,
            NormalizedAlias = NameNormalization.NormalizeName(alias),
        })),
    ];

    public static IReadOnlyList<IngredientDensityReference> Densities() =>
    [
        .. DensityDefinitions.Select(definition => new IngredientDensityReference
        {
            Id = SeedId.For(
                DensityTable,
                NameNormalization.NormalizeName(definition.Ingredient),
                DensityPolicy.NormalizeCondition(definition.Condition),
                SourceCode,
                EffectiveFrom.ToString("O")),
            IngredientId = IngredientId(definition.Ingredient),
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            MassQuantity = definition.Grams,
            MassUnitId = MeasurementSeedData.UnitId("g"),
            MassUnitDimension = MeasurementDimension.Mass,
            VolumeQuantity = 1m,
            VolumeUnitId = MeasurementSeedData.UnitId("cup-us"),
            VolumeUnitDimension = MeasurementDimension.Volume,
            ConditionNote = definition.Condition,
            NormalizedCondition = DensityPolicy.NormalizeCondition(definition.Condition),
            DisplayPrecision = 0,
            EffectiveFrom = EffectiveFrom,
            ReviewStatus = DensityReviewStatus.Unreviewed,
        }),
    ];

    public static IReadOnlyList<IngredientDietaryTrait> DietaryTraits() =>
    [
        .. DietaryTraitDefinitions.Select(definition => new IngredientDietaryTrait
        {
            Id = SeedId.For(
                DietaryTraitTable,
                NameNormalization.NormalizeName(definition.Ingredient),
                definition.Profile,
                SourceCode,
                EffectiveFrom.ToString("O")),
            IngredientId = IngredientId(definition.Ingredient),
            DietaryProfileId = CatalogueSeedData.DietaryProfileId(definition.Profile),
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            Compatibility = definition.Compatibility,
            EvidenceNote = definition.Evidence,
            EffectiveFrom = EffectiveFrom,
            ReviewStatus = TraitReviewStatus.Unreviewed,
        }),
    ];

    public static IReadOnlyList<IngredientAllergenTrait> AllergenTraits() =>
    [
        .. AllergenTraitDefinitions.Select(definition => new IngredientAllergenTrait
        {
            Id = SeedId.For(
                AllergenTraitTable,
                NameNormalization.NormalizeName(definition.Ingredient),
                definition.Allergen,
                SourceCode,
                EffectiveFrom.ToString("O")),
            IngredientId = IngredientId(definition.Ingredient),
            AllergenId = CatalogueSeedData.AllergenId(definition.Allergen),
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            Presence = definition.Presence,
            EvidenceNote = definition.Evidence,
            EffectiveFrom = EffectiveFrom,
            ReviewStatus = TraitReviewStatus.Unreviewed,
        }),
    ];

    private sealed record IngredientDefinition(
        string CanonicalName,
        string CategoryCode,
        string? CountUnitCode,
        string[] Aliases);

    private sealed record DensityDefinition(string Ingredient, decimal Grams, string Condition);

    private sealed record DietaryTraitDefinition(
        string Ingredient,
        string Profile,
        DietaryCompatibility Compatibility,
        string Evidence = TraitPolicy.NoEvidenceNote);

    private sealed record AllergenTraitDefinition(
        string Ingredient,
        string Allergen,
        AllergenPresence Presence,
        string Evidence = TraitPolicy.NoEvidenceNote);

    /// <remarks>
    /// Aliases are alternative names for the same thing, never narrower or richer ones. "Double cream" is not
    /// seeded against heavy cream and "caster sugar" is not seeded against granulated sugar, because they
    /// differ in fat content and in grind — matching them would quietly change a recipe's meaning, which is
    /// exactly what recipes.md forbids an ingredient reference from doing.
    /// </remarks>
    private static readonly IngredientDefinition[] IngredientDefinitions =
    [
        new("all-purpose flour", "baking", null, ["plain flour", "AP flour", "white flour"]),
        new("bread flour", "baking", null, ["strong flour", "strong white flour"]),
        new("cornstarch", "pantry-staples", null, ["cornflour"]),
        new("granulated sugar", "sweeteners", null, ["white sugar", "granulated white sugar"]),
        new("brown sugar", "sweeteners", null, ["soft brown sugar"]),
        new("honey", "sweeteners", null, []),
        new("unsalted butter", "dairy-and-eggs", null, ["sweet butter"]),
        new("large egg", "dairy-and-eggs", "each", ["eggs", "hen egg"]),
        new("whole milk", "dairy-and-eggs", null, ["full-fat milk", "full cream milk"]),
        new("heavy cream", "dairy-and-eggs", null, ["heavy whipping cream"]),
        new("kosher salt", "pantry-staples", null, []),
        new("fine sea salt", "pantry-staples", null, ["fine salt"]),
        new("black pepper", "herbs-and-spices", null, ["ground black pepper", "cracked black pepper"]),
        new("olive oil", "oils-and-fats", null, []),
        new("garlic", "produce", "clove", ["garlic cloves"]),
        new("yellow onion", "produce", "each", ["brown onion"]),
        new("green onion", "produce", "each", ["scallion", "scallions", "spring onion"]),
        new("lemon", "produce", "each", ["lemons"]),
        new("tomato", "produce", "each", ["tomatoes"]),
        new("carrot", "produce", "each", ["carrots"]),
        new("boneless skinless chicken breast", "meat-and-poultry", "each", ["chicken breast"]),
        new("long-grain white rice", "grains-and-pasta", null, ["white rice"]),
        new("rolled oats", "grains-and-pasta", null, ["old-fashioned oats", "porridge oats"]),
        new("peanut butter", "nuts-and-seeds", null, []),
        new("almonds", "nuts-and-seeds", null, ["whole almonds"]),
        new("soy sauce", "condiments-and-sauces", null, ["shoyu"]),
        new("water", "pantry-staples", null, []),
    ];

    /// <remarks>
    /// All measured against one US cup, all illustrative. All-purpose flour carries two rows because the
    /// condition is part of the measurement rather than a footnote: spooning and scooping the same cup differ
    /// by enough to matter in a bake, which is the whole reason <c>NormalizedCondition</c> is in the natural
    /// key.
    /// </remarks>
    private static readonly DensityDefinition[] DensityDefinitions =
    [
        new("all-purpose flour", 120m, "spooned and leveled"),
        new("all-purpose flour", 125m, "scooped and leveled"),
        new("bread flour", 127m, "spooned and leveled"),
        new("cornstarch", 120m, "spooned and leveled"),
        new("granulated sugar", 200m, DensityPolicy.UnspecifiedCondition),
        new("brown sugar", 220m, "packed"),
        new("honey", 340m, DensityPolicy.UnspecifiedCondition),
        new("unsalted butter", 227m, DensityPolicy.UnspecifiedCondition),
        new("whole milk", 244m, "at 20 °C"),
        new("water", 236m, "at 20 °C"),
        new("rolled oats", 90m, DensityPolicy.UnspecifiedCondition),
        new("olive oil", 216m, DensityPolicy.UnspecifiedCondition),
    ];

    /// <remarks>
    /// <para>
    /// No row here claims an ingredient is <em>compatible</em> with <c>gluten-free</c>, <c>dairy-free</c>, or
    /// <c>egg-free</c>, and none may be added. <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.IngredientDietaryTrait"/> has no counterpart to
    /// <c>CK_IngredientAllergenTraits_Absence_RequiresVettedSource</c>, on the stated ground that a community
    /// claim about an ingredient being vegan is an ordinary unvetted fact. That holds for vegan. It does not
    /// hold for the three allergen-adjacent profiles: "gluten-free" from an unvetted source is an
    /// allergen-absence claim wearing an editorial tag, and it is exactly what that constraint exists to make
    /// unstorable on the allergen side.
    /// </para>
    /// <para>
    /// <see cref="DietaryCompatibility.Incompatible"/> against those profiles is the safe direction and is
    /// seeded freely — saying a food contains gluten costs a creator an ingredient, not a reader's health.
    /// </para>
    /// </remarks>
    private static readonly DietaryTraitDefinition[] DietaryTraitDefinitions =
    [
        new("honey", "vegan", DietaryCompatibility.Incompatible),
        new("honey", "vegetarian", DietaryCompatibility.Compatible),
        new("large egg", "vegan", DietaryCompatibility.Incompatible),
        new("large egg", "vegetarian", DietaryCompatibility.Compatible),
        new("large egg", "egg-free", DietaryCompatibility.Incompatible),
        new("whole milk", "vegan", DietaryCompatibility.Incompatible),
        new("whole milk", "vegetarian", DietaryCompatibility.Compatible),
        new("whole milk", "dairy-free", DietaryCompatibility.Incompatible),
        new("unsalted butter", "vegan", DietaryCompatibility.Incompatible),
        new("unsalted butter", "dairy-free", DietaryCompatibility.Incompatible),
        new("all-purpose flour", "vegan", DietaryCompatibility.Compatible),
        new("all-purpose flour", "gluten-free", DietaryCompatibility.Incompatible),
        new("bread flour", "gluten-free", DietaryCompatibility.Incompatible),
        new("olive oil", "vegan", DietaryCompatibility.Compatible),
        new("almonds", "vegan", DietaryCompatibility.Compatible),
        new("boneless skinless chicken breast", "vegetarian", DietaryCompatibility.Incompatible),
        new("boneless skinless chicken breast", "pescatarian", DietaryCompatibility.Incompatible),

        // The two that exercise CK_IngredientDietaryTraits_EvidenceNote_Required: a trait saying "it depends"
        // is useless, and quietly alarming, unless it says what it depends on.
        new("granulated sugar", "vegan", DietaryCompatibility.DependsOnProduct,
            "Some cane sugar is refined using bone char. Beet sugar and certified vegan cane sugar are not. Which "
                + "applies depends on the producer, and is not determinable from the ingredient name."),
        new("soy sauce", "gluten-free", DietaryCompatibility.DependsOnProduct,
            "Most soy sauce is brewed with wheat. Tamari and products labelled gluten-free may not be. Check the "
                + "product label."),
    ];

    /// <remarks>
    /// Every row is <see cref="AllergenPresence.Present"/> or
    /// <see cref="AllergenPresence.PossiblePresence"/>. There is no absence claim, and there could not be: the
    /// source kind forbids one at the database level. The two <c>PossiblePresence</c> rows carry the evidence
    /// note <c>CK_IngredientAllergenTraits_EvidenceNote_Required</c> demands, because a bare "may contain"
    /// reads as a warning without saying whose.
    /// </remarks>
    private static readonly AllergenTraitDefinition[] AllergenTraitDefinitions =
    [
        new("whole milk", "milk", AllergenPresence.Present),
        new("heavy cream", "milk", AllergenPresence.Present),
        new("unsalted butter", "milk", AllergenPresence.Present),
        new("large egg", "egg", AllergenPresence.Present),
        new("all-purpose flour", "wheat", AllergenPresence.Present),
        new("bread flour", "wheat", AllergenPresence.Present),
        new("peanut butter", "peanut", AllergenPresence.Present),
        new("almonds", "tree-nuts", AllergenPresence.Present),
        new("soy sauce", "soy", AllergenPresence.Present),

        new("soy sauce", "wheat", AllergenPresence.PossiblePresence,
            "Most soy sauce is brewed with wheat, and tamari varieties may not be. What a given bottle contains "
                + "depends on the product; check the label."),
        new("rolled oats", "wheat", AllergenPresence.PossiblePresence,
            "Oats are commonly grown, transported, and milled alongside wheat. Some products state a dedicated "
                + "facility; check the product label."),
    ];
}
