using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceEmbeddingOperationConfiguration
    : IEntityTypeConfiguration<BrandSourceEmbeddingOperation>
{
    internal static readonly BrandSourceEmbeddingOperationStatus[] Terminal =
    [
        BrandSourceEmbeddingOperationStatus.Completed,
        BrandSourceEmbeddingOperationStatus.Failed,
        BrandSourceEmbeddingOperationStatus.Cancelled,
    ];

    public void Configure(EntityTypeBuilder<BrandSourceEmbeddingOperation> builder)
    {
        builder.ToTable("BrandSourceEmbeddingOperations", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceEmbeddingOperations_Status_Specified", "Status <> 0");
            table.HasCheckConstraint("CK_BrandSourceEmbeddingOperations_Attempts_NotNegative", "Attempts >= 0");

            // Only an extraction that produced text can be embedded. The composite key below can only resolve
            // against a Succeeded row once this pins the carried status.
            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_SourceStatus_Succeeded",
                $"SourceStatus = {(int)BrandSourceExtractionStatus.Succeeded}");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Failure_Status",
                $"(FailureCategory IS NULL AND Status <> {(int)BrandSourceEmbeddingOperationStatus.Failed}) OR "
                    + $"(FailureCategory IS NOT NULL AND Status = {(int)BrandSourceEmbeddingOperationStatus.Failed})");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_FailureSummary_Requires_Failure",
                "FailureSummary IS NULL OR FailureCategory IS NOT NULL");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Running_HasStarted",
                $"Status <> {(int)BrandSourceEmbeddingOperationStatus.Running} OR StartedAt IS NOT NULL");

            var terminal = string.Join(", ", Terminal.Select(status => (int)status).Order());

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Completed_Terminal",
                $"(CompletedAt IS NULL AND Status NOT IN ({terminal})) OR "
                    + $"(CompletedAt IS NOT NULL AND Status IN ({terminal}))");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Lease_Complete",
                "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR "
                    + "(LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Lease_Requires_Running",
                $"LeasedBy IS NULL OR Status = {(int)BrandSourceEmbeddingOperationStatus.Running}");

            // A set is the product of work that finished.
            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Set_Requires_Completed",
                $"BrandSourceChunkSetId IS NULL OR Status = {(int)BrandSourceEmbeddingOperationStatus.Completed}");

            table.HasCheckConstraint(
                "CK_BrandSourceEmbeddingOperations_Model_NotBlank",
                "EmbeddingModel IS NULL OR trim(EmbeddingModel) <> ''");
        });

        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).ValueGeneratedNever();

        builder.Property(operation => operation.SourceStatus).IsRequired();
        builder.Property(operation => operation.Status).IsRequired();
        builder.Property(operation => operation.Attempts).IsRequired();
        builder.Property(operation => operation.AvailableAt).IsRequired();
        builder.Property(operation => operation.QueuedAt).IsRequired();
        builder.Property(operation => operation.StatusChangedAt).IsRequired();
        builder.Property(operation => operation.EmbeddingModel).HasMaxLength(BrandPolicy.EmbeddingModelMaxLength);
        builder.Property(operation => operation.FailureSummary)
            .HasMaxLength(BrandPolicy.ExtractionFailureSummaryMaxLength);
        builder.Property(operation => operation.RowVersion).IsRowVersion();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(operation => operation.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The same four-column key a chunk set uses: the extraction is this workspace's, the version named
        // here is the one it read, and it succeeded — each refused by the database rather than by a check.
        builder.HasOne<BrandSourceExtraction>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.WorkspaceId,
                operation.BrandSourceExtractionId,
                operation.BrandSourceDocumentVersionId,
                operation.SourceStatus,
            })
            .HasPrincipalKey(extraction => new
            {
                extraction.WorkspaceId,
                extraction.Id,
                extraction.BrandSourceDocumentVersionId,
                extraction.Status,
            })
            .OnDelete(DeleteBehavior.Restrict);

        // At most one live operation per extraction: a duplicate delivery, a retrying execution strategy and
        // a sweep that raced a commit all find the slot taken. Terminal rows are outside the rule.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.BrandSourceExtractionId })
            .IsUnique()
            .HasFilter(
                $"Status IN ({(int)BrandSourceEmbeddingOperationStatus.Queued}, {(int)BrandSourceEmbeddingOperationStatus.Running})")
            .HasDatabaseName("UX_BrandSourceEmbeddingOperations_Workspace_Extraction_Live");

        // Deliberately NOT workspace-leading: the queue-claim seek, for the reason
        // IX_BrandSourceExtractionOperations_Status_AvailableAt gives.
        builder.HasIndex(operation => new { operation.Status, operation.AvailableAt })
            .HasDatabaseName("IX_BrandSourceEmbeddingOperations_Status_AvailableAt");

        builder.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAt })
            .HasDatabaseName("IX_BrandSourceEmbeddingOperations_Status_LeaseExpiresAt");
    }
}
