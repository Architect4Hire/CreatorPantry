using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideRuleConfiguration : IEntityTypeConfiguration<BrandStyleGuideRule>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideRule> builder)
    {
        builder.ToTable("BrandStyleGuideRules", table =>
        {
            table.HasCheckConstraint("CK_BrandStyleGuideRules_Kind_Specified", "Kind <> 0");
            table.HasCheckConstraint("CK_BrandStyleGuideRules_Text_NotBlank", "trim(Text) <> ''");
            table.HasCheckConstraint("CK_BrandStyleGuideRules_SortOrder_NonNegative", "SortOrder >= 0");
        });

        builder.HasKey(rule => rule.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(rule => rule.Id).ValueGeneratedNever();

        builder.Property(rule => rule.Kind).IsRequired();
        builder.Property(rule => rule.Text).IsRequired().HasMaxLength(BrandPolicy.StyleGuideRuleTextMaxLength);
        builder.Property(rule => rule.SortOrder).IsRequired();

        // Part of the version: it goes when the version does, which only workspace erasure causes.
        builder.HasOne<BrandStyleGuideVersion>()
            .WithMany(version => version.Rules)
            .HasForeignKey(rule => new { rule.WorkspaceId, rule.BrandStyleGuideVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(rule => new { rule.WorkspaceId, rule.BrandStyleGuideVersionId, rule.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_BrandStyleGuideRules_Workspace_Version_Order");
    }
}
