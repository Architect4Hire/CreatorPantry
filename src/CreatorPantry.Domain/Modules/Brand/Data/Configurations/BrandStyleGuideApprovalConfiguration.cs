using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideApprovalConfiguration : IEntityTypeConfiguration<BrandStyleGuideApproval>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuideApproval> builder)
    {
        builder.ToTable("BrandStyleGuideApprovals");

        // The version is the key: a version is approved at most once. Also the target of the workspace
        // default's foreign key, which is how only an approved version can be made the default.
        builder.HasKey(approval => new { approval.WorkspaceId, approval.BrandStyleGuideVersionId });

        builder.Property(approval => approval.Reason).HasMaxLength(BrandPolicy.ReasonMaxLength);
        builder.Property(approval => approval.ApprovedByMembershipId).IsRequired();
        builder.Property(approval => approval.ApprovedAt).IsRequired();

        // Part of the version: it goes when the version does, which only workspace erasure causes.
        builder.HasOne<BrandStyleGuideVersion>()
            .WithOne()
            .HasForeignKey<BrandStyleGuideApproval>(approval => new { approval.WorkspaceId, approval.BrandStyleGuideVersionId })
            .HasPrincipalKey<BrandStyleGuideVersion>(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
