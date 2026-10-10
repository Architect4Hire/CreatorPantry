using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class SocialRevisionConfiguration : IEntityTypeConfiguration<SocialRevision>
{
    public void Configure(EntityTypeBuilder<SocialRevision> builder)
    {
        builder.ToTable("SocialRevisions", table =>
        {
            table.HasCheckConstraint("CK_SocialRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
            table.HasCheckConstraint("CK_SocialRevisions_Body_NotBlank", "trim(Body) <> ''");

            // Revision 1 has no parent and every later one has exactly one; none is its own parent.
            table.HasCheckConstraint(
                "CK_SocialRevisions_Parent_RevisionNumber",
                "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_SocialRevisions_Parent_NotSelf",
                "ParentRevisionId IS NULL OR ParentRevisionId <> Id");

            // A proposal id belongs to a generated revision and nothing else, so provenance cannot disagree with source.
            table.HasCheckConstraint(
                "CK_SocialRevisions_AiProposal_Source",
                $"(AiProposalId IS NOT NULL AND Source = {(int)ContentRevisionSource.AiGenerated}) OR "
                    + $"(AiProposalId IS NULL AND Source <> {(int)ContentRevisionSource.AiGenerated})");

            // A generated revision names the template that wrote it. Edits carry the parent's forward, or none.
            table.HasCheckConstraint(
                "CK_SocialRevisions_Template_Generated",
                $"Source <> {(int)ContentRevisionSource.AiGenerated} OR "
                    + "(PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");

            // A reaffirmation re-pins an earlier revision's body, so it always has a parent.
            table.HasCheckConstraint(
                "CK_SocialRevisions_Reaffirmed_HasParent",
                $"Source <> {(int)ContentRevisionSource.Reaffirmed} OR ParentRevisionId IS NOT NULL");

            // A recipe pin is a recipe and one of its versions, or neither. Half a pin would name a recipe
            // with nothing to compare, and the composite key below does not check a row with a null in it.
            table.HasCheckConstraint(
                "CK_SocialRevisions_RecipePin_Whole",
                "(RecipeId IS NULL AND RecipeVersionId IS NULL) OR (RecipeId IS NOT NULL AND RecipeVersionId IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_SocialRevisions_CharacterCount_NonNegative",
                "CharacterCount IS NULL OR CharacterCount >= 0");
            table.HasCheckConstraint(
                "CK_SocialRevisions_CharacterLimit_Positive",
                "CharacterLimit IS NULL OR CharacterLimit >= 1");

            // The stored verdict agrees with the numbers beside it, so no reader has to decide which to believe.
            table.HasCheckConstraint(
                "CK_SocialRevisions_Limit_Status",
                $"(LimitStatus = {(int)SocialLimitStatus.NotChecked} AND CharacterCount IS NULL "
                    + "AND CharacterLimit IS NULL AND ChannelProfileVersion IS NULL) OR "
                    + $"(LimitStatus = {(int)SocialLimitStatus.Within} AND CharacterCount IS NOT NULL "
                    + "AND ChannelProfileVersion IS NOT NULL AND (CharacterLimit IS NULL OR CharacterCount <= CharacterLimit)) OR "
                    + $"(LimitStatus = {(int)SocialLimitStatus.Over} AND CharacterCount IS NOT NULL "
                    + "AND ChannelProfileVersion IS NOT NULL AND CharacterLimit IS NOT NULL AND CharacterCount > CharacterLimit)");
        });

        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.Id).ValueGeneratedNever();

        // Target of the channel's accepted pointer and the parent edge.
        builder.HasAlternateKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.Id })
            .HasName("AK_SocialRevisions_Workspace_Channel_Id");

        builder.Property(revision => revision.RevisionNumber).IsRequired();
        builder.Property(revision => revision.Source).IsRequired();
        builder.Property(revision => revision.Body).IsRequired();
        builder.Property(revision => revision.LimitStatus).IsRequired();
        builder.Property(revision => revision.ChannelProfileVersion).HasMaxLength(ContentPolicy.ChannelProfileVersionMaxLength);
        builder.Property(revision => revision.PromptTemplateId).HasMaxLength(ContentPolicy.TemplateIdMaxLength);
        builder.Property(revision => revision.PromptTemplateVersion).HasMaxLength(ContentPolicy.TemplateVersionMaxLength);
        builder.Property(revision => revision.PromptTemplateBodyChecksum).HasMaxLength(ContentPolicy.ChecksumMaxLength);
        builder.Property(revision => revision.CreativeContextVersion).HasMaxLength(ContentPolicy.ContextVersionMaxLength);
        builder.Property(revision => revision.ContextPackageChecksum).HasMaxLength(ContentPolicy.ChecksumMaxLength);
        builder.Property(revision => revision.CreatedByMembershipId).IsRequired();
        builder.Property(revision => revision.CreatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(revision => revision.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict throughout below: revisions are history and outlive what would otherwise remove them.
        builder.HasOne<SocialPackageChannel>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId })
            .HasPrincipalKey(channel => new { channel.WorkspaceId, channel.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The parent is a revision of the same channel.
        builder.HasOne<SocialRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.ParentRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace. Unpinned rows carry nulls and are not checked.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.RecipeId, revision.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BrandProfileRevision>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.BrandProfileRevisionId })
            .HasPrincipalKey(profileRevision => new { profileRevision.WorkspaceId, profileRevision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Paired like every other pin. ContentRevision's copy of this column predates the table and has no
        // key; this one never had that excuse.
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

        builder.HasIndex(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UX_SocialRevisions_Workspace_Channel_RevisionNumber");

        // Staleness propagation finds every post written against a recipe version. "Written about a recipe"
        // needs no index of its own: the recipe-version key's index already leads with workspace and recipe.
        builder.HasIndex(revision => new { revision.WorkspaceId, revision.RecipeVersionId })
            .HasFilter("RecipeVersionId IS NOT NULL")
            .HasDatabaseName("IX_SocialRevisions_Workspace_RecipeVersion");
    }
}
