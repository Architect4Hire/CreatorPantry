using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class MediaPictureAnalysisConfiguration : IEntityTypeConfiguration<MediaPictureAnalysis>
{
    public void Configure(EntityTypeBuilder<MediaPictureAnalysis> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaPictureAnalyses", table =>
        {
            // Exactly one picture: a version of a library asset, or a generated image. Never both and never
            // neither, so a row can always be found by the picture it is about.
            table.HasCheckConstraint(
                "CK_MediaPictureAnalyses_OnePicture",
                "(MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL AND GeneratedImageId IS NULL) "
                    + "OR (MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_MediaPictureAnalyses_VersionNumber_Range",
                "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");

            table.HasCheckConstraint(
                "CK_MediaPictureAnalyses_ContentChecksum_NotBlank", "trim(ContentChecksum) <> ''");
        });

        builder.HasKey(analysis => analysis.Id);

        // Set by the application, never the store.
        builder.Property(analysis => analysis.Id).ValueGeneratedNever();

        builder.Property(analysis => analysis.ContentChecksum)
            .IsRequired()
            .HasMaxLength(MediaPictureAnalysisPolicy.ChecksumMaxLength);

        // Unbounded in the column and bounded where it is written: the count and length of the observations
        // are held by MediaPictureAnalysisPolicy, which is where a limit a caller can be told about belongs.
        builder.Property(analysis => analysis.ObservationsJson).IsRequired();

        builder.Property(analysis => analysis.PromptTemplateId)
            .IsRequired()
            .HasMaxLength(MediaPictureAnalysisPolicy.PromptTemplateIdMaxLength);

        builder.Property(analysis => analysis.PromptTemplateVersion)
            .IsRequired()
            .HasMaxLength(MediaPictureAnalysisPolicy.PromptTemplateVersionMaxLength);

        builder.Property(analysis => analysis.AnalyzedAt).IsRequired();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(analysis => analysis.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite, on the workspace as well as the id, so a row cannot name another workspace's picture
        // even if something upstream were wrong. Restrict rather than cascade: the workspace cascade above
        // already reaches this table, and a second path to it is what SQL Server refuses.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(analysis => new { analysis.WorkspaceId, analysis.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GeneratedImage>()
            .WithMany()
            .HasForeignKey(analysis => new { analysis.WorkspaceId, analysis.GeneratedImageId })
            .HasPrincipalKey(image => new { image.WorkspaceId, image.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One reading per picture. Filtered, because each identity is null for the other kind of picture.
        builder.HasIndex(analysis => new
            {
                analysis.WorkspaceId,
                analysis.MediaAssetId,
                analysis.MediaAssetVersionNumber,
            })
            .IsUnique()
            .HasFilter("MediaAssetId IS NOT NULL")
            .HasDatabaseName("UX_MediaPictureAnalyses_Workspace_Asset_Version");

        builder.HasIndex(analysis => new { analysis.WorkspaceId, analysis.GeneratedImageId })
            .IsUnique()
            .HasFilter("GeneratedImageId IS NOT NULL")
            .HasDatabaseName("UX_MediaPictureAnalyses_Workspace_GeneratedImage");
    }
}
