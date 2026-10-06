using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaAssets");
        builder.HasKey(asset => asset.Id);

        // What every workspace-scoped child points at — the asset's versions, tags and utilization, and
        // the three link tables in other modules that have been waiting for it since Phase 2. A plain
        // MediaAssetId -> MediaAssets(Id) would still let one workspace's recipe link another's
        // photograph, which is exactly what RecipeAssetLinkConfiguration said this key had to prevent.
        builder.HasAlternateKey(asset => new { asset.WorkspaceId, asset.Id });

        builder.Property(asset => asset.Title)
            .IsRequired()
            .HasMaxLength(MediaPolicy.AssetTitleMaxLength);

        builder.Property(asset => asset.Description).HasMaxLength(MediaPolicy.AssetDescriptionMaxLength);
        builder.Property(asset => asset.AltText).HasMaxLength(MediaPolicy.AltTextMaxLength);
        builder.Property(asset => asset.ChannelKey).HasMaxLength(MediaPolicy.VocabularyKeyMaxLength);
        builder.Property(asset => asset.PlatformKey).HasMaxLength(MediaPolicy.VocabularyKeyMaxLength);
        builder.Property(asset => asset.StyleKey).HasMaxLength(MediaPolicy.VocabularyKeyMaxLength);
        builder.Property(asset => asset.RightsHolder).HasMaxLength(MediaPolicy.RightsTextMaxLength);
        builder.Property(asset => asset.AttributionText).HasMaxLength(MediaPolicy.RightsTextMaxLength);

        builder.Property(asset => asset.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(asset => asset.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Platform reference vocabulary: shared, not workspace-owned, and restricted so a cuisine cannot be
        // retired out from under a library that classified by it. The same shape Recipe uses.
        builder.HasOne<Cuisine>()
            .WithMany()
            .HasForeignKey(asset => asset.CuisineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(asset => asset.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        // DAM-002's default page: this workspace's live assets, newest first, with the id as the tie-break
        // so paging is a total order rather than nearly one. Filtered, because a soft-deleted asset is
        // excluded from every ordinary read and an index that carried them would grow for no reader.
        builder.HasIndex(asset => new { asset.WorkspaceId, asset.CreatedAt, asset.Id })
            .IsDescending(false, true, true)
            .HasFilter("DeletedAt IS NULL")
            .HasDatabaseName("IX_MediaAssets_Workspace_CreatedAt");

        // Alphabetical listing, the other sort DAM-002 offers. Titles are deliberately not unique — two of
        // a creator's photographs may share one — which is why the tie-break is part of the key.
        builder.HasIndex(asset => new { asset.WorkspaceId, asset.Title, asset.Id })
            .HasFilter("DeletedAt IS NULL")
            .HasDatabaseName("IX_MediaAssets_Workspace_Title");

        // No index per filter, deliberately, and for the reason RecipeConfiguration gives at length:
        // channel, platform, day, style, cuisine, course, the date bounds and the free-text match are
        // residual predicates evaluated after one of the seeks above has already narrowed the read to one
        // workspace. A creator's library is thousands of rows, not millions, and an index for each filter
        // combination would be paid for on every edit to save nothing measurable on a read.

        builder.ToTable(table =>
        {
            // A row that forgot to say what it was must not pass for the first real member of the enum.
            table.HasCheckConstraint("CK_MediaAssets_Kind_Declared", "Kind <> 0");

            // trim rather than LEN: a check constraint is created verbatim by every harness including the
            // SQLite ones, and SQLite has no LEN — which would fail CREATE TABLE and so fail every test in
            // every SQLite fixture, on a table none of them touch.
            table.HasCheckConstraint("CK_MediaAssets_Title_NotBlank", "trim(Title) <> ''");

            table.HasCheckConstraint(
                "CK_MediaAssets_CurrentVersionNumber_Positive", "CurrentVersionNumber >= 1");

            // A tombstone says both who and when, or neither. Half of one is a deletion nobody can account
            // for, which is the opposite of what DAM-005 asks a soft delete to be.
            table.HasCheckConstraint(
                "CK_MediaAssets_Deleted_Complete",
                "(DeletedAt IS NULL AND DeletedByMembershipId IS NULL) "
                    + "OR (DeletedAt IS NOT NULL AND DeletedByMembershipId IS NOT NULL)");
        });
    }
}
