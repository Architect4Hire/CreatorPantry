using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiOperationConfiguration : IEntityTypeConfiguration<AiOperation>
{
    public void Configure(EntityTypeBuilder<AiOperation> builder)
    {
        builder.ToTable("AiOperations", table =>
        {
            // A failure category belongs to a failed operation, and nothing else. Without this the two
            // columns can disagree, and then a row can claim an outcome it never had or fail for no recorded
            // reason — and the reason is what the retry decision is made from.
            table.HasCheckConstraint(
                "CK_AiOperations_Failure_Status",
                $"(FailureCategory IS NULL AND Status <> {(int)AiOperationStatus.Failed}) OR "
                    + $"(FailureCategory IS NOT NULL AND Status = {(int)AiOperationStatus.Failed})");

            // Zero is "not declared" in all three of these enums, and a row that never said what it was
            // doing must not pass for one that did.
            table.HasCheckConstraint(
                "CK_AiOperations_TaskType_Declared",
                $"TaskType <> {(int)AiTaskType.Unspecified}");

            table.HasCheckConstraint(
                "CK_AiOperations_Scope_Declared",
                $"Scope <> {(int)AiOperationScope.Unspecified}");

            table.HasCheckConstraint(
                "CK_AiOperations_FailureCategory_Declared",
                $"FailureCategory IS NULL OR FailureCategory <> {(int)AiFailureCategory.Unspecified}");

            // Running implies work started. Note what this deliberately does NOT say: that StartedAt is null
            // while Requested. Lease recovery returns a Running operation to Requested, and that row has
            // genuinely started — clearing the timestamp to satisfy a tidier constraint would erase the
            // evidence that a worker ever picked it up.
            table.HasCheckConstraint(
                "CK_AiOperations_Running_HasStarted",
                $"Status <> {(int)AiOperationStatus.Running} OR StartedAt IS NOT NULL");

            // CompletedAt is set in exactly the terminal states. The list is generated from
            // AiOperationTransitionPolicy rather than written out here, so the database and the state machine
            // cannot come to disagree about which states are final.
            var terminal = string.Join(
                ", ",
                AiOperationTransitionPolicy.TerminalStatuses.Select(status => (int)status).Order());

            table.HasCheckConstraint(
                "CK_AiOperations_Completed_Terminal",
                $"(CompletedAt IS NULL AND Status NOT IN ({terminal})) OR "
                    + $"(CompletedAt IS NOT NULL AND Status IN ({terminal}))");

            // A version names a recipe's history, so it cannot be recorded without the recipe it belongs to.
            table.HasCheckConstraint(
                "CK_AiOperations_Version_Requires_Recipe",
                "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");

            table.HasCheckConstraint(
                "CK_AiOperations_Attempts_NotNegative",
                "Attempts >= 0");

            // A lease is a token and a deadline together. Half a lease is not a lease: a row holding one
            // without the other cannot be renewed, validated on completion, or recovered.
            table.HasCheckConstraint(
                "CK_AiOperations_Lease_Complete",
                "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR "
                    + "(LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");

            // Only a running operation holds a lease. A terminal row still holding one would be re-claimed by
            // the recovery sweep and completed twice.
            table.HasCheckConstraint(
                "CK_AiOperations_Lease_Requires_Running",
                $"LeasedBy IS NULL OR Status = {(int)AiOperationStatus.Running}");
        });

        builder.HasKey(operation => operation.Id);

        // The target of the composite foreign keys the proposal entities will carry, and the reason
        // RecipeVersion.AiProposalId can eventually become a real constraint instead of a validated guess.
        builder.HasAlternateKey(operation => new { operation.WorkspaceId, operation.Id })
            .HasName("AK_AiOperations_Workspace_Id");

        builder.Property(operation => operation.TaskType).IsRequired();
        builder.Property(operation => operation.Scope).IsRequired();
        builder.Property(operation => operation.Status).IsRequired();
        builder.Property(operation => operation.RequestedByMembershipId).IsRequired();
        builder.Property(operation => operation.RequestedAt).IsRequired();
        builder.Property(operation => operation.StatusChangedAt).IsRequired();

        builder.Property(operation => operation.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(AiPolicy.IdempotencyKeyMaxLength);

        builder.Property(operation => operation.Attempts).IsRequired();
        builder.Property(operation => operation.AvailableAt).IsRequired();

        builder.Property(operation => operation.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(operation => operation.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, matching RecipeVersion. Deleting a recipe that has AI history is refused by the database,
        // so removing one stays a deliberate operation that says what becomes of the record — and an
        // operation's audit trail is not something a cascade gets to discard on a recipe's behalf. Deleting
        // the whole workspace still reaches this table, by its own cascade from Workspaces.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(operation => new { operation.WorkspaceId, operation.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The workspace component is what makes this safe rather than merely tidy: an id alone could name a
        // version of another workspace's recipe, and the composite key makes that unreferenceable.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(operation => new { operation.WorkspaceId, operation.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One key, one operation. This is what stops a replay arriving after the generic idempotency record
        // has expired from starting a second run of work the creator already paid for.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_AiOperations_Workspace_IdempotencyKey");

        // A recipe's AI history, newest first.
        builder.HasIndex(operation => new { operation.WorkspaceId, operation.RecipeId, operation.StatusChangedAt })
            .HasDatabaseName("IX_AiOperations_Workspace_Recipe_StatusChangedAt");

        // Deliberately NOT workspace-leading, and the only index in the schema that is not.
        //
        // This is the queue-claim seek. A worker looks for Requested work across every workspace, because it
        // does not know which workspace it is about to serve until it has found a row — so an index leading
        // with WorkspaceId cannot answer the query at all, and tenancy.md's workspace-leading rule is about
        // scoping creator-facing reads rather than about how a background claim finds its next item.
        //
        // It is safe because finding a row is not authorization. The worker re-resolves the workspace from
        // the row it claimed and validates it before invoking any facade; the global query filter still
        // governs everything read afterwards. What this index must never be used for is a creator-facing
        // list, where omitting the workspace really would cross the boundary.
        //
        // AvailableAt rather than RequestedAt, because that is the claim predicate: a requeued operation is
        // deliberately not claimable until its backoff has passed, and an index on the request time would
        // make the sweep read every backed-off row to discard it.
        builder.HasIndex(operation => new { operation.Status, operation.AvailableAt })
            .HasDatabaseName("IX_AiOperations_Status_AvailableAt");

        // The lease-recovery sweep: running operations whose claim has lapsed.
        builder.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAt })
            .HasDatabaseName("IX_AiOperations_Status_LeaseExpiresAt");
    }
}
