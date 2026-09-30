using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandChannelDefaultConfiguration : IEntityTypeConfiguration<BrandChannelDefault>
{
    public void Configure(EntityTypeBuilder<BrandChannelDefault> builder)
    {
        builder.ToTable("BrandChannelDefaults", table =>
            table.HasCheckConstraint("CK_BrandChannelDefaults_SortOrder_NonNegative", "SortOrder >= 0"));

        builder.HasKey(item => item.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.ChannelKey).IsRequired().HasMaxLength(BrandPolicy.ChannelKeyMaxLength);
        builder.Property(item => item.SortOrder).IsRequired();

        builder.HasOne<BrandProfile>()
            .WithMany(profile => profile.ChannelDefaults)
            .HasForeignKey(item => new { item.WorkspaceId, item.BrandProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(item => new { item.WorkspaceId, item.BrandProfileId, item.ChannelKey })
            .IsUnique()
            .HasDatabaseName("UX_BrandChannelDefaults_Workspace_Profile_Channel");

        builder.HasIndex(item => new { item.WorkspaceId, item.BrandProfileId, item.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_BrandChannelDefaults_Workspace_Profile_Order");
    }
}
