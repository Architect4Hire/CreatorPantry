using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class SocialPackageChannelConfiguration : IEntityTypeConfiguration<SocialPackageChannel>
{
    public void Configure(EntityTypeBuilder<SocialPackageChannel> builder)
    {
        builder.ToTable("SocialPackageChannels", table =>
        {
            table.HasCheckConstraint("CK_SocialPackageChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");

            // Stored as its integer, and an enum column accepts any integer.
            table.HasCheckConstraint(
                "CK_SocialPackageChannels_Status_Range",
                $"Status >= {(int)ContentProposalStatus.Proposed} AND Status <= {(int)ContentProposalStatus.NeedsReview}");

            // An acceptance claim needs something accepted.
            table.HasCheckConstraint(
                "CK_SocialPackageChannels_Accepted_HasRevision",
                $"Status NOT IN ({(int)ContentProposalStatus.Accepted}, {(int)ContentProposalStatus.NeedsReview}) "
                    + "OR AcceptedRevisionId IS NOT NULL");

            // Staleness is a state, not a residue: present exactly while NeedsReview.
            table.HasCheckConstraint(
                "CK_SocialPackageChannels_Staleness_Status",
                $"(Status = {(int)ContentProposalStatus.NeedsReview} AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR "
                    + $"(Status <> {(int)ContentProposalStatus.NeedsReview} AND StaleSince IS NULL AND StaleReasons = 0)");
        });

        builder.HasKey(channel => channel.Id);

        // Set by the application, never the store. See CreativeContextChannelConfiguration.
        builder.Property(channel => channel.Id).ValueGeneratedNever();

        // Target of the revision's composite key.
        builder.HasAlternateKey(channel => new { channel.WorkspaceId, channel.Id })
            .HasName("AK_SocialPackageChannels_Workspace_Id");

        builder.Property(channel => channel.ChannelKey).IsRequired().HasMaxLength(ContentPolicy.ChannelKeyMaxLength);
        builder.Property(channel => channel.Status).IsRequired();
        builder.Property(channel => channel.StaleReasons).IsRequired();
        builder.Property(channel => channel.CreatedAt).IsRequired();
        builder.Property(channel => channel.UpdatedAt).IsRequired();
        builder.Property(channel => channel.RowVersion).IsRowVersion();

        // Reaches the workspace only through its package, so there is one cascade path into this table.
        builder.HasOne<SocialPackage>()
            .WithMany(package => package.Channels)
            .HasForeignKey(channel => new { channel.WorkspaceId, channel.SocialPackageId })
            .HasPrincipalKey(package => new { package.WorkspaceId, package.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The accepted pointer can only name a revision of this same channel. Restrict: accepted history is retained.
        builder.HasOne<SocialRevision>()
            .WithMany()
            .HasForeignKey(channel => new { channel.WorkspaceId, channel.Id, channel.AcceptedRevisionId })
            .HasPrincipalKey(revision => new { revision.WorkspaceId, revision.SocialPackageChannelId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One output per channel; alternatives are revisions.
        builder.HasIndex(channel => new { channel.WorkspaceId, channel.SocialPackageId, channel.ChannelKey })
            .IsUnique()
            .HasDatabaseName("UX_SocialPackageChannels_Workspace_Package_Channel");

        // "What needs my review", and the staleness sweep's candidates.
        builder.HasIndex(channel => new { channel.WorkspaceId, channel.Status })
            .HasDatabaseName("IX_SocialPackageChannels_Workspace_Status");
    }
}
