using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandProfileRevisionConfiguration : IEntityTypeConfiguration<BrandProfileRevision>
{
    public void Configure(EntityTypeBuilder<BrandProfileRevision> builder)
    {
        builder.ToTable("BrandProfileRevisions", table =>
        {
            table.HasCheckConstraint("CK_BrandProfileRevisions_Revision_Positive", "Revision >= 1");
            table.HasCheckConstraint("CK_BrandProfileRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");
        });

        builder.HasKey(revision => revision.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(revision => revision.Id).ValueGeneratedNever();

        builder.Property(revision => revision.Revision).IsRequired();
        builder.Property(revision => revision.SchemaVersion).IsRequired();
        builder.Property(revision => revision.Document).IsRequired();
        builder.Property(revision => revision.Reason).HasMaxLength(BrandPolicy.ReasonMaxLength);
        builder.Property(revision => revision.ChangedByMembershipId).IsRequired();
        builder.Property(revision => revision.CreatedAt).IsRequired();

        // Composite, so a revision of another workspace's profile is unrepresentable. Cascade only from the
        // workspace's own deletion path: nothing deletes a profile today, and a profile's history goes with it.
        builder.HasOne<BrandProfile>()
            .WithMany()
            .HasForeignKey(revision => new { revision.WorkspaceId, revision.BrandProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One revision number per profile. This is also the second guard on a lost race: two writers that both
        // read revision N cannot both write N + 1.
        builder.HasIndex(revision => new { revision.WorkspaceId, revision.BrandProfileId, revision.Revision })
            .IsUnique()
            .HasDatabaseName("UX_BrandProfileRevisions_Workspace_Profile_Revision");
    }
}
