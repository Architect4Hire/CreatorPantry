using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeVersionSnapshotConfiguration : IEntityTypeConfiguration<RecipeVersionSnapshot>
{
    public void Configure(EntityTypeBuilder<RecipeVersionSnapshot> builder)
    {
        builder.ToTable("RecipeVersionSnapshots");

        // The version's own id is the key, which is what makes one-snapshot-per-version a fact rather than a
        // convention: a second row for the same version is a primary key violation.
        builder.HasKey(snapshot => snapshot.RecipeVersionId);

        // Left at the provider's default string mapping — nvarchar(max) on SQL Server — and that is a settled
        // decision, not a deferral. SQL Server 2025's native `json` type exists and EF Core 10 supports it,
        // but its advantages are indexing, path queries and partial updates, and this document has none of
        // those uses: choosing a document over shadow tables explicitly traded away querying into a snapshot,
        // and the row is written once and read whole. What it would cost is a provider-specific column type
        // whose name lands with numeric affinity under the SQLite the tests run on, so the suite would be
        // exercising a different column shape than production. Revisit only if snapshots ever need to be
        // queried by content — and then the write-time JSON validation comes along as a genuine bonus.
        builder.Property(snapshot => snapshot.Document)
            .IsRequired();

        builder.HasOne<RecipeVersion>()
            .WithOne(version => version.Snapshot)
            .HasForeignKey<RecipeVersionSnapshot>(snapshot => new { snapshot.WorkspaceId, snapshot.RecipeVersionId })
            .HasPrincipalKey<RecipeVersion>(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
