using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class DietaryProfileConfiguration : IEntityTypeConfiguration<DietaryProfile>
{
    public void Configure(EntityTypeBuilder<DietaryProfile> builder)
    {
        builder.ToTable("DietaryProfiles");
        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.Code)
            .IsRequired()
            .HasMaxLength(CodeFormat.CodeMaxLength);

        builder.Property(profile => profile.DisplayName)
            .IsRequired()
            .HasMaxLength(CodeFormat.DisplayNameMaxLength);

        // Required, unlike the display name on any other vocabulary: traits recorded against an undefined
        // profile are answering a question nobody has written down.
        builder.Property(profile => profile.Description)
            .IsRequired()
            .HasMaxLength(CodeFormat.DescriptionMaxLength);

        builder.Property(profile => profile.IsActive)
            .IsRequired();

        builder.HasIndex(profile => profile.Code)
            .IsUnique()
            .HasDatabaseName("UX_DietaryProfiles_Code");
    }
}
