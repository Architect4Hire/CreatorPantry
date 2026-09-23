using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class ReferenceSourceConfiguration : IEntityTypeConfiguration<ReferenceSource>
{
    public void Configure(EntityTypeBuilder<ReferenceSource> builder)
    {
        builder.ToTable("ReferenceSources");
        builder.HasKey(source => source.Id);

        builder.Property(source => source.Code)
            .IsRequired()
            .HasMaxLength(DensityPolicy.SourceCodeMaxLength);

        builder.Property(source => source.Name)
            .IsRequired()
            .HasMaxLength(DensityPolicy.SourceNameMaxLength);

        builder.Property(source => source.Kind)
            .IsRequired();

        builder.Property(source => source.Url)
            .HasMaxLength(DensityPolicy.SourceUrlMaxLength);

        // Required: a fact that cannot say where it came from is not usable.
        builder.Property(source => source.Citation)
            .IsRequired()
            .HasMaxLength(DensityPolicy.SourceCitationMaxLength);

        builder.Property(source => source.IsActive)
            .IsRequired();

        builder.HasIndex(source => source.Code)
            .IsUnique()
            .HasDatabaseName("UX_ReferenceSources_Code");
    }
}
