using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideDefaultConfiguration : IEntityTypeConfiguration<BrandStyleGuideDefault>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideDefault> builder)
    {
        builder.ToTable("BrandStyleGuideDefaults");

        // The workspace is the key: at most one default per workspace.
        builder.HasKey(@default => @default.WorkspaceId);
        builder.Property(@default => @default.WorkspaceId).ValueGeneratedNever();

        builder.Property(@default => @default.BrandStyleGuideVersionId).IsRequired();
        builder.Property(@default => @default.Reason).HasMaxLength(BrandPolicy.ReasonMaxLength);
        builder.Property(@default => @default.ActivatedByMembershipId).IsRequired();
        builder.Property(@default => @default.ActivatedAt).IsRequired();
        builder.Property(@default => @default.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithOne()
            .HasForeignKey<BrandStyleGuideDefault>(@default => @default.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Onto the approval, not the version: a draft has no approval row, so it cannot be the default, and a
        // version in another workspace has no row under this workspace id. Restrict: the default is repointed
        // or removed deliberately, never as a side effect.
        builder.HasOne<BrandStyleGuideApproval>()
            .WithMany()
            .HasForeignKey(@default => new { @default.WorkspaceId, @default.BrandStyleGuideVersionId })
            .HasPrincipalKey(approval => new { approval.WorkspaceId, approval.BrandStyleGuideVersionId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(@default => new { @default.WorkspaceId, @default.BrandStyleGuideVersionId })
            .HasDatabaseName("IX_BrandStyleGuideDefaults_Workspace_Version");
    }
}
