using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class LongFormRevisionRecipePinConfiguration : IEntityTypeConfiguration<LongFormRevisionRecipePin>
{
    public void Configure(EntityTypeBuilder<LongFormRevisionRecipePin> builder)
    {
        builder.ToTable("LongFormRevisionRecipePins", table =>
            table.HasCheckConstraint("CK_LongFormRevisionRecipePins_SortOrder_NonNegative", "SortOrder >= 0"));

        builder.HasKey(pin => pin.Id);
        builder.Property(pin => pin.Id).ValueGeneratedNever();

        builder.Property(pin => pin.SortOrder).IsRequired();
        builder.Property(pin => pin.CreativeContextReferenceId).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(pin => pin.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict below: a pin is history with its revision, and nothing but workspace erasure removes either.
        builder.HasOne<LongFormRevision>()
            .WithMany()
            .HasForeignKey(pin => new { pin.WorkspaceId, pin.LongFormRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(pin => new { pin.WorkspaceId, pin.RecipeId, pin.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One pin per recipe: a document is written against one version of a recipe, never two.
        builder.HasIndex(pin => new { pin.WorkspaceId, pin.LongFormRevisionId, pin.RecipeId })
            .IsUnique()
            .HasDatabaseName("UX_LongFormRevisionRecipePins_Workspace_Revision_Recipe");

        builder.HasIndex(pin => new { pin.WorkspaceId, pin.LongFormRevisionId, pin.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_LongFormRevisionRecipePins_Workspace_Revision_SortOrder");

        // Staleness propagation finds every document written against a recipe version.
        builder.HasIndex(pin => new { pin.WorkspaceId, pin.RecipeVersionId })
            .HasDatabaseName("IX_LongFormRevisionRecipePins_Workspace_RecipeVersion");
    }
}
