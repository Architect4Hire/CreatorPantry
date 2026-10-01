using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceChunkSetConfiguration : IEntityTypeConfiguration<BrandSourceChunkSet>
{
    public void Configure(EntityTypeBuilder<BrandSourceChunkSet> builder)
    {
        builder.ToTable("BrandSourceChunkSets", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceChunkSets_Status_Specified", "Status <> 0");
            table.HasCheckConstraint("CK_BrandSourceChunkSets_ChunkerId_NotBlank", "trim(ChunkerId) <> ''");
            table.HasCheckConstraint("CK_BrandSourceChunkSets_EmbeddingModel_NotBlank", "trim(EmbeddingModel) <> ''");
            table.HasCheckConstraint("CK_BrandSourceChunkSets_ChunkCount_NonNegative", "ChunkCount >= 0");

            // The source must be an extraction that produced text. The composite foreign key below can only
            // resolve against a Succeeded row, so this check is what gives that key its teeth: pin the carried
            // status and the key has one legal target per (workspace, extraction, version).
            table.HasCheckConstraint(
                "CK_BrandSourceChunkSets_SourceStatus_Succeeded",
                $"SourceStatus = {(int)BrandSourceExtractionStatus.Succeeded}");

            // A set is built at one width, and that width is the column's. Recording a different number would
            // describe vectors the table could not be holding.
            table.HasCheckConstraint(
                "CK_BrandSourceChunkSets_EmbeddingDimension_Fixed",
                $"EmbeddingDimension = {BrandPolicy.EmbeddingDimension}");

            // Embedded exactly when the set stopped being built, so a timestamp can never imply a completion
            // that did not happen, nor a finished set lack one.
            table.HasCheckConstraint(
                "CK_BrandSourceChunkSets_EmbeddedAt_Matches_Status",
                $"(Status = {(int)BrandSourceChunkSetStatus.Building} AND EmbeddedAt IS NULL) OR "
                    + $"(Status <> {(int)BrandSourceChunkSetStatus.Building} AND EmbeddedAt IS NOT NULL)");

            // An empty set cannot be the one retrieval reads: it would answer every query with nothing and look
            // like a corpus with no matches rather than a run that failed.
            table.HasCheckConstraint(
                "CK_BrandSourceChunkSets_Current_HasChunks",
                $"Status <> {(int)BrandSourceChunkSetStatus.Current} OR ChunkCount >= 1");
        });

        builder.HasKey(set => set.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added
        // through a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(set => set.Id).ValueGeneratedNever();

        // Target of a chunk's foreign key.
        builder.HasAlternateKey(set => new { set.WorkspaceId, set.Id })
            .HasName("AK_BrandSourceChunkSets_Workspace_Id");

        builder.Property(set => set.SourceStatus).IsRequired();
        builder.Property(set => set.Status).IsRequired();
        builder.Property(set => set.ChunkerId).IsRequired().HasMaxLength(BrandPolicy.ChunkerIdMaxLength);
        builder.Property(set => set.EmbeddingModel).IsRequired().HasMaxLength(BrandPolicy.EmbeddingModelMaxLength);
        builder.Property(set => set.EmbeddingDimension).IsRequired();
        builder.Property(set => set.ChunkCount).IsRequired();
        builder.Property(set => set.CreatedAt).IsRequired();
        builder.Property(set => set.RowVersion).IsRowVersion();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(set => set.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Four columns into one alternate key, which is what makes three separate invariants structural at
        // once: the extraction belongs to this workspace, the version named here is the version that
        // extraction read, and that extraction succeeded. Restrict, because the artifact these offsets are
        // measured in outlives any ordinary tidying of what points at it.
        builder.HasOne<BrandSourceExtraction>()
            .WithMany()
            .HasForeignKey(set => new
            {
                set.WorkspaceId,
                set.BrandSourceExtractionId,
                set.BrandSourceDocumentVersionId,
                set.SourceStatus,
            })
            .HasPrincipalKey(extraction => new
            {
                extraction.WorkspaceId,
                extraction.Id,
                extraction.BrandSourceDocumentVersionId,
                extraction.Status,
            })
            .OnDelete(DeleteBehavior.Restrict);

        // The invariant the set exists to hold: at most one current set, and at most one being built, per
        // extraction and embedding model. Superseded rows are filtered out so a swap that crashed half way
        // leaves work for the sweep rather than a wall the next run cannot get past.
        builder.HasIndex(set => new { set.WorkspaceId, set.BrandSourceExtractionId, set.EmbeddingModel, set.Status })
            .IsUnique()
            .HasFilter($"Status <> {(int)BrandSourceChunkSetStatus.Superseded}")
            .HasDatabaseName("UX_BrandSourceChunkSets_Workspace_Extraction_Model_Status");

        // "What is embedded for this document, and is any of it current." Serves the staleness read after a
        // document is replaced, without walking versions and extractions to get there.
        builder.HasIndex(set => new { set.WorkspaceId, set.BrandSourceDocumentId, set.Status })
            .HasDatabaseName("IX_BrandSourceChunkSets_Workspace_Document_Status");
    }
}
