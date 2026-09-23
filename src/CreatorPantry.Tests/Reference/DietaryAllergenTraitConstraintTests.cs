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
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CreatorPantry.Tests.Reference.ReferenceConstraintAssertions;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Exercises the EF configuration for <see cref="DietaryProfile"/>, <see cref="Allergen"/>,
/// <see cref="IngredientDietaryTrait"/>, and <see cref="IngredientAllergenTrait"/> against an in-memory SQLite
/// database, verifying the rules the <c>AddDietaryAndAllergenTraits</c> migration also creates without
/// applying it anywhere.
/// </summary>
/// <remarks>
/// The tests that matter most here are the ones about nothing: that an ingredient with no recorded trait
/// yields no evidence in either direction, and that an explicitly recorded <see cref="AllergenPresence.Unknown"/>
/// is not quietly an absence claim. Missing trait data means unknown, never safe (recipes.md).
/// </remarks>
public sealed class DietaryAllergenTraitConstraintTests : IDisposable
{
    private static readonly DateOnly Effective = new(2026, 1, 15);

    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    // --- Global reference data, not workspace-owned ------------------------------------------------------

    [Fact]
    public void Trait_entities_carry_no_workspace_id()
    {
        using var scope = _services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        foreach (var type in new[]
            {
                typeof(DietaryProfile), typeof(Allergen),
                typeof(IngredientDietaryTrait), typeof(IngredientAllergenTrait),
            })
        {
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
            Assert.Null(model.FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId)));
        }
    }

    [Fact]
    public async Task Traits_are_readable_without_a_resolved_workspace()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Present));
        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Incompatible));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.IngredientAllergenTraits.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.IngredientDietaryTraits.ToListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Unknown versus positive versus negative evidence ------------------------------------------------

    /// <summary>
    /// The headline rule. An ingredient the vocabulary knows, an allergen the vocabulary knows, and nothing
    /// recorded between them: the only available reading is unknown. There is no row to inspect, no column
    /// defaulting to absent, and nothing a caller could mistake for a clearance.
    /// </summary>
    [Fact]
    public async Task An_ingredient_with_no_trait_row_has_no_evidence_in_either_direction()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        var allergenEvidence = await db.IngredientAllergenTraits
            .Where(trait => trait.IngredientId == world.IngredientId && trait.AllergenId == world.AllergenId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var dietaryEvidence = await db.IngredientDietaryTraits
            .Where(trait => trait.IngredientId == world.IngredientId && trait.DietaryProfileId == world.ProfileId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(allergenEvidence);
        Assert.Empty(dietaryEvidence);
    }

    /// <summary>
    /// A recorded <see cref="AllergenPresence.Unknown"/> tells a reviewer that somebody looked. It tells an
    /// analysis exactly what no row tells it: nothing. Both must fail the same absence query.
    /// </summary>
    [Fact]
    public async Task An_explicitly_recorded_unknown_is_not_evidence_of_absence()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.Unknown);
        trait.ReviewStatus = TraitReviewStatus.Approved;
        db.IngredientAllergenTraits.Add(trait);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var recorded = await db.IngredientAllergenTraits.SingleAsync(TestContext.Current.CancellationToken);
        var absenceEvidence = await db.IngredientAllergenTraits
            .Where(candidate => candidate.Presence == AllergenPresence.NotListedBySource)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AllergenPresence.Unknown, recorded.Presence);
        Assert.Empty(absenceEvidence);
    }

    [Fact]
    public async Task Positive_evidence_is_recorded_as_present()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.ReviewStatus = TraitReviewStatus.Approved;

        db.IngredientAllergenTraits.Add(trait);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        Assert.Equal(
            AllergenPresence.Present,
            (await db.IngredientAllergenTraits.SingleAsync(TestContext.Current.CancellationToken)).Presence);
    }

    /// <summary>
    /// The strongest negative evidence the model can hold, stored with the note that keeps it honest. What is
    /// read back still says only that a source did not list the allergen — the vocabulary has no member that
    /// would let this be read as free from.
    /// </summary>
    [Fact]
    public async Task Negative_evidence_is_recorded_as_not_listed_by_its_source()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.NotListedBySource, "published composition, 2026-03, no milk derivatives");
        trait.ReviewStatus = TraitReviewStatus.Approved;

        db.IngredientAllergenTraits.Add(trait);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var recorded = await db.IngredientAllergenTraits.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AllergenPresence.NotListedBySource, recorded.Presence);
        Assert.NotEqual(TraitPolicy.NoEvidenceNote, recorded.EvidenceNote);
    }

    [Theory]
    [InlineData(DietaryCompatibility.Unknown, TraitPolicy.NoEvidenceNote)]
    [InlineData(DietaryCompatibility.Compatible, TraitPolicy.NoEvidenceNote)]
    [InlineData(DietaryCompatibility.Incompatible, TraitPolicy.NoEvidenceNote)]
    [InlineData(DietaryCompatibility.DependsOnProduct, "refined with bone char by some producers")]
    public async Task Every_dietary_state_round_trips(DietaryCompatibility compatibility, string note)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientDietaryTraits.Add(world.DietaryTrait(compatibility, note));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        Assert.Equal(
            compatibility,
            (await db.IngredientDietaryTraits.SingleAsync(TestContext.Current.CancellationToken)).Compatibility);
    }

    // --- There is nowhere to put a safety guarantee -----------------------------------------------------

    /// <summary>
    /// A standing guard rather than a behavior test. An <c>IsAllergenFree</c> or <c>CertifiedSafe</c> column
    /// would be read as a guarantee by every screen that touched it, and a missing row would default it to a
    /// confident <c>false</c>. Adding one to either trait breaks this test, which is the point.
    /// </summary>
    [Fact]
    public void Trait_entities_expose_no_boolean_property()
    {
        foreach (var type in new[] { typeof(IngredientAllergenTrait), typeof(IngredientDietaryTrait) })
        {
            var booleans = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
                .Select(property => $"{type.Name}.{property.Name}");

            Assert.Empty(booleans);
        }
    }

    /// <summary>
    /// The allergen vocabulary may say what a source stated and never that a food is safe. A member named
    /// <c>Safe</c>, <c>Free</c>, or <c>Certified</c> would be selected eventually, and rendered as a promise.
    /// </summary>
    [Theory]
    [InlineData("safe")]
    [InlineData("free")]
    [InlineData("certif")]
    public void The_allergen_vocabulary_has_no_state_that_reads_as_a_guarantee(string forbidden)
    {
        var offending = Enum.GetNames<AllergenPresence>()
            .Where(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        Assert.Empty(offending);
    }

    // --- Provenance is mandatory ------------------------------------------------------------------------

    [Fact]
    public async Task An_allergen_trait_cannot_exist_without_its_ingredient()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.IngredientId = Guid.NewGuid();

        db.IngredientAllergenTraits.Add(trait);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task An_allergen_trait_cannot_exist_without_its_allergen()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.AllergenId = Guid.NewGuid();

        db.IngredientAllergenTraits.Add(trait);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task An_allergen_trait_cannot_exist_without_a_source()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.ReferenceSourceId = Guid.NewGuid();

        db.IngredientAllergenTraits.Add(trait);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task A_dietary_trait_cannot_exist_without_its_profile()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var trait = world.DietaryTrait(DietaryCompatibility.Compatible);
        trait.DietaryProfileId = Guid.NewGuid();

        db.IngredientDietaryTraits.Add(trait);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>A trait cannot misreport its own provenance to dodge the rules that depend on it.</summary>
    [Fact]
    public async Task A_trait_cannot_claim_a_kind_its_source_does_not_have()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var trait = world.AllergenTrait(AllergenPresence.NotListedBySource, "claimed");
        trait.ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase;

        db.IngredientAllergenTraits.Add(trait);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    // --- A model estimate can never be approved ---------------------------------------------------------

    [Fact]
    public async Task An_ai_estimated_allergen_trait_cannot_be_approved()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.ReviewStatus = TraitReviewStatus.Approved;

        db.IngredientAllergenTraits.Add(trait);

        await AssertRejectedByAsync(db, "CK_IngredientAllergenTraits_AiEstimate_NotApproved");
    }

    [Fact]
    public async Task An_ai_estimated_dietary_trait_cannot_be_approved()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var trait = world.DietaryTrait(DietaryCompatibility.Compatible);
        trait.ReviewStatus = TraitReviewStatus.Approved;

        db.IngredientDietaryTraits.Add(trait);

        await AssertRejectedByAsync(db, "CK_IngredientDietaryTraits_AiEstimate_NotApproved");
    }

    /// <summary>Recording a model's suggestion for review is fine; only promoting it is not.</summary>
    [Theory]
    [InlineData(TraitReviewStatus.Unreviewed)]
    [InlineData(TraitReviewStatus.Rejected)]
    [InlineData(TraitReviewStatus.Superseded)]
    public async Task An_ai_estimated_trait_may_be_recorded_unapproved(TraitReviewStatus status)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var trait = world.AllergenTrait(AllergenPresence.Present);
        trait.ReviewStatus = status;

        db.IngredientAllergenTraits.Add(trait);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            status,
            (await db.IngredientAllergenTraits.SingleAsync(TestContext.Current.CancellationToken)).ReviewStatus);
    }

    // --- Absence claims require a vetted source ---------------------------------------------------------

    /// <summary>
    /// The asymmetric rule. A wrong <see cref="AllergenPresence.Present"/> costs a creator an ingredient; a
    /// wrong absence reaches somebody who is allergic. Hearsay therefore cannot record one at all — not even
    /// unreviewed, waiting for a review process that does not exist yet.
    /// </summary>
    [Theory]
    [InlineData(ReferenceSourceKind.CommunityContributed)]
    [InlineData(ReferenceSourceKind.AiEstimated)]
    public async Task An_unvetted_source_cannot_assert_that_an_allergen_is_absent(ReferenceSourceKind kind)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, kind);

        db.IngredientAllergenTraits.Add(
            world.AllergenTrait(AllergenPresence.NotListedBySource, "not in the ingredient list as I recall"));

        await AssertRejectedByAsync(db, "CK_IngredientAllergenTraits_Absence_RequiresVettedSource");
    }

    [Theory]
    [InlineData(ReferenceSourceKind.OfficialDatabase)]
    [InlineData(ReferenceSourceKind.Publisher)]
    [InlineData(ReferenceSourceKind.Manufacturer)]
    [InlineData(ReferenceSourceKind.LabMeasured)]
    public async Task A_vetted_source_may_assert_that_an_allergen_is_absent(ReferenceSourceKind kind)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, kind);

        db.IngredientAllergenTraits.Add(
            world.AllergenTrait(AllergenPresence.NotListedBySource, "published composition, 2026-03"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.IngredientAllergenTraits.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The restriction runs one way only: an unvetted source reporting that an allergen <em>is</em> present is
    /// exactly the kind of warning worth keeping, whatever its standing.
    /// </summary>
    [Theory]
    [InlineData(ReferenceSourceKind.CommunityContributed, AllergenPresence.Present)]
    [InlineData(ReferenceSourceKind.CommunityContributed, AllergenPresence.PossiblePresence)]
    [InlineData(ReferenceSourceKind.AiEstimated, AllergenPresence.Present)]
    public async Task An_unvetted_source_may_still_report_presence(ReferenceSourceKind kind, AllergenPresence presence)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, kind);

        db.IngredientAllergenTraits.Add(world.AllergenTrait(presence, "reported by a reader"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            presence,
            (await db.IngredientAllergenTraits.SingleAsync(TestContext.Current.CancellationToken)).Presence);
    }

    /// <summary>
    /// And it applies to allergens only. "This brand of sugar is vegan" from a reader is an ordinary unvetted
    /// claim, correctable in review; being wrong about it does not reach an allergic person. The dietary table
    /// deliberately has no counterpart constraint.
    /// </summary>
    [Fact]
    public async Task An_unvetted_source_may_state_dietary_compatibility()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.CommunityContributed);

        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Compatible));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DietaryCompatibility.Compatible,
            (await db.IngredientDietaryTraits.SingleAsync(TestContext.Current.CancellationToken)).Compatibility);
    }

    // --- The states that need to say what the source said ------------------------------------------------

    [Fact]
    public async Task A_cross_contact_claim_without_a_note_is_rejected()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.PossiblePresence));

        await AssertRejectedByAsync(db, "CK_IngredientAllergenTraits_EvidenceNote_Required");
    }

    [Fact]
    public async Task An_absence_claim_without_a_note_is_rejected()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.NotListedBySource));

        await AssertRejectedByAsync(db, "CK_IngredientAllergenTraits_EvidenceNote_Required");
    }

    /// <summary>The states that claim nothing, and the one that needs no defence, carry no such requirement.</summary>
    [Theory]
    [InlineData(AllergenPresence.Unknown)]
    [InlineData(AllergenPresence.Present)]
    public async Task Unknown_and_present_need_no_note(AllergenPresence presence)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientAllergenTraits.Add(world.AllergenTrait(presence));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.IngredientAllergenTraits.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dietary_trait_that_depends_on_the_product_must_say_what_it_depends_on()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.DependsOnProduct));

        await AssertRejectedByAsync(db, "CK_IngredientDietaryTraits_EvidenceNote_Required");
    }

    // --- Uniqueness and supersession --------------------------------------------------------------------

    [Fact]
    public async Task The_same_ingredient_allergen_source_and_date_cannot_repeat()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Present));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Unknown));

        await AssertRejectedByAsync(db, "IngredientAllergenTraits.IngredientId");
    }

    [Fact]
    public async Task The_same_ingredient_profile_source_and_date_cannot_repeat()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Compatible));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Incompatible));

        await AssertRejectedByAsync(db, "IngredientDietaryTraits.IngredientId");
    }

    /// <summary>
    /// Two authorities may contradict each other about the same allergen. The schema records both and leaves
    /// the choice to a later domain decision that can see their provenance — a unique index settling it here
    /// would silently pick whichever was imported first.
    /// </summary>
    [Fact]
    public async Task Two_sources_may_disagree_about_the_same_allergen()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var other = NewSource("manufacturer-x", ReferenceSourceKind.Manufacturer);
        db.ReferenceSources.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var listed = world.AllergenTrait(AllergenPresence.Present);
        var notListed = world.AllergenTrait(AllergenPresence.NotListedBySource, "current label, 2026-03");
        notListed.ReferenceSourceId = other.Id;
        notListed.ReferenceSourceKind = other.Kind;

        db.IngredientAllergenTraits.AddRange(listed, notListed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.IngredientAllergenTraits.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A supplier changes a line and the old claim becomes wrong. That is a second row on a later date plus
    /// <see cref="TraitReviewStatus.Superseded"/> on the first, never an edit that erases what was believed.
    /// </summary>
    [Fact]
    public async Task One_source_may_revise_its_claim_on_a_later_date()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var original = world.AllergenTrait(AllergenPresence.NotListedBySource, "label as published 2026-01");
        original.ReviewStatus = TraitReviewStatus.Superseded;
        var revised = world.AllergenTrait(AllergenPresence.Present);
        revised.EffectiveFrom = Effective.AddMonths(6);
        revised.ReviewStatus = TraitReviewStatus.Approved;

        db.IngredientAllergenTraits.AddRange(original, revised);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.IngredientAllergenTraits.CountAsync(TestContext.Current.CancellationToken));
    }

    // --- Lifecycle --------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_ingredient_removes_its_traits()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Present));
        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Incompatible));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Ingredients.Remove(await db.Ingredients.SingleAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.IngredientAllergenTraits.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.IngredientDietaryTraits.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Retiring a vocabulary entry is <see cref="Allergen.IsActive"/>; deleting one out from under recorded
    /// evidence would leave traits pointing at nothing, so the database refuses.
    /// </summary>
    [Fact]
    public async Task An_allergen_still_cited_by_a_trait_cannot_be_deleted()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Present));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Cleared first: the guarantee under test is the database's. See the same note in
        // IngredientDensityConstraintTests.
        db.ChangeTracker.Clear();
        db.Allergens.Remove(await db.Allergens.SingleAsync(TestContext.Current.CancellationToken));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task A_dietary_profile_still_cited_by_a_trait_cannot_be_deleted()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDietaryTraits.Add(world.DietaryTrait(DietaryCompatibility.Compatible));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        db.DietaryProfiles.Remove(await db.DietaryProfiles.SingleAsync(TestContext.Current.CancellationToken));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task A_source_still_cited_by_a_trait_cannot_be_deleted()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientAllergenTraits.Add(world.AllergenTrait(AllergenPresence.Present));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        db.ReferenceSources.Remove(
            await db.ReferenceSources.SingleAsync(source => source.Id == world.SourceId, TestContext.Current.CancellationToken));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    // --- The vocabularies -------------------------------------------------------------------------------

    [Fact]
    public async Task Two_allergens_cannot_share_a_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Allergens.Add(NewAllergen("milk"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Allergens.Add(NewAllergen("milk"));

        await AssertRejectedByAsync(db, "Allergens.Code");
    }

    [Fact]
    public async Task Two_dietary_profiles_cannot_share_a_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.DietaryProfiles.Add(NewProfile("vegan"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.DietaryProfiles.Add(NewProfile("vegan"));

        await AssertRejectedByAsync(db, "DietaryProfiles.Code");
    }

    /// <summary>
    /// Whether <c>tree-nuts</c> covers coconut decides what every trait recorded against it means, so the
    /// entry cannot be stored without saying.
    /// </summary>
    [Fact]
    public async Task An_allergen_requires_a_description()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var allergen = NewAllergen("tree-nuts");
        allergen.Description = null!;

        db.Allergens.Add(allergen);

        await AssertRejectedByAsync(db, "NOT NULL");
    }

    [Fact]
    public async Task A_dietary_profile_requires_a_description()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var profile = NewProfile("pescatarian");
        profile.Description = null!;

        db.DietaryProfiles.Add(profile);

        await AssertRejectedByAsync(db, "NOT NULL");
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>Flour, milk, a vegan profile, and one source — the minimum any trait test needs.</summary>
    private static async Task<TraitWorld> SeedAsync(
        CreatorPantryDbContext db, ReferenceSourceKind kind = ReferenceSourceKind.OfficialDatabase)
    {
        var flour = new Ingredient
        {
            Id = Guid.NewGuid(),
            CanonicalName = "All-Purpose Flour",
            NormalizedName = IngredientPolicy.NormalizeName("All-Purpose Flour"),
            SearchText = IngredientPolicy.BuildSearchText("all purpose flour", []),
            IsActive = true,
        };
        var milk = NewAllergen("milk");
        var vegan = NewProfile("vegan");
        var source = NewSource("usda-fdc", kind);

        db.Ingredients.Add(flour);
        db.Allergens.Add(milk);
        db.DietaryProfiles.Add(vegan);
        db.ReferenceSources.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new TraitWorld(flour.Id, milk.Id, vegan.Id, source.Id, kind);
    }

    private sealed record TraitWorld(Guid IngredientId, Guid AllergenId, Guid ProfileId, Guid SourceId, ReferenceSourceKind SourceKind)
    {
        /// <summary>A valid, unreviewed allergen trait for the seeded world, ready to be spoiled by one assignment.</summary>
        public IngredientAllergenTrait AllergenTrait(AllergenPresence presence, string note = TraitPolicy.NoEvidenceNote) => new()
        {
            Id = Guid.NewGuid(),
            IngredientId = IngredientId,
            AllergenId = AllergenId,
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            Presence = presence,
            EvidenceNote = note,
            EffectiveFrom = Effective,
            ReviewStatus = TraitReviewStatus.Unreviewed,
        };

        /// <inheritdoc cref="AllergenTrait"/>
        public IngredientDietaryTrait DietaryTrait(DietaryCompatibility compatibility, string note = TraitPolicy.NoEvidenceNote) => new()
        {
            Id = Guid.NewGuid(),
            IngredientId = IngredientId,
            DietaryProfileId = ProfileId,
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            Compatibility = compatibility,
            EvidenceNote = note,
            EffectiveFrom = Effective,
            ReviewStatus = TraitReviewStatus.Unreviewed,
        };
    }

    private static Allergen NewAllergen(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        Description = $"{code} and its derivatives, as listed by the cited source",
        IsActive = true,
    };

    private static DietaryProfile NewProfile(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        Description = $"the {code} pattern, as described by the cited source",
        IsActive = true,
    };

    private static ReferenceSource NewSource(string code, ReferenceSourceKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = code,
        Kind = kind,
        Citation = $"{code} reference data",
        IsActive = true,
    };
}
