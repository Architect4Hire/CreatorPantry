using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiProposalBrandSourceConfiguration : IEntityTypeConfiguration<AiProposalBrandSource>
{
    public void Configure(EntityTypeBuilder<AiProposalBrandSource> builder)
    {
        builder.ToTable("AiProposalBrandSources", table =>
        {
            // A document version counts from one. An ordinal is the passage's place in its document and a sort
            // order is its place in the prompt; both count from zero.
            table.HasCheckConstraint(
                "CK_AiProposalBrandSources_Positions",
                "DocumentVersionNumber > 0 AND Ordinal >= 0 AND SortOrder >= 0");
        });

        builder.HasKey(source => source.Id);

        // See AiProposalBrandSource for why the document and the passage are ids with no foreign key behind them.
        builder.HasOne<AiProposalBrandContext>()
            .WithMany(context => context.Sources)
            .HasForeignKey(source => new { source.WorkspaceId, source.AiProposalBrandContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One row per position, so a recorded package cannot claim the same slot twice.
        builder.HasIndex(source => new { source.WorkspaceId, source.AiProposalBrandContextId, source.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_AiProposalBrandSources_Context_SortOrder");

        // Which generations cited one document — the read a disconnection, correction or deletion needs, and the
        // reason these rows are worth storing beside the checksum that already pins their text.
        builder.HasIndex(source => new { source.WorkspaceId, source.BrandSourceDocumentId })
            .HasDatabaseName("IX_AiProposalBrandSources_Workspace_Document");
    }
}
