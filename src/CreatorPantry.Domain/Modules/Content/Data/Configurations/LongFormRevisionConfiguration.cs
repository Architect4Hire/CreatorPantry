using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class LongFormRevisionConfiguration : IEntityTypeConfiguration<LongFormRevision>
{
    public void Configure(EntityTypeBuilder<LongFormRevision> builder)
    {
        builder.ToTable("LongFormRevisions", table =>
        {
            table.HasCheckConstraint("CK_LongFormRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
            table.HasCheckConstraint("CK_LongFormRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");
            table.HasCheckConstraint("CK_LongFormRevisions_Content_NotBlank", "trim(Content) <> ''");

            // Revision 1 has no parent and every later one has exactly one; none is its own parent.
            table.HasCheckConstraint(
                "CK_LongFormRevisions_Parent_RevisionNumber",
                "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_LongFormRevisions_Parent_NotSelf",
                "ParentRevisionId IS NULL OR ParentRevisionId <> Id");

            // A proposal id belongs to a generated revision and nothing else, so provenance cannot disagree with source.
            table.HasCheckConstraint(
                "CK_LongFormRevisions_AiProposal_Source",
                $"(AiProposalId IS NOT NULL AND Source = {(int)ContentRevisionSource.AiGenerated}) OR "
                    + $"(AiProposalId IS NULL AND Source <> {(int)ContentRevisionSource.AiGenerated})");

            // A generated revision names the template that wrote it. Edits carry the parent's forward, or none.
            table.HasCheckConstraint(
                "CK_LongFormRevisions_Template_Generated",
                $"Source <> {(int)ContentRevisionSource.AiGenerated} OR "
                    + "(PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");

            // A reaffirmation re-pins an earlier revision's content, so it always has a parent.
            table.HasCheckConstraint(
                "CK_LongFormRevisions_Reaffirmed_HasParent",
                $"Source <> {(int)ContentRevisionSource.Reaffirmed} OR ParentRevisionId IS NOT NULL");
        });

        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.Id).ValueGeneratedNever();

        // Target of the recipe pin's composite key.
        builder.HasAlternateKey(revision => new { revision.WorkspaceId, revision.Id })
            .HasName("AK_LongFormRevisions_Workspace_Id");

        // Target of the draft's accepted pointer and the parent edge.
        builder.HasAlternateKey(revision => new { revision.WorkspaceId, revision.LongFormDraftId, revision.Id })
            .HasName("AK_LongFormRevisions_Workspace_Draft_Id");

        builder.Property(revision => revision.RevisionNumber).IsRequired();
        builder.Property(revision => revision.Source).IsRequired();
        builder.Property(revision => revision.PromptTemplateId).HasMaxLength(ContentPolicy.TemplateIdMaxLength);
        builder.Property(revision => revision.PromptTemplateVersion).HasMaxLength(ContentPolicy.TemplateVersionMaxLength);
        builder.Property(revision => revision.PromptTemplateBodyChecksum).HasMaxLength(ContentPolicy.ChecksumMaxLength);
        builder.Property(revision => revision.CreativeContextVersion).HasMaxLength(ContentPolicy.ContextVersionMaxLength);
        builder.Property(revision => revision.SchemaVersion).IsRequired();
        builder.Property(revision => revision.Content).IsRequired();
        builder.Property(revision => revision.CreatedByMembershipId).IsRequired();
        builder.Property(revision => revision.CreatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(revision => revision.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict throughout below: revisions are history and outlive what would otherwise remove them.
        builder.HasOne<LongFormDraft>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.LongFormDraftId })
            .HasPrincipalKey(draft => new { draft.WorkspaceId, draft.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The parent is a revision of the same draft.
        builder.HasOne<LongFormRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.LongFormDraftId, revision.ParentRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.LongFormDraftId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BrandProfileRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.BrandProfileRevisionId })
            .HasPrincipalKey(profileRevision => new { profileRevision.WorkspaceId, profileRevision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BrandStyleGuideVersion>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.BrandStyleGuideVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AiProposal>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(revision => new { revision.WorkspaceId, revision.LongFormDraftId, revision.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UX_LongFormRevisions_Workspace_Draft_RevisionNumber");

        // Staleness propagation finds every revision written against a brand revision.
        builder.HasIndex(revision => new { revision.WorkspaceId, revision.BrandProfileRevisionId })
            .HasDatabaseName("IX_LongFormRevisions_Workspace_BrandProfileRevision");
    }
}
