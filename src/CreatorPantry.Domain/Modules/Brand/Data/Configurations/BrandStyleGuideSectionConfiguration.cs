using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideSectionConfiguration : IEntityTypeConfiguration<BrandStyleGuideSection>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideSection> builder)
    {
        builder.ToTable("BrandStyleGuideSections", table =>
        {
            table.HasCheckConstraint("CK_BrandStyleGuideSections_SectionKey_Specified", "SectionKey <> 0");

            // A blank section has no row.
            table.HasCheckConstraint("CK_BrandStyleGuideSections_Body_NotBlank", "trim(Body) <> ''");

            // A channel variant names its channel, and nothing else names one.
            table.HasCheckConstraint(
                "CK_BrandStyleGuideSections_ChannelKey_Variant",
                $"(SectionKey = {(int)BrandStyleGuideSectionKey.ChannelVariant} AND trim(ChannelKey) <> '') OR "
                    + $"(SectionKey <> {(int)BrandStyleGuideSectionKey.ChannelVariant} AND ChannelKey = '')");
        });

        builder.HasKey(section => section.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(section => section.Id).ValueGeneratedNever();

        builder.Property(section => section.SectionKey).IsRequired();
        builder.Property(section => section.ChannelKey).IsRequired().HasMaxLength(BrandPolicy.ChannelKeyMaxLength);
        builder.Property(section => section.Body).IsRequired().HasMaxLength(BrandPolicy.StyleGuideSectionBodyMaxLength);

        // Part of the version: it goes when the version does, which only workspace erasure causes.
        builder.HasOne<BrandStyleGuideVersion>()
            .WithMany(version => version.Sections)
            .HasForeignKey(section => new { section.WorkspaceId, section.BrandStyleGuideVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The cardinality: one section per key, and one channel variant per channel.
        builder.HasIndex(section => new { section.WorkspaceId, section.BrandStyleGuideVersionId, section.SectionKey, section.ChannelKey })
            .IsUnique()
            .HasDatabaseName("UX_BrandStyleGuideSections_Workspace_Version_Key_Channel");
    }
}
