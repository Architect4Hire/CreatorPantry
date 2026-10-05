using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class GeneratedImageOperationConfiguration
    : IEntityTypeConfiguration<GeneratedImageOperation>
{
    public void Configure(EntityTypeBuilder<GeneratedImageOperation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("GeneratedImageOperations");
        builder.HasKey(operation => operation.Id);

        // The pair a workspace-scoped child foreign key points at, as every other module's aggregate root
        // exposes one.
        builder.HasAlternateKey(operation => new { operation.WorkspaceId, operation.Id });

        builder.Property(operation => operation.PromptText)
            .IsRequired()
            .HasMaxLength(ContentPromptTextMaxLength);

        builder.Property(operation => operation.AvoidText).HasMaxLength(ContentPromptTextMaxLength);

        builder.Property(operation => operation.ProviderName)
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(operation => operation.ModelName)
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(operation => operation.ModelDeployment)
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(operation => operation.FailureCategory)
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(operation => operation.FailureSummary)
            .HasMaxLength(MediaPolicy.FailureSummaryMaxLength);

        builder.Property(operation => operation.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(IdempotencyKeyMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(operation => operation.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace-paired and restricted, the pattern every sibling pin in this codebase follows: the
        // workspace's own cascade is what removes these rows, and a second cascade path into the same table
        // is what SQL Server refuses outright.
        builder.HasOne<AiProposal>()
            .WithMany()
            .HasForeignKey(operation => new { operation.WorkspaceId, operation.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A retry returns the first answer. Image generation is the most expensive call this product makes,
        // so this index is the difference between a lost response and a second charge.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_GeneratedImageOperations_Workspace_IdempotencyKey");

        // The worker's claim, and deliberately not led by WorkspaceId: a claim looks for due work before it
        // knows whose it is, which is tenancy.md's documented queue-claim carve-out. Filtered to the two
        // states a claim can act on, so a table of finished operations costs the sweep nothing.
        builder.HasIndex(operation => new { operation.Status, operation.AvailableAt })
            .HasFilter("Status IN (1, 2)")
            .HasDatabaseName("IX_GeneratedImageOperations_Status_AvailableAt");

        // A workspace reading its own history, newest first.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.RequestedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_GeneratedImageOperations_Workspace_RequestedAt");

        builder.ToTable(table =>
        {
            // A row that forgot to say what it was doing must not pass for the first real member of the enum.
            table.HasCheckConstraint(
                "CK_GeneratedImageOperations_Status_Declared",
                "Status <> 0");

            table.HasCheckConstraint(
                "CK_GeneratedImageOperations_VariantCount_Range",
                $"VariantCount >= {MediaPolicy.MinVariantsPerOperation} "
                    + $"AND VariantCount <= {MediaPolicy.MaxVariantsPerOperation}");

            // Blank is not a key. A column that is required but empty would satisfy the unique index with one
            // row and then collide with the next request that also forgot.
            table.HasCheckConstraint(
                "CK_GeneratedImageOperations_IdempotencyKey_NotBlank",
                "trim(IdempotencyKey) <> ''");
        });
    }

    /// <summary>
    /// The prompt column's length, matched to what the prompt library can hold.
    /// </summary>
    /// <remarks>
    /// Named here rather than referenced from <c>ContentPolicy</c> because the Content module's policy is one
    /// of the module-internal types that may not cross a boundary — and a generated image's prompt has to fit
    /// the prompt record a creator saves afterwards, so the two numbers must agree. A test asserts they do.
    /// </remarks>
    internal const int ContentPromptTextMaxLength = 4000;

    /// <inheritdoc cref="ContentPromptTextMaxLength"/>
    internal const int IdempotencyKeyMaxLength = 200;
}
