using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Measurement.Data.Configurations;

internal sealed class UnitAliasConfiguration : IEntityTypeConfiguration<UnitAlias>
{
    public void Configure(EntityTypeBuilder<UnitAlias> builder)
    {
        builder.ToTable("UnitAliases");
        builder.HasKey(alias => alias.Id);

        builder.Property(alias => alias.Alias)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.AliasMaxLength);

        builder.Property(alias => alias.NormalizedAlias)
            .IsRequired()
            .HasMaxLength(MeasurementPolicy.AliasMaxLength);

        // An alias has no meaning without its unit, so it goes when the unit goes.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(alias => alias.MeasurementUnitId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique across every unit, not per unit: an alias matching two units would make a recipe line
        // ambiguous, and there is no context at this layer to break the tie.
        builder.HasIndex(alias => alias.NormalizedAlias)
            .IsUnique()
            .HasDatabaseName("UX_UnitAliases_NormalizedAlias");

        // Listing one unit's aliases; also covers the foreign key, which the unique index above does not.
        builder.HasIndex(alias => alias.MeasurementUnitId)
            .HasDatabaseName("IX_UnitAliases_MeasurementUnitId");
    }
}
