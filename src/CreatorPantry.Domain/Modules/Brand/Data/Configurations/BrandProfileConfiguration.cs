using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandProfileConfiguration : IEntityTypeConfiguration<BrandProfile>
{
    public void Configure(EntityTypeBuilder<BrandProfile> builder)
    {
        builder.ToTable("BrandProfiles", table =>
        {
            table.HasCheckConstraint("CK_BrandProfiles_BrandName_NotBlank", "trim(BrandName) <> ''");
            table.HasCheckConstraint("CK_BrandProfiles_Revision_Positive", "Revision >= 1");
        });

        builder.HasKey(profile => profile.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(profile => profile.Id).ValueGeneratedNever();

        // The target of every child's composite foreign key, so a child owned by a different workspace than
        // its profile is unrepresentable rather than merely refused at save time.
        builder.HasAlternateKey(profile => new { profile.WorkspaceId, profile.Id })
            .HasName("AK_BrandProfiles_Workspace_Id");

        builder.Property(profile => profile.BrandName).IsRequired().HasMaxLength(BrandPolicy.BrandNameMaxLength);
        builder.Property(profile => profile.ShortDescription).HasMaxLength(BrandPolicy.ShortDescriptionMaxLength);
        builder.Property(profile => profile.DefaultAudience).HasMaxLength(BrandPolicy.DefaultAudienceMaxLength);
        builder.Property(profile => profile.Locale).HasMaxLength(BrandPolicy.LocaleMaxLength);
        builder.Property(profile => profile.TimeZoneId).HasMaxLength(BrandPolicy.TimeZoneIdMaxLength);

        builder.Property(profile => profile.Revision).IsRequired();
        builder.Property(profile => profile.CreatedByMembershipId).IsRequired();
        builder.Property(profile => profile.UpdatedByMembershipId).IsRequired();
        builder.Property(profile => profile.CreatedAt).IsRequired();
        builder.Property(profile => profile.UpdatedAt).IsRequired();
        builder.Property(profile => profile.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(profile => profile.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One brand per workspace. Also serves every lookup by workspace.
        builder.HasIndex(profile => profile.WorkspaceId)
            .IsUnique()
            .HasDatabaseName("UX_BrandProfiles_Workspace");
    }
}
