using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceExtractionOperationConfiguration
    : IEntityTypeConfiguration<BrandSourceExtractionOperation>
{
    /// <summary>The states an operation does not come back from. Generated into the terminal-timestamp check.</summary>
    internal static readonly BrandSourceExtractionOperationStatus[] Terminal =
    [
        BrandSourceExtractionOperationStatus.Completed,
        BrandSourceExtractionOperationStatus.Failed,
        BrandSourceExtractionOperationStatus.Cancelled,
    ];

    public void Configure(EntityTypeBuilder<BrandSourceExtractionOperation> builder)
    {
        builder.ToTable("BrandSourceExtractionOperations", table =>
        {
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Status_Specified",
                "Status <> 0");

            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Attempts_NotNegative",
                "Attempts >= 0");

            // A failure category belongs to a failed operation and nothing else. Without this the two columns
            // can disagree, and then a row can claim an outcome it never had or stop for no recorded reason —
            // and the reason is what the retry decision was made from.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Failure_Status",
                $"(FailureCategory IS NULL AND Status <> {(int)BrandSourceExtractionOperationStatus.Failed}) OR "
                    + $"(FailureCategory IS NOT NULL AND Status = {(int)BrandSourceExtractionOperationStatus.Failed})");

            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_FailureSummary_Requires_Failure",
                "FailureSummary IS NULL OR FailureCategory IS NOT NULL");

            // Running implies work started. Note what this deliberately does NOT say: that StartedAt is null
            // while Queued. Lease recovery returns a Running operation to Queued, and that row has genuinely
            // started — clearing the timestamp for a tidier constraint would erase the evidence of it.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Running_HasStarted",
                $"Status <> {(int)BrandSourceExtractionOperationStatus.Running} OR StartedAt IS NOT NULL");

            // CompletedAt is set in exactly the terminal states. The list is generated from Terminal rather
            // than written out, so the database and the code cannot come to disagree about which are final.
            var terminal = string.Join(", ", Terminal.Select(status => (int)status).Order());

            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Completed_Terminal",
                $"(CompletedAt IS NULL AND Status NOT IN ({terminal})) OR "
                    + $"(CompletedAt IS NOT NULL AND Status IN ({terminal}))");

            // A lease is a token and a deadline together. Half a lease is not a lease: a row holding one
            // without the other cannot be validated on completion or recovered by the sweep.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Lease_Complete",
                "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR "
                    + "(LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");

            // Only a running operation holds a lease. A terminal row still holding one would be re-claimed by
            // the recovery sweep and run a second time.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Lease_Requires_Running",
                $"LeasedBy IS NULL OR Status = {(int)BrandSourceExtractionOperationStatus.Running}");

            // An extraction is produced by work that finished, and a parser is named by work that started.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_Extraction_Requires_Completed",
                $"BrandSourceExtractionId IS NULL OR Status = {(int)BrandSourceExtractionOperationStatus.Completed}");

            table.HasCheckConstraint(
                "CK_BrandSourceExtractionOperations_ExtractorId_NotBlank",
                "ExtractorId IS NULL OR trim(ExtractorId) <> ''");
        });

        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).ValueGeneratedNever();

        builder.Property(operation => operation.Status).IsRequired();
        builder.Property(operation => operation.Attempts).IsRequired();
        builder.Property(operation => operation.AvailableAt).IsRequired();
        builder.Property(operation => operation.QueuedAt).IsRequired();
        builder.Property(operation => operation.StatusChangedAt).IsRequired();

        builder.Property(operation => operation.ExtractorId).HasMaxLength(BrandPolicy.ExtractorIdMaxLength);
        builder.Property(operation => operation.FailureSummary)
            .HasMaxLength(BrandPolicy.ExtractionFailureSummaryMaxLength);

        builder.Property(operation => operation.RowVersion).IsRowVersion();

        // The erasure path. Cascade comes from the workspace alone, as on every other row in this module.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(operation => operation.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, matching BrandSourceExtraction: the workspace component is what makes this safe rather than
        // merely tidy — an id alone could name a version of another workspace's document, and the composite key
        // makes that unreferenceable at the database instead of only refused by the code above it.
        builder.HasOne<BrandSourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(operation => new { operation.WorkspaceId, operation.BrandSourceDocumentVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One version, one operation. This is what stops a replayed upload — or a retrying execution strategy —
        // from queueing the same version twice, at the database rather than only in the code above it.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.BrandSourceDocumentVersionId })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceExtractionOperations_Workspace_Version");

        // Deliberately NOT workspace-leading, for the reason IX_AiOperations_Status_AvailableAt is not.
        //
        // This is the queue-claim seek. A worker looks for queued work across every workspace, because it does
        // not know which workspace it is about to serve until it has found a row — so an index leading with
        // WorkspaceId cannot answer the query at all. tenancy.md's workspace-leading rule is about scoping
        // creator-facing reads, and this index must never serve one.
        //
        // It is safe because finding a row is not authorization: the worker re-resolves the workspace from the
        // row it claimed and validates it before reading anything else, and the global query filter governs
        // everything after that. AvailableAt rather than QueuedAt, because that is the claim predicate — a
        // requeued operation is deliberately not claimable until its backoff has passed.
        builder.HasIndex(operation => new { operation.Status, operation.AvailableAt })
            .HasDatabaseName("IX_BrandSourceExtractionOperations_Status_AvailableAt");

        // The lease-recovery sweep: running operations whose claim has lapsed.
        builder.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAt })
            .HasDatabaseName("IX_BrandSourceExtractionOperations_Status_LeaseExpiresAt");
    }
}
