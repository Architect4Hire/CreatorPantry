using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class ReferenceSourceConfiguration : IEntityTypeConfiguration<ReferenceSource>
{
    public void Configure(EntityTypeBuilder<ReferenceSource> builder)
    {
        builder.ToTable("ReferenceSources", table =>
        {
            // A licence address with no licence named is a link nobody can say the meaning of.
            table.HasCheckConstraint(
                "CK_ReferenceSources_LicenseUrl_RequiresLicenseCode",
                "LicenseUrl IS NULL OR LicenseCode IS NOT NULL");
        });
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

        builder.Property(source => source.LicenseCode)
            .HasMaxLength(DensityPolicy.SourceLicenseCodeMaxLength);

        builder.Property(source => source.LicenseUrl)
            .HasMaxLength(DensityPolicy.SourceUrlMaxLength);

        builder.Property(source => source.IsActive)
            .IsRequired();

        builder.HasIndex(source => source.Code)
            .IsUnique()
            .HasDatabaseName("UX_ReferenceSources_Code");
    }
}
