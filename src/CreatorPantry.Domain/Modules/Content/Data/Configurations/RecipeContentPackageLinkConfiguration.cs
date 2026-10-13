using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class RecipeContentPackageLinkConfiguration : IEntityTypeConfiguration<RecipeContentPackageLink>
{
    public void Configure(EntityTypeBuilder<RecipeContentPackageLink> builder)
    {
        builder.ToTable("RecipeContentPackageLinks", table =>
        {
            // A revision is a revision of a proposal: both, or neither.
            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinks_Editorial_Paired",
                "(EditorialProposalId IS NULL AND EditorialRevisionId IS NULL) OR "
                    + "(EditorialProposalId IS NOT NULL AND EditorialRevisionId IS NOT NULL)");

            // The window is one answer in five columns. A start with no zone, or local times with no instants,
            // is not a window anybody can honour.
            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinks_Window_Whole",
                "(WindowStartsAt IS NULL AND WindowEndsAt IS NULL AND WindowTimeZoneId IS NULL "
                    + "AND WindowLocalStartsAt IS NULL AND WindowLocalEndsAt IS NULL) OR "
                    + "(WindowStartsAt IS NOT NULL AND WindowEndsAt IS NOT NULL AND WindowTimeZoneId IS NOT NULL "
                    + "AND WindowLocalStartsAt IS NOT NULL AND WindowLocalEndsAt IS NOT NULL)");
            // An allowance is a decision somebody made at a moment: both halves, or neither.
            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                "(NeedsReviewAllowedAt IS NULL AND NeedsReviewAllowedByMembershipId IS NULL "
                    + "AND NeedsReviewAllowedReasons IS NULL) OR "
                    + "(NeedsReviewAllowedAt IS NOT NULL AND NeedsReviewAllowedByMembershipId IS NOT NULL "
                    + "AND NeedsReviewAllowedReasons IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinks_Window_Ordered",
                "WindowLocalStartsAt IS NULL OR WindowLocalEndsAt >= WindowLocalStartsAt");
        });

        builder.HasKey(link => link.Id);

        // Set by the application, never the store.
        builder.Property(link => link.Id).ValueGeneratedNever();

        // Target of the asset and channel rows' composite keys.
        builder.HasAlternateKey(link => new { link.WorkspaceId, link.Id })
            .HasName("AK_RecipeContentPackageLinks_Workspace_Id");

        builder.Property(link => link.RecipeId).IsRequired();
        builder.Property(link => link.RecipeVersionId).IsRequired();
        builder.Property(link => link.WindowTimeZoneId).HasMaxLength(ContentCardPolicy.TimeZoneIdMaxLength);
        builder.Property(link => link.NeedsReviewAllowedReasons).HasMaxLength(ContentCardPolicy.AllowedReasonsMaxLength);
        builder.Property(link => link.CreatedByMembershipId).IsRequired();
        builder.Property(link => link.CreatedAt).IsRequired();

        // Reaches the workspace only through its card, so there is one cascade path into this table, and a
        // card's package goes with the card. Every key below is restricted for that reason as much as for the
        // obvious one: each target cascades from the same workspace.
        builder.HasOne<ContentCard>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.ContentCardId })
            .HasPrincipalKey(card => new { card.WorkspaceId, card.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.RecipeId, link.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A proposal *for that recipe*: a package cannot carry another recipe's editorial.
        builder.HasOne<ContentProposal>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.EditorialProposalId, link.RecipeId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id, proposal.RecipeId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // A revision of *that proposal*.
        builder.HasOne<ContentRevision>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.EditorialProposalId, link.EditorialRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SocialPackage>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.SocialPackageId })
            .HasPrincipalKey(package => new { package.WorkspaceId, package.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // One package per card.
        builder.HasIndex(link => new { link.WorkspaceId, link.ContentCardId })
            .IsUnique()
            .HasDatabaseName("UX_RecipeContentPackageLinks_Workspace_Card");

        // Staleness propagation finds every package put together from a recipe version.
        builder.HasIndex(link => new { link.WorkspaceId, link.RecipeVersionId })
            .HasDatabaseName("IX_RecipeContentPackageLinks_Workspace_RecipeVersion");
    }
}
