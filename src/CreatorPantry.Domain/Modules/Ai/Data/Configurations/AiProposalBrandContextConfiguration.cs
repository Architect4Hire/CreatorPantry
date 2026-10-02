using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiProposalBrandContextConfiguration : IEntityTypeConfiguration<AiProposalBrandContext>
{
    public void Configure(EntityTypeBuilder<AiProposalBrandContext> builder)
    {
        builder.ToTable("AiProposalBrandContexts", table =>
        {
            // A guide is named by id, version id and version number together or not at all. The assembler only
            // ever sets the three as a unit — it resolves a version before it will name a guide — and a row
            // holding some of them is a row nobody can act on: an id with no version cannot say what was read,
            // and a version with no guide cannot be found again.
            table.HasCheckConstraint(
                "CK_AiProposalBrandContexts_Guide_AllOrNone",
                "(BrandGuideId IS NULL AND BrandGuideVersionId IS NULL AND BrandGuideVersionNumber IS NULL) OR "
                    + "(BrandGuideId IS NOT NULL AND BrandGuideVersionId IS NOT NULL AND BrandGuideVersionNumber IS NOT NULL)");

            // No guide means no claim about which version was active. False would otherwise read as "an older
            // version was used", which is a different fact from "there was nothing to use".
            table.HasCheckConstraint(
                "CK_AiProposalBrandContexts_Active_Requires_Guide",
                "BrandGuideId IS NOT NULL OR GuideWasActiveVersion = CAST(0 AS bit)");

            // An audience and where it came from travel together. A resolved audience whose origin was not
            // recorded cannot be told apart from one the creator typed, which is the distinction the column
            // exists to keep.
            table.HasCheckConstraint(
                "CK_AiProposalBrandContexts_Audience_HasOrigin",
                "(Audience IS NULL AND AudienceOrigin IS NULL) OR (Audience IS NOT NULL AND AudienceOrigin IS NOT NULL)");

            // BrandContextOrigin has no zero member, so zero here is a field somebody forgot to set rather than
            // a declared origin. Written as a comparison to zero instead of a list of members on purpose: the
            // enum gains members as the product gains sources of precedence, and a list would need this
            // constraint migrated every time it did.
            table.HasCheckConstraint(
                "CK_AiProposalBrandContexts_AudienceOrigin_Declared",
                "AudienceOrigin IS NULL OR AudienceOrigin <> 0");

            // A profile revision counts from one; the token estimate and the two content counts from zero.
            table.HasCheckConstraint(
                "CK_AiProposalBrandContexts_Counts_NonNegative",
                "(BrandProfileRevision IS NULL OR BrandProfileRevision > 0) AND EstimatedTokens >= 0 AND "
                    + "GuidanceSectionCount >= 0 AND RuleCount >= 0");
        });

        builder.HasKey(context => context.Id);

        // The target of the source rows' composite foreign key, as AiProposals provides for its own children.
        builder.HasAlternateKey(context => new { context.WorkspaceId, context.Id })
            .HasName("AK_AiProposalBrandContexts_Workspace_Id");

        builder.Property(context => context.ChannelKey)
            .HasMaxLength(AiPolicy.BrandContextChannelKeyMaxLength);

        builder.Property(context => context.Audience)
            .HasMaxLength(AiPolicy.BrandContextAudienceMaxLength);

        builder.Property(context => context.Checksum)
            .IsRequired()
            .HasMaxLength(AiPolicy.ChecksumMaxLength);

        builder.Property(context => context.AssembledAt).IsRequired();

        // No foreign key to Workspaces, and none to the brand module's guide tables either, for the reason
        // AiProposalConfiguration records about its own missing edge: the proposal already cascades from
        // Workspaces through its operation and a style guide does too, so a second edge would leave SQL Server
        // with two cascade paths into this table and it refuses the DDL outright. Restrict was the alternative
        // and is worse — it would make archiving a style guide fail once any generation had been grounded on it.
        //
        // One row per proposal, enforced by the relationship rather than by an index of its own: this is the
        // proposal's own provenance, not a list it keeps.
        builder.HasOne<AiProposal>()
            .WithOne(proposal => proposal.BrandContext)
            .HasForeignKey<AiProposalBrandContext>(context => new { context.WorkspaceId, context.AiProposalId })
            .HasPrincipalKey<AiProposal>(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Which generations were grounded on one guide version — what makes a guide change reviewable after the
        // fact, the way IX_AiProposals_Workspace_Template does for a prompt change.
        builder.HasIndex(context => new { context.WorkspaceId, context.BrandGuideVersionId })
            .HasDatabaseName("IX_AiProposalBrandContexts_Workspace_GuideVersion");

        // Which generations carried identical brand content. Two proposals sharing a checksum were grounded on
        // the same words, which is how a guide edit is told apart from a prompt or model change.
        builder.HasIndex(context => new { context.WorkspaceId, context.Checksum })
            .HasDatabaseName("IX_AiProposalBrandContexts_Workspace_Checksum");
    }
}
