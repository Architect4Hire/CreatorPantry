using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeVersionConfiguration : IEntityTypeConfiguration<RecipeVersion>
{
    public void Configure(EntityTypeBuilder<RecipeVersion> builder)
    {
        builder.ToTable("RecipeVersions", table =>
        {
            table.HasCheckConstraint(
                "CK_RecipeVersions_VersionNumber_Positive",
                "VersionNumber >= 1");

            // A proposal id belongs to a version that came from a proposal, and nothing else. Without this
            // the two columns can disagree, and the provenance ai.md asks for stops being reliable in
            // exactly the case it matters.
            table.HasCheckConstraint(
                "CK_RecipeVersions_Proposal_Source",
                $"(AiProposalId IS NULL AND Source <> {(int)RecipeVersionSource.AiProposalAccepted}) OR "
                    + $"(AiProposalId IS NOT NULL AND Source = {(int)RecipeVersionSource.AiProposalAccepted})");

            // A version cannot be its own parent. Longer cycles are not expressible: a parent is always an
            // already-written row, and rows here are immutable.
            table.HasCheckConstraint(
                "CK_RecipeVersions_Parent_NotSelf",
                "ParentVersionId IS NULL OR ParentVersionId <> Id");

            // The same binding the proposal constraint enforces, for the same reason: a restored-from id
            // belongs to a version that came from a restore, and nothing else. Without it the two columns can
            // disagree, and then a history can claim a restore that restored nothing, or an ordinary edit
            // that came from somewhere.
            table.HasCheckConstraint(
                "CK_RecipeVersions_RestoredFrom_Source",
                $"(RestoredFromVersionId IS NULL AND Source <> {(int)RecipeVersionSource.Restore}) OR "
                    + $"(RestoredFromVersionId IS NOT NULL AND Source = {(int)RecipeVersionSource.Restore})");

            // A version cannot be restored from itself, exactly as it cannot be its own parent — and here the
            // row being restored is always older still, because it was read before this one existed.
            table.HasCheckConstraint(
                "CK_RecipeVersions_RestoredFrom_NotSelf",
                "RestoredFromVersionId IS NULL OR RestoredFromVersionId <> Id");
        });

        builder.HasKey(version => version.Id);

        // The target of the snapshot's composite foreign key, and of ParentVersionId's.
        builder.HasAlternateKey(version => new { version.WorkspaceId, version.Id })
            .HasName("AK_RecipeVersions_Workspace_Id");

        builder.Property(version => version.VersionNumber).IsRequired();
        builder.Property(version => version.Source).IsRequired();
        builder.Property(version => version.Readiness).IsRequired();
        builder.Property(version => version.Reason).HasMaxLength(RecipePolicy.NoteMaxLength);
        builder.Property(version => version.CreatedByMembershipId).IsRequired();
        builder.Property(version => version.CreatedAt).IsRequired();
        builder.Property(version => version.SnapshotSchemaVersion).IsRequired();

        // Plain bytes, not a row version. It is a copy of another row's token, recorded as evidence of what
        // this snapshot was taken from; marking it IsRowVersion would make the database generate it and
        // destroy the very value being recorded.
        builder.Property(version => version.BasedOnRecipeRowVersion).HasMaxLength(8);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: this is the constraint that makes versions outlive ordinary recipe edits.
        // Deleting a recipe that has history is refused by the database, so removing one has to be a
        // deliberate operation that says what becomes of the versions. Deleting the whole workspace still
        // reaches both tables, by their own cascades from Workspaces.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Lineage within one workspace. Restrict for the same reason as above — and because these rows are
        // immutable, a parent can never be removed out from under its child anyway.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.ParentVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The second lineage edge, configured exactly like the first: within one workspace, against the same
        // alternate key, and Restrict. Two relationships to the same entity type rather than one with two
        // meanings — see RecipeVersion.RestoredFromVersionId for why they cannot be the same column.
        //
        // The workspace component is what makes this safe rather than merely tidy: an id alone could name a
        // version of another workspace's recipe, and the composite key makes that unreferenceable at the
        // database rather than only refused by the code above.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.RestoredFromVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The version number creators cite. Unique per recipe, and never reused.
        //
        // Readiness is included rather than a key column so that a recipe search can read the latest version's
        // readiness — for its filter and for the row it returns — as a top-one seek of this index with no lookup
        // into the table. An included column does not participate in the uniqueness this index enforces.
        builder.HasIndex(version => new { version.WorkspaceId, version.RecipeId, version.VersionNumber })
            .IsUnique()
            .IncludeProperties(version => version.Readiness)
            .HasDatabaseName("UX_RecipeVersions_Workspace_Recipe_VersionNumber");

        // A recipe's history, newest first.
        builder.HasIndex(version => new { version.WorkspaceId, version.RecipeId, version.CreatedAt })
            .HasDatabaseName("IX_RecipeVersions_Workspace_Recipe_CreatedAt");

        // Finding every row written under an old document shape, without deserializing the archive.
        builder.HasIndex(version => new { version.WorkspaceId, version.SnapshotSchemaVersion })
            .HasDatabaseName("IX_RecipeVersions_Workspace_SnapshotSchemaVersion");
    }
}
