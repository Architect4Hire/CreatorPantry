using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class LongFormDraftConfiguration : IEntityTypeConfiguration<LongFormDraft>
{
    public void Configure(EntityTypeBuilder<LongFormDraft> builder)
    {
        builder.ToTable("LongFormDrafts", table =>
        {
            // Stored as their integers, and an enum column accepts any integer.
            table.HasCheckConstraint(
                "CK_LongFormDrafts_Kind_Range",
                $"Kind >= {(int)LongFormKind.BlogArticle} AND Kind <= {(int)LongFormKind.NewsletterIssue}");
            table.HasCheckConstraint(
                "CK_LongFormDrafts_Status_Range",
                $"Status >= {(int)LongFormDraftStatus.Draft} AND Status <= {(int)LongFormDraftStatus.NeedsReview}");

            // An acceptance claim needs something accepted.
            table.HasCheckConstraint(
                "CK_LongFormDrafts_Accepted_HasRevision",
                $"Status NOT IN ({(int)LongFormDraftStatus.Accepted}, {(int)LongFormDraftStatus.NeedsReview}) "
                    + "OR AcceptedRevisionId IS NOT NULL");

            // The instant of acceptance travels with the accepted pointer: neither without the other.
            table.HasCheckConstraint(
                "CK_LongFormDrafts_AcceptedAt_Revision",
                "(AcceptedRevisionId IS NULL AND AcceptedAt IS NULL) OR "
                    + "(AcceptedRevisionId IS NOT NULL AND AcceptedAt IS NOT NULL)");

            // Staleness is a state, not a residue: present exactly while NeedsReview.
            table.HasCheckConstraint(
                "CK_LongFormDrafts_Staleness_Status",
                $"(Status = {(int)LongFormDraftStatus.NeedsReview} AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR "
                    + $"(Status <> {(int)LongFormDraftStatus.NeedsReview} AND StaleSince IS NULL AND StaleReasons = 0)");
        });

        builder.HasKey(draft => draft.Id);

        // Set by the application, never the store.
        builder.Property(draft => draft.Id).ValueGeneratedNever();

        // Target of the revision's composite key.
        builder.HasAlternateKey(draft => new { draft.WorkspaceId, draft.Id })
            .HasName("AK_LongFormDrafts_Workspace_Id");

        builder.Property(draft => draft.Kind).IsRequired();
        builder.Property(draft => draft.Status).IsRequired();
        builder.Property(draft => draft.StaleReasons).IsRequired();
        builder.Property(draft => draft.CreatedByMembershipId).IsRequired();
        builder.Property(draft => draft.CreatedAt).IsRequired();
        builder.Property(draft => draft.UpdatedAt).IsRequired();
        builder.Property(draft => draft.RowVersion).IsRowVersion();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(draft => draft.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace-paired, so a draft for another workspace's context is unrepresentable. Restricted: a
        // context is archived rather than removed, and a second cascade path is what SQL Server refuses.
        builder.HasOne<CreativeContext>()
            .WithMany()
            .HasForeignKey(draft => new { draft.WorkspaceId, draft.CreativeContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The accepted pointer can only name a revision of this same draft. Restrict: accepted history is retained.
        builder.HasOne<LongFormRevision>()
            .WithMany()
            .HasForeignKey(draft => new { draft.WorkspaceId, draft.Id, draft.AcceptedRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.LongFormDraftId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Deliberately not unique: a creator writes more than one piece of a kind for the same work (B-32).
        builder.HasIndex(draft => new { draft.WorkspaceId, draft.CreativeContextId, draft.Kind })
            .HasDatabaseName("IX_LongFormDrafts_Workspace_Context_Kind");

        // "What needs my review", and the staleness sweep's candidates.
        builder.HasIndex(draft => new { draft.WorkspaceId, draft.Status })
            .HasDatabaseName("IX_LongFormDrafts_Workspace_Status");
    }
}
