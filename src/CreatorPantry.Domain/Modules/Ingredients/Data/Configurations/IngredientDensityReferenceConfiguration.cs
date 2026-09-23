using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class IngredientDensityReferenceConfiguration : IEntityTypeConfiguration<IngredientDensityReference>
{
    public void Configure(EntityTypeBuilder<IngredientDensityReference> builder)
    {
        builder.ToTable("IngredientDensityReferences", table =>
        {
            // A zero volume would divide by zero at the point of use; a zero or negative mass is not a
            // measurement. Unquoted identifiers keep these valid on SQL Server and on the SQLite database the
            // constraint tests run against.
            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_MassQuantity_Positive",
                "MassQuantity > 0");

            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_VolumeQuantity_Positive",
                "VolumeQuantity > 0");

            // Half of each dimension rule; the composite foreign keys below supply the other half by matching
            // these values against the referenced unit's own dimension.
            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_MassUnit_Dimension",
                $"MassUnitDimension = {(int)MeasurementDimension.Mass}");

            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_VolumeUnit_Dimension",
                $"VolumeUnitDimension = {(int)MeasurementDimension.Volume}");

            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_DisplayPrecision",
                $"DisplayPrecision BETWEEN {QuantityFormat.MinDisplayPrecision} AND {QuantityFormat.MaxDisplayPrecision}");

            // The food-safety-adjacent one: a model-estimated figure can be recorded, but it can never reach
            // Approved, which is the only status a conversion will act on. Without this, one careless import or
            // admin edit would let an invented density reach a creator as a vetted fact (ai.md, recipes.md).
            table.HasCheckConstraint(
                "CK_IngredientDensityReferences_AiEstimate_NotApproved",
                $"NOT (ReferenceSourceKind = {(int)ReferenceSourceKind.AiEstimated} "
                    + $"AND ReviewStatus = {(int)DensityReviewStatus.Approved})");
        });

        builder.HasKey(density => density.Id);

        builder.Property(density => density.MassQuantity)
            .IsRequired()
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(density => density.VolumeQuantity)
            .IsRequired()
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(density => density.ConditionNote)
            .IsRequired()
            .HasMaxLength(DensityPolicy.ConditionNoteMaxLength);

        // Never nullable: it is part of the natural key, and NULL in a unique index means different things on
        // SQL Server and SQLite. See DensityPolicy.UnspecifiedCondition.
        builder.Property(density => density.NormalizedCondition)
            .IsRequired()
            .HasMaxLength(DensityPolicy.ConditionNoteMaxLength);

        builder.Property(density => density.DisplayPrecision)
            .IsRequired();

        builder.Property(density => density.EffectiveFrom)
            .IsRequired();

        builder.Property(density => density.ReviewStatus)
            .IsRequired();

        builder.Property(density => density.ReferenceSourceKind)
            .IsRequired();

        // A density is meaningless without its ingredient, so it goes when the ingredient goes.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(density => density.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Pointing at (Id, Kind) rather than the primary key alone is what lets the AI-estimate check
        // constraint above trust the locally stored kind: the database will not let it disagree with the source.
        builder.HasOne<ReferenceSource>()
            .WithMany()
            .HasForeignKey(density => new { density.ReferenceSourceId, density.ReferenceSourceKind })
            .HasPrincipalKey(source => new { source.Id, source.Kind })
            .OnDelete(DeleteBehavior.Restrict);

        // The database rejects a mass measured in millilitres or a volume measured in grams, because the check
        // constraints have already fixed each side's dimension.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(density => new { density.MassUnitId, density.MassUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(density => new { density.VolumeUnitId, density.VolumeUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        // The natural key. Deliberately permissive about what it allows to coexist: several sources may measure
        // the same ingredient under the same condition and disagree, and one source may revise its own figure on
        // a later date. Choosing between them is a domain decision, so the schema records them all rather than
        // forcing one to win here.
        builder.HasIndex(density => new
            {
                density.IngredientId,
                density.NormalizedCondition,
                density.ReferenceSourceId,
                density.EffectiveFrom,
            })
            .IsUnique()
            .HasDatabaseName("UX_IngredientDensityReferences_Ingredient_Condition_Source_Effective");

        // Finding the usable densities for one ingredient, newest last.
        builder.HasIndex(density => new { density.IngredientId, density.ReviewStatus, density.EffectiveFrom })
            .HasDatabaseName("IX_IngredientDensityReferences_Ingredient_Status_Effective");
    }
}
