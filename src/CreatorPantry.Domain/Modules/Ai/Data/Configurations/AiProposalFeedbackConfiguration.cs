using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiProposalFeedbackConfiguration : IEntityTypeConfiguration<AiProposalFeedback>
{
    public void Configure(EntityTypeBuilder<AiProposalFeedback> builder)
    {
        builder.ToTable("AiProposalFeedback", table => table.HasCheckConstraint(
            // Feedback that says nothing is not feedback. Without this the table accumulates rows recording
            // only that someone opened the panel.
            "CK_AiProposalFeedback_SaysSomething",
            "WasHelpful IS NOT NULL OR Comment IS NOT NULL"));

        builder.HasKey(feedback => feedback.Id);

        builder.Property(feedback => feedback.MembershipId).IsRequired();
        builder.Property(feedback => feedback.CreatedAt).IsRequired();
        builder.Property(feedback => feedback.Comment).HasMaxLength(AiPolicy.MessageMaxLength);

        // See AiStructuredChangeConfiguration for why the child carries WorkspaceId but no foreign key to
        // Workspaces, and why the key into the parent is composite.
        builder.HasOne<AiProposal>()
            .WithMany(proposal => proposal.Feedback)
            .HasForeignKey(feedback => new { feedback.WorkspaceId, feedback.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Deliberately not unique on (proposal, membership): a creator who changes their mind leaves another
        // row rather than editing the first, which is what makes this usable for judging a prompt later.
        builder.HasIndex(feedback => new { feedback.WorkspaceId, feedback.AiProposalId, feedback.CreatedAt })
            .HasDatabaseName("IX_AiProposalFeedback_Workspace_Proposal_CreatedAt");
    }
}
