using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandLinkConfiguration : IEntityTypeConfiguration<BrandLink>
{
    public void Configure(EntityTypeBuilder<BrandLink> builder)
    {
        builder.ToTable("BrandLinks", table =>
        {
            table.HasCheckConstraint("CK_BrandLinks_SortOrder_NonNegative", "SortOrder >= 0");

            // Zero is not a kind: an unset enum must fail loudly rather than read as a real value.
            table.HasCheckConstraint("CK_BrandLinks_Kind_Specified", "Kind <> 0");
        });

        builder.HasKey(link => link.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(link => link.Id).ValueGeneratedNever();

        builder.Property(link => link.Kind).IsRequired();
        builder.Property(link => link.Url).IsRequired().HasMaxLength(BrandPolicy.UrlMaxLength);
        builder.Property(link => link.Label).HasMaxLength(BrandPolicy.LinkLabelMaxLength);
        builder.Property(link => link.SortOrder).IsRequired();

        builder.HasOne<BrandProfile>()
            .WithMany(profile => profile.Links)
            .HasForeignKey(link => new { link.WorkspaceId, link.BrandProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(link => new { link.WorkspaceId, link.BrandProfileId, link.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_BrandLinks_Workspace_Profile_Order");
    }
}
