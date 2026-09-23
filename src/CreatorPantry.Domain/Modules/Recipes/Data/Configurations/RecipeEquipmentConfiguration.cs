using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeEquipmentConfiguration : IEntityTypeConfiguration<RecipeEquipment>
{
    public void Configure(EntityTypeBuilder<RecipeEquipment> builder)
    {
        builder.ToTable("RecipeEquipment", table =>
            table.HasCheckConstraint(
                "CK_RecipeEquipment_SortOrder_NonNegative",
                "SortOrder >= 0"));

        builder.HasKey(equipment => equipment.Id);

        builder.Property(equipment => equipment.DisplayText)
            .IsRequired()
            .HasMaxLength(RecipePolicy.LineTextMaxLength);

        builder.Property(equipment => equipment.Note).HasMaxLength(RecipePolicy.NoteMaxLength);
        builder.Property(equipment => equipment.SortOrder).IsRequired();
        builder.Property(equipment => equipment.IsOptional).IsRequired();

        builder.HasOne<Recipe>()
            .WithMany(recipe => recipe.Equipment)
            .HasForeignKey(equipment => new { equipment.WorkspaceId, equipment.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<EquipmentType>()
            .WithMany()
            .HasForeignKey(equipment => equipment.EquipmentTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(equipment => new { equipment.WorkspaceId, equipment.RecipeId, equipment.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeEquipment_Workspace_Recipe_Order");

        // "Which of my recipes need a stand mixer."
        builder.HasIndex(equipment => new { equipment.WorkspaceId, equipment.EquipmentTypeId })
            .HasDatabaseName("IX_RecipeEquipment_Workspace_EquipmentType");
    }
}
