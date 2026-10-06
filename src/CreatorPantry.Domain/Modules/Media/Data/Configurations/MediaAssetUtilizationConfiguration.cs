using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class MediaAssetUtilizationConfiguration : IEntityTypeConfiguration<MediaAssetUtilization>
{
    public void Configure(EntityTypeBuilder<MediaAssetUtilization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaAssetUtilizations");
        builder.HasKey(utilization => utilization.Id);

        builder.Property(utilization => utilization.PlatformKey)
            .IsRequired()
            .HasMaxLength(MediaPolicy.VocabularyKeyMaxLength);

        builder.Property(utilization => utilization.CampaignName)
            .HasMaxLength(MediaPolicy.CampaignNameMaxLength);

        builder.Property(utilization => utilization.Notes).HasMaxLength(MediaPolicy.NotesMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(utilization => utilization.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restricted, so Workspace has one cascade path into this table.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(utilization => new { utilization.WorkspaceId, utilization.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // DAM-003's utilization history for one asset, most recent first.
        builder.HasIndex(utilization => new
            {
                utilization.WorkspaceId,
                utilization.MediaAssetId,
                utilization.UtilizedOn,
            })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_MediaAssetUtilizations_Workspace_Asset_UtilizedOn");

        builder.ToTable(table =>
            table.HasCheckConstraint(
                "CK_MediaAssetUtilizations_PlatformKey_NotBlank", "trim(PlatformKey) <> ''"));
    }
}

internal sealed class MediaAssetTagConfiguration : IEntityTypeConfiguration<MediaAssetTag>
{
    public void Configure(EntityTypeBuilder<MediaAssetTag> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaAssetTags");

        // The pair is the identity: an asset carries a tag once or not at all.
        builder.HasKey(tag => new { tag.MediaAssetId, tag.WorkspaceTagId });

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(tag => tag.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<MediaAsset>()
            .WithMany(asset => asset.Tags)
            .HasForeignKey(tag => new { tag.WorkspaceId, tag.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The shared workspace vocabulary the recipe library already uses. Workspace-paired, so one
        // workspace's asset cannot carry another's tag.
        builder.HasOne<WorkspaceTag>()
            .WithMany()
            .HasForeignKey(tag => new { tag.WorkspaceId, tag.WorkspaceTagId })
            .HasPrincipalKey(tag => new { tag.WorkspaceId, tag.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // DAM-002 filtering by tag: every asset carrying this one.
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.WorkspaceTagId, tag.MediaAssetId })
            .HasDatabaseName("IX_MediaAssetTags_Workspace_Tag_Asset");
    }
}
