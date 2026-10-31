using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class AccountAiUsageEntryConfiguration : IEntityTypeConfiguration<AccountAiUsageEntry>
{
    public void Configure(EntityTypeBuilder<AccountAiUsageEntry> builder)
    {
        builder.ToTable("AccountAiUsageEntries", table =>
        {
            // Written with unquoted identifiers and the enums' own integer values so the same expression is
            // valid on SQL Server and on the SQLite database the constraint tests run against.
            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_Attempt_Positive",
                "AttemptNumber >= 1");

            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_TaskType_Declared",
                $"TaskType <> {(int)AiTaskType.Unspecified}");

            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_Outcome_Declared",
                $"Outcome <> {(int)AiUsageOutcome.Unspecified}");

            // Null means the provider did not report usage, which is not the same as zero. A negative count
            // would be neither, so it is refused rather than left to be interpreted.
            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_Tokens_NotNegative",
                "(InputTokens IS NULL OR InputTokens >= 0) "
                    + "AND (OutputTokens IS NULL OR OutputTokens >= 0) "
                    + "AND (TotalTokens IS NULL OR TotalTokens >= 0)");

            // What makes UsageReported load-bearing rather than a restatement of the nullability: "the
            // provider said nothing" must be storable exactly one way, and "reported" must mean the row
            // actually carries something. Note this deliberately does not require all three counts — a
            // provider that reports a total and no breakdown is reporting usage.
            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_UsageReported_Matches_Tokens",
                "(UsageReported = 0 AND InputTokens IS NULL AND OutputTokens IS NULL AND TotalTokens IS NULL) "
                    + "OR (UsageReported = 1 AND (InputTokens IS NOT NULL OR OutputTokens IS NOT NULL OR TotalTokens IS NOT NULL))");

            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_Cost_NotNegative",
                "EstimatedCost IS NULL OR EstimatedCost >= 0");

            // One kind of work per row (AP.2.5). Both set would let one charge be counted under two
            // operations; neither set would leave a charge that cannot be deduplicated, which is the one
            // thing the unique indexes below exist to prevent.
            table.HasCheckConstraint(
                "CK_AccountAiUsageEntries_Names_One_Operation",
                "(AiOperationId IS NOT NULL AND GeneratedImageOperationId IS NULL) "
                    + "OR (AiOperationId IS NULL AND GeneratedImageOperationId IS NOT NULL)");
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.AccountId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(entry => entry.OccurredAt).IsRequired();
        builder.Property(entry => entry.AttemptNumber).IsRequired();
        builder.Property(entry => entry.TaskType).IsRequired();
        builder.Property(entry => entry.WorkspaceId).IsRequired();
        builder.Property(entry => entry.IsBillable).IsRequired();
        builder.Property(entry => entry.UsageReported).IsRequired();
        builder.Property(entry => entry.Outcome).IsRequired();

        builder.Property(entry => entry.ProviderName)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.ProviderIdentifierMaxLength);

        builder.Property(entry => entry.ModelName)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.ProviderIdentifierMaxLength);

        builder.Property(entry => entry.ModelDeployment)
            .HasMaxLength(AiUsagePolicy.ProviderIdentifierMaxLength);

        // Cost is money-shaped, so it gets a fixed scale rather than whatever the provider defaults to. Same
        // precision AiExecutionMetadata.EstimatedCost uses, because the two describe the same attempt.
        builder.Property(entry => entry.EstimatedCost).HasPrecision(18, 6);

        // No relationships are configured, and that is the decision rather than an omission: see
        // AccountAiUsageEntry's remarks. A foreign key to Workspace, AiOperation or ApplicationUser would put
        // an account's usage history inside a cascade that 9A.3 forbids it to be in.

        // One attempt, at most one entry — what makes a duplicate delivery post once. Deliberately does not
        // lead with WorkspaceId: operation ids are globally unique, and making the dedup lookup need a
        // workspace would reintroduce the scoping this table exists to escape.
        //
        // Filtered, since AP.2.5 made the column nullable: without the filter SQL Server would treat the
        // image rows' null operation id as one value and let only one of them exist per attempt number.
        builder.HasIndex(entry => new { entry.AiOperationId, entry.AttemptNumber })
            .IsUnique()
            .HasFilter("AiOperationId IS NOT NULL")
            .HasDatabaseName("UX_AccountAiUsageEntries_Operation_Attempt");

        // The same guarantee for a picture's charges, on its own operation id.
        builder.HasIndex(entry => new { entry.GeneratedImageOperationId, entry.AttemptNumber })
            .IsUnique()
            .HasFilter("GeneratedImageOperationId IS NOT NULL")
            .HasDatabaseName("UX_AccountAiUsageEntries_ImageOperation_Attempt");

        // The account/period seek: what this account has spent in the current period, and the order its
        // history pages in. Leads with the account because that is the only thing the read seam authorizes by.
        builder.HasIndex(entry => new { entry.AccountId, entry.OccurredAt })
            .HasDatabaseName("IX_AccountAiUsageEntries_Account_OccurredAt");

        // The account/workspace breakdown, with OccurredAt trailing so one period's slice is a range seek
        // rather than a scan of the account's whole history.
        builder.HasIndex(entry => new { entry.AccountId, entry.WorkspaceId, entry.OccurredAt })
            .HasDatabaseName("IX_AccountAiUsageEntries_Account_Workspace_OccurredAt");
    }
}
