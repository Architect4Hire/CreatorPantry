using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Measurement.Data.Configurations;

internal sealed class MeasurementUnitConfiguration : IEntityTypeConfiguration<MeasurementUnit>
{
    public void Configure(EntityTypeBuilder<MeasurementUnit> builder)
    {
        builder.ToTable("MeasurementUnits", table =>
        {
            // The dimension rules from recipes.md, enforced by the database rather than by convention alone.
            // Written with unquoted identifiers and the enum's own integer values so the same expression is
            // valid on SQL Server and on the SQLite database the constraint tests run against.
            //
            // Mass (0), Volume (1), and Count (2) convert by a single multiplier against their dimension's
            // base unit, so they must carry one. Temperature (3) is affine and Qualitative (4) has no numeric
            // relationship at all, so a factor there would be meaningless and is refused.
            table.HasCheckConstraint(
                "CK_MeasurementUnits_Factor_Dimension",
                "(Dimension IN (0, 1, 2) AND BaseUnitFactor IS NOT NULL) OR (Dimension IN (3, 4) AND BaseUnitFactor IS NULL)");

            // A zero or negative factor would silently produce zero or negative scaled quantities.
            table.HasCheckConstraint(
                "CK_MeasurementUnits_Factor_Positive",
                "BaseUnitFactor IS NULL OR BaseUnitFactor > 0");

            table.HasCheckConstraint(
                "CK_MeasurementUnits_DisplayPrecision",
                $"DisplayPrecision BETWEEN {MeasurementPolicy.MinDisplayPrecision} AND {MeasurementPolicy.MaxDisplayPrecision}");
        });

        builder.HasKey(unit => unit.Id);

        builder.Property(unit => unit.Code)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.CodeMaxLength);

        builder.Property(unit => unit.DisplayName)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.DisplayNameMaxLength);

        builder.Property(unit => unit.PluralName)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.DisplayNameMaxLength);

        builder.Property(unit => unit.Abbreviation)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.AbbreviationMaxLength);

        builder.Property(unit => unit.Dimension)
            .IsRequired();

        builder.Property(unit => unit.System)
            .IsRequired();

        // Explicit precision and scale: the provider default (decimal(18, 2)) would round 4.92892159375 ml
        // per US teaspoon down to 4.93 and lose accuracy on every scaled recipe.
        builder.Property(unit => unit.BaseUnitFactor)
            .HasColumnType(MeasurementPolicy.BaseUnitFactorColumnType);

        builder.Property(unit => unit.DisplayPrecision)
            .IsRequired();

        builder.Property(unit => unit.IsActive)
            .IsRequired();

        // The permanent key seed data and future recipe references resolve a unit by.
        builder.HasIndex(unit => unit.Code)
            .IsUnique()
            .HasDatabaseName("UX_MeasurementUnits_Code");

        // Offering the units a creator may pick for one kind of quantity.
        builder.HasIndex(unit => new { unit.Dimension, unit.IsActive })
            .HasDatabaseName("IX_MeasurementUnits_Dimension_IsActive");
    }
}
