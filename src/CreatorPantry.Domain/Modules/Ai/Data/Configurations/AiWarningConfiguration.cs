using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiWarningConfiguration : IEntityTypeConfiguration<AiWarning>
{
    public void Configure(EntityTypeBuilder<AiWarning> builder)
    {
        builder.ToTable("AiWarnings", table => table.HasCheckConstraint(
            "CK_AiWarnings_Kind_Declared",
            $"Kind <> {(int)AiWarningKind.Unspecified}"));

        builder.HasKey(warning => warning.Id);

        builder.Property(warning => warning.Kind).IsRequired();
        builder.Property(warning => warning.SortOrder).IsRequired();

        builder.Property(warning => warning.Message)
            .IsRequired()
            .HasMaxLength(AiPolicy.MessageMaxLength);

        // See AiStructuredChangeConfiguration for why the child carries WorkspaceId but no foreign key to
        // Workspaces, and why the key into the parent is composite.
        builder.HasOne<AiProposal>()
            .WithMany(proposal => proposal.Warnings)
            .HasForeignKey(warning => new { warning.WorkspaceId, warning.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade, and this is the one edge where that matters for a reason other than
        // retention: a warning attached to a change must not be removed by anything that removes the change
        // alone, because a proposal losing its cautions while keeping its suggestions is the worst outcome
        // this table has. Both rows cascade together from the proposal above regardless.
        builder.HasOne<AiStructuredChange>()
            .WithMany()
            .HasForeignKey(warning => new { warning.WorkspaceId, warning.AiStructuredChangeId })
            .HasPrincipalKey(change => new { change.WorkspaceId, change.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(warning => new { warning.WorkspaceId, warning.AiProposalId, warning.SortOrder })
            .HasDatabaseName("IX_AiWarnings_Workspace_Proposal_SortOrder");
    }
}
