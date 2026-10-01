using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceChunkConfiguration : IEntityTypeConfiguration<BrandSourceChunk>
{
    public void Configure(EntityTypeBuilder<BrandSourceChunk> builder)
    {
        builder.ToTable("BrandSourceChunks", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceChunks_Ordinal_Positive", "Ordinal >= 1");
            table.HasCheckConstraint("CK_BrandSourceChunks_StartByteOffset_NonNegative", "StartByteOffset >= 0");
            table.HasCheckConstraint("CK_BrandSourceChunks_Text_NotBlank", "trim(Text) <> ''");
            table.HasCheckConstraint("CK_BrandSourceChunks_ContentChecksum_Sha256", "ContentChecksum LIKE 'sha256:%'");

            // An empty slice has no passage in it, and one past the cap describes a chunk the embedding model
            // was never asked for.
            table.HasCheckConstraint(
                "CK_BrandSourceChunks_ByteLength_Bounded",
                $"ByteLength >= 1 AND ByteLength <= {BrandPolicy.ChunkMaxBytes}");
        });

        // Non-clustered, so the clustering key below can be the one the retrieval scan wants. The chunk's
        // identity is still its Guid; what moves is only how the rows are laid out on disk.
        builder.HasKey(chunk => chunk.Id).IsClustered(false);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added
        // through a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(chunk => chunk.Id).ValueGeneratedNever();

        builder.Property(chunk => chunk.Ordinal).IsRequired();
        builder.Property(chunk => chunk.StartByteOffset).IsRequired();
        builder.Property(chunk => chunk.ByteLength).IsRequired();
        builder.Property(chunk => chunk.ContentChecksum).IsRequired().HasMaxLength(BrandPolicy.ChecksumMaxLength);
        builder.Property(chunk => chunk.Text).IsRequired().HasMaxLength(BrandPolicy.ChunkTextMaxLength);

        // The engine's own type, so VECTOR_DISTANCE runs in the database under the workspace query filter
        // rather than in this process over everything it had to load first. The width is DDL: see
        // BrandPolicy.EmbeddingDimension for what that costs and why it is still the right trade.
        builder.Property(chunk => chunk.Embedding)
            .IsRequired()
            .HasColumnType($"vector({BrandPolicy.EmbeddingDimension})");

        // Part of the set: it goes when the set does. No separate foreign key to the workspace, because two
        // cascade paths into one table is what SQL Server refuses — the workspace reaches these rows through
        // the set, and the composite key is what makes a chunk of another workspace's set unrepresentable.
        builder.HasOne<BrandSourceChunkSet>()
            .WithMany(set => set.Chunks)
            .HasForeignKey(chunk => new { chunk.WorkspaceId, chunk.BrandSourceChunkSetId })
            .HasPrincipalKey(set => new { set.WorkspaceId, set.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One ordinal per set, and the physical order of the table. Clustered deliberately: an exact nearest-
        // neighbour search reads every candidate vector, so what matters most is that one workspace's set is
        // contiguous and the scan never touches another's pages. A ~6 KB vector per row makes that the
        // difference between megabytes and tens of megabytes of reads.
        builder.HasIndex(chunk => new { chunk.WorkspaceId, chunk.BrandSourceChunkSetId, chunk.Ordinal })
            .IsUnique()
            .IsClustered()
            .HasDatabaseName("UX_BrandSourceChunks_Workspace_Set_Ordinal");
    }
}
