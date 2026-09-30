using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class ContentRevisionConfiguration : IEntityTypeConfiguration<ContentRevision>
{
    public void Configure(EntityTypeBuilder<ContentRevision> builder)
    {
        builder.ToTable("ContentRevisions", table =>
        {
            table.HasCheckConstraint("CK_ContentRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
            table.HasCheckConstraint("CK_ContentRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");

            // Revision 1 has no parent and every later one has exactly one; none is its own parent.
            table.HasCheckConstraint(
                "CK_ContentRevisions_Parent_RevisionNumber",
                "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_ContentRevisions_Parent_NotSelf",
                "ParentRevisionId IS NULL OR ParentRevisionId <> Id");

            // A proposal id belongs to a generated revision and nothing else, so provenance cannot disagree with source.
            table.HasCheckConstraint(
                "CK_ContentRevisions_AiProposal_Source",
                $"(AiProposalId IS NOT NULL AND Source = {(int)ContentRevisionSource.AiGenerated}) OR "
                    + $"(AiProposalId IS NULL AND Source <> {(int)ContentRevisionSource.AiGenerated})");

            // A generated revision names the template that wrote it. Edits carry the parent's forward, or none.
            table.HasCheckConstraint(
                "CK_ContentRevisions_Template_Generated",
                $"Source <> {(int)ContentRevisionSource.AiGenerated} OR "
                    + "(PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");

            // A reaffirmation re-pins an earlier revision's content, so it always has a parent.
            table.HasCheckConstraint(
                "CK_ContentRevisions_Reaffirmed_HasParent",
                $"Source <> {(int)ContentRevisionSource.Reaffirmed} OR ParentRevisionId IS NOT NULL");
        });

        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.Id).ValueGeneratedNever();

        // Target of the proposal's accepted pointer, the transition's revision reference, and the parent edge.
        builder.HasAlternateKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.Id })
            .HasName("AK_ContentRevisions_Workspace_Proposal_Id");

        builder.Property(revision => revision.RevisionNumber).IsRequired();
        builder.Property(revision => revision.Source).IsRequired();
        builder.Property(revision => revision.PromptTemplateId).HasMaxLength(ContentPolicy.TemplateIdMaxLength);
        builder.Property(revision => revision.PromptTemplateVersion).HasMaxLength(ContentPolicy.TemplateVersionMaxLength);
        builder.Property(revision => revision.PromptTemplateBodyChecksum).HasMaxLength(ContentPolicy.ChecksumMaxLength);
        builder.Property(revision => revision.SchemaVersion).IsRequired();
        builder.Property(revision => revision.Content).IsRequired();
        builder.Property(revision => revision.CreatedByMembershipId).IsRequired();
        builder.Property(revision => revision.CreatedAt).IsRequired();
        builder.Ignore(revision => revision.Pins);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(revision => revision.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict throughout below: revisions are history and outlive what would otherwise remove them.
        // The recipe id can only be the proposal's own.
        builder.HasOne<ContentProposal>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.RecipeId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id, proposal.RecipeId })
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.RecipeId, revision.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The parent is a revision of the same proposal.
        builder.HasOne<ContentRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.ParentRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BrandProfileRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.BrandProfileRevisionId })
            .HasPrincipalKey(profileRevision => new { profileRevision.WorkspaceId, profileRevision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProposal>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UX_ContentRevisions_Workspace_Proposal_RevisionNumber");

        // Staleness propagation finds every revision written against a recipe version, or a brand revision.
        builder.HasIndex(revision => new { revision.WorkspaceId, revision.RecipeVersionId })
            .HasDatabaseName("IX_ContentRevisions_Workspace_RecipeVersion");
        builder.HasIndex(revision => new { revision.WorkspaceId, revision.BrandProfileRevisionId })
            .HasDatabaseName("IX_ContentRevisions_Workspace_BrandProfileRevision");
    }
}
