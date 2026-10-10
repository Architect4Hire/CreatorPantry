using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiProposalCreativeContextConfiguration : IEntityTypeConfiguration<AiProposalCreativeContext>
{
    public void Configure(EntityTypeBuilder<AiProposalCreativeContext> builder)
    {
        builder.ToTable("AiProposalCreativeContexts", table =>
        {
            // A recipe pin is a recipe and one of its versions, or neither.
            table.HasCheckConstraint(
                "CK_AiProposalCreativeContexts_RecipePin_Whole",
                "(RecipeId IS NULL AND RecipeVersionId IS NULL) OR (RecipeId IS NOT NULL AND RecipeVersionId IS NOT NULL)");

            table.HasCheckConstraint("CK_AiProposalCreativeContexts_EstimatedTokens_NonNegative", "EstimatedTokens >= 0");
        });

        builder.HasKey(context => context.Id);
        builder.Property(context => context.Id).ValueGeneratedNever();

        builder.Property(context => context.CreativeContextId).IsRequired();
        builder.Property(context => context.ContextVersion).HasMaxLength(AiPolicy.CreativeContextVersionMaxLength);
        builder.Property(context => context.Checksum).IsRequired().HasMaxLength(AiPolicy.ChecksumMaxLength);
        builder.Property(context => context.AssembledAt).IsRequired();

        // No foreign key to Workspaces, CreativeContexts or RecipeVersions, for the reason
        // AiProposalBrandContextConfiguration gives about its own missing edges: the proposal already cascades
        // from Workspaces through its operation, and each of those tables cascades from Workspaces too, so
        // another edge would be a second cascade path that SQL Server refuses. The handler reads the context
        // through the content module's facade, under the workspace filter, before any of these ids is written.
        //
        // One row per proposal, enforced by the relationship: this is the proposal's own provenance.
        builder.HasOne<AiProposal>()
            .WithOne(proposal => proposal.CreativeContext)
            .HasForeignKey<AiProposalCreativeContext>(context => new { context.WorkspaceId, context.AiProposalId })
            .HasPrincipalKey<AiProposal>(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // "What has been generated for this piece of work."
        builder.HasIndex(context => new { context.WorkspaceId, context.CreativeContextId })
            .HasDatabaseName("IX_AiProposalCreativeContexts_Workspace_Context");
    }
}
