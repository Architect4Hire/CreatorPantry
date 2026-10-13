using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class ContentCardConfiguration : IEntityTypeConfiguration<ContentCard>
{
    public void Configure(EntityTypeBuilder<ContentCard> builder)
    {
        builder.ToTable("ContentCards", table =>
        {
            table.HasCheckConstraint("CK_ContentCards_Title_NotBlank", "trim(Title) <> ''");

            // Stored as an integer, and an enum column accepts any integer. Zero is not a column.
            table.HasCheckConstraint(
                "CK_ContentCards_Column_Range",
                $"\"Column\" >= {(int)ContentCardColumn.ToDo} AND \"Column\" <= {(int)ContentCardColumn.Approved}");

            table.HasCheckConstraint(
                "CK_ContentCards_Origin_Range",
                $"Origin >= {(int)ContentCardOrigin.Board} AND Origin <= {(int)ContentCardOrigin.ContentPipeline}");

            // The pipeline's card is the card *for a piece of work*. Without one it would be the pipeline's
            // card for nothing, and SQL Server's unique index would treat two such rows as the same.
            table.HasCheckConstraint(
                "CK_ContentCards_PipelineOrigin_WithContext",
                $"Origin <> {(int)ContentCardOrigin.ContentPipeline} OR CreativeContextId IS NOT NULL");

            // The day is the date's own weekday, so one cannot be present without the other. That it is the
            // *right* weekday is Business's to compute: no portable expression says it in both engines.
            table.HasCheckConstraint(
                "CK_ContentCards_PlannedDay_WithDate",
                "(PlannedDate IS NULL AND PlannedDayOfWeek IS NULL) OR "
                    + "(PlannedDate IS NOT NULL AND PlannedDayOfWeek IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_ContentCards_PlannedDay_Range",
                $"PlannedDayOfWeek IS NULL OR (PlannedDayOfWeek >= {(int)DayOfWeek.Sunday} "
                    + $"AND PlannedDayOfWeek <= {(int)DayOfWeek.Saturday})");

            // A version pin is a pin on something. Without its parent the composite key would not be checked
            // at all, and the card would name a version of nothing.
            table.HasCheckConstraint(
                "CK_ContentCards_RecipeVersion_WithRecipe", "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");
            table.HasCheckConstraint(
                "CK_ContentCards_AssetVersion_WithAsset",
                "MediaAssetVersionNumber IS NULL OR (MediaAssetId IS NOT NULL AND MediaAssetVersionNumber >= 1)");
        });

        builder.HasKey(card => card.Id);

        // Set by the application, never the store.
        builder.Property(card => card.Id).ValueGeneratedNever();

        // Target of every child's composite key, so a move or a package owned by a different workspace than
        // its card is unrepresentable.
        builder.HasAlternateKey(card => new { card.WorkspaceId, card.Id })
            .HasName("AK_ContentCards_Workspace_Id");

        builder.Property(card => card.Title).IsRequired().HasMaxLength(ContentCardPolicy.TitleMaxLength);
        builder.Property(card => card.Description).HasMaxLength(ContentCardPolicy.DescriptionMaxLength);
        builder.Property(card => card.Column).IsRequired();

        // A default, so the cards that were on the board before the pipeline could put one there are what they
        // always were: put there directly.
        // The sentinel is stated rather than left implicit: zero is not an origin, so a card that reaches an
        // insert without one takes the store's default and the range check never sees a zero.
        builder.Property(card => card.Origin)
            .IsRequired()
            .HasDefaultValue(ContentCardOrigin.Board)
            .HasSentinel(default(ContentCardOrigin));
        builder.Property(card => card.SortRank).IsRequired();
        builder.Property(card => card.ChannelKey).HasMaxLength(ContentPolicy.ChannelKeyMaxLength);
        builder.Property(card => card.CuisineCode).HasMaxLength(ContentCardPolicy.VocabularyCodeMaxLength);
        builder.Property(card => card.DishTypeCode).HasMaxLength(ContentCardPolicy.VocabularyCodeMaxLength);
        builder.Property(card => card.MethodCode).HasMaxLength(ContentCardPolicy.VocabularyCodeMaxLength);

        builder.Property(card => card.CreatedByMembershipId).IsRequired();
        builder.Property(card => card.UpdatedByMembershipId).IsRequired();
        builder.Property(card => card.CreatedAt).IsRequired();
        builder.Property(card => card.UpdatedAt).IsRequired();
        builder.Property(card => card.RowVersion).IsRowVersion();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(card => card.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every lineage key is workspace-paired, so a card cannot name another workspace's record, and
        // restricted: each target cascades from the same workspace, and a second cascade path is what SQL
        // Server refuses. A record with a card on it is unlinked before it is removed.
        builder.HasOne<CreativeContext>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.CreativeContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.RecipeId, card.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PromptRecord>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.PromptRecordId })
            .HasPrincipalKey(record => new { record.WorkspaceId, record.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.MediaAssetId, card.MediaAssetVersionNumber })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SocialPackage>()
            .WithMany()
            .HasForeignKey(card => new { card.WorkspaceId, card.SocialPackageId })
            .HasPrincipalKey(package => new { package.WorkspaceId, package.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The board: one column read in order. Not unique — two cards may hold one rank, and the id that
        // closes the index is what makes their order the same on every read.
        builder.HasIndex(card => new { card.WorkspaceId, card.Column, card.SortRank, card.Id })
            .HasDatabaseName("IX_ContentCards_Workspace_Column_Rank");

        // One pipeline card per piece of work (13.7a), kept by the store rather than by a key that expires: the
        // commit can be sent again at any time, by two requests at once, and there is still one card. Cards
        // put on the board by hand for the same work are outside the filter and unaffected.
        //
        // Named, so it sits beside the plain index on the same two columns instead of replacing it: that one
        // serves the foreign key for every card, and a filtered index cannot.
        builder.HasIndex(card => new { card.WorkspaceId, card.CreativeContextId }, "UX_ContentCards_Workspace_PipelineContext")
            .IsUnique()
            .HasFilter($"Origin = {(int)ContentCardOrigin.ContentPipeline}");
        builder.HasIndex(card => new { card.WorkspaceId, card.CreativeContextId })
            .HasDatabaseName("IX_ContentCards_WorkspaceId_CreativeContextId");

        // The board's filters (KAN-001): by channel, by exact date, and by weekday.
        builder.HasIndex(card => new { card.WorkspaceId, card.ChannelKey })
            .HasDatabaseName("IX_ContentCards_Workspace_Channel");
        builder.HasIndex(card => new { card.WorkspaceId, card.PlannedDate })
            .HasDatabaseName("IX_ContentCards_Workspace_PlannedDate");
        builder.HasIndex(card => new { card.WorkspaceId, card.PlannedDayOfWeek })
            .HasDatabaseName("IX_ContentCards_Workspace_PlannedDay");
    }
}
