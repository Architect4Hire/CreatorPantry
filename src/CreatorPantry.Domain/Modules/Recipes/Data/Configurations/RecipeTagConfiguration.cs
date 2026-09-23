using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeTagConfiguration : IEntityTypeConfiguration<RecipeTag>
{
    public void Configure(EntityTypeBuilder<RecipeTag> builder)
    {
        builder.ToTable("RecipeTags");

        // The whole row is the key, so one tag can be applied to one recipe exactly once and a duplicate is a
        // key violation rather than a state anything has to check for.
        builder.HasKey(tag => new { tag.WorkspaceId, tag.RecipeId, tag.WorkspaceTagId });

        // Part of the recipe: deleting one takes its tag links with it.
        builder.HasOne<Recipe>()
            .WithMany(recipe => recipe.Tags)
            .HasForeignKey(tag => new { tag.WorkspaceId, tag.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Not part of the vocabulary: deleting a tag that recipes still carry is refused. Cascade here would
        // be a second path into this table from Workspaces, which SQL Server rejects outright — and it would
        // also strip a tag from creator content on what looks like a tidy-up. The vocabulary retires entries
        // with IsActive instead.
        builder.HasOne<WorkspaceTag>()
            .WithMany()
            .HasForeignKey(tag => new { tag.WorkspaceId, tag.WorkspaceTagId })
            .HasPrincipalKey(tag => new { tag.WorkspaceId, tag.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // "Which recipes carry this tag" — the reverse of the primary key's leading columns.
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.WorkspaceTagId })
            .HasDatabaseName("IX_RecipeTags_Workspace_Tag");
    }
}
