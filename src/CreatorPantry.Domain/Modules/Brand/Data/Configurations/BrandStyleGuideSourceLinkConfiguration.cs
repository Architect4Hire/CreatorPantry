using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideSourceLinkConfiguration : IEntityTypeConfiguration<BrandStyleGuideSourceLink>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideSourceLink> builder)
    {
        builder.ToTable("BrandStyleGuideSourceLinks");

        // The whole row is the key, so a guide version cites a document version exactly once.
        builder.HasKey(link => new { link.WorkspaceId, link.BrandStyleGuideVersionId, link.BrandSourceDocumentVersionId });

        // Part of the guide version: it goes when the version does, which only workspace erasure causes.
        builder.HasOne<BrandStyleGuideVersion>()
            .WithMany(version => version.SourceLinks)
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandStyleGuideVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a cited upload cannot be removed from under the guide that cites it. Cascade here would
        // also be a second path into this table from Workspaces, which SQL Server rejects outright.
        builder.HasOne<BrandSourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandSourceDocumentVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // "Which guide versions cite this upload" — what removal shows before confirmation, and what
        // staleness looks up when a document is replaced.
        builder.HasIndex(link => new { link.WorkspaceId, link.BrandSourceDocumentVersionId })
            .HasDatabaseName("IX_BrandStyleGuideSourceLinks_Workspace_SourceDocumentVersion");
    }
}
