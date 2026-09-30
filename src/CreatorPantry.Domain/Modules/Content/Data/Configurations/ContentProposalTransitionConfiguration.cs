using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class ContentProposalTransitionConfiguration : IEntityTypeConfiguration<ContentProposalTransition>
{
    public void Configure(EntityTypeBuilder<ContentProposalTransition> builder)
    {
        builder.ToTable("ContentProposalTransitions", table =>
        {
            table.HasCheckConstraint(
                "CK_ContentProposalTransitions_StaleReasons_Status",
                $"(ToStatus = {(int)ContentProposalStatus.NeedsReview} AND StaleReasons <> 0) OR "
                    + $"(ToStatus <> {(int)ContentProposalStatus.NeedsReview} AND StaleReasons = 0)");

            // Only the system marks content stale; every other move has a person behind it.
            table.HasCheckConstraint(
                "CK_ContentProposalTransitions_Actor_Status",
                $"(ToStatus = {(int)ContentProposalStatus.NeedsReview} AND ActorMembershipId IS NULL) OR "
                    + $"(ToStatus <> {(int)ContentProposalStatus.NeedsReview} AND ActorMembershipId IS NOT NULL)");
        });

        builder.HasKey(transition => transition.Id);
        builder.Property(transition => transition.Id).ValueGeneratedNever();

        builder.Property(transition => transition.ToStatus).IsRequired();
        builder.Property(transition => transition.Reason).HasMaxLength(ContentPolicy.ReasonMaxLength);
        builder.Property(transition => transition.StaleReasons).IsRequired();
        builder.Property(transition => transition.OccurredAt).IsRequired();
        builder.Property(transition => transition.MachineVersion).IsRequired().HasMaxLength(ContentPolicy.MachineVersionMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(transition => transition.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ContentProposal>()
            .WithMany()
            .HasForeignKey(transition => new { transition.WorkspaceId, transition.ContentProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The revision the move concerns is one of this proposal's.
        builder.HasOne<ContentRevision>()
            .WithMany()
            .HasForeignKey(transition => new { transition.WorkspaceId, transition.ContentProposalId, transition.ContentRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.ContentProposalId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A proposal's history, in order.
        builder.HasIndex(transition => new { transition.WorkspaceId, transition.ContentProposalId, transition.OccurredAt })
            .HasDatabaseName("IX_ContentProposalTransitions_Workspace_Proposal_OccurredAt");
    }
}
