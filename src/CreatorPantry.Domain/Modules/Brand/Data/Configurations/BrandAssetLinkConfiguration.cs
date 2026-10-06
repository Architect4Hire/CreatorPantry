using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandAssetLinkConfiguration : IEntityTypeConfiguration<BrandAssetLink>
{
    public void Configure(EntityTypeBuilder<BrandAssetLink> builder)
    {
        builder.ToTable("BrandAssetLinks", table =>
        {
            table.HasCheckConstraint("CK_BrandAssetLinks_SortOrder_NonNegative", "SortOrder >= 0");
            table.HasCheckConstraint("CK_BrandAssetLinks_Role_Specified", "Role <> 0");
        });

        builder.HasKey(link => link.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(link => link.Id).ValueGeneratedNever();

        builder.Property(link => link.MediaAssetId).IsRequired();
        builder.Property(link => link.Role).IsRequired();
        builder.Property(link => link.SortOrder).IsRequired();

        builder.HasOne<BrandProfile>()
            .WithMany(profile => profile.AssetLinks)
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The constraint this comment used to promise, now that 12.9 has landed the aggregate. Composite
        // so one workspace's profile cannot show another's logo, and restricted because deleting a link
        // never deletes the asset and Workspace already cascades here through BrandProfile.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.WorkspaceId, link.BrandProfileId, link.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_BrandAssetLinks_Workspace_Profile_Order");

        // "Where is this image used", which the media library needs before it will let anything be deleted.
        builder.HasIndex(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasDatabaseName("IX_BrandAssetLinks_Workspace_MediaAsset");

        // At most one primary logo per profile.
        builder.HasIndex(link => new { link.WorkspaceId, link.BrandProfileId })
            .IsUnique()
            .HasFilter($"Role = {(int)BrandAssetRole.PrimaryLogo}")
            .HasDatabaseName("UX_BrandAssetLinks_Workspace_Profile_PrimaryLogo");
    }
}
