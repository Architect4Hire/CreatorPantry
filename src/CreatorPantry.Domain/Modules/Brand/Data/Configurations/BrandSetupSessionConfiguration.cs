using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSetupSessionConfiguration : IEntityTypeConfiguration<BrandSetupSession>
{
    public void Configure(EntityTypeBuilder<BrandSetupSession> builder)
    {
        builder.ToTable("BrandSetupSessions", table =>
        {
            table.HasCheckConstraint("CK_BrandSetupSessions_Status_Specified", "Status IN (1, 2)");
            table.HasCheckConstraint(
                "CK_BrandSetupSessions_Completed_Consistent",
                "(Status = 2 AND CompletedUtc IS NOT NULL) OR (Status = 1 AND CompletedUtc IS NULL)");
        });

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.UserId).IsRequired().HasMaxLength(450); // Identity key length.
        builder.Property(session => session.Status).IsRequired();
        builder.Property(session => session.CurrentStep).IsRequired().HasMaxLength(32);
        builder.Property(session => session.FurthestStep).IsRequired().HasMaxLength(32);
        builder.PrimitiveCollection(session => session.CompletedSteps).IsRequired();
        builder.PrimitiveCollection(session => session.SkippedSteps).IsRequired();

        // nvarchar(max): the 64 KB bound is enforced at the edge in bytes, which a column length in characters
        // cannot express.
        builder.Property(session => session.DraftJson)
            .IsRequired()
            .HasMaxLength(SaveBrandSetupSessionViewModelValidator.DraftMaxBytes);

        builder.Property(session => session.CreatedUtc).IsRequired();
        builder.Property(session => session.UpdatedUtc).IsRequired();
        builder.Property(session => session.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(session => session.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One live session per user per workspace. Also serves every lookup the repository makes.
        builder.HasIndex(session => new { session.WorkspaceId, session.UserId })
            .IsUnique()
            .HasDatabaseName("UX_BrandSetupSessions_Workspace_User");
    }
}
