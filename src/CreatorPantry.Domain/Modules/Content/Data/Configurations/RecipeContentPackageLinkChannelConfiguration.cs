using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class RecipeContentPackageLinkChannelConfiguration : IEntityTypeConfiguration<RecipeContentPackageLinkChannel>
{
    public void Configure(EntityTypeBuilder<RecipeContentPackageLinkChannel> builder)
    {
        builder.ToTable("RecipeContentPackageLinkChannels", table =>
        {
            table.HasCheckConstraint("CK_RecipeContentPackageLinkChannels_SortOrder_NonNegative", "SortOrder >= 0");
            table.HasCheckConstraint("CK_RecipeContentPackageLinkChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");

            // A revision is a revision of one channel's post: both, or neither.
            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinkChannels_Post_Paired",
                "(SocialPackageChannelId IS NULL AND SocialRevisionId IS NULL) OR "
                    + "(SocialPackageChannelId IS NOT NULL AND SocialRevisionId IS NOT NULL)");
        });

        builder.HasKey(channel => channel.Id);
        builder.Property(channel => channel.Id).ValueGeneratedNever();

        builder.Property(channel => channel.ChannelKey).IsRequired().HasMaxLength(ContentPolicy.ChannelKeyMaxLength);
        builder.Property(channel => channel.SortOrder).IsRequired();

        // One cascade path: workspace, card, link, this.
        builder.HasOne<RecipeContentPackageLink>()
            .WithMany(link => link.Channels)
            .HasForeignKey(channel => new { channel.WorkspaceId, channel.RecipeContentPackageLinkId })
            .HasPrincipalKey(link => new { link.WorkspaceId, link.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // A revision of *that channel's* post, in this workspace.
        builder.HasOne<SocialRevision>()
            .WithMany()
            .HasForeignKey(channel => new { channel.WorkspaceId, channel.SocialPackageChannelId, channel.SocialRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // A package names a channel once.
        builder.HasIndex(channel => new { channel.WorkspaceId, channel.RecipeContentPackageLinkId, channel.ChannelKey })
            .IsUnique()
            .HasDatabaseName("UX_RecipeContentPackageLinkChannels_Workspace_Link_Channel");
        builder.HasIndex(channel => new { channel.WorkspaceId, channel.RecipeContentPackageLinkId, channel.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeContentPackageLinkChannels_Workspace_Link_SortOrder");
    }
}
