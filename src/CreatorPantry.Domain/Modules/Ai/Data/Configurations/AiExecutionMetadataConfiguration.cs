using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiExecutionMetadataConfiguration : IEntityTypeConfiguration<AiExecutionMetadata>
{
    public void Configure(EntityTypeBuilder<AiExecutionMetadata> builder)
    {
        builder.ToTable("AiExecutionMetadata", table =>
        {
            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Attempt_Positive",
                "AttemptNumber >= 1");

            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Latency_NotNegative",
                "LatencyMilliseconds >= 0");

            // Null means the provider did not report usage, which is not the same as zero. A negative count
            // would be neither, so it is refused rather than left to be interpreted.
            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Tokens_NotNegative",
                "(InputTokens IS NULL OR InputTokens >= 0) AND (OutputTokens IS NULL OR OutputTokens >= 0)");

            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Cost_NotNegative",
                "EstimatedCost IS NULL OR EstimatedCost >= 0");

            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_FailureCategory_Declared",
                $"FailureCategory IS NULL OR FailureCategory <> {(int)AiFailureCategory.Unspecified}");

            // A summary explains a failure. On a successful attempt there is nothing for it to explain, and a
            // free-text field with no failure behind it is where a provider payload ends up.
            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Summary_Requires_Failure",
                "FailureSummary IS NULL OR FailureCategory IS NOT NULL");

            table.HasCheckConstraint(
                "CK_AiExecutionMetadata_Completed_After_Started",
                "CompletedAt >= StartedAt");
        });

        builder.HasKey(metadata => metadata.Id);

        builder.Property(metadata => metadata.AttemptNumber).IsRequired();
        builder.Property(metadata => metadata.StartedAt).IsRequired();
        builder.Property(metadata => metadata.CompletedAt).IsRequired();
        builder.Property(metadata => metadata.LatencyMilliseconds).IsRequired();
        builder.Property(metadata => metadata.SafetyBlocked).IsRequired();
        builder.Property(metadata => metadata.CorrelationId).IsRequired();

        builder.Property(metadata => metadata.ProviderName)
            .IsRequired()
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(metadata => metadata.ModelName)
            .IsRequired()
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(metadata => metadata.ModelDeployment)
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(metadata => metadata.PromptTemplateId)
            .IsRequired()
            .HasMaxLength(AiPolicy.TemplateIdMaxLength);

        builder.Property(metadata => metadata.PromptTemplateVersion)
            .IsRequired()
            .HasMaxLength(AiPolicy.TemplateVersionMaxLength);

        // Short on purpose. See AiExecutionMetadata's remarks: this is the only free-text column in the
        // diagnostic record, and a generous limit would invite a transcript into it.
        builder.Property(metadata => metadata.FailureSummary)
            .HasMaxLength(AiPolicy.DiagnosticMaxLength);

        // Cost is money-shaped, so it gets a fixed scale rather than whatever the provider defaults to.
        builder.Property(metadata => metadata.EstimatedCost).HasPrecision(18, 6);

        // Hangs off the operation, not the proposal: a failed attempt has no proposal to belong to. See
        // AiStructuredChangeConfiguration for why there is no second foreign key to Workspaces.
        builder.HasOne<AiOperation>()
            .WithMany()
            .HasForeignKey(metadata => new { metadata.WorkspaceId, metadata.AiOperationId })
            .HasPrincipalKey(operation => new { operation.WorkspaceId, operation.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One row per attempt, and attempts are numbered rather than merely counted.
        builder.HasIndex(metadata => new { metadata.WorkspaceId, metadata.AiOperationId, metadata.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("UX_AiExecutionMetadata_Workspace_Operation_Attempt");

        // Following one request across gateway, API and worker.
        builder.HasIndex(metadata => new { metadata.WorkspaceId, metadata.CorrelationId })
            .HasDatabaseName("IX_AiExecutionMetadata_Workspace_Correlation");
    }
}
