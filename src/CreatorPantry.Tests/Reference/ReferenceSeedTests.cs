using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.MigrationService;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Runs the real migration-service host and the real reference seeder against one SQLite database, repeatedly,
/// the way successive deployments run it against SQL Server.
/// </summary>
public sealed class ReferenceSeedTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // --- Idempotency -------------------------------------------------------------------------------------

    /// <summary>
    /// The headline guarantee: three runs leave exactly what one run left, compared row by row and column by
    /// column rather than by counting. Comparing values at all is only possible because seeded ids are derived
    /// from natural keys — with database-generated ids, a re-inserted row would look different even when the
    /// seeding was correct.
    /// </summary>
    [Fact]
    public async Task Repeated_runs_produce_identical_records()
    {
        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());
        var afterFirstRun = await SnapshotAsync();

        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());
        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());

        Assert.Equal(afterFirstRun, await SnapshotAsync());
    }

    /// <summary>
    /// Stronger than comparing the result: the second run does not write at all. A seeder that deleted and
    /// re-inserted every row would pass the snapshot test above while churning the table on every deployment.
    /// </summary>
    [Fact]
    public async Task Second_run_issues_no_writes()
    {
        await RunMigrationHostAsync();

        var writes = new WriteCountingInterceptor();
        await RunMigrationHostAsync(interceptor: writes);

        Assert.Equal(0, writes.TrackedWrites);
    }

    [Fact]
    public async Task Seeded_row_counts_match_the_declared_catalogue()
    {
        await RunMigrationHostAsync();

        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(MeasurementSeedData.Units().Count, await context.MeasurementUnits.CountAsync(cancellationToken));
        Assert.Equal(MeasurementSeedData.Aliases().Count, await context.UnitAliases.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.FoodCategories().Count, await context.FoodCategories.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.DietaryProfiles().Count, await context.DietaryProfiles.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.Allergens().Count, await context.Allergens.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.Cuisines().Count, await context.Cuisines.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.Courses().Count, await context.Courses.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.CookingTechniques().Count, await context.CookingTechniques.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.EquipmentTypes().Count, await context.EquipmentTypes.CountAsync(cancellationToken));
        Assert.Equal(SampleIngredientSeedData.Ingredients().Count, await context.Ingredients.CountAsync(cancellationToken));
        Assert.Equal(SampleIngredientSeedData.Densities().Count, await context.IngredientDensityReferences.CountAsync(cancellationToken));
        Assert.Equal(SampleIngredientSeedData.DietaryTraits().Count, await context.IngredientDietaryTraits.CountAsync(cancellationToken));
        Assert.Equal(SampleIngredientSeedData.AllergenTraits().Count, await context.IngredientAllergenTraits.CountAsync(cancellationToken));
    }

    /// <summary>
    /// The reconciling half of the contract: a seeded column that has drifted is restored, and the row keeps
    /// its identity rather than being replaced.
    /// </summary>
    [Fact]
    public async Task Drifted_row_is_reconciled_without_replacing_it()
    {
        await RunMigrationHostAsync();

        var cancellationToken = TestContext.Current.CancellationToken;
        Guid originalId;

        await using (var context = CreateContext())
        {
            var cup = await context.MeasurementUnits.SingleAsync(unit => unit.Code == "cup-us", cancellationToken);
            originalId = cup.Id;

            cup.DisplayName = "drifted";
            cup.IsActive = false;
            await context.SaveChangesAsync(cancellationToken);
        }

        await RunMigrationHostAsync();

        await using (var context = CreateContext())
        {
            var cup = await context.MeasurementUnits.SingleAsync(unit => unit.Code == "cup-us", cancellationToken);

            Assert.Equal(originalId, cup.Id);
            Assert.Equal("US cup", cup.DisplayName);
            Assert.True(cup.IsActive);
        }
    }

    /// <summary>
    /// Seeded ids are a pure function of the catalogue. Two independent builds agree, which is what makes a
    /// developer's database and a CI database hold the same rows.
    /// </summary>
    [Fact]
    public void Seed_identifiers_are_derived_not_generated()
    {
        Assert.Equal(
            MeasurementSeedData.Units().Select(unit => unit.Id),
            MeasurementSeedData.Units().Select(unit => unit.Id));

        Assert.Equal(
            SampleIngredientSeedData.Ingredients().Select(ingredient => ingredient.Id),
            SampleIngredientSeedData.Ingredients().Select(ingredient => ingredient.Id));

        Assert.DoesNotContain(Guid.Empty, MeasurementSeedData.Units().Select(unit => unit.Id));
    }

    // --- Tiering -----------------------------------------------------------------------------------------

    /// <summary>
    /// Production gets a working unit and vocabulary catalogue and none of the illustrative facts — no sample
    /// ingredients, and so no densities or traits citing a development source.
    /// </summary>
    [Fact]
    public async Task Production_tier_seeds_the_catalogue_but_no_sample_data()
    {
        await RunMigrationHostAsync(includeDevelopmentSampleData: false);

        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(MeasurementSeedData.Units().Count, await context.MeasurementUnits.CountAsync(cancellationToken));
        Assert.Equal(CatalogueSeedData.Allergens().Count, await context.Allergens.CountAsync(cancellationToken));

        Assert.Equal(0, await context.ReferenceSources.CountAsync(cancellationToken));
        Assert.Equal(0, await context.Ingredients.CountAsync(cancellationToken));
        Assert.Equal(0, await context.IngredientDensityReferences.CountAsync(cancellationToken));
        Assert.Equal(0, await context.IngredientDietaryTraits.CountAsync(cancellationToken));
        Assert.Equal(0, await context.IngredientAllergenTraits.CountAsync(cancellationToken));
    }

    // --- Safety and provenance ---------------------------------------------------------------------------

    /// <summary>
    /// The seed set states no allergen absence. Note what is being verified: not that this file was written
    /// carefully, but that the source kind it cites makes an absence claim unstorable — the schema rejects one
    /// rather than trusting the seeder.
    /// </summary>
    [Fact]
    public async Task No_seeded_allergen_trait_claims_absence()
    {
        await RunMigrationHostAsync();

        await using var context = CreateContext();
        var traits = await context.IngredientAllergenTraits.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(traits);
        Assert.DoesNotContain(traits, trait => trait.Presence == AllergenPresence.NotListedBySource);
        Assert.All(traits, trait => Assert.False(TraitPolicy.MayAssertAllergenAbsence(trait.ReferenceSourceKind)));
    }

    /// <summary>
    /// Nothing seeded reaches the one review state an analysis acts on, and nothing seeded claims the standing
    /// of an official database.
    /// </summary>
    [Fact]
    public async Task Seeded_facts_are_unreviewed_and_cite_an_unvetted_development_source()
    {
        await RunMigrationHostAsync();

        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var source = Assert.Single(await context.ReferenceSources.AsNoTracking().ToListAsync(cancellationToken));
        Assert.Equal(ReferenceSourceKind.CommunityContributed, source.Kind);
        Assert.Contains("not suitable for production use", source.Citation, StringComparison.OrdinalIgnoreCase);

        Assert.All(
            await context.IngredientDensityReferences.AsNoTracking().ToListAsync(cancellationToken),
            density => Assert.Equal(DensityReviewStatus.Unreviewed, density.ReviewStatus));

        Assert.All(
            await context.IngredientDietaryTraits.AsNoTracking().ToListAsync(cancellationToken),
            trait => Assert.Equal(TraitReviewStatus.Unreviewed, trait.ReviewStatus));

        Assert.All(
            await context.IngredientAllergenTraits.AsNoTracking().ToListAsync(cancellationToken),
            trait => Assert.Equal(TraitReviewStatus.Unreviewed, trait.ReviewStatus));
    }

    /// <summary>
    /// Every state a reader could mistake for safety guidance says what its source actually stated.
    /// </summary>
    [Fact]
    public async Task States_that_need_evidence_carry_it()
    {
        await RunMigrationHostAsync();

        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var allergenTraits = await context.IngredientAllergenTraits.AsNoTracking().ToListAsync(cancellationToken);
        Assert.Contains(allergenTraits, trait => trait.Presence == AllergenPresence.PossiblePresence);
        Assert.All(
            allergenTraits.Where(trait => TraitPolicy.RequiresEvidenceNote(trait.Presence)),
            trait => Assert.NotEqual(TraitPolicy.NoEvidenceNote, trait.EvidenceNote));

        var dietaryTraits = await context.IngredientDietaryTraits.AsNoTracking().ToListAsync(cancellationToken);
        Assert.Contains(dietaryTraits, trait => trait.Compatibility == DietaryCompatibility.DependsOnProduct);
        Assert.All(
            dietaryTraits.Where(trait => TraitPolicy.RequiresEvidenceNote(trait.Compatibility)),
            trait => Assert.NotEqual(TraitPolicy.NoEvidenceNote, trait.EvidenceNote));
    }

    // --- The collisions no index can catch ---------------------------------------------------------------

    /// <summary>
    /// <see cref="IngredientAlias.NormalizedAlias"/> documents one ambiguity its unique index cannot reach: an
    /// alias equal to some <em>other</em> ingredient's normalized canonical name, since no single index spans
    /// two tables. Resolution precedence is a documented rule, and this is the test that rule said would
    /// verify it.
    /// </summary>
    [Fact]
    public void No_ingredient_alias_collides_with_a_canonical_name()
    {
        var canonicalNames = SampleIngredientSeedData.Ingredients()
            .Select(ingredient => ingredient.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);

        var colliding = SampleIngredientSeedData.IngredientAliases()
            .Where(alias => canonicalNames.Contains(alias.NormalizedAlias))
            .Select(alias => alias.Alias)
            .ToList();

        Assert.Empty(colliding);
    }

    /// <summary>The same gap on the vocabulary side, where the unreachable collision is with a display name.</summary>
    [Theory]
    [MemberData(nameof(VocabularySets))]
    public void No_vocabulary_alias_collides_with_a_display_name(
        string vocabulary,
        string[] displayNames,
        string[] aliases)
    {
        var normalizedDisplayNames = displayNames
            .Select(VocabularyPolicy.NormalizeAlias)
            .ToHashSet(StringComparer.Ordinal);

        var colliding = aliases.Where(normalizedDisplayNames.Contains).ToList();

        Assert.True(colliding.Count == 0, $"{vocabulary} aliases collide with a display name: {string.Join(", ", colliding)}");
    }

    /// <summary>
    /// No alias resolves a volume word that means different things in US customary and imperial.
    /// </summary>
    /// <remarks>
    /// This is the invariant, and it is about <em>resolution</em> rather than search: an alias is what lets an
    /// importer turn "1 pint milk" into a unit reference without asking anyone, and an imperial pint is 20%
    /// larger than a US one. A creator may still find and pick "US pint" from a list — that choice is made
    /// knowingly. The unique index makes this one-way, so the assertion guards a door that cannot be reopened
    /// cheaply.
    /// </remarks>
    [Theory]
    [InlineData("pint")]
    [InlineData("pints")]
    [InlineData("quart")]
    [InlineData("quarts")]
    [InlineData("gallon")]
    [InlineData("gallons")]
    [InlineData("fl oz")]
    [InlineData("fluid ounce")]
    [InlineData("fluid ounces")]
    public void No_alias_claims_a_volume_word_that_differs_between_traditions(string word)
    {
        var key = MeasurementPolicy.NormalizeAlias(word);

        Assert.DoesNotContain(MeasurementSeedData.Aliases(), alias => alias.NormalizedAlias == key);
    }

    /// <summary>
    /// Every seeded code is lowercase-kebab, as the <c>*Policy.CodePattern</c> constants declare.
    /// </summary>
    /// <remarks>
    /// The pattern is documented as what "holds" the lowercase form — <c>ControlledVocabularyConfiguration</c>
    /// says so explicitly, because collation cannot: SQL Server rejects <c>tsp</c> and <c>TSP</c> as duplicate
    /// codes while SQLite accepts both, so a catalogue that passes the constraint tests can still fail to
    /// deploy. In fact the pattern is applied to no write path at all; the seeder is the only writer, so this
    /// test is the enforcement.
    /// </remarks>
    [Fact]
    public void Every_seeded_code_matches_its_declared_pattern()
    {
        var codes = new List<(string Table, string Code, string Pattern)>();

        codes.AddRange(MeasurementSeedData.Units().Select(unit =>
            ("MeasurementUnits", unit.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.FoodCategories().Select(category =>
            ("FoodCategories", category.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.DietaryProfiles().Select(profile =>
            ("DietaryProfiles", profile.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.Allergens().Select(allergen =>
            ("Allergens", allergen.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.Cuisines().Select(entry => ("Cuisines", entry.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.Courses().Select(entry => ("Courses", entry.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.CookingTechniques().Select(entry => ("CookingTechniques", entry.Code, CodeFormat.CodePattern)));
        codes.AddRange(CatalogueSeedData.EquipmentTypes().Select(entry => ("EquipmentTypes", entry.Code, CodeFormat.CodePattern)));
        codes.AddRange(SampleIngredientSeedData.Sources().Select(source =>
            ("ReferenceSources", source.Code, CodeFormat.CodePattern)));

        var offending = codes
            .Where(entry => !Regex.IsMatch(entry.Code, entry.Pattern))
            .Select(entry => $"{entry.Table}.{entry.Code}")
            .ToList();

        Assert.True(offending.Count == 0, $"codes not matching their pattern: {string.Join(", ", offending)}");
        Assert.NotEmpty(codes);
    }

    /// <summary>
    /// Unit aliases must be unique across the <em>whole</em> catalogue, not per unit. The database enforces it,
    /// but only once a run gets that far; catching it here names the offending pair instead of surfacing an
    /// opaque constraint violation at deployment time.
    /// </summary>
    [Fact]
    public void Unit_aliases_are_globally_unambiguous()
    {
        var duplicates = MeasurementSeedData.Aliases()
            .GroupBy(alias => alias.NormalizedAlias, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Ambiguous unit aliases: {string.Join(", ", duplicates)}");
    }

    public static TheoryData<string, string[], string[]> VocabularySets() =>
        new()
        {
            {
                "Cuisine",
                [.. CatalogueSeedData.Cuisines().Select(entry => entry.DisplayName)],
                [.. CatalogueSeedData.CuisineAliases().Select(alias => alias.NormalizedAlias)]
            },
            {
                "Course",
                [.. CatalogueSeedData.Courses().Select(entry => entry.DisplayName)],
                [.. CatalogueSeedData.CourseAliases().Select(alias => alias.NormalizedAlias)]
            },
            {
                "CookingTechnique",
                [.. CatalogueSeedData.CookingTechniques().Select(entry => entry.DisplayName)],
                [.. CatalogueSeedData.CookingTechniqueAliases().Select(alias => alias.NormalizedAlias)]
            },
            {
                "EquipmentType",
                [.. CatalogueSeedData.EquipmentTypes().Select(entry => entry.DisplayName)],
                [.. CatalogueSeedData.EquipmentTypeAliases().Select(alias => alias.NormalizedAlias)]
            },
        };

    // --- Harness -----------------------------------------------------------------------------------------

    private async Task<MigrationOutcome> RunMigrationHostAsync(
        bool includeDevelopmentSampleData = true,
        WriteCountingInterceptor? interceptor = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<CreatorPantryDbContext>(options =>
        {
            options.UseSqlite(_connection);

            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        builder.Services.AddReferenceSeeding(includeDevelopmentSampleData);
        builder.Services.AddMigrationHost();

        using var host = builder.Build();
        var state = host.Services.GetRequiredService<MigrationRunState>();
        await host.RunAsync(TestContext.Current.CancellationToken);

        return state.Outcome;
    }

    /// <summary>Every reference table, ordered by its stable id and serialized so a diff names the column.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var tables = new Dictionary<string, object>
        {
            ["MeasurementUnits"] = await Ordered(context.MeasurementUnits, unit => unit.Id),
            ["UnitAliases"] = await Ordered(context.UnitAliases, alias => alias.Id),
            ["FoodCategories"] = await Ordered(context.FoodCategories, category => category.Id),
            ["DietaryProfiles"] = await Ordered(context.DietaryProfiles, profile => profile.Id),
            ["Allergens"] = await Ordered(context.Allergens, allergen => allergen.Id),
            ["Cuisines"] = await Ordered(context.Cuisines, cuisine => cuisine.Id),
            ["CuisineAliases"] = await Ordered(context.CuisineAliases, alias => alias.Id),
            ["Courses"] = await Ordered(context.Courses, course => course.Id),
            ["CourseAliases"] = await Ordered(context.CourseAliases, alias => alias.Id),
            ["CookingTechniques"] = await Ordered(context.CookingTechniques, technique => technique.Id),
            ["CookingTechniqueAliases"] = await Ordered(context.CookingTechniqueAliases, alias => alias.Id),
            ["EquipmentTypes"] = await Ordered(context.EquipmentTypes, equipment => equipment.Id),
            ["EquipmentTypeAliases"] = await Ordered(context.EquipmentTypeAliases, alias => alias.Id),
            ["ReferenceSources"] = await Ordered(context.ReferenceSources, source => source.Id),
            ["Ingredients"] = await Ordered(context.Ingredients, ingredient => ingredient.Id),
            ["IngredientAliases"] = await Ordered(context.IngredientAliases, alias => alias.Id),
            ["IngredientDensityReferences"] = await Ordered(context.IngredientDensityReferences, density => density.Id),
            ["IngredientDietaryTraits"] = await Ordered(context.IngredientDietaryTraits, trait => trait.Id),
            ["IngredientAllergenTraits"] = await Ordered(context.IngredientAllergenTraits, trait => trait.Id),
        };

        return JsonSerializer.Serialize(tables, new JsonSerializerOptions { WriteIndented = true });

        async Task<object> Ordered<TEntity>(DbSet<TEntity> set, Func<TEntity, Guid> id)
            where TEntity : class =>
            (await set.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(id).ToList();
    }

    private CreatorPantryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CreatorPantryDbContext>().UseSqlite(_connection).Options);

    /// <summary>Counts entities the seeder actually asked the database to change.</summary>
    private sealed class WriteCountingInterceptor : ISaveChangesInterceptor
    {
        public int TrackedWrites { get; private set; }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            TrackedWrites += eventData.Context?.ChangeTracker.Entries()
                .Count(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) ?? 0;

            return ValueTask.FromResult(result);
        }
    }
}
