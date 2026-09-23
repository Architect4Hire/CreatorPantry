using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class FoodCategoryConfiguration : IEntityTypeConfiguration<FoodCategory>
{
    public void Configure(EntityTypeBuilder<FoodCategory> builder)
    {
        builder.ToTable("FoodCategories");
        builder.HasKey(category => category.Id);

        builder.Property(category => category.Code)
            .IsRequired()
            .HasMaxLength(CodeFormat.CodeMaxLength);

        builder.Property(category => category.DisplayName)
            .IsRequired()
            .HasMaxLength(CodeFormat.DisplayNameMaxLength);

        builder.Property(category => category.IsActive)
            .IsRequired();

        builder.HasIndex(category => category.Code)
            .IsUnique()
            .HasDatabaseName("UX_FoodCategories_Code");
    }
}
