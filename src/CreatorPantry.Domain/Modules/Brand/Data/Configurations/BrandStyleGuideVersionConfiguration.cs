using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideVersionConfiguration : IEntityTypeConfiguration<BrandStyleGuideVersion>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideVersion> builder)
    {
        builder.ToTable("BrandStyleGuideVersions", table =>
        {
            table.HasCheckConstraint("CK_BrandStyleGuideVersions_VersionNumber_Positive", "VersionNumber >= 1");

            // Version 1 was edited from nothing, and no version is its own parent.
            table.HasCheckConstraint(
                "CK_BrandStyleGuideVersions_Parent_VersionNumber",
                "VersionNumber > 1 OR ParentVersionId IS NULL");
            table.HasCheckConstraint(
                "CK_BrandStyleGuideVersions_Parent_NotSelf",
                "ParentVersionId IS NULL OR ParentVersionId <> Id");
        });

        builder.HasKey(version => version.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(version => version.Id).ValueGeneratedNever();

        // Target of the sections', rules', source links' and approval's foreign keys.
        builder.HasAlternateKey(version => new { version.WorkspaceId, version.Id })
            .HasName("AK_BrandStyleGuideVersions_Workspace_Id");

        // Target of the parent edge, which carries the guide so a parent can only be the same guide's.
        builder.HasAlternateKey(version => new { version.WorkspaceId, version.BrandStyleGuideId, version.Id })
            .HasName("AK_BrandStyleGuideVersions_Workspace_Guide_Id");

        builder.Property(version => version.VersionNumber).IsRequired();
        builder.Property(version => version.ChangeReason).HasMaxLength(BrandPolicy.ReasonMaxLength);
        builder.Property(version => version.CreatedByMembershipId).IsRequired();
        builder.Property(version => version.CreatedAt).IsRequired();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: versions are the guide's history, and no ordinary delete of the guide may take them.
        builder.HasOne<BrandStyleGuide>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.BrandStyleGuideId })
            .HasPrincipalKey(guide => new { guide.WorkspaceId, guide.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BrandStyleGuideVersion>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.BrandStyleGuideId, version.ParentVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.BrandStyleGuideId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One version number per guide, which also finds the latest: the highest. The second guard on a lost
        // race: two edits that both read version N cannot both write N + 1.
        builder.HasIndex(version => new { version.WorkspaceId, version.BrandStyleGuideId, version.VersionNumber })
            .IsUnique()
            .HasDatabaseName("UX_BrandStyleGuideVersions_Workspace_Guide_VersionNumber");
    }
}
