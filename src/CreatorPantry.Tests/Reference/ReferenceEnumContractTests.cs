using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using System.Reflection;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Pins the persisted numeric value of every reference enum, and guards the vocabulary of every reference
/// enum and entity against members that would read as a safety guarantee.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the integers matter more than they look.</strong> The food-safety rules of Phase 4 are check
/// constraints, and those constraints are <em>numbers</em> in the applied migration —
/// <c>NOT (Presence = 30 AND ReferenceSourceKind IN (4, 5))</c> in
/// <c>20260923101536_AddDietaryAndAllergenTraits</c>. The EF configuration regenerates the same constraint
/// from the enum at model-build time, and every test in this suite builds its schema from the model. So
/// renumbering a member keeps the whole suite green while the deployed database goes on enforcing a number
/// that no longer means anything — and "an unvetted source may not assert allergen absence" quietly stops
/// being true.
/// </para>
/// <para>
/// Several of these enums carry a "do not renumber" comment. This file is what makes that comment enforceable.
/// </para>
/// </remarks>
public sealed class ReferenceEnumContractTests
{
    /// <summary>
    /// Every enum whose values reach the database, with the members and numbers the shipped constraints and
    /// stored rows depend on. A changed number, a removed member, or an inserted one fails here.
    /// </summary>
    public static TheoryData<Type, Dictionary<string, int>> PersistedEnums() =>
        new()
        {
            {
                typeof(AllergenPresence),
                new()
                {
                    [nameof(AllergenPresence.Unknown)] = 0,
                    [nameof(AllergenPresence.Present)] = 10,
                    [nameof(AllergenPresence.PossiblePresence)] = 20,
                    [nameof(AllergenPresence.NotListedBySource)] = 30,
                }
            },
            {
                typeof(DietaryCompatibility),
                new()
                {
                    [nameof(DietaryCompatibility.Unknown)] = 0,
                    [nameof(DietaryCompatibility.Compatible)] = 10,
                    [nameof(DietaryCompatibility.Incompatible)] = 20,
                    [nameof(DietaryCompatibility.DependsOnProduct)] = 30,
                }
            },
            {
                typeof(TraitReviewStatus),
                new()
                {
                    [nameof(TraitReviewStatus.Unreviewed)] = 0,
                    [nameof(TraitReviewStatus.Approved)] = 10,
                    [nameof(TraitReviewStatus.Rejected)] = 20,
                    [nameof(TraitReviewStatus.Superseded)] = 30,
                }
            },
            {
                typeof(DensityReviewStatus),
                new()
                {
                    [nameof(DensityReviewStatus.Unreviewed)] = 0,
                    [nameof(DensityReviewStatus.Approved)] = 10,
                    [nameof(DensityReviewStatus.Rejected)] = 20,
                    [nameof(DensityReviewStatus.Superseded)] = 30,
                }
            },
            {
                typeof(ReferenceSourceKind),
                new()
                {
                    [nameof(ReferenceSourceKind.OfficialDatabase)] = 0,
                    [nameof(ReferenceSourceKind.Publisher)] = 1,
                    [nameof(ReferenceSourceKind.Manufacturer)] = 2,
                    [nameof(ReferenceSourceKind.LabMeasured)] = 3,
                    [nameof(ReferenceSourceKind.CommunityContributed)] = 4,
                    [nameof(ReferenceSourceKind.AiEstimated)] = 5,
                }
            },
            {
                typeof(MeasurementDimension),
                new()
                {
                    [nameof(MeasurementDimension.Mass)] = 0,
                    [nameof(MeasurementDimension.Volume)] = 1,
                    [nameof(MeasurementDimension.Count)] = 2,
                    [nameof(MeasurementDimension.Temperature)] = 3,
                    [nameof(MeasurementDimension.Qualitative)] = 4,
                }
            },
            {
                typeof(MeasurementSystem),
                new()
                {
                    [nameof(MeasurementSystem.Neutral)] = 0,
                    [nameof(MeasurementSystem.Metric)] = 1,
                    [nameof(MeasurementSystem.UsCustomary)] = 2,
                    [nameof(MeasurementSystem.Imperial)] = 3,
                }
            },
        };

    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void A_persisted_enum_keeps_its_members_and_their_numbers(Type enumType, Dictionary<string, int> expected)
    {
        var actual = Enum.GetNames(enumType)
            .ToDictionary(name => name, name => (int)Enum.Parse(enumType, name));

        Assert.Equal(expected.OrderBy(entry => entry.Key), actual.OrderBy(entry => entry.Key));
    }

    /// <summary>
    /// The constraint SQL the migration shipped, restated as the numbers it contains. If this and the theory
    /// above ever disagree, the database is enforcing something the code no longer means.
    /// </summary>
    [Fact]
    public void The_shipped_allergen_constraint_numbers_still_mean_what_they_did()
    {
        // "NOT (Presence = 30 AND ReferenceSourceKind IN (4, 5))"
        Assert.Equal(30, (int)AllergenPresence.NotListedBySource);
        Assert.Equal(4, (int)ReferenceSourceKind.CommunityContributed);
        Assert.Equal(5, (int)ReferenceSourceKind.AiEstimated);

        // "NOT (ReferenceSourceKind = 5 AND ReviewStatus = 10)"
        Assert.Equal(10, (int)TraitReviewStatus.Approved);
        Assert.Equal(10, (int)DensityReviewStatus.Approved);

        // "Presence NOT IN (20, 30) OR EvidenceNote <> ''"
        Assert.Equal(20, (int)AllergenPresence.PossiblePresence);

        // "Compatibility NOT IN (30) OR EvidenceNote <> ''"
        Assert.Equal(30, (int)DietaryCompatibility.DependsOnProduct);

        // "(Dimension IN (0, 1, 2) AND BaseUnitFactor IS NOT NULL) OR (Dimension IN (3, 4) AND ...)"
        Assert.Equal([0, 1, 2], new[] { MeasurementDimension.Mass, MeasurementDimension.Volume, MeasurementDimension.Count }.Select(d => (int)d));
        Assert.Equal([3, 4], new[] { MeasurementDimension.Temperature, MeasurementDimension.Qualitative }.Select(d => (int)d));
    }

    /// <summary>
    /// The policy predicates that generate constraint SQL must keep agreeing with the enum they read. These
    /// are the rules in their executable form; the numbers above are the same rules as the database holds them.
    /// </summary>
    [Fact]
    public void The_policy_predicates_still_classify_the_same_source_kinds()
    {
        Assert.False(TraitPolicy.MayAssertAllergenAbsence(ReferenceSourceKind.CommunityContributed));
        Assert.False(TraitPolicy.MayAssertAllergenAbsence(ReferenceSourceKind.AiEstimated));
        Assert.All(
            new[]
            {
                ReferenceSourceKind.OfficialDatabase, ReferenceSourceKind.Publisher,
                ReferenceSourceKind.Manufacturer, ReferenceSourceKind.LabMeasured,
            },
            kind => Assert.True(TraitPolicy.MayAssertAllergenAbsence(kind)));

        Assert.True(TraitPolicy.RequiresEvidenceNote(AllergenPresence.PossiblePresence));
        Assert.True(TraitPolicy.RequiresEvidenceNote(AllergenPresence.NotListedBySource));
        Assert.False(TraitPolicy.RequiresEvidenceNote(AllergenPresence.Present));
        Assert.False(TraitPolicy.RequiresEvidenceNote(AllergenPresence.Unknown));
    }

    // --- Vocabulary guards -------------------------------------------------------------------------------

    /// <summary>
    /// No reference enum may gain a member that reads as a safety guarantee.
    /// </summary>
    /// <remarks>
    /// <see cref="AllergenPresence"/> already has a dedicated guard; this extends the same rule to the enums
    /// that did not. <see cref="DietaryCompatibility"/> is the one that matters most — its remarks explain
    /// that a positive claim is meaningful only about <em>composition</em>, and a member named
    /// <c>CertifiedGlutenFree</c> or <c>SafeFor…</c> would quietly turn it into a suitability ruling.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PersistedEnums))]
    public void No_reference_enum_member_reads_as_a_guarantee(Type enumType, Dictionary<string, int> expected)
    {
        _ = expected;

        var offending = Enum.GetNames(enumType).Where(ReadsAsGuarantee).ToList();

        Assert.True(
            offending.Count == 0,
            $"{enumType.Name} has member(s) that read as a safety guarantee: {string.Join(", ", offending)}");
    }

    /// <summary>
    /// No reference entity may carry a boolean, a confidence scalar, or a property whose name reads as a
    /// guarantee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trait tables already had this guard. It did not cover the other six reference entities, and
    /// <see cref="Ingredient"/> is the most natural place for someone to add <c>IsGlutenFree</c> and the worst
    /// place for it to be: a missing row would default to a confident <c>false</c>, which is precisely the
    /// inference recipes.md forbids.
    /// </para>
    /// <para>
    /// <c>IsActive</c> is allowed by name because it is a catalogue-lifecycle flag, not a claim about food.
    /// Nothing else is.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ReferenceEntities))]
    public void No_reference_entity_carries_a_guarantee_shaped_property(Type entityType)
    {
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var unexpectedBooleans = properties
            .Where(property => property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
            .Select(property => property.Name)
            .Where(name => name is not (nameof(Ingredient.IsActive) or "RequiresSafetyCaution"))
            .ToList();

        Assert.True(
            unexpectedBooleans.Count == 0,
            $"{entityType.Name} carries boolean(s) outside the allow-list: {string.Join(", ", unexpectedBooleans)}. "
                + "A boolean about food reads as a guarantee and defaults to a confident answer when unset.");

        var guaranteeShaped = properties.Select(property => property.Name).Where(ReadsAsGuarantee).ToList();

        Assert.True(
            guaranteeShaped.Count == 0,
            $"{entityType.Name} has propert(ies) that read as a guarantee or a confidence score: "
                + string.Join(", ", guaranteeShaped));
    }

    public static TheoryData<Type> ReferenceEntities() =>
    [
        typeof(Ingredient), typeof(IngredientAlias), typeof(IngredientDensityReference),
        typeof(IngredientAllergenTrait), typeof(IngredientDietaryTrait),
        typeof(Allergen), typeof(DietaryProfile), typeof(ReferenceSource),
        typeof(MeasurementUnit), typeof(UnitAlias), typeof(FoodCategory),
        typeof(Cuisine), typeof(Course), typeof(CookingTechnique), typeof(EquipmentType),
    ];

    /// <summary>
    /// Names that would be read as a safety, certification, or confidence claim by whatever renders them.
    /// </summary>
    /// <remarks>
    /// <c>RequiresSafetyCaution</c> is the one deliberate use of "safety" in the catalogue and it points the
    /// other way — it demands a caution rather than promising its absence — so it is excluded by name.
    /// </remarks>
    private static bool ReadsAsGuarantee(string name) =>
        name != "RequiresSafetyCaution"
        && new[] { "Safe", "Free", "Certif", "Confidence", "Score", "Probability", "Guarantee", "Verified" }
            .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
