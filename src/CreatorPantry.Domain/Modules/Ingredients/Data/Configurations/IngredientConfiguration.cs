using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class IngredientConfiguration : IEntityTypeConfiguration<Ingredient>
{
    public void Configure(EntityTypeBuilder<Ingredient> builder)
    {
        builder.ToTable("Ingredients", table =>
            // Half of the "a default count unit is a Count unit" rule: this pins the redundant dimension
            // column to Count, and the composite foreign key below pins it to the referenced unit's own
            // dimension. Neither alone is enough; together they make the rule unforgeable. Written with
            // unquoted identifiers so the same expression is valid on SQL Server and on the SQLite database
            // the constraint tests run against.
            table.HasCheckConstraint(
                "CK_Ingredients_DefaultCountUnit_Dimension",
                $"(DefaultCountUnitId IS NULL AND DefaultCountUnitDimension IS NULL) OR "
                    + $"(DefaultCountUnitId IS NOT NULL AND DefaultCountUnitDimension = {(int)MeasurementDimension.Count})"));

        builder.HasKey(ingredient => ingredient.Id);

        builder.Property(ingredient => ingredient.CanonicalName)
            .IsRequired()
            .HasMaxLength(NameNormalization.NameMaxLength);

        builder.Property(ingredient => ingredient.NormalizedName)
            .IsRequired()
            .HasMaxLength(NameNormalization.NameMaxLength);

        builder.Property(ingredient => ingredient.SearchText)
            .IsRequired()
            .HasMaxLength(NameNormalization.SearchTextMaxLength);

        builder.Property(ingredient => ingredient.IsActive)
            .IsRequired();

        // The natural key: one ingredient per normalized name, which is what collapses "Flour", "flour", and
        // "FLOUR" into a single row.
        builder.HasIndex(ingredient => ingredient.NormalizedName)
            .IsUnique()
            .HasDatabaseName("UX_Ingredients_NormalizedName");

        // Retiring a category must never delete the ingredients grouped under it — they become uncategorized.
        builder.HasOne<FoodCategory>()
            .WithMany()
            .HasForeignKey(ingredient => ingredient.FoodCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // The other half of the dimension rule. Pointing at the alternate key (Id, Dimension) rather than the
        // primary key alone means the database itself rejects a default count unit that is measured by mass,
        // volume, or temperature, because the check constraint above has already fixed this side to Count.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(ingredient => new { ingredient.DefaultCountUnitId, ingredient.DefaultCountUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        // Offering the active ingredients within one category.
        builder.HasIndex(ingredient => new { ingredient.FoodCategoryId, ingredient.IsActive })
            .HasDatabaseName("IX_Ingredients_Category_IsActive");

        // SearchText is deliberately unindexed: a LIKE '%term%' prefilter cannot use a B-tree, and the
        // full-text/vector support that would serve it is a later decision.
    }
}
