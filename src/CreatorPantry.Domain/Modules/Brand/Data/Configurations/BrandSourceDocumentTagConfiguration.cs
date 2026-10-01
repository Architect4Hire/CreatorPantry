using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceDocumentTagConfiguration : IEntityTypeConfiguration<BrandSourceDocumentTag>
{
    public void Configure(EntityTypeBuilder<BrandSourceDocumentTag> builder)
    {
        builder.ToTable("BrandSourceDocumentTags");

        // The whole row is the key, so one tag can be applied to one document exactly once.
        builder.HasKey(link => new { link.WorkspaceId, link.BrandSourceDocumentId, link.BrandSourceTagId });

        // Part of the document: the link goes when the document does, which only workspace erasure causes.
        builder.HasOne<BrandSourceDocument>()
            .WithMany(document => document.Tags)
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandSourceDocumentId })
            .HasPrincipalKey(document => new { document.WorkspaceId, document.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Not part of the vocabulary: deleting a tag that documents still carry is refused. Cascade here would
        // be a second path into this table from Workspaces, which SQL Server rejects outright. The vocabulary
        // retires entries with IsActive instead.
        builder.HasOne<BrandSourceTag>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandSourceTagId })
            .HasPrincipalKey(tag => new { tag.WorkspaceId, tag.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // "Which documents carry this tag" — the list's tag filter, and the reverse of the key's leading columns.
        builder.HasIndex(link => new { link.WorkspaceId, link.BrandSourceTagId })
            .HasDatabaseName("IX_BrandSourceDocumentTags_Workspace_Tag");
    }
}
