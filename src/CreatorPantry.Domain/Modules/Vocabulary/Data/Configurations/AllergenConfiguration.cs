using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class AllergenConfiguration : IEntityTypeConfiguration<Allergen>
{
    public void Configure(EntityTypeBuilder<Allergen> builder)
    {
        builder.ToTable("Allergens");
        builder.HasKey(allergen => allergen.Id);

        builder.Property(allergen => allergen.Code)
            .IsRequired()
            .HasMaxLength(TraitPolicy.CodeMaxLength);

        builder.Property(allergen => allergen.DisplayName)
            .IsRequired()
            .HasMaxLength(TraitPolicy.DisplayNameMaxLength);

        // Required: whether "tree-nuts" includes coconut decides what every trait recorded against it means.
        builder.Property(allergen => allergen.Description)
            .IsRequired()
            .HasMaxLength(TraitPolicy.DescriptionMaxLength);

        builder.Property(allergen => allergen.IsActive)
            .IsRequired();

        builder.HasIndex(allergen => allergen.Code)
            .IsUnique()
            .HasDatabaseName("UX_Allergens_Code");
    }
}
