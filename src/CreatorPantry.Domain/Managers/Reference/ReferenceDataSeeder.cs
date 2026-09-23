using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Managers.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// Brings the global reference catalogue up to date: inserts seeded rows that are missing, reconciles rows
/// whose columns have drifted from the catalogue, and deletes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Rows are matched by primary key, which for seeded data is derived from the natural key (see
/// <see cref="SeedId"/>) and is therefore the same thing said twice. Two consequences worth stating rather
/// than discovering:
/// </para>
/// <list type="bullet">
/// <item>
/// Reconciliation makes the catalogue the source of truth for seeded columns, including <c>IsActive</c>. An
/// operator who retires a seeded row by hand has that retirement reverted on the next deployment; retiring one
/// for good means removing it from the catalogue here.
/// </item>
/// <item>
/// Changing a row's natural key mints a new id. The old row is not deleted — this seeder never deletes — so a
/// rename leaves both behind and needs a migration to retire the old one deliberately.
/// </item>
/// </list>
/// <para>
/// A row that already exists under a <em>different</em> id but the same code is an operator-created
/// collision. The unique index rejects the insert, loudly, which is the right outcome for a platform
/// catalogue: silently adopting a hand-made row would make the seed set mean something different in that one
/// database.
/// </para>
/// </remarks>
internal sealed class ReferenceDataSeeder(
    CreatorPantryDbContext context,
    ReferenceSeedOptions options,
    ILogger<ReferenceDataSeeder> logger) : IDataSeeder
{
    public string Name => "Reference catalogue";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var catalogueRows = await SeedCatalogueAsync(cancellationToken);

        if (!options.IncludeDevelopmentSampleData)
        {
            logger.LogInformation(
                "Seeded {RowCount} reference catalogue rows. Development sample data skipped.", catalogueRows);
            return;
        }

        var sampleRows = await SeedDevelopmentSampleAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {CatalogueRowCount} reference catalogue rows and {SampleRowCount} development sample rows.",
            catalogueRows,
            sampleRows);
    }

    /// <summary>
    /// Tier A: units, vocabularies, and the definitions behind them. Vocabulary and definitional factors only,
    /// so this carries no third-party data and runs in every environment.
    /// </summary>
    private async Task<int> SeedCatalogueAsync(CancellationToken cancellationToken)
    {
        var rows =
            await ReconcileAsync(context.MeasurementUnits, MeasurementSeedData.Units(), unit => unit.Id, cancellationToken)
            + await ReconcileAsync(context.UnitAliases, MeasurementSeedData.Aliases(), alias => alias.Id, cancellationToken)
            + await ReconcileAsync(context.FoodCategories, CatalogueSeedData.FoodCategories(), category => category.Id, cancellationToken)
            + await ReconcileAsync(context.DietaryProfiles, CatalogueSeedData.DietaryProfiles(), profile => profile.Id, cancellationToken)
            + await ReconcileAsync(context.Allergens, CatalogueSeedData.Allergens(), allergen => allergen.Id, cancellationToken)
            + await ReconcileAsync(context.Cuisines, CatalogueSeedData.Cuisines(), cuisine => cuisine.Id, cancellationToken)
            + await ReconcileAsync(context.CuisineAliases, CatalogueSeedData.CuisineAliases(), alias => alias.Id, cancellationToken)
            + await ReconcileAsync(context.Courses, CatalogueSeedData.Courses(), course => course.Id, cancellationToken)
            + await ReconcileAsync(context.CourseAliases, CatalogueSeedData.CourseAliases(), alias => alias.Id, cancellationToken)
            + await ReconcileAsync(context.CookingTechniques, CatalogueSeedData.CookingTechniques(), technique => technique.Id, cancellationToken)
            + await ReconcileAsync(context.CookingTechniqueAliases, CatalogueSeedData.CookingTechniqueAliases(), alias => alias.Id, cancellationToken)
            + await ReconcileAsync(context.EquipmentTypes, CatalogueSeedData.EquipmentTypes(), equipment => equipment.Id, cancellationToken)
            + await ReconcileAsync(context.EquipmentTypeAliases, CatalogueSeedData.EquipmentTypeAliases(), alias => alias.Id, cancellationToken);

        // One save for the tier. EF orders the inserts from the model's relationships, so aliases land after
        // the entries they point at without this method having to know the graph.
        await context.SaveChangesAsync(cancellationToken);

        return rows;
    }

    /// <summary>
    /// Tier B: the sample ingredient set and the illustrative facts about it. Skipped in Production — see
    /// <see cref="SampleIngredientSeedData"/> for why this data is not production reference material.
    /// </summary>
    private async Task<int> SeedDevelopmentSampleAsync(CancellationToken cancellationToken)
    {
        var rows =
            await ReconcileAsync(context.ReferenceSources, SampleIngredientSeedData.Sources(), source => source.Id, cancellationToken)
            + await ReconcileAsync(context.Ingredients, SampleIngredientSeedData.Ingredients(), ingredient => ingredient.Id, cancellationToken)
            + await ReconcileAsync(context.IngredientAliases, SampleIngredientSeedData.IngredientAliases(), alias => alias.Id, cancellationToken)
            + await ReconcileAsync(context.IngredientDensityReferences, SampleIngredientSeedData.Densities(), density => density.Id, cancellationToken)
            + await ReconcileAsync(context.IngredientDietaryTraits, SampleIngredientSeedData.DietaryTraits(), trait => trait.Id, cancellationToken)
            + await ReconcileAsync(context.IngredientAllergenTraits, SampleIngredientSeedData.AllergenTraits(), trait => trait.Id, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        return rows;
    }

    /// <summary>Inserts what is missing and reconciles what has drifted, for one table.</summary>
    /// <returns>The number of rows the catalogue declares for this table.</returns>
    private async Task<int> ReconcileAsync<TEntity>(
        DbSet<TEntity> set,
        IReadOnlyList<TEntity> seeded,
        Func<TEntity, Guid> id,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        // Reading the whole table: these are small global catalogues, and one round trip beats a query per row.
        var existing = await set.ToDictionaryAsync(id, cancellationToken);

        foreach (var seed in seeded)
        {
            if (existing.TryGetValue(id(seed), out var row))
            {
                // Copies every scalar by name and marks nothing modified when the values already match, so a
                // second run against an up-to-date database issues no UPDATE at all.
                context.Entry(row).CurrentValues.SetValues(seed);
            }
            else
            {
                set.Add(seed);
            }
        }

        return seeded.Count;
    }
}
