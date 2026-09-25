using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiStructuredChangeConfiguration : IEntityTypeConfiguration<AiStructuredChange>
{
    public void Configure(EntityTypeBuilder<AiStructuredChange> builder)
    {
        builder.ToTable("AiStructuredChanges", table =>
        {
            table.HasCheckConstraint(
                "CK_AiStructuredChanges_ChangeKind_Declared",
                $"ChangeKind <> {(int)AiChangeKind.Unspecified}");

            table.HasCheckConstraint(
                "CK_AiStructuredChanges_TargetKind_Declared",
                $"TargetKind <> {(int)AiChangeTargetKind.Unspecified}");

            // A field name belongs to a change that sets a field, and nothing else. An addition proposes a
            // whole child, and a removal names no field — without this the column can carry a value nothing
            // reads, which is how a diff comes to claim it changed something it did not.
            table.HasCheckConstraint(
                "CK_AiStructuredChanges_FieldName_Set",
                $"(FieldName IS NOT NULL AND ChangeKind = {(int)AiChangeKind.Set}) OR "
                    + $"(FieldName IS NULL AND ChangeKind <> {(int)AiChangeKind.Set})");

            // Only the kinds that place a child may say where. A Set carrying a position would be proposing
            // two different things in one row.
            table.HasCheckConstraint(
                "CK_AiStructuredChanges_Position_Kind",
                $"ProposedPosition IS NULL OR ChangeKind IN ({(int)AiChangeKind.Add}, {(int)AiChangeKind.Move})");

            table.HasCheckConstraint(
                "CK_AiStructuredChanges_Position_NotNegative",
                "ProposedPosition IS NULL OR ProposedPosition >= 0");

            // A decision has a time and an actor, or it has not been made. All three columns move together, so
            // a row cannot claim to have been decided by nobody or at no time.
            table.HasCheckConstraint(
                "CK_AiStructuredChanges_Decision_Complete",
                $"(Disposition = {(int)AiChangeDisposition.Pending} "
                    + "AND DecidedAt IS NULL AND DecidedByMembershipId IS NULL) OR "
                    + $"(Disposition <> {(int)AiChangeDisposition.Pending} "
                    + "AND DecidedAt IS NOT NULL AND DecidedByMembershipId IS NOT NULL)");

            // A change must propose something. A row with neither value is not a change — a Remove carries the
            // before it removes, a Move carries the before it moves, and every other kind carries an after.
            table.HasCheckConstraint(
                "CK_AiStructuredChanges_HasAValue",
                "BeforeValue IS NOT NULL OR AfterValue IS NOT NULL OR ProposedPosition IS NOT NULL");
        });

        builder.HasKey(change => change.Id);

        // Warnings point at individual changes, so this needs to be referenceable within its workspace.
        builder.HasAlternateKey(change => new { change.WorkspaceId, change.Id })
            .HasName("AK_AiStructuredChanges_Workspace_Id");

        builder.Property(change => change.ChangeKind).IsRequired();
        builder.Property(change => change.TargetKind).IsRequired();
        builder.Property(change => change.Disposition).IsRequired();
        builder.Property(change => change.SortOrder).IsRequired();

        builder.Property(change => change.FieldName).HasMaxLength(AiPolicy.FieldNameMaxLength);
        builder.Property(change => change.BeforeValue).HasMaxLength(AiPolicy.ChangeValueMaxLength);
        builder.Property(change => change.AfterValue).HasMaxLength(AiPolicy.ChangeValueMaxLength);

        // Its own WorkspaceId rather than borrowing its parent's scope: the global query filter is applied per
        // entity type, so a child without one is readable across workspaces by anything querying this table
        // directly. No foreign key to Workspaces, though — that would give SQL Server a second cascade path to
        // the same rows, and the composite key below already forces this row's workspace to match its parent's.
        builder.HasOne<AiProposal>()
            .WithMany(proposal => proposal.Changes)
            .HasForeignKey(change => new { change.WorkspaceId, change.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // A proposal's changes in review order.
        builder.HasIndex(change => new { change.WorkspaceId, change.AiProposalId, change.SortOrder })
            .HasDatabaseName("IX_AiStructuredChanges_Workspace_Proposal_SortOrder");

        // What the creator actually took, without reading every change of every proposal.
        builder.HasIndex(change => new { change.WorkspaceId, change.AiProposalId, change.Disposition })
            .HasDatabaseName("IX_AiStructuredChanges_Workspace_Proposal_Disposition");
    }
}
