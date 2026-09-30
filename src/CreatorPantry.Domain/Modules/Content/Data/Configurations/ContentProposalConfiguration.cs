using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class ContentProposalConfiguration : IEntityTypeConfiguration<ContentProposal>
{
    public void Configure(EntityTypeBuilder<ContentProposal> builder)
    {
        builder.ToTable("ContentProposals", table =>
        {
            // An acceptance claim needs something accepted.
            table.HasCheckConstraint(
                "CK_ContentProposals_Accepted_HasRevision",
                $"Status NOT IN ({(int)ContentProposalStatus.Accepted}, {(int)ContentProposalStatus.NeedsReview}) "
                    + "OR AcceptedRevisionId IS NOT NULL");

            // Staleness is a state, not a residue: present exactly while NeedsReview.
            table.HasCheckConstraint(
                "CK_ContentProposals_Staleness_Status",
                $"(Status = {(int)ContentProposalStatus.NeedsReview} AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR "
                    + $"(Status <> {(int)ContentProposalStatus.NeedsReview} AND StaleSince IS NULL AND StaleReasons = 0)");
        });

        builder.HasKey(proposal => proposal.Id);
        builder.Property(proposal => proposal.Id).ValueGeneratedNever();

        // Target of the revision's and transition's composite keys.
        builder.HasAlternateKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .HasName("AK_ContentProposals_Workspace_Id");

        // Also carries the recipe, so a revision's RecipeId can only be its proposal's.
        builder.HasAlternateKey(proposal => new { proposal.WorkspaceId, proposal.Id, proposal.RecipeId })
            .HasName("AK_ContentProposals_Workspace_Id_Recipe");

        builder.Property(proposal => proposal.Kind).IsRequired();
        builder.Property(proposal => proposal.Status).IsRequired();
        builder.Property(proposal => proposal.StaleReasons).IsRequired();
        builder.Property(proposal => proposal.CreatedByMembershipId).IsRequired();
        builder.Property(proposal => proposal.CreatedAt).IsRequired();
        builder.Property(proposal => proposal.UpdatedAt).IsRequired();
        builder.Property(proposal => proposal.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(proposal => proposal.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a recipe with derivatives cannot simply be deleted, as with its versions.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.WorkspaceId, proposal.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The accepted pointer can only name a revision of this same proposal. Restrict: accepted history is retained.
        builder.HasOne<ContentRevision>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.WorkspaceId, proposal.Id, proposal.AcceptedRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One slot per recipe and kind; alternatives are revisions.
        builder.HasIndex(proposal => new { proposal.WorkspaceId, proposal.RecipeId, proposal.Kind })
            .IsUnique()
            .HasDatabaseName("UX_ContentProposals_Workspace_Recipe_Kind");

        // "What needs my review", and the staleness sweep's candidates.
        builder.HasIndex(proposal => new { proposal.WorkspaceId, proposal.Status })
            .HasDatabaseName("IX_ContentProposals_Workspace_Status");
    }
}
