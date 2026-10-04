using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideEditSessionConfiguration : IEntityTypeConfiguration<BrandStyleGuideEditSession>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideEditSession> builder)
    {
        builder.ToTable("BrandStyleGuideEditSessions", table =>
            table.HasCheckConstraint(
                "CK_BrandStyleGuideEditSessions_BaselineVersionNumber_Positive",
                "BaselineVersionNumber >= 1"));

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.UserId).IsRequired().HasMaxLength(450); // Identity key length.
        builder.Property(session => session.BaselineVersionNumber).IsRequired();

        // nvarchar(max): the 512 KB bound is enforced at the edge in bytes, which a column length in
        // characters cannot express.
        builder.Property(session => session.DraftJson)
            .IsRequired()
            .HasMaxLength(BrandStyleGuideEditSessionDraft.MaxBytes);

        builder.Property(session => session.CreatedUtc).IsRequired();
        builder.Property(session => session.UpdatedUtc).IsRequired();
        builder.Property(session => session.RowVersion).IsRowVersion();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(session => session.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, as the guide's versions take it: the workspace is the only cascade. Two cascade paths into
        // one table is what SQL Server refuses outright (error 1785), and the workspace edge is the one that has
        // to survive, because erasing a workspace must take its drafts with it. No ordinary code deletes a
        // guide — the version foreign key already refuses it — so nothing is held back in practice.
        builder.HasOne<BrandStyleGuide>()
            .WithMany()
            .HasForeignKey(session => new { session.WorkspaceId, session.BrandStyleGuideId })
            .HasPrincipalKey(guide => new { guide.WorkspaceId, guide.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One unsaved edit per creator per guide — where they were, not a history of where they have been.
        // Also serves every lookup the repository makes.
        builder.HasIndex(session => new { session.WorkspaceId, session.BrandStyleGuideId, session.UserId })
            .IsUnique()
            .HasDatabaseName("UX_BrandStyleGuideEditSessions_Workspace_Guide_User");
    }
}
